using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Central static manager that owns ALL stat data for ALL entities.
    /// 
    /// Design principles:
    ///   - Entities are registered/unregistered and receive a lightweight EntityStatHandle.
    ///   - All stat data lives in flat arrays owned by this manager.
    ///   - Eager evaluation: mutations (AddModifier, SetBaseValue, etc.) immediately
    ///     recalculate the affected stat and cascade through derived dependencies.
    ///     GetValue is always a direct array read with no computation.
    ///   - Per-entity stat storage is a compact Dictionary&lt;int,int&gt; mapping statId -> slot index
    ///     into per-entity parallel arrays (base values, cached values).
    ///   - Modifiers are stored in a flat list per entity-stat pair, accessed via slot.
    ///   - Timed modifiers across ALL entities are stored in a single flat list and batch-ticked.
    ///   - All public methods are static. No instance allocation for callers.
    /// </summary>
    public static class StatManager
    {
        // ------------------------------------------------------------------
        // Internal storage
        // ------------------------------------------------------------------

        private struct EntitySlot
        {
            public int  Generation;
            public bool Alive;

            // Per-stat parallel arrays, indexed by local slot index.
            public int   StatCount;
            public int[] StatIdsBySlot;               // slot -> statId
            public Dictionary<int, int> StatIdToSlot; // statId -> slot
            public float[]  BaseValues;
            public float[]  CachedValues;
            public bool[]   DirtyFlags;
            public List<StatModifier>[] Modifiers;    // lazily allocated per slot
        }

        private struct TimedEntry
        {
            public int           EntityIndex;
            public int           EntityGeneration;
            public int           StatSlot;
            public int           StatId;
            public StatModifier  Modifier;
            public float         RemainingTime;
            public float         TotalDuration;
        }

        private static EntitySlot[] _entities;
        private static int _entityCount;
        private static int _entityCapacity;
        private static Stack<int> _freeList;

        private static List<TimedEntry> _timedModifiers;

        private static StatDefinitionDatabase _database;
        private static bool _initialized;

        // Dependency graph: when stat X changes, which stats need to be marked dirty?
        // Key = statId that changed, Value = array of dependent statIds.
        // Built once at Initialize from StatDefinition (with derivation) data.
        private static Dictionary<int, int[]> _dependencyGraph;

        // Cached formula references per statId. Null entry = not a derived stat.
        private static Dictionary<int, StatFormulaOp[]> _formulas;

        // Custom C# evaluator functions per statId. Takes priority over postfix formulas.
        // Signature: (int entityIndex) => float derivedBaseValue
        // The entityIndex can be used with GetValueInternal to read other stats.
        private static Dictionary<int, Func<int, float>> _customEvaluators;

        // Event for stat changes (entityHandle, statId, oldValue, newValue).
        // Consumers filter by entity. Avoids per-entity delegate allocation.
        public static event Action<EntityStatHandle, int, float, float> OnStatChanged;

        private const int DefaultEntityCapacity = 256;
        private const int DefaultStatsPerEntity = 8;
        private const int DefaultTimedCapacity  = 128;

        // Runtime backstop against dependency cycles. The dependency graph is
        // validated to be acyclic at registration (see RegisterStatDefinition),
        // but this guards against any undeclared cycle reaching the recursive
        // cascade — aborting the cascade instead of overflowing the stack.
        private const int MaxRecalcDepth = 256;
        private static int  _recalcDepth;
        private static bool _cycleWarned;

        // ------------------------------------------------------------------
        // Enter Play Mode cleanup (domain reload disabled)
        // ------------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _entities       = null;
            _entityCount    = 0;
            _entityCapacity = 0;
            _freeList       = null;
            _timedModifiers = null;
            _database       = null;
            _initialized    = false;
            _dependencyGraph = null;
            _formulas       = null;
            _customEvaluators = null;
            OnStatChanged   = null;
            _recalcDepth    = 0;
            _cycleWarned    = false;
            StatFormulaEvaluator.GetStatValueFn = null;
            StatFormulaEvaluator.GetBaseValueFn = null;
            StatFormulaEvaluator.ResetDiagnostics();
        }

        // ------------------------------------------------------------------
        // Initialization
        // ------------------------------------------------------------------

        /// <summary>
        /// Boot the stat system infrastructure. Call once at engine startup,
        /// before any plugins register content.
        /// 
        /// Does NOT load any stat definitions — those come from plugins via
        /// RegisterStatDefinition() or RegisterStatDefinitions().
        /// </summary>
        public static void Boot(int entityCapacity = DefaultEntityCapacity)
        {
            if (_initialized)
                Debug.LogWarning("StatManager.Boot() called while already initialized — " +
                                 "re-initializing and discarding all existing entities and registrations.");

            _entityCapacity = entityCapacity;
            _entityCount    = 0;
            _entities       = new EntitySlot[entityCapacity];
            _freeList       = new Stack<int>(64);

            _timedModifiers   = new List<TimedEntry>(DefaultTimedCapacity);
            _dependencyGraph  = new Dictionary<int, int[]>();
            _formulas         = new Dictionary<int, StatFormulaOp[]>();
            _customEvaluators = new Dictionary<int, Func<int, float>>();
            _recalcDepth      = 0;
            _cycleWarned      = false;
            StatFormulaEvaluator.ResetDiagnostics();

            // Create the internal database (populated by RegisterStatDefinition).
            _database = ScriptableObject.CreateInstance<StatDefinitionDatabase>();
            _database.Initialize();

            _initialized = true;

            // Register evaluator callbacks (avoids delegate allocation per Recalculate).
            StatFormulaEvaluator.GetStatValueFn = GetValueInternal;
            StatFormulaEvaluator.GetBaseValueFn = GetBaseValueInternal;
        }

        /// <summary>
        /// Convenience: register all definitions from a database asset at once.
        /// Equivalent to calling RegisterStatDefinition for each entry.
        /// This is how a plugin loads its content pack.
        /// 
        /// Returns the number of definitions successfully registered.
        /// </summary>
        public static int RegisterStatDefinitions(StatDefinitionDatabase database)
        {
            AssertInitialized();
            database.Initialize();

            int registered = 0;
            foreach (var def in database.All)
            {
                if (RegisterStatDefinition(def))
                    registered++;
            }
            return registered;
        }

        public static void Shutdown()
        {
            _entities       = null;
            _entityCount    = 0;
            _entityCapacity = 0;
            _freeList       = null;
            _timedModifiers = null;
            _initialized    = false;
            _dependencyGraph = null;
            _formulas       = null;
            _customEvaluators = null;
            OnStatChanged   = null;
            _recalcDepth    = 0;
            _cycleWarned    = false;
            StatFormulaEvaluator.GetStatValueFn = null;
            StatFormulaEvaluator.GetBaseValueFn = null;
            StatFormulaEvaluator.ResetDiagnostics();
        }

        // ------------------------------------------------------------------
        // Dynamic stat registration (mod support)
        // ------------------------------------------------------------------

        /// <summary>
        /// Register a new stat definition at runtime (e.g., from a mod/plugin).
        /// Updates the database, dependency graph, and formula cache.
        /// Call this during plugin loading, BEFORE registering entities that use
        /// the new stat. Entities registered before this call won't have the stat.
        /// 
        /// Returns true if registration succeeded (stat ID not already taken).
        /// </summary>
        public static bool RegisterStatDefinition(StatDefinition definition)
        {
            AssertInitialized();

            if (definition == null)
            {
                Debug.LogError("StatManager.RegisterStatDefinition: definition is null.");
                return false;
            }

            // Validate the postfix formula BEFORE committing anything, so bad
            // data is rejected at registration instead of throwing later in the
            // (hot) recalculation path.
            if (definition.HasFormula &&
                !StatFormulaEvaluator.Validate(definition.Formula, out string formulaError))
            {
                Debug.LogError(
                    $"StatManager.RegisterStatDefinition: stat {definition.StatId} " +
                    $"('{definition.DisplayName}') has an invalid formula: {formulaError}. " +
                    "Registration rejected.");
                return false;
            }

            // Reject dependencies that would introduce a cycle in the graph.
            // A cyclic derivation would infinitely recurse the eager cascade.
            if (definition.HasDependencies &&
                WouldCreateCycle(definition.StatId, definition.Dependencies, out int cycleDep))
            {
                Debug.LogError(
                    $"StatManager.RegisterStatDefinition: stat {definition.StatId} " +
                    $"('{definition.DisplayName}') declares a dependency on {cycleDep} that " +
                    "would create a dependency cycle. Registration rejected.");
                return false;
            }

            if (!_database.RegisterDefinition(definition))
                return false;

            // If it has a formula, update the formula cache.
            if (definition.HasFormula)
                _formulas[definition.StatId] = definition.Formula;

            // If it has dependencies, update the dependency graph.
            if (definition.HasDependencies)
            {
                for (int i = 0; i < definition.Dependencies.Length; i++)
                    AddDependencyEdge(definition.Dependencies[i], definition.StatId);
            }

            return true;
        }

        /// <summary>
        /// Override an already-registered stat definition (e.g., a mod rebalancing a core
        /// stat's constraints, formula, or dependencies). Unlike RegisterStatDefinition,
        /// which is add-only, this REPLACES the definition for an existing stat ID.
        ///
        /// Constraints, formula, and dependencies take effect immediately: the stat is
        /// recalculated on every live entity that has it (cascading to dependents). The
        /// new default base value applies only to entities registered AFTER this call —
        /// entities that already exist keep the base value they were seeded with (use
        /// SetBaseValue to retro-adjust those).
        ///
        /// Returns false if the stat isn't registered yet (call RegisterStatDefinition
        /// first), or if the new formula/dependencies fail validation.
        /// </summary>
        public static bool OverrideStatDefinition(StatDefinition definition)
        {
            AssertInitialized();

            if (definition == null)
            {
                Debug.LogError("StatManager.OverrideStatDefinition: definition is null.");
                return false;
            }

            if (!_database.TryGetDefinition(definition.StatId, out var existing))
            {
                Debug.LogError(
                    $"StatManager.OverrideStatDefinition: stat {definition.StatId} is not " +
                    "registered — call RegisterStatDefinition first. Override rejected.");
                return false;
            }

            // Validate the new formula and dependency graph before mutating anything.
            if (definition.HasFormula &&
                !StatFormulaEvaluator.Validate(definition.Formula, out string formulaError))
            {
                Debug.LogError(
                    $"StatManager.OverrideStatDefinition: stat {definition.StatId} " +
                    $"('{definition.DisplayName}') has an invalid formula: {formulaError}. " +
                    "Override rejected.");
                return false;
            }

            if (definition.HasDependencies &&
                WouldCreateCycle(definition.StatId, definition.Dependencies, out int cycleDep))
            {
                Debug.LogError(
                    $"StatManager.OverrideStatDefinition: stat {definition.StatId} " +
                    $"('{definition.DisplayName}') dependency on {cycleDep} would create a cycle. " +
                    "Override rejected.");
                return false;
            }

            // Commit. Swap dependency edges (drop the old set, add the new set).
            if (existing.HasDependencies)
            {
                for (int i = 0; i < existing.Dependencies.Length; i++)
                    RemoveDependencyEdge(existing.Dependencies[i], definition.StatId);
            }
            if (definition.HasDependencies)
            {
                for (int i = 0; i < definition.Dependencies.Length; i++)
                    AddDependencyEdge(definition.Dependencies[i], definition.StatId);
            }

            // Swap the formula cache.
            if (definition.HasFormula)
                _formulas[definition.StatId] = definition.Formula;
            else
                _formulas.Remove(definition.StatId);

            // Replace the definition (constraints, default base, display name).
            _database.OverrideDefinition(definition);

            // Recalculate on all live entities so the new constraints/formula apply now.
            RecalculateStatOnAllEntities(definition.StatId);

            return true;
        }

        // ------------------------------------------------------------------
        // Custom evaluators (bespoke C# formulas)
        // ------------------------------------------------------------------

        /// <summary>
        /// Delegate type for custom stat evaluators.
        /// Receives a StatQuery helper for reading other stats on the same entity.
        /// Returns the derived base value (before modifiers are applied).
        /// </summary>
        public delegate float CustomStatEvaluator(StatQuery query);

        /// <summary>
        /// Read-only accessor passed to custom evaluators. Provides safe access to
        /// the entity's stat values without exposing internal indices.
        /// </summary>
        public readonly struct StatQuery
        {
            internal readonly int EntityIndex;

            internal StatQuery(int entityIndex) => EntityIndex = entityIndex;

            /// <summary>Get the final evaluated value of a stat on this entity.</summary>
            public float GetValue(int statId) => GetValueInternal(EntityIndex, statId);

            /// <summary>Get the raw base value (before modifiers/formula) of a stat.</summary>
            public float GetBaseValue(int statId) => GetBaseValueInternal(EntityIndex, statId);

            /// <summary>Check if this entity has a specific stat.</summary>
            public bool HasStat(int statId)
            {
                ref var slot = ref _entities[EntityIndex];
                return slot.StatIdToSlot.ContainsKey(statId);
            }
        }

        /// <summary>
        /// Register a custom C# evaluator for a derived stat. Takes priority over
        /// any postfix formula on the same stat ID.
        /// 
        /// Use for complex formulas that are difficult to express in postfix notation:
        /// weighted averages, conditional logic, lookups from external systems, etc.
        /// 
        /// The stat must still have a StatDefinition registered (for constraints,
        /// display name, etc.) and should be a StatDefinition (with derivation) with its
        /// dependencies declared so the dependency graph cascades correctly.
        /// The postfix formula array can be left empty if the custom evaluator
        /// handles everything.
        /// 
        /// Example:
        ///   StatManager.RegisterCustomEvaluator(StatIds.Strength, query => {
        ///       float blade  = query.GetValue(StatIds.BladeSkill);
        ///       float heavy  = query.GetValue(StatIds.HeavyArmorSkill);
        ///       float athlet = query.GetValue(StatIds.AthleticsSkill);
        ///       float max = Mathf.Max(blade, Mathf.Max(heavy, athlet));
        ///       float avg = (blade + heavy + athlet) / 3f;
        ///       return Mathf.Lerp(avg, max, 0.3f);
        ///   });
        /// </summary>
        public static void RegisterCustomEvaluator(int statId, CustomStatEvaluator evaluator)
        {
            AssertInitialized();
            _customEvaluators[statId] = (entityIndex) => evaluator(new StatQuery(entityIndex));
        }

        /// <summary>Remove a custom evaluator. Falls back to postfix formula or plain base.</summary>
        public static bool RemoveCustomEvaluator(int statId)
        {
            AssertInitialized();
            return _customEvaluators.Remove(statId);
        }

        /// <summary>Check if a stat has a custom evaluator registered.</summary>
        public static bool HasCustomEvaluator(int statId)
        {
            return _customEvaluators != null && _customEvaluators.ContainsKey(statId);
        }

        // ------------------------------------------------------------------
        // Entity registration
        // ------------------------------------------------------------------

        /// <summary>
        /// Register a new stat entity. Returns a handle the caller stores.
        /// statIds defines which stats this entity has.
        /// </summary>
        public static EntityStatHandle RegisterEntity(ReadOnlySpan<int> statIds)
        {
            AssertInitialized();

            int index;
            if (_freeList.Count > 0)
            {
                index = _freeList.Pop();
            }
            else
            {
                index = _entityCount++;
                if (index >= _entityCapacity)
                {
                    GrowEntityArray();
                }
            }

            ref var slot = ref _entities[index];
            slot.Generation++;
            slot.Alive     = true;
            slot.StatCount = statIds.Length;

            // Allocate (or reuse) per-stat arrays.
            EnsureArraySize(ref slot.StatIdsBySlot, statIds.Length);
            EnsureArraySize(ref slot.BaseValues,    statIds.Length);
            EnsureArraySize(ref slot.CachedValues,  statIds.Length);
            EnsureArraySize(ref slot.DirtyFlags,    statIds.Length);
            EnsureModifierArraySize(ref slot.Modifiers, statIds.Length);

            slot.StatIdToSlot ??= new Dictionary<int, int>(statIds.Length);
            slot.StatIdToSlot.Clear();

            for (int i = 0; i < statIds.Length; i++)
            {
                int statId = statIds[i];
                slot.StatIdsBySlot[i] = statId;
                slot.StatIdToSlot[statId] = i;
                slot.BaseValues[i]   = _database.GetDefaultBaseValue(statId);
                bool derived         = IsDerivedStat(statId);
                slot.DirtyFlags[i]   = derived;
                slot.CachedValues[i] = InitialCachedValue(statId, slot.BaseValues[i], derived);

                // Clear any leftover modifiers from a recycled slot.
                slot.Modifiers[i]?.Clear();
            }

            // Eagerly evaluate any derived stats so values are correct immediately.
            for (int i = 0; i < statIds.Length; i++)
            {
                if (slot.DirtyFlags[i])
                    Recalculate(index, ref slot, i, statIds[i]);
            }

            return new EntityStatHandle(index, slot.Generation);
        }

        /// <summary>
        /// Register a new entity using StatDefinition assets.
        /// Convenience overload for when you have the definition references.
        /// </summary>
        public static EntityStatHandle RegisterEntity(StatDefinition[] definitions)
        {
            Span<int> ids = definitions.Length <= 32
                ? stackalloc int[definitions.Length]
                : new int[definitions.Length];

            for (int i = 0; i < definitions.Length; i++)
                ids[i] = definitions[i].StatId;

            return RegisterEntity(ids);
        }

        /// <summary>
        /// Register multiple entities with the same stat set in one batch.
        /// Pre-grows arrays once, allocates slots in a tight loop, then evaluates
        /// derived stats. Much faster than calling RegisterEntity N times when
        /// loading a chunk with hundreds of NPCs.
        /// 
        /// Caller provides a Span to receive the handles. Must be at least
        /// count elements. Returns the number actually registered (always count
        /// on success).
        /// </summary>
        public static int RegisterEntities(int count, ReadOnlySpan<int> statIds, Span<EntityStatHandle> outHandles)
        {
            AssertInitialized();
            if (count <= 0) return 0;

            // Pre-grow: ensure we have room for all new entities in one resize.
            int slotsNeeded = count - _freeList.Count;
            if (slotsNeeded > 0)
            {
                int required = _entityCount + slotsNeeded;
                while (_entityCapacity < required)
                    GrowEntityArray();
            }

            // Allocate all slot indices.
            Span<int> indices = count <= 128
                ? stackalloc int[count]
                : new int[count];

            for (int i = 0; i < count; i++)
            {
                if (_freeList.Count > 0)
                    indices[i] = _freeList.Pop();
                else
                    indices[i] = _entityCount++;
            }

            // Initialize each slot.
            for (int e = 0; e < count; e++)
            {
                int index = indices[e];
                ref var slot = ref _entities[index];
                slot.Generation++;
                slot.Alive     = true;
                slot.StatCount = statIds.Length;

                EnsureArraySize(ref slot.StatIdsBySlot, statIds.Length);
                EnsureArraySize(ref slot.BaseValues,    statIds.Length);
                EnsureArraySize(ref slot.CachedValues,  statIds.Length);
                EnsureArraySize(ref slot.DirtyFlags,    statIds.Length);
                EnsureModifierArraySize(ref slot.Modifiers, statIds.Length);

                slot.StatIdToSlot ??= new Dictionary<int, int>(statIds.Length);
                slot.StatIdToSlot.Clear();

                for (int i = 0; i < statIds.Length; i++)
                {
                    int statId = statIds[i];
                    slot.StatIdsBySlot[i] = statId;
                    slot.StatIdToSlot[statId] = i;
                    slot.BaseValues[i]   = _database.GetDefaultBaseValue(statId);
                    bool derived         = IsDerivedStat(statId);
                    slot.DirtyFlags[i]   = derived;
                    slot.CachedValues[i] = InitialCachedValue(statId, slot.BaseValues[i], derived);
                    slot.Modifiers[i]?.Clear();
                }

                // Eagerly evaluate derived stats.
                for (int i = 0; i < statIds.Length; i++)
                {
                    if (slot.DirtyFlags[i])
                        Recalculate(index, ref slot, i, statIds[i]);
                }

                outHandles[e] = new EntityStatHandle(index, slot.Generation);
            }

            return count;
        }

        /// <summary>
        /// Unregister an entity. Removes all modifiers and timed effects.
        /// The handle becomes invalid; the slot is recycled.
        /// </summary>
        public static void UnregisterEntity(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return;

            ref var slot = ref _entities[handle.Index];
            slot.Alive = false;

            // Clear modifier lists (don't deallocate — recycle on next registration).
            for (int i = 0; i < slot.StatCount; i++)
                slot.Modifiers[i]?.Clear();

            slot.StatIdToSlot?.Clear();

            // Remove any timed modifiers for this entity.
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                if (_timedModifiers[i].EntityIndex == handle.Index &&
                    _timedModifiers[i].EntityGeneration == handle.Generation)
                {
                    _timedModifiers.RemoveAt(i);
                }
            }

            _freeList.Push(handle.Index);
        }

        /// <summary>
        /// Unregister multiple entities in one batch. Scans the timed modifier
        /// list once for all entities rather than once per entity.
        /// Use when unloading a chunk with many entities.
        /// </summary>
        public static void UnregisterEntities(ReadOnlySpan<EntityStatHandle> handles)
        {
            AssertInitialized();
            if (handles.Length == 0) return;

            // Build a set of (index, generation) pairs to remove.
            // Using a HashSet<long> packing index + generation avoids allocating a custom comparer.
            var removeSet = new HashSet<long>(handles.Length);
            for (int h = 0; h < handles.Length; h++)
            {
                var handle = handles[h];
                if (!ValidateHandle(handle)) continue;

                ref var slot = ref _entities[handle.Index];
                slot.Alive = false;

                for (int i = 0; i < slot.StatCount; i++)
                    slot.Modifiers[i]?.Clear();

                slot.StatIdToSlot?.Clear();
                _freeList.Push(handle.Index);

                // Pack index and generation into a single long for set lookup.
                removeSet.Add(((long)handle.Index << 32) | (uint)handle.Generation);
            }

            // Single pass over timed modifiers to remove all matching entries.
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                var entry = _timedModifiers[i];
                long key = ((long)entry.EntityIndex << 32) | (uint)entry.EntityGeneration;
                if (removeSet.Contains(key))
                    _timedModifiers.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------------
        // Stat queries
        // ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetValue(EntityStatHandle handle, int statId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var slot = ref _entities[handle.Index];
            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return 0f;
            return slot.CachedValues[si];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetBaseValue(EntityStatHandle handle, int statId)
        {
            if (!ValidateHandle(handle)) return 0f;
            ref var slot = ref _entities[handle.Index];
            return slot.StatIdToSlot.TryGetValue(statId, out int si) ? slot.BaseValues[si] : 0f;
        }

        public static bool HasStat(EntityStatHandle handle, int statId)
        {
            if (!ValidateHandle(handle)) return false;
            return _entities[handle.Index].StatIdToSlot.ContainsKey(statId);
        }

        /// <summary>
        /// Get final values for multiple stats at once. Avoids repeated handle validation.
        /// Caller provides a Span to fill. Returns how many were written.
        /// </summary>
        public static int GetValues(EntityStatHandle handle, ReadOnlySpan<int> statIds, Span<float> outValues)
        {
            if (!ValidateHandle(handle)) return 0;
            ref var slot = ref _entities[handle.Index];

            int written = 0;
            for (int i = 0; i < statIds.Length && i < outValues.Length; i++)
            {
                if (slot.StatIdToSlot.TryGetValue(statIds[i], out int si))
                    outValues[written++] = slot.CachedValues[si];
            }
            return written;
        }

        // ------------------------------------------------------------------
        // Stat mutation
        // ------------------------------------------------------------------

        public static void SetBaseValue(EntityStatHandle handle, int statId, float value)
        {
            if (!ValidateHandle(handle)) return;
            ref var slot = ref _entities[handle.Index];

            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return;

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (slot.BaseValues[si] == value) return;

            slot.BaseValues[si] = value;
            RecalculateImmediate(handle.Index, ref slot, si);
        }

        public static void AddModifier(EntityStatHandle handle, int statId, StatModifier modifier)
        {
            if (!ValidateHandle(handle)) return;
            ref var slot = ref _entities[handle.Index];

            if (!slot.StatIdToSlot.TryGetValue(statId, out int si))
            {
                Debug.LogWarning($"StatManager.AddModifier: entity {handle} doesn't have statId {statId}.");
                return;
            }

            var list = slot.Modifiers[si] ??= new List<StatModifier>(4);
            InsertSorted(list, modifier);
            RecalculateImmediate(handle.Index, ref slot, si);
        }

        public static bool RemoveModifier(EntityStatHandle handle, int statId, StatModifier modifier)
        {
            if (!ValidateHandle(handle)) return false;
            ref var slot = ref _entities[handle.Index];

            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return false;

            var list = slot.Modifiers[si];
            if (list == null) return false;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Equals(modifier))
                {
                    list.RemoveAt(i);
                    // Keep timed-modifier bookkeeping in sync: if this modifier had a
                    // countdown, remove it so no ghost timer lingers (or leaks into saves).
                    RemoveOneTimedEntry(handle.Index, handle.Generation, statId, modifier);
                    RecalculateImmediate(handle.Index, ref slot, si);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Remove ALL modifiers from a source across ALL stats on an entity.
        /// O(totalModifiers) for this entity. Returns count removed.
        /// </summary>
        public static int RemoveAllFromSource(EntityStatHandle handle, long sourceId)
        {
            if (!ValidateHandle(handle)) return 0;
            ref var slot = ref _entities[handle.Index];

            int totalRemoved = 0;
            for (int si = 0; si < slot.StatCount; si++)
            {
                var list = slot.Modifiers[si];
                if (list == null || list.Count == 0) continue;

                int removed = 0;
                for (int m = list.Count - 1; m >= 0; m--)
                {
                    if (list[m].SourceId == sourceId)
                    {
                        list.RemoveAt(m);
                        removed++;
                    }
                }

                if (removed > 0)
                {
                    RecalculateImmediate(handle.Index, ref slot, si);
                    totalRemoved += removed;
                }
            }

            // Also strip any timed-modifier bookkeeping from this source so timers
            // can't outlive the modifiers they track (documented: removes timed + permanent).
            RemoveTimedEntriesFromSource(handle.Index, handle.Generation, sourceId);

            return totalRemoved;
        }

        // ------------------------------------------------------------------
        // Timed modifiers (batch-ticked)
        // ------------------------------------------------------------------

        /// <summary>
        /// Apply a modifier that expires after a duration. The modifier is added
        /// immediately and the manager tracks the countdown internally.
        /// </summary>
        public static void AddTimedModifier(EntityStatHandle handle, int statId,
            StatModifier modifier, float duration)
        {
            // Validate up front so the direct _entities access below is safe even when
            // the handle is stale/out-of-range (AddModifier alone would only warn).
            if (!ValidateHandle(handle)) return;

            AddModifier(handle, statId, modifier);

            ref var slot = ref _entities[handle.Index];
            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return;

            _timedModifiers.Add(new TimedEntry
            {
                EntityIndex      = handle.Index,
                EntityGeneration = handle.Generation,
                StatSlot         = si,
                StatId           = statId,
                Modifier         = modifier,
                RemainingTime    = duration,
                TotalDuration    = duration,
            });
        }

        /// <summary>
        /// Cancel all timed modifiers from a source on a specific entity.
        /// Removes both the countdown entry and its backing modifier in one pass.
        /// </summary>
        public static int CancelTimedFromSource(EntityStatHandle handle, long sourceId)
        {
            if (!ValidateHandle(handle)) return 0;
            ref var slot = ref _entities[handle.Index];

            int removed = 0;
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                var entry = _timedModifiers[i];
                if (entry.EntityIndex != handle.Index ||
                    entry.EntityGeneration != handle.Generation ||
                    entry.Modifier.SourceId != sourceId)
                    continue;

                // Remove the backing modifier from the stat, then the timer.
                // Self-contained (does not call RemoveModifier) so we don't mutate
                // _timedModifiers re-entrantly while iterating it here.
                if (slot.StatIdToSlot.TryGetValue(entry.StatId, out int si))
                {
                    var list = slot.Modifiers[si];
                    if (list != null)
                    {
                        for (int m = list.Count - 1; m >= 0; m--)
                        {
                            if (list[m].Equals(entry.Modifier))
                            {
                                list.RemoveAt(m);
                                RecalculateImmediate(handle.Index, ref slot, si);
                                break;
                            }
                        }
                    }
                }

                _timedModifiers.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>
        /// Batch-tick ALL timed modifiers across ALL entities.
        /// Call once per frame (or per simulation step) from your game loop.
        /// </summary>
        public static void TickTimedModifiers(float deltaTime)
        {
            AssertInitialized();

            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                var entry = _timedModifiers[i];
                entry.RemainingTime -= deltaTime;

                if (entry.RemainingTime <= 0f)
                {
                    // Entity may have been unregistered; validate generation.
                    if (entry.EntityIndex < _entityCount &&
                        _entities[entry.EntityIndex].Generation == entry.EntityGeneration &&
                        _entities[entry.EntityIndex].Alive)
                    {
                        ref var slot = ref _entities[entry.EntityIndex];
                        var list = slot.Modifiers[entry.StatSlot];
                        if (list != null)
                        {
                            for (int m = list.Count - 1; m >= 0; m--)
                            {
                                if (list[m].Equals(entry.Modifier))
                                {
                                    list.RemoveAt(m);
                                    RecalculateImmediate(entry.EntityIndex, ref slot, entry.StatSlot);
                                    break;
                                }
                            }
                        }
                    }

                    _timedModifiers.RemoveAt(i);
                }
                else
                {
                    _timedModifiers[i] = entry;
                }
            }
        }

        /// <summary>Read-only access to timed modifier count for diagnostics.</summary>
        public static int TimedModifierCount => _timedModifiers?.Count ?? 0;

        /// <summary>
        /// Remove at most one timed entry matching a specific modifier on a stat.
        /// Preserves the 1:1 correspondence between a timed modifier and its countdown
        /// when the underlying modifier is removed directly.
        /// </summary>
        private static void RemoveOneTimedEntry(int entityIndex, int generation, int statId, StatModifier modifier)
        {
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                var e = _timedModifiers[i];
                if (e.EntityIndex == entityIndex &&
                    e.EntityGeneration == generation &&
                    e.StatId == statId &&
                    e.Modifier.Equals(modifier))
                {
                    _timedModifiers.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Remove all timed entries from a given source on a single entity.
        /// </summary>
        private static void RemoveTimedEntriesFromSource(int entityIndex, int generation, long sourceId)
        {
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                var e = _timedModifiers[i];
                if (e.EntityIndex == entityIndex &&
                    e.EntityGeneration == generation &&
                    e.Modifier.SourceId == sourceId)
                {
                    _timedModifiers.RemoveAt(i);
                }
            }
        }

        // ------------------------------------------------------------------
        // Batch operations
        // ------------------------------------------------------------------

        /// <summary>
        /// Safety method: recalculate any stats that are still flagged dirty.
        /// In normal operation with eager recalculation this should be a no-op.
        /// Useful after RestoreEntityState or as a debug consistency check.
        /// </summary>
        public static void RecalculateAllDirty()
        {
            AssertInitialized();

            for (int e = 0; e < _entityCount; e++)
            {
                ref var slot = ref _entities[e];
                if (!slot.Alive) continue;

                for (int si = 0; si < slot.StatCount; si++)
                {
                    if (slot.DirtyFlags[si])
                        Recalculate(e, ref slot, si, slot.StatIdsBySlot[si]);
                }
            }
        }

        /// <summary>
        /// Remove all modifiers from a source across ALL entities.
        /// Useful for a global effect that's removed (e.g., world event ending).
        /// </summary>
        public static int RemoveSourceFromAllEntities(long sourceId)
        {
            AssertInitialized();
            int totalRemoved = 0;

            for (int e = 0; e < _entityCount; e++)
            {
                ref var slot = ref _entities[e];
                if (!slot.Alive) continue;

                for (int si = 0; si < slot.StatCount; si++)
                {
                    var list = slot.Modifiers[si];
                    if (list == null || list.Count == 0) continue;

                    int before = list.Count;
                    for (int m = list.Count - 1; m >= 0; m--)
                    {
                        if (list[m].SourceId == sourceId)
                            list.RemoveAt(m);
                    }

                    if (list.Count < before)
                    {
                        RecalculateImmediate(e, ref slot, si);
                        totalRemoved += before - list.Count;
                    }
                }
            }

            // Strip timed-modifier bookkeeping from this source across every entity.
            for (int i = _timedModifiers.Count - 1; i >= 0; i--)
            {
                if (_timedModifiers[i].Modifier.SourceId == sourceId)
                    _timedModifiers.RemoveAt(i);
            }

            return totalRemoved;
        }

        // ------------------------------------------------------------------
        // Serialization
        // ------------------------------------------------------------------

        [Serializable]
        public struct EntityStatSaveData
        {
            public int   StatId;
            public float BaseValue;
            public StatModifier[] Modifiers;
        }

        [Serializable]
        public struct TimedModifierSaveData
        {
            public int           StatId;
            public StatModifier  Modifier;
            public float         RemainingTime;
            public float         TotalDuration;
        }

        [Serializable]
        public struct FullEntitySaveData
        {
            public EntityStatSaveData[]     Stats;
            public TimedModifierSaveData[]  TimedModifiers;
        }

        /// <summary>Capture full state for a single entity.</summary>
        public static FullEntitySaveData CaptureEntityState(EntityStatHandle handle)
        {
            var result = new FullEntitySaveData();
            if (!ValidateHandle(handle)) return result;

            ref var slot = ref _entities[handle.Index];

            // Stats.
            result.Stats = new EntityStatSaveData[slot.StatCount];
            for (int si = 0; si < slot.StatCount; si++)
            {
                var list = slot.Modifiers[si];
                result.Stats[si] = new EntityStatSaveData
                {
                    StatId    = slot.StatIdsBySlot[si],
                    BaseValue = slot.BaseValues[si],
                    Modifiers = list != null && list.Count > 0 ? list.ToArray() : Array.Empty<StatModifier>(),
                };
            }

            // Timed modifiers for this entity.
            var timed = new List<TimedModifierSaveData>();
            for (int i = 0; i < _timedModifiers.Count; i++)
            {
                var entry = _timedModifiers[i];
                if (entry.EntityIndex == handle.Index && entry.EntityGeneration == handle.Generation)
                {
                    timed.Add(new TimedModifierSaveData
                    {
                        StatId        = entry.StatId,
                        Modifier      = entry.Modifier,
                        RemainingTime = entry.RemainingTime,
                        TotalDuration = entry.TotalDuration,
                    });
                }
            }
            result.TimedModifiers = timed.ToArray();

            return result;
        }

        /// <summary>
        /// Check if an entity has any stat modifications from its default state.
        /// If false, you can skip serializing stat data entirely — the entity
        /// will reconstruct identically from its template on next load.
        /// </summary>
        public static bool HasStatChanges(EntityStatHandle handle)
        {
            if (!ValidateHandle(handle)) return false;
            ref var slot = ref _entities[handle.Index];

            for (int si = 0; si < slot.StatCount; si++)
            {
                int statId = slot.StatIdsBySlot[si];

                // Base value differs from definition default.
                float defaultBase = _database.GetDefaultBaseValue(statId);
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (slot.BaseValues[si] != defaultBase)
                    return true;

                // Has any modifiers.
                var list = slot.Modifiers[si];
                if (list != null && list.Count > 0)
                    return true;
            }

            // Has any timed modifiers.
            for (int i = 0; i < _timedModifiers.Count; i++)
            {
                if (_timedModifiers[i].EntityIndex == handle.Index &&
                    _timedModifiers[i].EntityGeneration == handle.Generation)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Capture only stats that differ from their definition defaults.
        /// Produces a much smaller save for entities with few or no modifications.
        /// 
        /// An untouched NPC produces Stats.Length == 0 and TimedModifiers.Length == 0.
        /// RestoreEntityState handles sparse data correctly — stats not present
        /// in the save keep their default values.
        /// </summary>
        public static FullEntitySaveData CaptureEntityStateDelta(EntityStatHandle handle)
        {
            var result = new FullEntitySaveData();
            if (!ValidateHandle(handle)) return result;

            ref var slot = ref _entities[handle.Index];

            // Only include stats with non-default base values or active modifiers.
            var changedStats = new List<EntityStatSaveData>();
            for (int si = 0; si < slot.StatCount; si++)
            {
                int statId = slot.StatIdsBySlot[si];
                float defaultBase = _database.GetDefaultBaseValue(statId);

                var list = slot.Modifiers[si];
                bool hasModifiers = list != null && list.Count > 0;

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                bool baseChanged = slot.BaseValues[si] != defaultBase;

                if (baseChanged || hasModifiers)
                {
                    changedStats.Add(new EntityStatSaveData
                    {
                        StatId    = statId,
                        BaseValue = slot.BaseValues[si],
                        Modifiers = hasModifiers ? list.ToArray() : Array.Empty<StatModifier>(),
                    });
                }
            }
            result.Stats = changedStats.ToArray();

            // Timed modifiers (same as full capture — all are relevant).
            var timed = new List<TimedModifierSaveData>();
            for (int i = 0; i < _timedModifiers.Count; i++)
            {
                var entry = _timedModifiers[i];
                if (entry.EntityIndex == handle.Index && entry.EntityGeneration == handle.Generation)
                {
                    timed.Add(new TimedModifierSaveData
                    {
                        StatId        = entry.StatId,
                        Modifier      = entry.Modifier,
                        RemainingTime = entry.RemainingTime,
                        TotalDuration = entry.TotalDuration,
                    });
                }
            }
            result.TimedModifiers = timed.ToArray();

            return result;
        }

        /// <summary>
        /// Restore entity state from save data. The entity must already be registered
        /// (call RegisterEntity first with the same stat set). This overwrites base
        /// values, clears existing modifiers, and restores saved modifiers + timers.
        /// </summary>
        public static void RestoreEntityState(EntityStatHandle handle, FullEntitySaveData data)
        {
            if (!ValidateHandle(handle)) return;
            ref var slot = ref _entities[handle.Index];

            // Restore stat base values and modifiers.
            if (data.Stats != null)
            {
                for (int i = 0; i < data.Stats.Length; i++)
                {
                    var entry = data.Stats[i];
                    if (!slot.StatIdToSlot.TryGetValue(entry.StatId, out int si)) continue;

                    slot.BaseValues[si] = entry.BaseValue;

                    var list = slot.Modifiers[si];
                    list?.Clear();

                    if (entry.Modifiers != null && entry.Modifiers.Length > 0)
                    {
                        list ??= slot.Modifiers[si] = new List<StatModifier>(entry.Modifiers.Length);
                        for (int m = 0; m < entry.Modifiers.Length; m++)
                            InsertSorted(list, entry.Modifiers[m]);
                    }

                    RecalculateImmediate(handle.Index, ref slot, si);
                }
            }

            // Restore timed modifiers.
            if (data.TimedModifiers != null)
            {
                for (int i = 0; i < data.TimedModifiers.Length; i++)
                {
                    var t = data.TimedModifiers[i];
                    if (!slot.StatIdToSlot.TryGetValue(t.StatId, out int si)) continue;

                    _timedModifiers.Add(new TimedEntry
                    {
                        EntityIndex      = handle.Index,
                        EntityGeneration = handle.Generation,
                        StatSlot         = si,
                        StatId           = t.StatId,
                        Modifier         = t.Modifier,
                        RemainingTime    = t.RemainingTime,
                        TotalDuration    = t.TotalDuration,
                    });
                    // The modifier itself was already restored above in the modifiers array.
                }
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics
        // ------------------------------------------------------------------

        public static int ActiveEntityCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _entityCount; i++)
                    if (_entities[i].Alive) count++;
                return count;
            }
        }

        public static int GetModifierCount(EntityStatHandle handle, int statId)
        {
            if (!ValidateHandle(handle)) return 0;
            ref var slot = ref _entities[handle.Index];
            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return 0;
            return slot.Modifiers[si]?.Count ?? 0;
        }

        /// <summary>
        /// Get a full breakdown of how a stat's value is computed.
        /// Shows every stage of the pipeline and every active modifier.
        /// Allocates — intended for debug tools, not hot paths.
        /// </summary>
        public static StatBreakdown GetStatBreakdown(EntityStatHandle handle, int statId)
        {
            var result = new StatBreakdown { StatId = statId };

            if (!ValidateHandle(handle)) return result;
            ref var slot = ref _entities[handle.Index];
            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return result;

            // Stat identity.
            if (_database.TryGetDefinition(statId, out var def))
                result.StatName = def.DisplayName;
            result.IsDerived = (_formulas != null && _formulas.ContainsKey(statId)) ||
                               (_customEvaluators != null && _customEvaluators.ContainsKey(statId));

            // Compute the derived base.
            result.StoredBaseValue = slot.BaseValues[si];
            if (_customEvaluators != null && _customEvaluators.TryGetValue(statId, out var customEval))
                result.FormulaResult = customEval(handle.Index);
            else if (result.IsDerived && _formulas.TryGetValue(statId, out var formula))
                result.FormulaResult = StatFormulaEvaluator.Evaluate(formula, handle.Index);
            result.EffectiveBase = result.StoredBaseValue + result.FormulaResult;

            // Walk the modifier pipeline, mirroring Recalculate.
            float value = result.EffectiveBase;
            float flatTotal = 0f;
            float percentAddTotal = 0f;

            var list = slot.Modifiers[si];
            if (list != null && list.Count > 0)
            {
                result.Modifiers = list.ToArray();

                for (int i = 0; i < list.Count; i++)
                {
                    var mod = list[i];
                    switch (mod.Type)
                    {
                        case ModifierType.Flat:
                            flatTotal += mod.Value;
                            break;
                        case ModifierType.PercentAdd:
                            percentAddTotal += mod.Value;
                            break;
                        case ModifierType.PercentMult:
                            // Flush PercentAdd before first PercentMult.
                            break;
                    }
                }
            }
            else
            {
                result.Modifiers = System.Array.Empty<StatModifier>();
            }

            // Replay for staged values.
            result.FlatTotal = flatTotal;
            result.AfterFlat = result.EffectiveBase + flatTotal;

            result.PercentAddTotal = percentAddTotal;
            result.AfterPercentAdd = result.AfterFlat * (1f + percentAddTotal);

            // Replay PercentMult sequentially.
            float afterMult = result.AfterPercentAdd;
            if (list != null)
            {
                // Need to replay properly: flush PercentAdd at the right time.
                afterMult = result.EffectiveBase;
                float pctAddAcc = 0f;
                for (int i = 0; i < list.Count; i++)
                {
                    var mod = list[i];
                    switch (mod.Type)
                    {
                        case ModifierType.Flat:
                            afterMult += mod.Value;
                            break;
                        case ModifierType.PercentAdd:
                            pctAddAcc += mod.Value;
                            break;
                        case ModifierType.PercentMult:
                            if (pctAddAcc != 0f)
                            {
                                afterMult *= 1f + pctAddAcc;
                                pctAddAcc = 0f;
                            }
                            afterMult *= 1f + mod.Value;
                            break;
                    }
                }
                if (pctAddAcc != 0f)
                    afterMult *= 1f + pctAddAcc;
            }
            result.AfterPercentMult = afterMult;

            // Constraints.
            result.FinalValue = afterMult;
            if (_database.TryGetConstraints(statId, out var c))
                result.FinalValue = c.Apply(result.FinalValue);

            // Timed modifier info.
            var timedList = new System.Collections.Generic.List<TimedModifierInfo>();
            for (int i = 0; i < _timedModifiers.Count; i++)
            {
                var entry = _timedModifiers[i];
                if (entry.EntityIndex == handle.Index &&
                    entry.EntityGeneration == handle.Generation &&
                    entry.StatId == statId)
                {
                    timedList.Add(new TimedModifierInfo
                    {
                        Modifier      = entry.Modifier,
                        RemainingTime = entry.RemainingTime,
                        TotalDuration = entry.TotalDuration,
                    });
                }
            }
            result.TimedModifiers = timedList.Count > 0 ? timedList.ToArray() : System.Array.Empty<TimedModifierInfo>();

            return result;
        }

        /// <summary>
        /// Get a full snapshot of all stats and pools on an entity.
        /// Allocates — intended for debug tools, not hot paths.
        /// </summary>
        public static EntityStatSnapshot GetEntitySnapshot(EntityStatHandle handle)
        {
            var result = new EntityStatSnapshot { Handle = handle };

            if (!ValidateHandle(handle)) return result;
            ref var slot = ref _entities[handle.Index];

            // Stats.
            result.Stats = new StatBreakdown[slot.StatCount];
            for (int si = 0; si < slot.StatCount; si++)
                result.Stats[si] = GetStatBreakdown(handle, slot.StatIdsBySlot[si]);

            // Pools.
            int poolCount = ResourcePoolManager.PoolCountForEntity(handle);
            if (poolCount > 0)
            {
                var pools = new System.Collections.Generic.List<PoolSnapshot>(poolCount);
                // Iterate the stat IDs and check if each is a pool.
                for (int si = 0; si < slot.StatCount; si++)
                {
                    int statId = slot.StatIdsBySlot[si];
                    if (ResourcePoolManager.HasPool(handle, statId))
                    {
                        pools.Add(new PoolSnapshot
                        {
                            PoolId  = statId,
                            Current = ResourcePoolManager.GetCurrent(handle, statId),
                            Max     = ResourcePoolManager.GetMax(handle, statId),
                            Shield  = ResourcePoolManager.GetShield(handle, statId),
                            Ratio   = ResourcePoolManager.GetRatio(handle, statId),
                        });
                    }
                }
                result.Pools = pools.ToArray();
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Internal: evaluation
        // ------------------------------------------------------------------

        private static void Recalculate(int entityIndex, ref EntitySlot slot, int si, int statId)
        {
            // Runtime backstop against undeclared dependency cycles. Declared cycles are
            // rejected at registration (WouldCreateCycle); this aborts the cascade rather
            // than overflowing the stack if one still reaches the recursive path.
            if (_recalcDepth >= MaxRecalcDepth)
            {
                if (!_cycleWarned)
                {
                    _cycleWarned = true;
                    Debug.LogError(
                        $"StatManager: recalculation depth exceeded {MaxRecalcDepth} while updating " +
                        $"stat {statId}. Aborting cascade to avoid a stack overflow — this indicates a " +
                        "dependency cycle.");
                }
                return;
            }

            _recalcDepth++;
            try { RecalculateBody(entityIndex, ref slot, si, statId); }
            finally { _recalcDepth--; }
        }

        private static void RecalculateBody(int entityIndex, ref EntitySlot slot, int si, int statId)
        {
            float oldValue = slot.CachedValues[si];

            // Determine the base value. Priority: custom evaluator > postfix formula > stored base.
            float baseValue;
            if (_customEvaluators != null && _customEvaluators.TryGetValue(statId, out var customEval))
            {
                baseValue = customEval(entityIndex);
                baseValue += slot.BaseValues[si];
            }
            else if (_formulas != null && _formulas.TryGetValue(statId, out var formula))
            {
                baseValue = StatFormulaEvaluator.Evaluate(formula, entityIndex);
                baseValue += slot.BaseValues[si];
            }
            else
            {
                baseValue = slot.BaseValues[si];
            }

            // Apply modifier pipeline: Flat -> PercentAdd -> PercentMult.
            float final_ = baseValue;

            var list = slot.Modifiers[si];
            if (list != null && list.Count > 0)
            {
                float percentAddSum = 0f;

                for (int i = 0; i < list.Count; i++)
                {
                    var mod = list[i];
                    switch (mod.Type)
                    {
                        case ModifierType.Flat:
                            final_ += mod.Value;
                            break;

                        case ModifierType.PercentAdd:
                            percentAddSum += mod.Value;
                            break;

                        case ModifierType.PercentMult:
                            if (percentAddSum != 0f)
                            {
                                final_ *= 1f + percentAddSum;
                                percentAddSum = 0f;
                            }
                            final_ *= 1f + mod.Value;
                            break;
                    }
                }

                if (percentAddSum != 0f)
                    final_ *= 1f + percentAddSum;
            }

            // Apply constraints.
            if (_database.TryGetConstraints(statId, out var c))
                final_ = c.Apply(final_);

            slot.CachedValues[si] = final_;
            slot.DirtyFlags[si]   = false;

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (oldValue != final_)
            {
                // Eagerly recalculate any stats that depend on this one.
                if (_dependencyGraph != null && _dependencyGraph.TryGetValue(statId, out var dependents))
                {
                    for (int d = 0; d < dependents.Length; d++)
                    {
                        if (slot.StatIdToSlot.TryGetValue(dependents[d], out int depSi))
                        {
                            Recalculate(entityIndex, ref slot, depSi, dependents[d]);
                        }
                    }
                }

                if (OnStatChanged != null)
                {
                    var handle = new EntityStatHandle(entityIndex, slot.Generation);
                    OnStatChanged.Invoke(handle, statId, oldValue, final_);
                }
            }
        }

        /// <summary>
        /// Internal value accessor used by formula evaluation.
        /// Triggers Recalculate on the target stat if dirty (recursive descent).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetValueInternal(int entityIndex, int statId)
        {
            ref var slot = ref _entities[entityIndex];
            if (!slot.StatIdToSlot.TryGetValue(statId, out int si)) return 0f;
            if (slot.DirtyFlags[si])
                Recalculate(entityIndex, ref slot, si, statId);
            return slot.CachedValues[si];
        }

        /// <summary>
        /// Internal base value accessor used by formula evaluation (PushStatBase).
        /// Returns the raw base value without modifiers or formula evaluation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetBaseValueInternal(int entityIndex, int statId)
        {
            ref var slot = ref _entities[entityIndex];
            return slot.StatIdToSlot.TryGetValue(statId, out int si) ? slot.BaseValues[si] : 0f;
        }

        /// <summary>
        /// Eagerly recalculate a stat and cascade to dependents.
        /// Called whenever a stat's inputs change (base value, modifiers).
        /// No lazy deferral — values are always up to date.
        /// </summary>
        private static void RecalculateImmediate(int entityIndex, ref EntitySlot slot, int si)
        {
            int statId = slot.StatIdsBySlot[si];
            Recalculate(entityIndex, ref slot, si, statId);
            // Recalculate handles dependency cascade internally via OnStatChanged path.
        }

        // ------------------------------------------------------------------
        // Internal: utility
        // ------------------------------------------------------------------

        /// <summary>
        /// Check if a stat has any derivation logic (custom evaluator or postfix formula).
        /// Used to mark stats dirty at registration so they evaluate on first access.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsDerivedStat(int statId)
        {
            return (_formulas != null && _formulas.ContainsKey(statId)) ||
                   (_customEvaluators != null && _customEvaluators.ContainsKey(statId));
        }

        /// <summary>
        /// Initial cached value for a freshly registered stat. Derived stats get a
        /// placeholder (Recalculate overwrites it immediately after); non-derived stats
        /// don't go through Recalculate at registration, so their constraints (clamp/round)
        /// are applied here to keep GetValue consistent from t=0 rather than only after
        /// the first mutation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float InitialCachedValue(int statId, float baseValue, bool derived)
        {
            if (derived) return baseValue;
            return _database.TryGetConstraints(statId, out var c) ? c.Apply(baseValue) : baseValue;
        }

        /// <summary>Add a dependents-edge (dep -> dependent), skipping duplicates.</summary>
        private static void AddDependencyEdge(int depId, int dependentId)
        {
            if (_dependencyGraph.TryGetValue(depId, out var existing))
            {
                for (int j = 0; j < existing.Length; j++)
                    if (existing[j] == dependentId) return; // already present

                var grown = new int[existing.Length + 1];
                existing.CopyTo(grown, 0);
                grown[existing.Length] = dependentId;
                _dependencyGraph[depId] = grown;
            }
            else
            {
                _dependencyGraph[depId] = new[] { dependentId };
            }
        }

        /// <summary>Remove a dependents-edge (dep -> dependent); drops the key when it empties.</summary>
        private static void RemoveDependencyEdge(int depId, int dependentId)
        {
            if (!_dependencyGraph.TryGetValue(depId, out var existing)) return;

            int idx = -1;
            for (int j = 0; j < existing.Length; j++)
                if (existing[j] == dependentId) { idx = j; break; }
            if (idx < 0) return;

            if (existing.Length == 1)
            {
                _dependencyGraph.Remove(depId);
                return;
            }

            var shrunk = new int[existing.Length - 1];
            int w = 0;
            for (int j = 0; j < existing.Length; j++)
                if (j != idx) shrunk[w++] = existing[j];
            _dependencyGraph[depId] = shrunk;
        }

        /// <summary>Recalculate a stat on every live entity that has it (cascades to dependents).</summary>
        private static void RecalculateStatOnAllEntities(int statId)
        {
            for (int e = 0; e < _entityCount; e++)
            {
                ref var slot = ref _entities[e];
                if (!slot.Alive) continue;
                if (slot.StatIdToSlot.TryGetValue(statId, out int si))
                    Recalculate(e, ref slot, si, statId);
            }
        }

        /// <summary>
        /// Would adding the given dependencies for <paramref name="newStatId"/> introduce
        /// a cycle in the dependency graph? Registering "newStat depends on dep" adds a
        /// dep -> newStat edge; that closes a cycle iff dep already (transitively) depends
        /// on newStat — i.e., dep is reachable from newStat through the existing graph.
        /// Runs at registration only (cold path), so the allocation is acceptable.
        /// </summary>
        private static bool WouldCreateCycle(int newStatId, int[] dependencies, out int offendingDep)
        {
            offendingDep = 0;
            if (dependencies == null) return false;

            for (int i = 0; i < dependencies.Length; i++)
            {
                int dep = dependencies[i];

                // A stat depending on itself is a degenerate cycle.
                if (dep == newStatId)
                {
                    offendingDep = dep;
                    return true;
                }

                if (DependentsReach(newStatId, dep))
                {
                    offendingDep = dep;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Is <paramref name="target"/> reachable from <paramref name="from"/> by walking
        /// dependents edges (_dependencyGraph: statId -> stats that depend on it)?
        /// The visited set also guards against traversing any pre-existing cycle.
        /// </summary>
        private static bool DependentsReach(int from, int target)
        {
            if (_dependencyGraph == null || _dependencyGraph.Count == 0) return false;

            var visited = new HashSet<int>();
            var stack   = new Stack<int>();
            stack.Push(from);

            while (stack.Count > 0)
            {
                int node = stack.Pop();
                if (!visited.Add(node)) continue;

                if (_dependencyGraph.TryGetValue(node, out var dependents))
                {
                    for (int i = 0; i < dependents.Length; i++)
                    {
                        if (dependents[i] == target) return true;
                        stack.Push(dependents[i]);
                    }
                }
            }
            return false;
        }

        private static void InsertSorted(List<StatModifier> list, StatModifier modifier)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                var existing = list[mid];
                int cmp = ((int)existing.Type).CompareTo((int)modifier.Type);
                if (cmp == 0) cmp = existing.Order.CompareTo(modifier.Order);
                if (cmp <= 0) lo = mid + 1; else hi = mid;
            }
            list.Insert(lo, modifier);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ValidateHandle(EntityStatHandle handle)
        {
            if (!_initialized)
            {
                Debug.LogError("StatManager not initialized.");
                return false;
            }

            if (handle.Index < 0 || handle.Index >= _entityCount)
                return false;

            ref var slot = ref _entities[handle.Index];
            return slot.Alive && slot.Generation == handle.Generation;
        }

        private static void GrowEntityArray()
        {
            int newCapacity = _entityCapacity * 2;
            var newArray = new EntitySlot[newCapacity];
            Array.Copy(_entities, newArray, _entityCapacity);
            _entities = newArray;
            _entityCapacity = newCapacity;
        }

        private static void EnsureArraySize<T>(ref T[] array, int minSize)
        {
            if (array == null || array.Length < minSize)
                array = new T[minSize];
        }

        private static void EnsureModifierArraySize(ref List<StatModifier>[] array, int minSize)
        {
            if (array == null || array.Length < minSize)
                array = new List<StatModifier>[minSize];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AssertInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("StatManager.Boot() has not been called.");
        }
    }
}