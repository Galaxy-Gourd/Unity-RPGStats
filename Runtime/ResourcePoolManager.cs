using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Manages resource pools (current HP, current Stamina, current Magicka, etc.)
    /// as a companion to StatManager which owns the "max" values.
    ///
    /// A resource pool is a (current, max) pair where:
    ///   - "max" is a stat on StatManager (e.g., MaxHealth = stat 100)
    ///   - "current" is a float tracked here, clamped to [0, max]
    ///
    /// When the max stat changes (e.g., Fortify Health buff expires), current is
    /// automatically clamped down. Current never exceeds max unless explicitly
    /// set via SetCurrent with clamp=false (for overheal mechanics).
    ///
    /// Design:
    ///   - Flat arrays parallel to StatManager's entity slots (same EntityStatHandle)
    ///   - Per-entity pool data is a compact struct with parallel arrays
    ///   - Zero allocation on hot paths (damage, heal, regen tick)
    ///   - Serializable for save/load
    ///   - Subscribes to StatManager.OnStatChanged for auto-clamp
    /// </summary>
    public static class ResourcePoolManager
    {
        // ------------------------------------------------------------------
        // Pool definition: which stat IDs are resource pools?
        // ------------------------------------------------------------------

        /// <summary>
        /// Defines a resource pool: a poolId (for lookup) linked to a maxStatId
        /// (the stat on StatManager that provides the ceiling).
        /// </summary>
        public readonly struct PoolDefinition
        {
            /// <summary>Unique identifier for this pool. Can match the maxStatId or be separate.</summary>
            public readonly int PoolId;

            /// <summary>The StatManager stat ID that provides the max value.</summary>
            public readonly int MaxStatId;

            /// <summary>If true, current starts at max on registration. If false, starts at 0.</summary>
            public readonly bool StartFull;

            public PoolDefinition(int poolId, int maxStatId, bool startFull = true)
            {
                PoolId    = poolId;
                MaxStatId = maxStatId;
                StartFull = startFull;
            }
        }

        // ------------------------------------------------------------------
        // Internal storage
        // ------------------------------------------------------------------

        private struct EntityPoolData
        {
            public bool Alive;
            public int  Generation;
            public int  PoolCount;

            public int[]   PoolIds;          // slot -> poolId
            public int[]   MaxStatIds;       // slot -> maxStatId (for fast OnStatChanged lookup)
            public float[] CurrentValues;    // slot -> current value
            public float[] ShieldValues;     // slot -> temporary shield buffer (absorbed before current)

            public Dictionary<int, int> PoolIdToSlot;  // poolId -> slot
            public Dictionary<int, int> MaxStatToSlot;  // maxStatId -> slot (for OnStatChanged)
        }

        private static EntityPoolData[] _entities;
        private static int _entityCapacity;
        private static bool _initialized;

        // Global pool definitions — registered once at init.
        private static PoolDefinition[] _definitions;
        private static Dictionary<int, PoolDefinition> _defLookup;

        /// <summary>Fired when a pool's current value changes: (handle, poolId, oldCurrent, newCurrent).</summary>
        public static event Action<EntityStatHandle, int, float, float> OnPoolChanged;

        /// <summary>Fired when a pool's shield changes: (handle, poolId, oldShield, newShield).</summary>
        public static event Action<EntityStatHandle, int, float, float> OnShieldChanged;

        /// <summary>
        /// Fired once when a pool's current transitions from above zero to depleted (&lt;= 0).
        /// Does not re-fire while the pool stays depleted. Use for death/knock-out handling
        /// instead of polling IsDepleted: (handle, poolId).
        /// </summary>
        public static event Action<EntityStatHandle, int> OnPoolDepleted;

        // ------------------------------------------------------------------
        // Enter Play Mode cleanup
        // ------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _entities       = null;
            _entityCapacity = 0;
            _initialized    = false;
            _definitions    = null;
            _defLookup      = null;
            OnPoolChanged   = null;
            OnShieldChanged = null;
            OnPoolDepleted  = null;
        }

        // ------------------------------------------------------------------
        // Initialization
        // ------------------------------------------------------------------

        /// <summary>
        /// Boot the resource pool infrastructure. Call once at engine startup,
        /// after StatManager.Boot(). Does NOT register any pool definitions —
        /// those come from plugins via RegisterPoolDefinition().
        /// </summary>
        public static void Boot(int entityCapacity = 256)
        {
            if (_initialized)
                Debug.LogWarning("ResourcePoolManager.Boot() called while already initialized — " +
                                 "re-initializing and discarding all existing pools.");

            _definitions    = System.Array.Empty<PoolDefinition>();
            _defLookup      = new Dictionary<int, PoolDefinition>();
            _entityCapacity = entityCapacity;
            _entities       = new EntityPoolData[entityCapacity];
            _initialized    = true;

            StatManager.OnStatChanged += OnMaxStatChanged;
        }

        public static void Shutdown()
        {
            StatManager.OnStatChanged -= OnMaxStatChanged;
            _entities       = null;
            _entityCapacity = 0;
            _initialized    = false;
            _definitions    = null;
            _defLookup      = null;
            OnPoolChanged   = null;
            OnShieldChanged = null;
            OnPoolDepleted  = null;
        }

        // ------------------------------------------------------------------
        // Dynamic pool registration (mod support)
        // ------------------------------------------------------------------

        /// <summary>
        /// Register a new pool definition at runtime (e.g., a mod adding a "Psi" resource).
        /// Call during plugin loading, BEFORE registering entities that use the new pool.
        /// Returns true if registration succeeded (pool ID not already taken).
        /// </summary>
        public static bool RegisterPoolDefinition(PoolDefinition definition)
        {
            AssertInitialized();

            if (_defLookup.ContainsKey(definition.PoolId))
                return false;

            // Grow the definitions array.
            var newDefs = new PoolDefinition[_definitions.Length + 1];
            _definitions.CopyTo(newDefs, 0);
            newDefs[_definitions.Length] = definition;
            _definitions = newDefs;

            _defLookup[definition.PoolId] = definition;
            return true;
        }

        // ------------------------------------------------------------------
        // Entity registration
        // ------------------------------------------------------------------

        /// <summary>
        /// Register pools for an entity. Call after StatManager.RegisterEntity().
        /// Uses the same EntityStatHandle. Only creates pools for definitions
        /// whose maxStatId exists on the entity.
        /// </summary>
        public static void RegisterEntity(EntityStatHandle handle)
        {
            AssertInitialized();
            EnsureCapacity(handle.Index + 1);

            ref var data = ref _entities[handle.Index];
            data.Alive      = true;
            data.Generation = handle.Generation;

            // Count how many pool definitions apply to this entity.
            int count = 0;
            for (int i = 0; i < _definitions.Length; i++)
            {
                if (StatManager.HasStat(handle, _definitions[i].MaxStatId))
                    count++;
            }

            data.PoolCount = count;
            EnsureArraySize(ref data.PoolIds, count);
            EnsureArraySize(ref data.MaxStatIds, count);
            EnsureArraySize(ref data.CurrentValues, count);
            EnsureArraySize(ref data.ShieldValues, count);
            data.PoolIdToSlot  ??= new Dictionary<int, int>(count);
            data.MaxStatToSlot ??= new Dictionary<int, int>(count);
            data.PoolIdToSlot.Clear();
            data.MaxStatToSlot.Clear();

            int slot = 0;
            for (int i = 0; i < _definitions.Length; i++)
            {
                var def = _definitions[i];
                if (!StatManager.HasStat(handle, def.MaxStatId)) continue;

                data.PoolIds[slot]       = def.PoolId;
                data.MaxStatIds[slot]    = def.MaxStatId;
                data.PoolIdToSlot[def.PoolId]     = slot;
                data.MaxStatToSlot[def.MaxStatId] = slot;

                if (def.StartFull)
                    data.CurrentValues[slot] = StatManager.GetValue(handle, def.MaxStatId);
                else
                    data.CurrentValues[slot] = 0f;

                data.ShieldValues[slot] = 0f;

                slot++;
            }
        }

        /// <summary>
        /// Unregister pools for an entity.
        /// </summary>
        public static void UnregisterEntity(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return;
            ref var data = ref _entities[handle.Index];
            data.Alive = false;
            data.PoolIdToSlot?.Clear();
            data.MaxStatToSlot?.Clear();
        }

        /// <summary>
        /// Unregister pools for multiple entities in one batch.
        /// </summary>
        public static void UnregisterEntities(ReadOnlySpan<EntityStatHandle> handles)
        {
            for (int i = 0; i < handles.Length; i++)
                UnregisterEntity(handles[i]);
        }

        /// <summary>
        /// Register pools for multiple entities in one batch.
        /// Handles must already be registered with StatManager.
        /// Pre-grows the pool array once.
        /// </summary>
        public static void RegisterEntities(ReadOnlySpan<EntityStatHandle> handles)
        {
            AssertInitialized();
            if (handles.Length == 0) return;

            // Find the max index to pre-grow once.
            int maxIndex = 0;
            for (int i = 0; i < handles.Length; i++)
                if (handles[i].Index > maxIndex) maxIndex = handles[i].Index;
            EnsureCapacity(maxIndex + 1);

            // Register each entity's pools.
            for (int i = 0; i < handles.Length; i++)
                RegisterEntity(handles[i]);
        }

        // ------------------------------------------------------------------
        // Query
        // ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetCurrent(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            return data.PoolIdToSlot.TryGetValue(poolId, out int si) ? data.CurrentValues[si] : 0f;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetMax(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;
            return StatManager.GetValue(handle, data.MaxStatIds[si]);
        }

        /// <summary>Returns current / max as a 0-1 fraction. Safe if max is 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetRatio(EntityStatHandle handle, int poolId)
        {
            float max = GetMax(handle, poolId);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (max == 0f) return 0f;
            return GetCurrent(handle, poolId) / max;
        }

        /// <summary>Get the current shield (temporary HP) buffer on a pool.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetShield(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            return data.PoolIdToSlot.TryGetValue(poolId, out int si) ? data.ShieldValues[si] : 0f;
        }

        /// <summary>
        /// Get effective HP: current + shield. This is the total damage the entity
        /// can absorb before the pool is depleted.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetEffectiveTotal(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;
            return data.CurrentValues[si] + data.ShieldValues[si];
        }

        public static bool HasPool(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return false;
            return _entities[handle.Index].PoolIdToSlot.ContainsKey(poolId);
        }

        // ------------------------------------------------------------------
        // Mutation
        // ------------------------------------------------------------------

        /// <summary>
        /// Apply damage. Shield is consumed first; overflow hits current.
        /// Clamps at 0. Returns actual total damage absorbed (shield + current reduction).
        /// </summary>
        public static float ApplyDamage(EntityStatHandle handle, int poolId, float amount)
        {
            if (amount <= 0f) return 0f;
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;

            float remaining = amount;
            float shieldBefore = data.ShieldValues[si];
            float currentBefore = data.CurrentValues[si];

            // Absorb with shield first.
            if (shieldBefore > 0f)
            {
                float shieldAbsorb = Mathf.Min(shieldBefore, remaining);
                data.ShieldValues[si] -= shieldAbsorb;
                remaining -= shieldAbsorb;
            }

            // Remainder hits current.
            if (remaining > 0f)
            {
                data.CurrentValues[si] = Mathf.Max(0f, currentBefore - remaining);
            }

            float shieldDamage  = shieldBefore - data.ShieldValues[si];
            float currentDamage = currentBefore - data.CurrentValues[si];

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (currentBefore != data.CurrentValues[si])
                FirePoolChanged(handle, poolId, currentBefore, data.CurrentValues[si]);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (shieldBefore != data.ShieldValues[si])
                FireShieldChanged(handle, poolId, shieldBefore, data.ShieldValues[si]);
            if (currentBefore > 0f && data.CurrentValues[si] <= 0f)
                FirePoolDepleted(handle, poolId);

            return shieldDamage + currentDamage;
        }

        /// <summary>
        /// Heal (increase current value). Clamps at max. Does NOT affect shield.
        /// Returns actual amount healed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Heal(EntityStatHandle handle, int poolId, float amount)
        {
            if (amount <= 0f) return 0f;
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;

            float max = StatManager.GetValue(handle, data.MaxStatIds[si]);
            float old = data.CurrentValues[si];
            float next = Mathf.Min(max, old + amount);
            data.CurrentValues[si] = next;

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (old != next)
                FirePoolChanged(handle, poolId, old, next);

            return next - old;
        }

        /// <summary>
        /// Set current value directly. Clamped to [0, max] by default.
        /// Pass clamp=false for overheal or special mechanics.
        /// </summary>
        public static void SetCurrent(EntityStatHandle handle, int poolId, float value, bool clamp = true)
        {
            if (!ValidateHandle(handle)) return;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return;

            float old = data.CurrentValues[si];

            if (clamp)
            {
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);
                value = Mathf.Clamp(value, 0f, max);
            }

            data.CurrentValues[si] = value;

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (old != value)
                FirePoolChanged(handle, poolId, old, value);
            if (old > 0f && value <= 0f)
                FirePoolDepleted(handle, poolId);
        }

        /// <summary>
        /// Restore all pools on an entity to their max values. Also clears shields.
        /// </summary>
        public static void RestoreAll(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return;
            ref var data = ref _entities[handle.Index];

            for (int si = 0; si < data.PoolCount; si++)
            {
                float old = data.CurrentValues[si];
                float oldShield = data.ShieldValues[si];
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);
                data.CurrentValues[si] = max;
                data.ShieldValues[si]  = 0f;

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (old != max)
                    FirePoolChanged(handle, data.PoolIds[si], old, max);
                if (oldShield > 0f)
                    FireShieldChanged(handle, data.PoolIds[si], oldShield, 0f);
            }
        }

        // ------------------------------------------------------------------
        // Shield (temporary HP) operations
        // ------------------------------------------------------------------

        /// <summary>
        /// Add shield (temporary HP) to a pool. Shield stacks additively.
        /// Shield is absorbed before current HP when taking damage and is not
        /// affected by max stat changes (auto-clamp ignores shield).
        /// 
        /// Shield does not heal — it's a separate buffer on top of current.
        /// Returns new total shield value.
        /// </summary>
        public static float AddShield(EntityStatHandle handle, int poolId, float amount)
        {
            if (amount <= 0f) return GetShield(handle, poolId);
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;

            float old = data.ShieldValues[si];
            data.ShieldValues[si] += amount;
            FireShieldChanged(handle, poolId, old, data.ShieldValues[si]);
            return data.ShieldValues[si];
        }

        /// <summary>
        /// Set shield to a specific value. Use for abilities that replace
        /// rather than stack (e.g., "barrier refreshes to 50, doesn't stack").
        /// </summary>
        public static void SetShield(EntityStatHandle handle, int poolId, float value)
        {
            if (!ValidateHandle(handle)) return;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return;
            float old = data.ShieldValues[si];
            float next = Mathf.Max(0f, value);
            data.ShieldValues[si] = next;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (old != next)
                FireShieldChanged(handle, poolId, old, next);
        }

        /// <summary>
        /// Remove all shield from a pool. Returns amount removed.
        /// </summary>
        public static float ClearShield(EntityStatHandle handle, int poolId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var data = ref _entities[handle.Index];
            if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) return 0f;

            float old = data.ShieldValues[si];
            data.ShieldValues[si] = 0f;
            if (old > 0f)
                FireShieldChanged(handle, poolId, old, 0f);
            return old;
        }

        /// <summary>
        /// Tick shield decay across ALL entities for a specific pool.
        /// Decays shield by decayPerSecond * deltaTime. When shield hits 0 it stops.
        /// Use for temporary shields that fade over time.
        /// </summary>
        public static void TickShieldDecay(int poolId, float decayPerSecond, float deltaTime)
        {
            AssertInitialized();

            float decayAmount = decayPerSecond * deltaTime;
            if (decayAmount <= 0f) return;

            for (int e = 0; e < _entityCapacity; e++)
            {
                ref var data = ref _entities[e];
                if (!data.Alive) continue;
                if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) continue;
                if (data.ShieldValues[si] <= 0f) continue;

                float old = data.ShieldValues[si];
                data.ShieldValues[si] = Mathf.Max(0f, old - decayAmount);
                if (OnShieldChanged != null)
                    FireShieldChanged(new EntityStatHandle(e, data.Generation), poolId, old, data.ShieldValues[si]);
            }
        }

        /// <summary>
        /// Check if a pool is depleted (current &lt;= 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsDepleted(EntityStatHandle handle, int poolId)
        {
            return GetCurrent(handle, poolId) <= 0f;
        }

        // ------------------------------------------------------------------
        // Batch regen tick
        // ------------------------------------------------------------------

        /// <summary>
        /// Tick regeneration for a specific pool across ALL entities.
        /// regenStatId is the stat that provides the regen rate (e.g., HealthRegen).
        /// The regen stat's value is added to current per second.
        /// 
        /// Call once per frame: ResourcePoolManager.TickRegen(StatIds.MaxHealth, StatIds.HealthRegen, deltaTime);
        /// </summary>
        public static void TickRegen(int poolId, int regenStatId, float deltaTime)
        {
            AssertInitialized();

            for (int e = 0; e < _entityCapacity; e++)
            {
                ref var data = ref _entities[e];
                if (!data.Alive) continue;
                if (!data.PoolIdToSlot.TryGetValue(poolId, out int si)) continue;

                var handle = new EntityStatHandle(e, data.Generation);
                if (!StatManager.HasStat(handle, regenStatId)) continue;

                float regenRate = StatManager.GetValue(handle, regenStatId);
                if (regenRate <= 0f) continue;

                float old = data.CurrentValues[si];
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (old >= max) continue; // already full

                float next = Mathf.Min(max, old + regenRate * deltaTime);
                data.CurrentValues[si] = next;

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (old != next)
                    FirePoolChanged(handle, poolId, old, next);
            }
        }

        // ------------------------------------------------------------------
        // Serialization
        // ------------------------------------------------------------------

        [Serializable]
        public struct PoolSaveEntry
        {
            public int   PoolId;
            public float CurrentValue;
            public float ShieldValue;
        }

        [Serializable]
        public struct EntityPoolSaveData
        {
            public PoolSaveEntry[] Pools;
        }

        public static EntityPoolSaveData CaptureEntityState(EntityStatHandle handle)
        {
            var result = new EntityPoolSaveData();
            if (!ValidateHandle(handle)) return result;

            ref var data = ref _entities[handle.Index];
            result.Pools = new PoolSaveEntry[data.PoolCount];

            for (int si = 0; si < data.PoolCount; si++)
            {
                result.Pools[si] = new PoolSaveEntry
                {
                    PoolId       = data.PoolIds[si],
                    CurrentValue = data.CurrentValues[si],
                    ShieldValue  = data.ShieldValues[si],
                };
            }

            return result;
        }

        /// <summary>
        /// Check if any pool on an entity differs from its default state
        /// (current != max, or shield > 0). If false, skip pool serialization.
        /// </summary>
        public static bool HasPoolChanges(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return false;
            ref var data = ref _entities[handle.Index];

            for (int si = 0; si < data.PoolCount; si++)
            {
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (data.CurrentValues[si] != max) return true;
                if (data.ShieldValues[si] > 0f) return true;
            }
            return false;
        }

        /// <summary>
        /// Capture only pools that differ from their default state.
        /// Pools at full current with no shield are skipped.
        /// RestoreEntityState handles sparse data — missing pools stay at defaults.
        /// </summary>
        public static EntityPoolSaveData CaptureEntityStateDelta(EntityStatHandle handle)
        {
            var result = new EntityPoolSaveData();
            if (!ValidateHandle(handle)) return result;

            ref var data = ref _entities[handle.Index];

            var changed = new List<PoolSaveEntry>();
            for (int si = 0; si < data.PoolCount; si++)
            {
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                bool currentChanged = data.CurrentValues[si] != max;
                bool hasShield = data.ShieldValues[si] > 0f;

                if (currentChanged || hasShield)
                {
                    changed.Add(new PoolSaveEntry
                    {
                        PoolId       = data.PoolIds[si],
                        CurrentValue = data.CurrentValues[si],
                        ShieldValue  = data.ShieldValues[si],
                    });
                }
            }

            result.Pools = changed.ToArray();
            return result;
        }

        public static void RestoreEntityState(EntityStatHandle handle, EntityPoolSaveData saveData)
        {
            if (!ValidateHandle(handle)) return;
            if (saveData.Pools == null) return;

            ref var data = ref _entities[handle.Index];

            for (int i = 0; i < saveData.Pools.Length; i++)
            {
                var entry = saveData.Pools[i];
                if (!data.PoolIdToSlot.TryGetValue(entry.PoolId, out int si)) continue;

                // Clamp current to max (max may have changed since save). Shield is unclamped.
                float max = StatManager.GetValue(handle, data.MaxStatIds[si]);
                data.CurrentValues[si] = Mathf.Clamp(entry.CurrentValue, 0f, max);
                data.ShieldValues[si]  = Mathf.Max(0f, entry.ShieldValue);
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        public static int PoolCountForEntity(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return 0;
            return _entities[handle.Index].PoolCount;
        }

        // ------------------------------------------------------------------
        // Internal
        // ------------------------------------------------------------------

        /// <summary>
        /// Called when any stat changes on StatManager. If the changed stat is a
        /// max stat for a pool, clamp current down to the new max.
        /// </summary>
        private static void OnMaxStatChanged(EntityStatHandle handle, int statId, float oldVal, float newVal)
        {
            if (!_initialized) return;
            if (handle.Index >= _entityCapacity) return;

            ref var data = ref _entities[handle.Index];
            if (!data.Alive || data.Generation != handle.Generation) return;
            if (!data.MaxStatToSlot.TryGetValue(statId, out int si)) return;

            float current = data.CurrentValues[si];

            // If max decreased below current, clamp down.
            if (current > newVal)
            {
                data.CurrentValues[si] = newVal;
                FirePoolChanged(handle, data.PoolIds[si], current, newVal);
                if (current > 0f && newVal <= 0f)
                    FirePoolDepleted(handle, data.PoolIds[si]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FirePoolChanged(EntityStatHandle handle, int poolId, float oldVal, float newVal)
        {
            OnPoolChanged?.Invoke(handle, poolId, oldVal, newVal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FireShieldChanged(EntityStatHandle handle, int poolId, float oldVal, float newVal)
        {
            OnShieldChanged?.Invoke(handle, poolId, oldVal, newVal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FirePoolDepleted(EntityStatHandle handle, int poolId)
        {
            OnPoolDepleted?.Invoke(handle, poolId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateHandle(EntityStatHandle handle)
        {
            if (!_initialized) return false;
            if (handle.Index < 0 || handle.Index >= _entityCapacity) return false;
            ref var data = ref _entities[handle.Index];
            return data.Alive && data.Generation == handle.Generation;
        }

        private static void EnsureCapacity(int required)
        {
            if (required <= _entityCapacity) return;
            int newCap = Math.Max(required, _entityCapacity * 2);
            var newArr = new EntityPoolData[newCap];
            if (_entities != null)
                Array.Copy(_entities, newArr, _entityCapacity);
            _entities = newArr;
            _entityCapacity = newCap;
        }

        private static void EnsureArraySize<T>(ref T[] array, int minSize)
        {
            if (array == null || array.Length < minSize)
                array = new T[minSize];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AssertInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("ResourcePoolManager.Boot() has not been called.");
        }
    }
}