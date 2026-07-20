using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace RPG.Stats.Tests
{
    /// <summary>
    /// EditMode test suite for the stat system. Each test boots a fresh StatManager +
    /// ResourcePoolManager in SetUp (seeded with a programmatic test database) and tears
    /// them down afterward, so every test is fully isolated. Run from the Unity Test
    /// Runner (Window > General > Test Runner > EditMode) or headless via the CLI
    /// (-runTests -testPlatform EditMode).
    ///
    /// Covers registration/recycling, the modifier pipeline, timed modifiers, clamping,
    /// derived-stat cascade, resource pools + shields (incl. events), save/restore (+ delta),
    /// dynamic and override registration, formula/cycle validation, and debug tooling.
    /// </summary>
    public class StatSystemTests
    {
        private StatDefinitionDatabase _database;

        // Records stat-change events for verification (see Test_StatChangedEvents).
        private readonly List<(EntityStatHandle handle, int statId, float oldVal, float newVal)> _changeLog = new();

        [SetUp]
        public void SetUp()
        {
            _database = CreateTestDatabase();

            StatManager.Boot(entityCapacity: 64);
            StatManager.OnStatChanged += OnStatChanged;
            StatManager.RegisterStatDefinitions(_database);

            ResourcePoolManager.Boot(entityCapacity: 64);
            ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(StatIds.MaxHealth, StatIds.MaxHealth, startFull: true));
            ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(StatIds.MaxStamina, StatIds.MaxStamina, startFull: true));

            _changeLog.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            StatManager.OnStatChanged -= OnStatChanged;
            ResourcePoolManager.Shutdown();
            StatManager.Shutdown();

            // Backstop: a test that installs a log sink and throws before disposing it
            // would otherwise leak the capture into every test that follows.
            StatLog.Reset();

            if (_database != null)
            {
                UnityEngine.Object.DestroyImmediate(_database);
                _database = null;
            }
        }

        // ==================================================================
        // Tests
        // ==================================================================

        [Test, Category("Registration")]
        [Description("Registers an entity and verifies the handle is valid, exposes its stats, and increments ActiveEntityCount; unregistering decrements it.")]
        public void Test_EntityRegistration()
        {
            Log("--- Entity Registration ---");

            var handle = RegisterTestEntity();
            Check("Handle is valid", handle.IsValid);
            Check("Has Strength stat", StatManager.HasStat(handle, StatIds.Strength));
            Check("Has MaxHealth stat", StatManager.HasStat(handle, StatIds.MaxHealth));
            Check("Does not have unregistered stat", !StatManager.HasStat(handle, 9999));
            Check("Active entity count is 1", StatManager.ActiveEntityCount == 1);

            StatManager.UnregisterEntity(handle);
            Check("Active entity count is 0 after unregister", StatManager.ActiveEntityCount == 0);
        }

        [Test, Category("Registration")]
        [Description("New entities seed each stat's base and cached value from its StatDefinition default.")]
        public void Test_BaseValueDefaults()
        {
            Log("--- Base Value Defaults ---");

            var handle = RegisterTestEntity();

            // Strength definition has defaultBaseValue = 10.
            Check("Strength base = 10", Approx(StatManager.GetBaseValue(handle, StatIds.Strength), 10f));
            Check("Strength value = 10 (no modifiers)", Approx(StatManager.GetValue(handle, StatIds.Strength), 10f));

            // MaxHealth definition has defaultBaseValue = 100.
            Check("MaxHealth base = 100", Approx(StatManager.GetBaseValue(handle, StatIds.MaxHealth), 100f));

            // MoveSpeed definition has defaultBaseValue = 5.
            Check("MoveSpeed base = 5", Approx(StatManager.GetBaseValue(handle, StatIds.MoveSpeed), 5f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("Flat modifiers sum onto the base value and stack across sources without changing the stored base.")]
        public void Test_FlatModifiers()
        {
            Log("--- Flat Modifiers ---");

            var handle = RegisterTestEntity();
            const long swordId = 5001;

            // Strength base = 10. Add +5 flat.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(5f, ModifierType.Flat, swordId));

            Check("Strength = 15 after +5 flat", Approx(handle.GetValue(StatIds.Strength), 15f));
            Check("Base value unchanged at 10", Approx(handle.GetBaseValue(StatIds.Strength), 10f));

            // Add another +3 flat from different source.
            const long ringId = 5002;
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(3f, ModifierType.Flat, ringId));

            Check("Strength = 18 after +5 and +3 flat", Approx(handle.GetValue(StatIds.Strength), 18f));

            // Negative flat modifier.
            const long curseId = 9001;
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(-4f, ModifierType.Flat, curseId));

            Check("Strength = 14 after adding -4 flat", Approx(handle.GetValue(StatIds.Strength), 14f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("PercentAdd modifiers sum into a single additive multiplier applied once.")]
        public void Test_PercentAddModifiers()
        {
            Log("--- PercentAdd Modifiers ---");

            var handle = RegisterTestEntity();
            // Strength base = 10.

            // +50% additive.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, 6001));

            Check("Strength = 15 after +50% add", Approx(handle.GetValue(StatIds.Strength), 15f));

            // Another +25% additive. Should sum: 10 * (1 + 0.5 + 0.25) = 10 * 1.75 = 17.5.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.25f, ModifierType.PercentAdd, 6002));

            Check("Strength = 17.5 after +50% and +25% add (summed)", Approx(handle.GetValue(StatIds.Strength), 17.5f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("PercentMult modifiers compound multiplicatively, each applied in sequence.")]
        public void Test_PercentMultModifiers()
        {
            Log("--- PercentMult Modifiers ---");

            var handle = RegisterTestEntity();
            // Strength base = 10.

            // +10% multiplicative.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.1f, ModifierType.PercentMult, 7001));

            Check("Strength = 11 after +10% mult", Approx(handle.GetValue(StatIds.Strength), 11f));

            // Another +10% multiplicative. Should compound: 10 * 1.1 * 1.1 = 12.1.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.1f, ModifierType.PercentMult, 7002));

            Check("Strength = 12.1 after two +10% mult (compounded)", Approx(handle.GetValue(StatIds.Strength), 12.1f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("Full pipeline order: (base + flat) * (1 + sumPercentAdd) * (1 + percentMult).")]
        public void Test_FullEvaluationPipeline()
        {
            Log("--- Full Evaluation Pipeline (Flat -> PercentAdd -> PercentMult) ---");

            var handle = RegisterTestEntity();
            // Strength base = 10.

            // +5 flat -> base becomes 15 before percentages.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(5f, ModifierType.Flat, 1001));

            // +50% additive -> 15 * 1.5 = 22.5.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, 1002));

            // +10% multiplicative -> 22.5 * 1.1 = 24.75.
            StatManager.AddModifier(handle, StatIds.Strength,
                new StatModifier(0.1f, ModifierType.PercentMult, 1003));

            float expected = (10f + 5f) * (1f + 0.5f) * (1f + 0.1f);
            Check($"Full pipeline: expected {expected}, got {handle.GetValue(StatIds.Strength)}",
                Approx(handle.GetValue(StatIds.Strength), expected));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("Multiple modifiers from one source on one stat stack correctly and report the right count.")]
        public void Test_ModifierStacking()
        {
            Log("--- Modifier Stacking (same source, multiple modifiers) ---");

            var handle = RegisterTestEntity();
            const long enchantedArmorId = 3001;

            // Armor gives +20 flat AND +10% additive to MaxHealth (base 100).
            StatManager.AddModifier(handle, StatIds.MaxHealth,
                new StatModifier(20f, ModifierType.Flat, enchantedArmorId));
            StatManager.AddModifier(handle, StatIds.MaxHealth,
                new StatModifier(0.1f, ModifierType.PercentAdd, enchantedArmorId));

            // (100 + 20) * 1.1 = 132.
            float expected = (100f + 20f) * 1.1f;
            Check($"MaxHealth = {expected} with armor flat + percent",
                Approx(handle.GetValue(StatIds.MaxHealth), expected));

            Check("Modifier count = 2", StatManager.GetModifierCount(handle, StatIds.MaxHealth) == 2);

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Modifiers")]
        [Description("RemoveAllFromSource strips every modifier from a source across all stats in a single call.")]
        public void Test_SourceRemoval()
        {
            Log("--- Source-Based Removal ---");

            var handle = RegisterTestEntity();
            const long swordId = 5001;
            const long potionId = 8001;

            // Sword modifies Strength (+5 flat) and PhysDamage (+10 flat).
            handle.AddFlat(StatIds.Strength, 5f, swordId);
            handle.AddFlat(StatIds.PhysDamage, 10f, swordId);

            // Potion modifies Strength (+50% add).
            handle.AddPercentAdd(StatIds.Strength, 0.5f, potionId);

            // Verify pre-removal.
            float strBefore = handle.GetValue(StatIds.Strength);
            // (10 + 5) * 1.5 = 22.5
            Check("Strength = 22.5 before removal", Approx(strBefore, 22.5f));

            // Remove all sword modifiers.
            int removed = handle.RemoveAllFromSource(swordId);
            Check("Removed 2 modifiers from sword", removed == 2);

            // Strength: 10 * 1.5 = 15 (flat from sword gone, potion remains).
            Check("Strength = 15 after sword removal", Approx(handle.GetValue(StatIds.Strength), 15f));

            // PhysDamage back to base (0 default).
            Check("PhysDamage back to base", Approx(handle.GetValue(StatIds.PhysDamage), 0f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("TimedModifiers")]
        [Description("Timed modifiers apply immediately and are removed automatically once their duration elapses via TickTimedModifiers.")]
        public void Test_TimedModifierExpiration()
        {
            Log("--- Timed Modifier Expiration ---");

            var handle = RegisterTestEntity();
            const long buffId = 8001;

            // +50% Strength for 2.0 seconds.
            StatManager.AddTimedModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, buffId), 2.0f);

            Check("Strength = 15 while buff active", Approx(handle.GetValue(StatIds.Strength), 15f));
            Check("Timed modifier count = 1", StatManager.TimedModifierCount == 1);

            // Tick 1.0 seconds. Buff still active.
            StatManager.TickTimedModifiers(1.0f);
            Check("Strength = 15 after 1.0s tick", Approx(handle.GetValue(StatIds.Strength), 15f));
            Check("Timed count still 1", StatManager.TimedModifierCount == 1);

            // Tick another 1.5 seconds. Buff should expire (total 2.5s > 2.0s).
            StatManager.TickTimedModifiers(1.5f);
            Check("Strength = 10 after buff expired", Approx(handle.GetValue(StatIds.Strength), 10f));
            Check("Timed count = 0", StatManager.TimedModifierCount == 0);
            Check("Modifier count on stat = 0", StatManager.GetModifierCount(handle, StatIds.Strength) == 0);

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("TimedModifiers")]
        [Description("CancelTimedFromSource removes an active timed modifier and its timer before it expires.")]
        public void Test_TimedModifierCancellation()
        {
            Log("--- Timed Modifier Early Cancellation ---");

            var handle = RegisterTestEntity();
            const long curseId = 9001;

            // -30% MoveSpeed for 10 seconds.
            StatManager.AddTimedModifier(handle, StatIds.MoveSpeed,
                new StatModifier(-0.3f, ModifierType.PercentAdd, curseId), 10f);

            float cursedSpeed = handle.GetValue(StatIds.MoveSpeed);
            Check($"MoveSpeed cursed = {cursedSpeed}", Approx(cursedSpeed, 5f * 0.7f));

            // Dispel after 1 second.
            StatManager.TickTimedModifiers(1.0f);
            int cancelled = handle.CancelTimedFromSource(curseId);
            Check("Cancelled 1 timed modifier", cancelled == 1);
            Check("MoveSpeed restored to base", Approx(handle.GetValue(StatIds.MoveSpeed), 5f));
            Check("Timed count = 0", StatManager.TimedModifierCount == 0);

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Constraints")]
        [Description("Min/max constraints from the definition clamp the final value on both ends.")]
        public void Test_Clamping()
        {
            Log("--- Min/Max Clamping ---");

            var handle = RegisterTestEntity();

            // MaxHealth has min=0, max=9999 in our test definitions.
            // Apply a massive negative to test min clamp.
            handle.AddFlat(StatIds.MaxHealth, -500f, 9999);
            Check("MaxHealth clamped at min 0", Approx(handle.GetValue(StatIds.MaxHealth), 0f));

            handle.RemoveAllFromSource(9999);

            // Apply massive positive to test max clamp.
            handle.AddFlat(StatIds.MaxHealth, 50000f, 9998);
            Check("MaxHealth clamped at max 9999", Approx(handle.GetValue(StatIds.MaxHealth), 9999f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Evaluation")]
        [Description("GetValue returns a cached value and only recomputes after an input changes.")]
        public void Test_DirtyFlagCaching()
        {
            Log("--- Dirty Flag Caching ---");

            var handle = RegisterTestEntity();

            // First access triggers recalculation.
            float v1 = handle.GetValue(StatIds.Strength);

            // Second access should return cached (same value, no recalc).
            float v2 = handle.GetValue(StatIds.Strength);
            Check("Cached value matches first read", Approx(v1, v2));

            // Modify base -> should dirty.
            handle.SetBaseValue(StatIds.Strength, 20f);
            float v3 = handle.GetValue(StatIds.Strength);
            Check("Value updated after base change", Approx(v3, 20f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Registration")]
        [Description("A recycled slot bumps its generation so stale handles fail validation and return defaults.")]
        public void Test_HandleGenerationSafety()
        {
            Log("--- Handle Generation Safety ---");

            var handle1 = RegisterTestEntity();
            int index1 = handle1.Index;
            int gen1 = handle1.Generation;

            StatManager.UnregisterEntity(handle1);

            // Register a new entity — should recycle the same slot with bumped generation.
            var handle2 = RegisterTestEntity();

            if (handle2.Index == index1)
            {
                Check("Recycled slot has higher generation", handle2.Generation > gen1);
            }

            // Stale handle should fail gracefully.
            float staleValue = StatManager.GetValue(handle1, StatIds.Strength);
            Check("Stale handle returns 0", Approx(staleValue, 0f));

            bool staleHas = StatManager.HasStat(handle1, StatIds.Strength);
            Check("Stale handle HasStat returns false", !staleHas);

            StatManager.UnregisterEntity(handle2);
        }

        [Test, Category("Serialization")]
        [Description("CaptureEntityState/RestoreEntityState round-trips base values, modifiers, and timer countdowns.")]
        public void Test_SaveRestoreRoundTrip()
        {
            Log("--- Save / Restore Round Trip ---");

            var handle = RegisterTestEntity();
            const long swordId = 5001;
            const long buffId  = 8001;

            // Set up some state: modified base, flat modifier, timed modifier.
            handle.SetBaseValue(StatIds.Strength, 25f);
            handle.AddFlat(StatIds.Strength, 5f, swordId);
            StatManager.AddTimedModifier(handle, StatIds.MaxHealth,
                new StatModifier(0.2f, ModifierType.PercentAdd, buffId), 60f);

            // Tick a bit so timed modifier has partial remaining time.
            StatManager.TickTimedModifiers(10f);

            float strBeforeSave = handle.GetValue(StatIds.Strength);
            float hpBeforeSave  = handle.GetValue(StatIds.MaxHealth);

            // Capture state.
            var saveData = StatManager.CaptureEntityState(handle);

            // Destroy the entity.
            StatManager.UnregisterEntity(handle);
            Check("Entity gone after unregister", StatManager.ActiveEntityCount == 0);

            // Re-register with same stat set and restore.
            var restored = RegisterTestEntity();
            StatManager.RestoreEntityState(restored, saveData);

            float strAfterLoad = restored.GetValue(StatIds.Strength);
            float hpAfterLoad  = restored.GetValue(StatIds.MaxHealth);

            Check($"Strength survives save/load: {strBeforeSave} == {strAfterLoad}",
                Approx(strBeforeSave, strAfterLoad));
            Check($"MaxHealth survives save/load: {hpBeforeSave} == {hpAfterLoad}",
                Approx(hpBeforeSave, hpAfterLoad));

            Check("Restored base value = 25", Approx(restored.GetBaseValue(StatIds.Strength), 25f));
            Check("Restored modifier count = 1 on Strength", StatManager.GetModifierCount(restored, StatIds.Strength) == 1);

            // Timed modifier should still be tracked with ~50s remaining.
            Check("Timed modifier restored", StatManager.TimedModifierCount == 1);

            // Tick the remaining time and verify it expires.
            StatManager.TickTimedModifiers(51f);
            Check("Timed modifier expired after remaining duration",
                Approx(restored.GetValue(StatIds.MaxHealth), 100f + 0f)); // base only, buff gone

            // Sword flat should still be there.
            Check("Sword modifier persists", Approx(restored.GetValue(StatIds.Strength), 30f));

            StatManager.UnregisterEntity(restored);
        }

        [Test, Category("Queries")]
        [Description("GetValues fills a span with multiple stat values in one validated call.")]
        public void Test_BatchGetValues()
        {
            Log("--- Batch GetValues ---");

            var handle = RegisterTestEntity();
            handle.AddFlat(StatIds.Strength, 5f, 1111);

            System.Span<int> ids = stackalloc int[] { StatIds.Strength, StatIds.MaxHealth, StatIds.MoveSpeed };
            System.Span<float> vals = stackalloc float[3];
            int written = StatManager.GetValues(handle, ids, vals);

            Check("Batch returned 3 values", written == 3);
            Check("Batch Strength = 15", Approx(vals[0], 15f));
            Check("Batch MaxHealth = 100", Approx(vals[1], 100f));
            Check("Batch MoveSpeed = 5", Approx(vals[2], 5f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Performance")]
        [Description("Registers, modifies, queries, and ticks 1000 entities to exercise the system at scale.")]
        public void Test_ScaleTest()
        {
            Log("--- Scale Test (1000 entities) ---");

            const int count = 1000;
            var handles = new EntityStatHandle[count];

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Register.
            for (int i = 0; i < count; i++)
                handles[i] = RegisterTestEntity();

            long registerMs = sw.ElapsedMilliseconds;

            // Add a modifier to each.
            for (int i = 0; i < count; i++)
                handles[i].AddFlat(StatIds.Strength, i * 0.1f, i + 10000);

            // Add a timed modifier to every 10th entity.
            for (int i = 0; i < count; i += 10)
                StatManager.AddTimedModifier(handles[i], StatIds.MaxHealth,
                    new StatModifier(0.05f, ModifierType.PercentAdd, i + 20000), 5f);

            long modifyMs = sw.ElapsedMilliseconds - registerMs;

            // Query all.
            float sum = 0f;
            for (int i = 0; i < count; i++)
                sum += handles[i].GetValue(StatIds.Strength);

            long queryMs = sw.ElapsedMilliseconds - registerMs - modifyMs;

            // Tick timed.
            StatManager.TickTimedModifiers(6f);

            long tickMs = sw.ElapsedMilliseconds - registerMs - modifyMs - queryMs;
            sw.Stop();

            Check($"Active entity count = {count}", StatManager.ActiveEntityCount == count);
            Check("All timed modifiers expired", StatManager.TimedModifierCount == 0);
            Check("Sum of Strength values is positive", sum > 0f);

            Log($"  Register {count}: {registerMs}ms | Modify: {modifyMs}ms | Query: {queryMs}ms | Tick: {tickMs}ms | Total: {sw.ElapsedMilliseconds}ms");

            // Unregister all.
            for (int i = 0; i < count; i++)
                StatManager.UnregisterEntity(handles[i]);

            Check("All entities unregistered", StatManager.ActiveEntityCount == 0);
        }

        [Test, Category("Events")]
        [Description("OnStatChanged fires with the correct stat id and old/new values when a stat changes.")]
        public void Test_StatChangedEvents()
        {
            Log("--- OnStatChanged Events ---");

            _changeLog.Clear();

            var handle = RegisterTestEntity();
            float baseBefore = handle.GetValue(StatIds.Strength); // force initial calc, cache it

            // This should trigger a change event.
            handle.AddFlat(StatIds.Strength, 5f, 1111);
            float _ = handle.GetValue(StatIds.Strength); // trigger recalc

            Check("Change event fired", _changeLog.Count >= 1);

            if (_changeLog.Count > 0)
            {
                var last = _changeLog[^1];
                Check("Change event statId = Strength", last.statId == StatIds.Strength);
                Check($"Change event old = {baseBefore}", Approx(last.oldVal, baseBefore));
                Check($"Change event new = {baseBefore + 5f}", Approx(last.newVal, baseBefore + 5f));
            }

            StatManager.UnregisterEntity(handle);
            _changeLog.Clear();
        }

        [Test, Category("Registration")]
        [Description("A recycled slot starts clean (default base, zero modifiers) with no leakage from the prior entity.")]
        public void Test_SlotRecycling()
        {
            Log("--- Slot Recycling ---");

            // Register and unregister multiple times, verify no leaks or corruption.
            var h1 = RegisterTestEntity();
            var h2 = RegisterTestEntity();
            h1.AddFlat(StatIds.Strength, 99f, 1);
            StatManager.UnregisterEntity(h1);

            var h3 = RegisterTestEntity();
            // h3 may have recycled h1's slot. Strength should be at default, not 99+10.
            Check("Recycled slot has clean base value",
                Approx(h3.GetValue(StatIds.Strength), 10f));
            Check("Recycled slot has 0 modifiers",
                StatManager.GetModifierCount(h3, StatIds.Strength) == 0);

            StatManager.UnregisterEntity(h2);
            StatManager.UnregisterEntity(h3);
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        // All test-only stat IDs derive from a single baseline to avoid
        // collisions with real StatIds and with each other.
        private const int TestStatBase     = 90000;
        private const int TestHealthRegenId = TestStatBase + 1;
        private const int TestCarryWeightId = TestStatBase + 2;
        private const int TestLuckId        = TestStatBase + 3;
        private const int TestCritChanceId  = TestStatBase + 4;
        private const int TestMaxPsiId      = TestStatBase + 5;
        private const int TestInvalidFormulaId = TestStatBase + 6;
        private const int TestCycleAId         = TestStatBase + 7;
        private const int TestCycleBId         = TestStatBase + 8;
        private const int TestSelfDepId        = TestStatBase + 9;
        private const int TestOverrideId       = TestStatBase + 10;
        private const int TestOverrideGhostId  = TestStatBase + 11;

        private static readonly int[] TestStatIds = {
            StatIds.Strength, StatIds.MaxHealth, StatIds.MaxStamina,
            StatIds.PhysDamage, StatIds.MoveSpeed,
            TestHealthRegenId, TestCarryWeightId
        };

        private EntityStatHandle RegisterTestEntity()
        {
            return StatManager.RegisterEntity(TestStatIds);
        }

        /// <summary>Register entity with both stats and resource pools.</summary>
        private EntityStatHandle RegisterTestEntityWithPools()
        {
            var handle = StatManager.RegisterEntity(TestStatIds);
            ResourcePoolManager.RegisterEntity(handle);
            return handle;
        }

        private void UnregisterTestEntityWithPools(EntityStatHandle handle)
        {
            ResourcePoolManager.UnregisterEntity(handle);
            StatManager.UnregisterEntity(handle);
        }

        /// <summary>
        /// Create a minimal StatDefinitionDatabase programmatically so the test
        /// works without any asset setup. Just drop this script on a GameObject.
        /// 
        /// Now includes derived stats:
        ///   MaxHealth = 50 + (Endurance * 5)  ... but we don't have Endurance in the
        ///     test set, so for simplicity MaxHealth is a normal stat with base 100.
        ///   HealthRegen = MaxHealth * 0.01    (derived, depends on MaxHealth)
        ///   CarryWeight = 50 + (Strength * 10) (derived, depends on Strength)
        /// </summary>
        private StatDefinitionDatabase CreateTestDatabase()
        {
            var db = ScriptableObject.CreateInstance<StatDefinitionDatabase>();

            var defs = new StatDefinition[]
            {
                CreateDef(StatIds.Strength,  "Strength",   10f),
                CreateDef(StatIds.MaxHealth, "Max Health", 100f, hasMin: true, min: 0f, hasMax: true, max: 9999f),
                CreateDef(StatIds.MaxStamina,"Max Stamina", 80f, hasMin: true, min: 0f, hasMax: true, max: 9999f),
                CreateDef(StatIds.PhysDamage,"Phys Damage", 0f),
                CreateDef(StatIds.MoveSpeed, "Move Speed",  5f,  hasMin: true, min: 0f),

                // HealthRegen = MaxHealth * 0.01
                // Formula (postfix): PushStat(MaxHealth), PushConstant(0.01), Multiply
                // defaultBaseValue = 0 so formula is sole contributor.
                CreateDef(TestHealthRegenId, "Health Regen", 0f,
                    dependencies: new[] { StatIds.MaxHealth },
                    formula: new[]
                    {
                        StatFormulaOp.Stat(StatIds.MaxHealth),
                        StatFormulaOp.Const(0.01f),
                        StatFormulaOp.Mul(),
                    }),

                // CarryWeight = 50 + (Strength * 10)
                // Formula (postfix): PushConstant(50), PushStat(Strength), PushConstant(10), Multiply, Add
                CreateDef(TestCarryWeightId, "Carry Weight", 0f,
                    dependencies: new[] { StatIds.Strength },
                    formula: new[]
                    {
                        StatFormulaOp.Const(50f),
                        StatFormulaOp.Stat(StatIds.Strength),
                        StatFormulaOp.Const(10f),
                        StatFormulaOp.Mul(),
                        StatFormulaOp.Add(),
                    }),
            };

            // Use reflection to set the private 'definitions' field since SO
            // fields are normally only set via inspector / serialization.
            var field = typeof(StatDefinitionDatabase)
                .GetField("definitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(db, defs);

            return db;
        }

        private StatDefinition CreateDef(int id, string displayName, float defaultBase,
            bool hasMin = false, float min = 0f, bool hasMax = false, float max = 0f, bool roundToInt = false)
        {
            var def = ScriptableObject.CreateInstance<StatDefinition>();

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(StatDefinition);
            t.GetField("statId", flags)?.SetValue(def, id);
            t.GetField("displayName", flags)?.SetValue(def, displayName);
            t.GetField("defaultBaseValue", flags)?.SetValue(def, defaultBase);
            t.GetField("hasMinValue", flags)?.SetValue(def, hasMin);
            t.GetField("minValue", flags)?.SetValue(def, min);
            t.GetField("hasMaxValue", flags)?.SetValue(def, hasMax);
            t.GetField("maxValue", flags)?.SetValue(def, max);
            t.GetField("roundToInt", flags)?.SetValue(def, roundToInt);

            return def;
        }

        private StatDefinition CreateDef(int id, string displayName, float defaultBase,
            int[] dependencies, StatFormulaOp[] formula,
            bool hasMin = false, float min = 0f, bool hasMax = false, float max = 0f)
        {
            var def = CreateDef(id, displayName, defaultBase, hasMin, min, hasMax, max);

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var t = typeof(StatDefinition);
            t.GetField("dependencies", flags)?.SetValue(def, dependencies);
            t.GetField("formula", flags)?.SetValue(def, formula);

            return def;
        }

        // ==================================================================
        // Derived stat tests
        // ==================================================================

        [Test, Category("DerivedStats")]
        [Description("A postfix formula computes a derived stat from its dependencies (CarryWeight, HealthRegen).")]
        public void Test_DerivedStatBasic()
        {
            Log("--- Derived Stat: Basic Formula ---");

            var handle = RegisterTestEntity();

            // CarryWeight = 50 + (Strength * 10). Strength base = 10.
            // Expected: 50 + (10 * 10) = 150.
            float carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight = 150 (50 + 10*10), got {carry}", Approx(carry, 150f));

            // HealthRegen = MaxHealth * 0.01. MaxHealth base = 100.
            // Expected: 100 * 0.01 = 1.0.
            float regen = handle.GetValue(TestHealthRegenId);
            Check($"HealthRegen = 1.0 (100*0.01), got {regen}", Approx(regen, 1.0f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DerivedStats")]
        [Description("Changing a dependency eagerly recalculates the derived stat, and reverting it restores the value.")]
        public void Test_DerivedStatCascade()
        {
            Log("--- Derived Stat: Automatic Cascade ---");

            var handle = RegisterTestEntity();

            // Initial CarryWeight = 150.
            Check("CarryWeight starts at 150", Approx(handle.GetValue(TestCarryWeightId), 150f));

            // Buff Strength with +5 flat. New Strength = 15.
            // CarryWeight should auto-update: 50 + (15 * 10) = 200.
            handle.AddFlat(StatIds.Strength, 5f, 1001);

            float carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight cascaded to 200 after STR buff, got {carry}", Approx(carry, 200f));

            // Remove the buff. Strength back to 10, CarryWeight back to 150.
            handle.RemoveAllFromSource(1001);
            carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight cascaded back to 150, got {carry}", Approx(carry, 150f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DerivedStats")]
        [Description("Modifiers on a derived stat stack on top of its formula result.")]
        public void Test_DerivedStatWithModifiers()
        {
            Log("--- Derived Stat: Formula + Own Modifiers ---");

            var handle = RegisterTestEntity();

            // CarryWeight formula = 150 (from 50 + 10*10).
            // Now add a +20% PercentAdd modifier directly to CarryWeight (e.g., a backpack enchantment).
            const long backpackId = 4001;
            handle.AddPercentAdd(TestCarryWeightId, 0.2f, backpackId);

            // Expected: 150 * 1.2 = 180.
            float carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight = 180 with +20% modifier, got {carry}", Approx(carry, 180f));

            // Now also buff Strength +5. CarryWeight formula base becomes 200.
            // With +20% modifier: 200 * 1.2 = 240.
            handle.AddFlat(StatIds.Strength, 5f, 1002);
            carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight = 240 (STR buff + backpack), got {carry}", Approx(carry, 240f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DerivedStats")]
        [Description("A derived stat recalculates when any of its dependencies change.")]
        public void Test_DerivedStatMultiDependency()
        {
            Log("--- Derived Stat: Multiple Dependencies ---");

            // HealthRegen depends on MaxHealth.
            // Buff MaxHealth -> HealthRegen should cascade.
            var handle = RegisterTestEntity();

            Check("HealthRegen starts at 1.0", Approx(handle.GetValue(TestHealthRegenId), 1.0f));

            // +100 flat to MaxHealth. New MaxHealth = 200. HealthRegen = 200 * 0.01 = 2.0.
            handle.AddFlat(StatIds.MaxHealth, 100f, 2001);
            float regen = handle.GetValue(TestHealthRegenId);
            Check($"HealthRegen = 2.0 after MaxHealth buff, got {regen}", Approx(regen, 2.0f));

            // Remove buff. MaxHealth = 100. HealthRegen = 1.0.
            handle.RemoveAllFromSource(2001);
            regen = handle.GetValue(TestHealthRegenId);
            Check($"HealthRegen back to 1.0, got {regen}", Approx(regen, 1.0f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DerivedStats")]
        [Description("A timed buff on a dependency cascades to the derived stat and reverts when it expires.")]
        public void Test_DerivedStatChain()
        {
            Log("--- Derived Stat: Timed Modifier Cascade ---");

            var handle = RegisterTestEntity();

            // Apply timed +50% Strength for 3 seconds.
            // Strength: 10 * 1.5 = 15. CarryWeight: 50 + 15*10 = 200.
            StatManager.AddTimedModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, 3001), 3f);

            Check("CarryWeight = 200 during timed STR buff",
                Approx(handle.GetValue(TestCarryWeightId), 200f));

            // Tick past expiration.
            StatManager.TickTimedModifiers(4f);

            // Strength back to 10. CarryWeight back to 150.
            float carry = handle.GetValue(TestCarryWeightId);
            Check($"CarryWeight = 150 after timed buff expired, got {carry}", Approx(carry, 150f));

            StatManager.UnregisterEntity(handle);
        }

        // ==================================================================
        // Resource pool tests
        // ==================================================================

        [Test, Category("ResourcePools")]
        [Description("Pools register per entity, start full, and report current/max/ratio correctly.")]
        public void Test_ResourcePool_Basic()
        {
            Log("--- Resource Pool: Basic ---");

            var handle = RegisterTestEntityWithPools();

            Check("Has health pool", ResourcePoolManager.HasPool(handle, StatIds.MaxHealth));
            Check("Has stamina pool", ResourcePoolManager.HasPool(handle, StatIds.MaxStamina));
            Check("Pool count = 2", ResourcePoolManager.PoolCountForEntity(handle) == 2);

            // Pools start full.
            Check("Health current = 100", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));
            Check("Health max = 100", Approx(handle.GetPoolMax(StatIds.MaxHealth), 100f));
            Check("Health ratio = 1.0", Approx(handle.GetPoolRatio(StatIds.MaxHealth), 1f));

            Check("Stamina current = 80", Approx(handle.GetCurrent(StatIds.MaxStamina), 80f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("ResourcePools")]
        [Description("ApplyDamage and Heal move current within [0, max], clamping over-heal and over-kill.")]
        public void Test_ResourcePool_DamageAndHeal()
        {
            Log("--- Resource Pool: Damage and Heal ---");

            var handle = RegisterTestEntityWithPools();

            // Take 30 damage.
            float dealt = handle.ApplyDamage(StatIds.MaxHealth, 30f);
            Check("Dealt 30 damage", Approx(dealt, 30f));
            Check("Health = 70 after damage", Approx(handle.GetCurrent(StatIds.MaxHealth), 70f));
            Check("Ratio = 0.7", Approx(handle.GetPoolRatio(StatIds.MaxHealth), 0.7f));

            // Heal 20.
            float healed = handle.Heal(StatIds.MaxHealth, 20f);
            Check("Healed 20", Approx(healed, 20f));
            Check("Health = 90 after heal", Approx(handle.GetCurrent(StatIds.MaxHealth), 90f));

            // Overheal attempt: heal 50 but max is 100.
            healed = handle.Heal(StatIds.MaxHealth, 50f);
            Check("Healed only 10 (clamped at max)", Approx(healed, 10f));
            Check("Health = 100 (clamped)", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));

            // Overkill: deal 200 damage when current is 100.
            dealt = handle.ApplyDamage(StatIds.MaxHealth, 200f);
            Check("Only dealt 100 (clamped at 0)", Approx(dealt, 100f));
            Check("Health = 0", Approx(handle.GetCurrent(StatIds.MaxHealth), 0f));
            Check("Is depleted", handle.IsDepleted(StatIds.MaxHealth));

            // Negative damage does nothing.
            dealt = handle.ApplyDamage(StatIds.MaxHealth, -10f);
            Check("Negative damage = 0", Approx(dealt, 0f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("ResourcePools")]
        [Description("When the max stat drops below current, current auto-clamps down to the new max.")]
        public void Test_ResourcePool_ClampOnMaxChange()
        {
            Log("--- Resource Pool: Clamp When Max Decreases ---");

            var handle = RegisterTestEntityWithPools();

            // Health starts at 100/100. Buff max to 200.
            handle.AddFlat(StatIds.MaxHealth, 100f, 4001);
            // Current is still 100 (buff didn't auto-fill).
            Check("Current still 100 after max buff", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));
            Check("Max is now 200", Approx(handle.GetPoolMax(StatIds.MaxHealth), 200f));

            // Heal to full (200).
            handle.Heal(StatIds.MaxHealth, 999f);
            Check("Current = 200 after full heal", Approx(handle.GetCurrent(StatIds.MaxHealth), 200f));

            // Remove the buff. Max drops back to 100.
            // Current auto-clamps from 200 down to 100 (eager recalculation).
            handle.RemoveAllFromSource(4001);
            Check("Max back to 100", Approx(handle.GetPoolMax(StatIds.MaxHealth), 100f));
            Check("Current clamped to 100", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("ResourcePools")]
        [Description("RestoreAll refills every pool to max and clears shields.")]
        public void Test_ResourcePool_RestoreAll()
        {
            Log("--- Resource Pool: Restore All ---");

            var handle = RegisterTestEntityWithPools();

            handle.ApplyDamage(StatIds.MaxHealth, 60f);
            handle.ApplyDamage(StatIds.MaxStamina, 40f);

            Check("Health = 40 before restore", Approx(handle.GetCurrent(StatIds.MaxHealth), 40f));
            Check("Stamina = 40 before restore", Approx(handle.GetCurrent(StatIds.MaxStamina), 40f));

            handle.RestoreAllPools();

            Check("Health = 100 after restore", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));
            Check("Stamina = 80 after restore", Approx(handle.GetCurrent(StatIds.MaxStamina), 80f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("ResourcePools")]
        [Description("Pool current values round-trip through capture/restore.")]
        public void Test_ResourcePool_SaveRestore()
        {
            Log("--- Resource Pool: Save / Restore ---");

            var handle = RegisterTestEntityWithPools();
            handle.ApplyDamage(StatIds.MaxHealth, 35f);
            handle.ApplyDamage(StatIds.MaxStamina, 20f);

            float hpBefore = handle.GetCurrent(StatIds.MaxHealth);
            float spBefore = handle.GetCurrent(StatIds.MaxStamina);

            // Save both stat and pool state.
            var statSave = StatManager.CaptureEntityState(handle);
            var poolSave = ResourcePoolManager.CaptureEntityState(handle);

            // Destroy and recreate.
            UnregisterTestEntityWithPools(handle);

            var restored = RegisterTestEntityWithPools();
            StatManager.RestoreEntityState(restored, statSave);
            ResourcePoolManager.RestoreEntityState(restored, poolSave);

            Check($"HP survives save/load: {hpBefore}",
                Approx(ResourcePoolManager.GetCurrent(restored, StatIds.MaxHealth), hpBefore));
            Check($"SP survives save/load: {spBefore}",
                Approx(ResourcePoolManager.GetCurrent(restored, StatIds.MaxStamina), spBefore));

            UnregisterTestEntityWithPools(restored);
        }

        [Test, Category("ResourcePools")]
        [Description("TickRegen adds the regen-stat rate to current per second, capped at max.")]
        public void Test_ResourcePool_Regen()
        {
            Log("--- Resource Pool: Batch Regen Tick ---");

            var handle = RegisterTestEntityWithPools();

            // Damage health to 50/100.
            handle.ApplyDamage(StatIds.MaxHealth, 50f);
            Check("Health = 50 before regen", Approx(handle.GetCurrent(StatIds.MaxHealth), 50f));

            // HealthRegen stat = MaxHealth * 0.01 = 1.0 HP/sec.
            float regenRate = handle.GetValue(TestHealthRegenId);
            Check($"Regen rate = 1.0, got {regenRate}", Approx(regenRate, 1.0f));

            // Tick 10 seconds of regen. Should heal 10 HP.
            ResourcePoolManager.TickRegen(StatIds.MaxHealth, TestHealthRegenId, 10f);
            float after = handle.GetCurrent(StatIds.MaxHealth);
            Check($"Health = 60 after 10s regen, got {after}", Approx(after, 60f));

            // Tick enough to cap at max.
            ResourcePoolManager.TickRegen(StatIds.MaxHealth, TestHealthRegenId, 100f);
            Check("Health capped at 100", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));

            // Already full — tick should do nothing.
            ResourcePoolManager.TickRegen(StatIds.MaxHealth, TestHealthRegenId, 5f);
            Check("Still 100 (already full)", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));

            UnregisterTestEntityWithPools(handle);
        }

        // ==================================================================
        // Shield (temporary HP) tests
        // ==================================================================

        [Test, Category("Shields")]
        [Description("AddShield/SetShield/ClearShield manage the shield buffer and effective-total reporting.")]
        public void Test_Shield_Basic()
        {
            Log("--- Shield: Basic ---");

            var handle = RegisterTestEntityWithPools();

            // Shield starts at 0.
            Check("Shield starts at 0", Approx(handle.GetShield(StatIds.MaxHealth), 0f));
            Check("Effective total = 100 (current only)", Approx(handle.GetEffectiveTotal(StatIds.MaxHealth), 100f));

            // Add 30 shield.
            float newShield = handle.AddShield(StatIds.MaxHealth, 30f);
            Check("Shield = 30 after add", Approx(newShield, 30f));
            Check("GetShield = 30", Approx(handle.GetShield(StatIds.MaxHealth), 30f));
            Check("Effective total = 130", Approx(handle.GetEffectiveTotal(StatIds.MaxHealth), 130f));

            // Shield stacks additively.
            handle.AddShield(StatIds.MaxHealth, 20f);
            Check("Shield = 50 after stacking", Approx(handle.GetShield(StatIds.MaxHealth), 50f));

            // SetShield replaces.
            handle.SetShield(StatIds.MaxHealth, 10f);
            Check("Shield = 10 after SetShield", Approx(handle.GetShield(StatIds.MaxHealth), 10f));

            // ClearShield returns old value and zeroes.
            float cleared = handle.ClearShield(StatIds.MaxHealth);
            Check("ClearShield returned 10", Approx(cleared, 10f));
            Check("Shield = 0 after clear", Approx(handle.GetShield(StatIds.MaxHealth), 0f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("Shields")]
        [Description("Damage consumes shield before current, including partial and overkill cases.")]
        public void Test_Shield_DamageAbsorption()
        {
            Log("--- Shield: Damage Absorption ---");

            var handle = RegisterTestEntityWithPools();
            handle.AddShield(StatIds.MaxHealth, 40f);

            // 100 current + 40 shield. Deal 25 damage -> shield absorbs all.
            float dealt = handle.ApplyDamage(StatIds.MaxHealth, 25f);
            Check("25 damage dealt (all from shield)", Approx(dealt, 25f));
            Check("Shield = 15", Approx(handle.GetShield(StatIds.MaxHealth), 15f));
            Check("Current still 100", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));

            // Deal 30 damage -> 15 from shield, 15 from current.
            dealt = handle.ApplyDamage(StatIds.MaxHealth, 30f);
            Check("30 damage dealt (15 shield + 15 current)", Approx(dealt, 30f));
            Check("Shield = 0", Approx(handle.GetShield(StatIds.MaxHealth), 0f));
            Check("Current = 85", Approx(handle.GetCurrent(StatIds.MaxHealth), 85f));

            // Deal damage with no shield left -> all from current.
            dealt = handle.ApplyDamage(StatIds.MaxHealth, 10f);
            Check("10 damage from current", Approx(dealt, 10f));
            Check("Current = 75", Approx(handle.GetCurrent(StatIds.MaxHealth), 75f));

            // Overkill with shield: 50 shield + 20 current. Deal 100.
            handle.AddShield(StatIds.MaxHealth, 50f);
            ResourcePoolManager.SetCurrent(handle, StatIds.MaxHealth, 20f);
            dealt = handle.ApplyDamage(StatIds.MaxHealth, 100f);
            Check("Overkill: dealt 70 (50 shield + 20 current)", Approx(dealt, 70f));
            Check("Shield = 0 after overkill", Approx(handle.GetShield(StatIds.MaxHealth), 0f));
            Check("Current = 0 after overkill", Approx(handle.GetCurrent(StatIds.MaxHealth), 0f));
            Check("Is depleted", handle.IsDepleted(StatIds.MaxHealth));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("Shields")]
        [Description("TickShieldDecay reduces shield toward zero and never goes negative.")]
        public void Test_Shield_Decay()
        {
            Log("--- Shield: Decay Tick ---");

            var handle = RegisterTestEntityWithPools();
            handle.AddShield(StatIds.MaxHealth, 50f);

            // Decay at 10/sec for 2 seconds = 20 lost.
            ResourcePoolManager.TickShieldDecay(StatIds.MaxHealth, 10f, 2f);
            Check("Shield = 30 after 2s decay at 10/s", Approx(handle.GetShield(StatIds.MaxHealth), 30f));

            // Decay the rest.
            ResourcePoolManager.TickShieldDecay(StatIds.MaxHealth, 10f, 5f);
            Check("Shield = 0 after full decay", Approx(handle.GetShield(StatIds.MaxHealth), 0f));

            // Decay on zero shield does nothing (no negative).
            ResourcePoolManager.TickShieldDecay(StatIds.MaxHealth, 10f, 1f);
            Check("Shield still 0", Approx(handle.GetShield(StatIds.MaxHealth), 0f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("Shields")]
        [Description("Shield is ignored by max-stat auto-clamping and persists when max drops.")]
        public void Test_Shield_NotAffectedByMaxClamp()
        {
            Log("--- Shield: Survives Max Stat Change ---");

            var handle = RegisterTestEntityWithPools();

            // 100/100 current + 40 shield.
            handle.AddShield(StatIds.MaxHealth, 40f);

            // Buff max to 200, heal to full, then remove buff.
            handle.AddFlat(StatIds.MaxHealth, 100f, 5001);
            handle.Heal(StatIds.MaxHealth, 999f);
            Check("Current = 200 (buffed)", Approx(handle.GetCurrent(StatIds.MaxHealth), 200f));
            Check("Shield = 40 (unchanged by buff)", Approx(handle.GetShield(StatIds.MaxHealth), 40f));

            // Remove buff. Max drops to 100. Current clamps to 100.
            // Shield should be UNAFFECTED.
            handle.RemoveAllFromSource(5001);
            Check("Max back to 100", Approx(handle.GetValue(StatIds.MaxHealth), 100f));
            Check("Current clamped to 100", Approx(handle.GetCurrent(StatIds.MaxHealth), 100f));
            Check("Shield still 40 (not clamped)", Approx(handle.GetShield(StatIds.MaxHealth), 40f));
            Check("Effective total = 140", Approx(handle.GetEffectiveTotal(StatIds.MaxHealth), 140f));

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("Shields")]
        [Description("Shield values round-trip through capture/restore.")]
        public void Test_Shield_SaveRestore()
        {
            Log("--- Shield: Save / Restore ---");

            var handle = RegisterTestEntityWithPools();
            handle.ApplyDamage(StatIds.MaxHealth, 20f);
            handle.AddShield(StatIds.MaxHealth, 35f);

            float currentBefore = handle.GetCurrent(StatIds.MaxHealth);
            float shieldBefore  = handle.GetShield(StatIds.MaxHealth);

            var statSave = StatManager.CaptureEntityState(handle);
            var poolSave = ResourcePoolManager.CaptureEntityState(handle);

            UnregisterTestEntityWithPools(handle);

            var restored = RegisterTestEntityWithPools();
            StatManager.RestoreEntityState(restored, statSave);
            ResourcePoolManager.RestoreEntityState(restored, poolSave);

            Check($"Current survives: {currentBefore}",
                Approx(ResourcePoolManager.GetCurrent(restored, StatIds.MaxHealth), currentBefore));
            Check($"Shield survives: {shieldBefore}",
                Approx(ResourcePoolManager.GetShield(restored, StatIds.MaxHealth), shieldBefore));

            UnregisterTestEntityWithPools(restored);
        }

        // ==================================================================
        // Dynamic registration tests (mod support)
        // ==================================================================

        [Test, Category("DynamicRegistration")]
        [Description("A stat can be registered at runtime (mod-style); duplicates are rejected and modifiers work on it.")]
        public void Test_DynamicStatRegistration()
        {
            Log("--- Dynamic: Register Stat at Runtime ---");

            // Simulate a mod registering a "Luck" stat after system init.
            var luckDef = CreateDef(TestLuckId, "Luck", 5f);
            bool registered = StatManager.RegisterStatDefinition(luckDef);
            Check("Luck registered successfully", registered);

            // Duplicate registration should fail.
            bool duplicate = StatManager.RegisterStatDefinition(luckDef);
            Check("Duplicate registration rejected", !duplicate);

            // Register an entity that includes the mod stat.
            int[] modEntityStats = { StatIds.Strength, StatIds.MaxHealth, TestLuckId };
            var handle = StatManager.RegisterEntity(modEntityStats);

            Check("Entity has Luck stat", StatManager.HasStat(handle, TestLuckId));
            Check("Luck base = 5", Approx(StatManager.GetBaseValue(handle, TestLuckId), 5f));

            // Modifiers work on the mod stat.
            handle.AddFlat(TestLuckId, 3f, 99001);
            Check("Luck = 8 after +3 flat", Approx(handle.GetValue(TestLuckId), 8f));

            handle.RemoveAllFromSource(99001);
            Check("Luck back to 5", Approx(handle.GetValue(TestLuckId), 5f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DynamicRegistration")]
        [Description("A derived stat registered at runtime cascades from its dependency correctly.")]
        public void Test_DynamicDerivedStatRegistration()
        {
            Log("--- Dynamic: Register Derived Stat at Runtime ---");

            // Isolated test: register the Luck stat this derived stat depends on.
            StatManager.RegisterStatDefinition(CreateDef(TestLuckId, "Luck", 5f));

            // Mod defines CritChance = Luck * 0.02

            var critDef = CreateDef(TestCritChanceId, "Crit Chance", 0f,
                dependencies: new[] { TestLuckId },
                formula: new[]
                {
                    StatFormulaOp.Stat(TestLuckId),
                    StatFormulaOp.Const(0.02f),
                    StatFormulaOp.Mul(),
                });

            bool registered = StatManager.RegisterStatDefinition(critDef);
            Check("CritChance registered successfully", registered);

            // Register an entity with both mod stats.
            int[] modEntityStats = { StatIds.Strength, TestLuckId, TestCritChanceId };
            var handle = StatManager.RegisterEntity(modEntityStats);

            // Luck = 5, CritChance = 5 * 0.02 = 0.1
            Check("CritChance = 0.1", Approx(handle.GetValue(TestCritChanceId), 0.1f));

            // Buff Luck -> CritChance should cascade.
            handle.AddFlat(TestLuckId, 10f, 99002);
            // Luck = 15, CritChance = 15 * 0.02 = 0.3
            Check("CritChance cascaded to 0.3 after Luck buff",
                Approx(handle.GetValue(TestCritChanceId), 0.3f));

            handle.RemoveAllFromSource(99002);
            Check("CritChance back to 0.1", Approx(handle.GetValue(TestCritChanceId), 0.1f));

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("DynamicRegistration")]
        [Description("A resource pool can be registered at runtime; duplicates are rejected and damage/heal work.")]
        public void Test_DynamicPoolRegistration()
        {
            Log("--- Dynamic: Register Pool at Runtime ---");

            // Mod defines a "Psi" resource pool.
            var psiDef = CreateDef(TestMaxPsiId, "Max Psi", 60f, hasMin: true, min: 0f);
            StatManager.RegisterStatDefinition(psiDef);

            bool poolRegistered = ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(TestMaxPsiId, TestMaxPsiId, startFull: true));
            Check("Psi pool registered", poolRegistered);

            // Duplicate should fail.
            bool duplicate = ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(TestMaxPsiId, TestMaxPsiId));
            Check("Duplicate pool rejected", !duplicate);

            // Register entity with the mod stat and pool.
            int[] modEntityStats = { StatIds.Strength, TestMaxPsiId };
            var handle = StatManager.RegisterEntity(modEntityStats);
            ResourcePoolManager.RegisterEntity(handle);

            Check("Entity has Psi pool", ResourcePoolManager.HasPool(handle, TestMaxPsiId));
            Check("Psi current = 60 (start full)", Approx(handle.GetCurrent(TestMaxPsiId), 60f));

            // Damage and heal work.
            handle.ApplyDamage(TestMaxPsiId, 25f);
            Check("Psi = 35 after damage", Approx(handle.GetCurrent(TestMaxPsiId), 35f));

            handle.Heal(TestMaxPsiId, 10f);
            Check("Psi = 45 after heal", Approx(handle.GetCurrent(TestMaxPsiId), 45f));

            ResourcePoolManager.UnregisterEntity(handle);
            StatManager.UnregisterEntity(handle);
        }

        // ==================================================================
        // Full save/load round-trip (end-to-end)
        // ==================================================================

        [Test, Category("Serialization")]
        [Description("End-to-end: a fully-kitted character survives capture -> JSON -> restore with live behavior intact.")]
        public void Test_FullSaveLoadRoundTrip()
        {
            Log("--- Full Save/Load Round Trip ---");

            // === BUILD A FULLY KITTED CHARACTER ===

            var handle = RegisterTestEntityWithPools();

            // 1. Level-up: bump Strength base from 10 -> 18.
            handle.SetBaseValue(StatIds.Strength, 18f);

            // 2. Equip iron sword: +12 flat PhysDamage, +5 flat Strength.
            const long swordId = 50001;
            handle.AddFlat(StatIds.PhysDamage, 12f, swordId);
            handle.AddFlat(StatIds.Strength, 5f, swordId);

            // 3. Equip enchanted ring: +20% additive MaxHealth.
            const long ringId = 50002;
            handle.AddPercentAdd(StatIds.MaxHealth, 0.20f, ringId);

            // 4. Active potion: +50% Strength for 120s (simulate 30s elapsed).
            const long potionId = 80001;
            StatManager.AddTimedModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, potionId), 120f);
            StatManager.TickTimedModifiers(30f); // 90s remaining

            // 5. Disease: -15 flat MaxStamina (permanent until cured).
            const long diseaseId = 91001;
            handle.AddFlat(StatIds.MaxStamina, -15f, diseaseId);

            // 6. Take some damage and add shield.
            handle.ApplyDamage(StatIds.MaxHealth, 35f);
            handle.ApplyDamage(StatIds.MaxStamina, 20f);
            handle.AddShield(StatIds.MaxHealth, 40f);

            // === SNAPSHOT ALL VALUES BEFORE SAVE ===

            float strBefore       = handle.GetValue(StatIds.Strength);
            float strBaseBefore   = handle.GetBaseValue(StatIds.Strength);
            float dmgBefore       = handle.GetValue(StatIds.PhysDamage);
            float maxHpBefore     = handle.GetValue(StatIds.MaxHealth);
            float maxSpBefore     = handle.GetValue(StatIds.MaxStamina);
            float moveSpBefore    = handle.GetValue(StatIds.MoveSpeed);
            float carryBefore     = handle.GetValue(TestCarryWeightId);
            float regenBefore     = handle.GetValue(TestHealthRegenId);
            float hpCurrent       = handle.GetCurrent(StatIds.MaxHealth);
            float spCurrent       = handle.GetCurrent(StatIds.MaxStamina);
            float shieldBefore    = handle.GetShield(StatIds.MaxHealth);
            int   timedCountBefore = StatManager.TimedModifierCount;

            Log($"  Pre-save: STR={strBefore} (base {strBaseBefore}), DMG={dmgBefore}, " +
                $"MaxHP={maxHpBefore}, HP={hpCurrent}/{maxHpBefore}+{shieldBefore}sh, " +
                $"MaxSP={maxSpBefore}, SP={spCurrent}, Carry={carryBefore}, Regen={regenBefore}");

            // === SERIALIZE ===

            var statSave = StatManager.CaptureEntityState(handle);
            var poolSave = ResourcePoolManager.CaptureEntityState(handle);

            // Convert to JSON to prove it round-trips through real serialization.
            string statJson = JsonUtility.ToJson(statSave);
            string poolJson = JsonUtility.ToJson(poolSave);

            Log($"  Stat JSON length: {statJson.Length} chars");
            Log($"  Pool JSON length: {poolJson.Length} chars");

            Check("Stat JSON is non-empty", statJson.Length > 10);
            Check("Pool JSON is non-empty", poolJson.Length > 10);

            // === DESTROY THE ENTITY ===

            UnregisterTestEntityWithPools(handle);
            Check("Entity gone", StatManager.ActiveEntityCount == 0);

            // === DESERIALIZE AND REBUILD ===

            var loadedStatSave = JsonUtility.FromJson<StatManager.FullEntitySaveData>(statJson);
            var loadedPoolSave = JsonUtility.FromJson<ResourcePoolManager.EntityPoolSaveData>(poolJson);

            var restored = RegisterTestEntityWithPools();
            StatManager.RestoreEntityState(restored, loadedStatSave);
            ResourcePoolManager.RestoreEntityState(restored, loadedPoolSave);

            // === VERIFY EVERYTHING MATCHES ===

            Check($"STR value matches: {strBefore}",
                Approx(restored.GetValue(StatIds.Strength), strBefore));
            Check($"STR base matches: {strBaseBefore}",
                Approx(restored.GetBaseValue(StatIds.Strength), strBaseBefore));
            Check($"PhysDamage matches: {dmgBefore}",
                Approx(restored.GetValue(StatIds.PhysDamage), dmgBefore));
            Check($"MaxHealth matches: {maxHpBefore}",
                Approx(restored.GetValue(StatIds.MaxHealth), maxHpBefore));
            Check($"MaxStamina matches: {maxSpBefore}",
                Approx(restored.GetValue(StatIds.MaxStamina), maxSpBefore));
            Check($"MoveSpeed matches: {moveSpBefore}",
                Approx(restored.GetValue(StatIds.MoveSpeed), moveSpBefore));
            Check($"CarryWeight matches: {carryBefore}",
                Approx(restored.GetValue(TestCarryWeightId), carryBefore));
            Check($"HealthRegen matches: {regenBefore}",
                Approx(restored.GetValue(TestHealthRegenId), regenBefore));
            Check($"HP current matches: {hpCurrent}",
                Approx(restored.GetCurrent(StatIds.MaxHealth), hpCurrent));
            Check($"SP current matches: {spCurrent}",
                Approx(restored.GetCurrent(StatIds.MaxStamina), spCurrent));
            Check($"Shield matches: {shieldBefore}",
                Approx(restored.GetShield(StatIds.MaxHealth), shieldBefore));
            Check($"Timed modifier count matches: {timedCountBefore}",
                StatManager.TimedModifierCount == timedCountBefore);

            // === VERIFY LIVE BEHAVIOR POST-LOAD ===

            // Potion should still be ticking (~90s remaining at save).
            // Tick 80 more seconds — potion still active.
            StatManager.TickTimedModifiers(80f);
            Check("STR unchanged after 80s (potion still active)",
                Approx(restored.GetValue(StatIds.Strength), strBefore));

            // Tick 20 more — potion expires (total 100s since save > 90s remaining).
            StatManager.TickTimedModifiers(20f);
            float strAfterExpiry = restored.GetValue(StatIds.Strength);
            Check($"STR decreased after potion expired: {strAfterExpiry} < {strBefore}",
                strAfterExpiry < strBefore);

            // Unequip sword — should strip sword modifiers cleanly.
            restored.RemoveAllFromSource(swordId);
            Check("PhysDamage = 0 after unequip",
                Approx(restored.GetValue(StatIds.PhysDamage), 0f));

            // Cure disease — stamina restored.
            restored.RemoveAllFromSource(diseaseId);
            Check("MaxStamina = 80 after cure",
                Approx(restored.GetValue(StatIds.MaxStamina), 80f));

            // Derived stats still cascade after load.
            float carryAfter = restored.GetValue(TestCarryWeightId);
            Log($"  Post-load CarryWeight: {carryAfter} (Strength base still {restored.GetBaseValue(StatIds.Strength)})");
            Check("CarryWeight is coherent with current Strength",
                Approx(carryAfter, 50f + restored.GetValue(StatIds.Strength) * 10f));

            UnregisterTestEntityWithPools(restored);
        }

        // ==================================================================
        // Debug feature tests
        // ==================================================================

        [Test, Category("DebugTooling")]
        [Description("GetStatBreakdown reports every pipeline stage and matches GetValue.")]
        public void Test_StatBreakdown()
        {
            Log("--- Debug: Stat Breakdown ---");

            var handle = RegisterTestEntityWithPools();

            // Setup: base 10, +5 flat from sword, +50% percentAdd from potion, +10% percentMult from blessing.
            const long swordId    = 50001;
            const long potionId   = 80001;
            const long blessingId = 90001;

            handle.AddFlat(StatIds.Strength, 5f, swordId);
            StatManager.AddTimedModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, potionId), 60f);
            handle.AddPercentMult(StatIds.Strength, 0.1f, blessingId);

            var breakdown = StatManager.GetStatBreakdown(handle, StatIds.Strength);

            Check("Breakdown statId correct", breakdown.StatId == StatIds.Strength);
            Check("Breakdown statName populated", breakdown.StatName != null);
            Check("Breakdown not derived", !breakdown.IsDerived);
            Check("Breakdown storedBase = 10", Approx(breakdown.StoredBaseValue, 10f));
            Check("Breakdown effectiveBase = 10", Approx(breakdown.EffectiveBase, 10f));
            Check("Breakdown flatTotal = 5", Approx(breakdown.FlatTotal, 5f));
            Check("Breakdown afterFlat = 15", Approx(breakdown.AfterFlat, 15f));
            Check("Breakdown percentAddTotal = 0.5", Approx(breakdown.PercentAddTotal, 0.5f));
            Check("Breakdown afterPercentAdd = 22.5", Approx(breakdown.AfterPercentAdd, 22.5f));
            // 22.5 * 1.1 = 24.75
            Check("Breakdown afterPercentMult = 24.75", Approx(breakdown.AfterPercentMult, 24.75f));
            Check("Breakdown finalValue matches GetValue",
                Approx(breakdown.FinalValue, handle.GetValue(StatIds.Strength)));
            Check("Breakdown has 3 modifiers", breakdown.Modifiers.Length == 3);
            Check("Breakdown has 1 timed modifier", breakdown.TimedModifiers.Length == 1);
            Check($"Timed modifier remaining ~60s", breakdown.TimedModifiers[0].RemainingTime > 59f);

            // Test derived stat breakdown.
            var carryBreakdown = StatManager.GetStatBreakdown(handle, TestCarryWeightId);
            Check("CarryWeight breakdown is derived", carryBreakdown.IsDerived);
            Check("CarryWeight formula result > 0", carryBreakdown.FormulaResult > 0f);
            Check("CarryWeight finalValue matches GetValue",
                Approx(carryBreakdown.FinalValue, handle.GetValue(TestCarryWeightId)));

            // Log the full breakdown to console for visual verification.
            Log($"\n{breakdown}");

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("DebugTooling")]
        [Description("GetEntitySnapshot returns all stats plus pool snapshots for an entity.")]
        public void Test_EntitySnapshot()
        {
            Log("--- Debug: Entity Snapshot ---");

            var handle = RegisterTestEntityWithPools();
            handle.AddFlat(StatIds.Strength, 5f, 50001);
            handle.ApplyDamage(StatIds.MaxHealth, 30f);
            handle.AddShield(StatIds.MaxHealth, 20f);

            var snapshot = StatManager.GetEntitySnapshot(handle);

            Check("Snapshot handle matches", snapshot.Handle == handle);
            Check("Snapshot has stats", snapshot.Stats != null && snapshot.Stats.Length > 0);
            Check("Snapshot has pools", snapshot.Pools != null && snapshot.Pools.Length > 0);

            // Find the health pool in the snapshot.
            bool foundHealthPool = false;
            for (int i = 0; i < snapshot.Pools.Length; i++)
            {
                if (snapshot.Pools[i].PoolId == StatIds.MaxHealth)
                {
                    Check("Pool current = 70", Approx(snapshot.Pools[i].Current, 70f));
                    Check("Pool max = 100", Approx(snapshot.Pools[i].Max, 100f));
                    Check("Pool shield = 20", Approx(snapshot.Pools[i].Shield, 20f));
                    foundHealthPool = true;
                }
            }
            Check("Health pool found in snapshot", foundHealthPool);

            // Log the full snapshot for visual verification.
            Log($"\n{snapshot}");

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("DebugTooling")]
        [Description("DiffSaveData/DiffPoolData detect base changes, added modifiers, and pool changes between snapshots.")]
        public void Test_SnapshotDiff()
        {
            Log("--- Debug: Snapshot Diff ---");

            var handle = RegisterTestEntityWithPools();

            // Capture "before" state.
            var beforeStats = StatManager.CaptureEntityState(handle);
            var beforePools = ResourcePoolManager.CaptureEntityState(handle);

            // Make changes: buff strength, take damage.
            const long buffId = 55001;
            handle.AddFlat(StatIds.Strength, 8f, buffId);
            handle.SetBaseValue(StatIds.MoveSpeed, 7f);
            handle.ApplyDamage(StatIds.MaxHealth, 40f);
            handle.AddShield(StatIds.MaxHealth, 15f);

            // Capture "after" state.
            var afterStats = StatManager.CaptureEntityState(handle);
            var afterPools = ResourcePoolManager.CaptureEntityState(handle);

            // Diff.
            var statDiff = StatDebug.DiffSaveData(beforeStats, afterStats);
            var poolDiff = StatDebug.DiffPoolData(beforePools, afterPools);

            Check("Stat diff has changes", statDiff.HasChanges);
            Check("Pool diff has changes", poolDiff.HasChanges);

            // Verify the stat diff found the Strength modifier addition.
            bool foundStrDiff = false;
            if (statDiff.StatDiffs != null)
            {
                for (int i = 0; i < statDiff.StatDiffs.Length; i++)
                {
                    if (statDiff.StatDiffs[i].StatId == StatIds.Strength)
                    {
                        Check("Strength diff has added modifier",
                            statDiff.StatDiffs[i].Added != null && statDiff.StatDiffs[i].Added.Length > 0);
                        foundStrDiff = true;
                    }
                }
            }
            Check("Found Strength in stat diff", foundStrDiff);

            // Verify MoveSpeed base change detected.
            bool foundMoveDiff = false;
            if (statDiff.StatDiffs != null)
            {
                for (int i = 0; i < statDiff.StatDiffs.Length; i++)
                {
                    if (statDiff.StatDiffs[i].StatId == StatIds.MoveSpeed)
                    {
                        Check("MoveSpeed base changed 5 -> 7",
                            Approx(statDiff.StatDiffs[i].OldBase, 5f) &&
                            Approx(statDiff.StatDiffs[i].NewBase, 7f));
                        foundMoveDiff = true;
                    }
                }
            }
            Check("Found MoveSpeed in stat diff", foundMoveDiff);

            // Verify health pool diff.
            bool foundHpDiff = false;
            if (poolDiff.PoolDiffs != null)
            {
                for (int i = 0; i < poolDiff.PoolDiffs.Length; i++)
                {
                    if (poolDiff.PoolDiffs[i].PoolId == StatIds.MaxHealth)
                    {
                        Check("HP current changed",
                            Approx(poolDiff.PoolDiffs[i].OldCurrent, 100f) &&
                            Approx(poolDiff.PoolDiffs[i].NewCurrent, 60f));
                        Check("HP shield changed",
                            Approx(poolDiff.PoolDiffs[i].OldShield, 0f) &&
                            Approx(poolDiff.PoolDiffs[i].NewShield, 15f));
                        foundHpDiff = true;
                    }
                }
            }
            Check("Found MaxHealth in pool diff", foundHpDiff);

            // Log the diffs for visual verification.
            Log($"\n{statDiff}");
            Log($"\n{poolDiff}");

            UnregisterTestEntityWithPools(handle);
        }

        // ==================================================================
        // Bulk registration tests
        // ==================================================================

        [Test, Category("BulkOps")]
        [Description("RegisterEntities/UnregisterEntities register and tear down many entities in one batched call.")]
        public void Test_BulkRegistration()
        {
            Log("--- Bulk Registration ---");

            const int count = 500;
            Span<EntityStatHandle> handles = new EntityStatHandle[count];

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Bulk register stats.
            int registered = StatManager.RegisterEntities(count, TestStatIds, handles);
            long statMs = sw.ElapsedMilliseconds;

            Check($"Registered {count} stat entities", registered == count);

            // Bulk register pools.
            ResourcePoolManager.RegisterEntities(handles);
            long poolMs = sw.ElapsedMilliseconds - statMs;

            sw.Stop();

            // Verify all handles are valid and distinct.
            bool allValid = true;
            var seenIndices = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (!handles[i].IsValid) { allValid = false; break; }
                seenIndices.Add(handles[i].Index);
            }
            Check("All handles valid", allValid);
            Check("All handles distinct", seenIndices.Count == count);

            // Verify stats are correct on a sample.
            Check("First entity Strength = 10",
                Approx(handles[0].GetValue(StatIds.Strength), 10f));
            Check("Last entity Strength = 10",
                Approx(handles[count - 1].GetValue(StatIds.Strength), 10f));

            // Verify derived stats evaluated.
            float carry = handles[0].GetValue(TestCarryWeightId);
            Check($"First entity CarryWeight = 150, got {carry}", Approx(carry, 150f));

            // Verify pools.
            Check("First entity has health pool",
                ResourcePoolManager.HasPool(handles[0], StatIds.MaxHealth));
            Check("First entity HP = 100",
                Approx(handles[0].GetCurrent(StatIds.MaxHealth), 100f));
            Check("Last entity has health pool",
                ResourcePoolManager.HasPool(handles[count - 1], StatIds.MaxHealth));

            // Verify modifiers work on bulk-registered entities.
            handles[0].AddFlat(StatIds.Strength, 5f, 77001);
            Check("Modifier works on bulk entity",
                Approx(handles[0].GetValue(StatIds.Strength), 15f));
            handles[0].RemoveAllFromSource(77001);

            // Compare timing: bulk vs single.
            sw.Restart();
            var singleHandles = new EntityStatHandle[count];
            for (int i = 0; i < count; i++)
            {
                singleHandles[i] = RegisterTestEntity();
                ResourcePoolManager.RegisterEntity(singleHandles[i]);
            }
            long singleRegMs = sw.ElapsedMilliseconds;

            // Add timed modifiers to some entities so unregister has work to do.
            for (int i = 0; i < count; i += 5)
            {
                StatManager.AddTimedModifier(handles[i], StatIds.Strength,
                    new StatModifier(1f, ModifierType.Flat, i + 30000), 60f);
                StatManager.AddTimedModifier(singleHandles[i], StatIds.Strength,
                    new StatModifier(1f, ModifierType.Flat, i + 40000), 60f);
            }

            // Bulk unregister.
            sw.Restart();
            ResourcePoolManager.UnregisterEntities(handles);
            StatManager.UnregisterEntities(handles);
            long bulkUnregMs = sw.ElapsedMilliseconds;

            // Single unregister.
            sw.Restart();
            for (int i = 0; i < count; i++)
            {
                ResourcePoolManager.UnregisterEntity(singleHandles[i]);
                StatManager.UnregisterEntity(singleHandles[i]);
            }
            long singleUnregMs = sw.ElapsedMilliseconds;
            sw.Stop();

            Log($"  Bulk register: {statMs}ms stats + {poolMs}ms pools = {statMs + poolMs}ms");
            Log($"  Single register: {singleRegMs}ms");
            Log($"  Bulk unregister: {bulkUnregMs}ms");
            Log($"  Single unregister: {singleUnregMs}ms");

            Check("All cleaned up", StatManager.ActiveEntityCount == 0);
        }

        // ==================================================================
        // Delta serialization tests
        // ==================================================================

        [Test, Category("Serialization")]
        [Description("Delta capture emits only changed stats/pools; sparse restore leaves untouched stats at defaults.")]
        public void Test_DeltaSerialization()
        {
            Log("--- Delta Serialization ---");

            // === Untouched entity should produce empty delta ===

            var pristine = RegisterTestEntityWithPools();

            Check("Pristine entity has no stat changes", !StatManager.HasStatChanges(pristine));
            Check("Pristine entity has no pool changes", !ResourcePoolManager.HasPoolChanges(pristine));

            var statDelta = StatManager.CaptureEntityStateDelta(pristine);
            var poolDelta = ResourcePoolManager.CaptureEntityStateDelta(pristine);

            Check("Delta stat entries = 0", statDelta.Stats.Length == 0);
            Check("Delta timed entries = 0", statDelta.TimedModifiers.Length == 0);
            Check("Delta pool entries = 0", poolDelta.Pools.Length == 0);

            UnregisterTestEntityWithPools(pristine);

            // === Modified entity should produce sparse delta ===

            var modified = RegisterTestEntityWithPools();
            const long swordId = 50001;

            // Modify Strength (base change + modifier) and take HP damage.
            modified.SetBaseValue(StatIds.Strength, 15f);
            modified.AddFlat(StatIds.Strength, 5f, swordId);
            modified.ApplyDamage(StatIds.MaxHealth, 30f);
            modified.AddShield(StatIds.MaxHealth, 10f);

            Check("Modified entity has stat changes", StatManager.HasStatChanges(modified));
            Check("Modified entity has pool changes", ResourcePoolManager.HasPoolChanges(modified));

            var modStatDelta = StatManager.CaptureEntityStateDelta(modified);
            var modPoolDelta = ResourcePoolManager.CaptureEntityStateDelta(modified);

            // Only Strength should be in the stat delta (base changed + has modifier).
            // MaxHealth, PhysDamage, MoveSpeed, derived stats — all at defaults with no modifiers.
            Check("Delta has 1 stat entry (Strength only)", modStatDelta.Stats.Length == 1);
            Check("Delta stat entry is Strength", modStatDelta.Stats[0].StatId == StatIds.Strength);
            Check("Delta Strength base = 15", Approx(modStatDelta.Stats[0].BaseValue, 15f));
            Check("Delta Strength has 1 modifier", modStatDelta.Stats[0].Modifiers.Length == 1);

            // Only MaxHealth pool should be in pool delta (damaged + has shield).
            // MaxStamina is at full with no shield.
            Check("Delta has 1 pool entry (MaxHealth only)", modPoolDelta.Pools.Length == 1);
            Check("Delta pool is MaxHealth", modPoolDelta.Pools[0].PoolId == StatIds.MaxHealth);
            Check("Delta pool current = 70", Approx(modPoolDelta.Pools[0].CurrentValue, 70f));
            Check("Delta pool shield = 10", Approx(modPoolDelta.Pools[0].ShieldValue, 10f));

            // === Delta restore round-trip ===

            // Capture values for comparison.
            float strBefore   = modified.GetValue(StatIds.Strength);
            float hpCurBefore = modified.GetCurrent(StatIds.MaxHealth);
            float shieldBefore = modified.GetShield(StatIds.MaxHealth);
            float carryBefore = modified.GetValue(TestCarryWeightId);

            // Serialize delta to JSON.
            string statJson = UnityEngine.JsonUtility.ToJson(modStatDelta);
            string poolJson = UnityEngine.JsonUtility.ToJson(modPoolDelta);

            // Compare sizes.
            var fullStatData = StatManager.CaptureEntityState(modified);
            var fullPoolData = ResourcePoolManager.CaptureEntityState(modified);
            string fullStatJson = UnityEngine.JsonUtility.ToJson(fullStatData);
            string fullPoolJson = UnityEngine.JsonUtility.ToJson(fullPoolData);

            Log($"  Stat JSON: delta={statJson.Length} chars, full={fullStatJson.Length} chars " +
                $"({100 * statJson.Length / fullStatJson.Length}% of full)");
            Log($"  Pool JSON: delta={poolJson.Length} chars, full={fullPoolJson.Length} chars " +
                $"({100 * poolJson.Length / fullPoolJson.Length}% of full)");

            Check("Delta stat JSON smaller than full", statJson.Length < fullStatJson.Length);
            Check("Delta pool JSON smaller than full", poolJson.Length < fullPoolJson.Length);

            // Destroy and restore from delta.
            UnregisterTestEntityWithPools(modified);

            var loaded = RegisterTestEntityWithPools();
            var loadedStatDelta = UnityEngine.JsonUtility.FromJson<StatManager.FullEntitySaveData>(statJson);
            var loadedPoolDelta = UnityEngine.JsonUtility.FromJson<ResourcePoolManager.EntityPoolSaveData>(poolJson);
            StatManager.RestoreEntityState(loaded, loadedStatDelta);
            ResourcePoolManager.RestoreEntityState(loaded, loadedPoolDelta);

            // Verify modified values restored correctly.
            Check("STR matches after delta restore", Approx(loaded.GetValue(StatIds.Strength), strBefore));
            Check("HP current matches", Approx(loaded.GetCurrent(StatIds.MaxHealth), hpCurBefore));
            Check("Shield matches", Approx(loaded.GetShield(StatIds.MaxHealth), shieldBefore));
            Check("CarryWeight cascaded correctly", Approx(loaded.GetValue(TestCarryWeightId), carryBefore));

            // Verify unmodified stats are at defaults (not corrupted by sparse restore).
            Check("MoveSpeed at default", Approx(loaded.GetValue(StatIds.MoveSpeed), 5f));
            Check("MaxStamina pool at full",
                Approx(loaded.GetCurrent(StatIds.MaxStamina), loaded.GetPoolMax(StatIds.MaxStamina)));

            UnregisterTestEntityWithPools(loaded);
        }

        // ==================================================================
        // Custom evaluator tests
        // ==================================================================

        [Test, Category("DerivedStats")]
        [Description("A C# custom evaluator overrides the postfix formula, cascades, stacks modifiers, and reverts on removal.")]
        public void Test_CustomEvaluator()
        {
            Log("--- Custom Evaluator ---");

            // Register a custom evaluator for CarryWeight that overrides the postfix formula.
            // Custom: CarryWeight = 30 + (Strength * 5) — different from the postfix (50 + Strength * 10).
            StatManager.RegisterCustomEvaluator(TestCarryWeightId, query =>
            {
                float str = query.GetValue(StatIds.Strength);
                return 30f + str * 5f;
            });

            Check("Has custom evaluator", StatManager.HasCustomEvaluator(TestCarryWeightId));

            var handle = RegisterTestEntity();

            // Strength base = 10. Custom: 30 + 10*5 = 80 (not 150 from postfix).
            float carry = handle.GetValue(TestCarryWeightId);
            Check($"Custom evaluator: CarryWeight = 80, got {carry}", Approx(carry, 80f));

            // Dependency cascade still works: buff Strength -> CarryWeight recalculates.
            handle.AddFlat(StatIds.Strength, 10f, 77001);
            // Strength = 20. Custom: 30 + 20*5 = 130.
            carry = handle.GetValue(TestCarryWeightId);
            Check($"Custom cascade: CarryWeight = 130, got {carry}", Approx(carry, 130f));

            // Modifiers on the derived stat itself still stack on top.
            handle.AddFlat(TestCarryWeightId, 20f, 77002);
            // Custom base: 130 + 20 flat = 150.
            carry = handle.GetValue(TestCarryWeightId);
            Check($"Custom + modifier: CarryWeight = 150, got {carry}", Approx(carry, 150f));

            handle.RemoveAllFromSource(77001);
            handle.RemoveAllFromSource(77002);

            // Breakdown should report as derived.
            var breakdown = StatManager.GetStatBreakdown(handle, TestCarryWeightId);
            Check("Breakdown shows derived", breakdown.IsDerived);
            Check("Breakdown formula result = 80", Approx(breakdown.FormulaResult, 80f));

            StatManager.UnregisterEntity(handle);

            // Remove custom evaluator — falls back to postfix formula.
            StatManager.RemoveCustomEvaluator(TestCarryWeightId);
            Check("Custom evaluator removed", !StatManager.HasCustomEvaluator(TestCarryWeightId));

            // Verify fallback works.
            var handle2 = RegisterTestEntity();
            carry = handle2.GetValue(TestCarryWeightId);
            Check($"Fallback to postfix: CarryWeight = 150, got {carry}", Approx(carry, 150f));

            StatManager.UnregisterEntity(handle2);
        }

        // ==================================================================
        // Robustness: registration validation & bookkeeping
        // ==================================================================

        [Test, Category("Robustness")]
        [Description("RegisterStatDefinition and Validate reject malformed postfix formulas (underflow, leftover operands).")]
        public void Test_InvalidFormulaRejected()
        {
            Log("--- Robustness: Invalid Formula Rejected ---");
            // Rejecting bad data must BOTH return false and say why. Capturing StatLog
            // asserts the diagnostic instead of leaving it in the console as noise.
            using var log = new LogCapture();

            // [Add] with nothing on the stack underflows — must be rejected at registration.
            var underflow = CreateDef(TestInvalidFormulaId, "Bad Underflow", 0f,
                dependencies: new[] { StatIds.Strength },
                formula: new[] { StatFormulaOp.Add() });
            Check("Underflow formula rejected",
                !StatManager.RegisterStatDefinition(underflow));
            Check("Underflow rejection reports the stat and the reason",
                log.HasError($"stat {TestInvalidFormulaId}", "invalid formula"));

            // A formula that leaves two values on the stack (missing an operator) is invalid.
            var leftover = CreateDef(TestInvalidFormulaId, "Bad Leftover", 0f,
                dependencies: System.Array.Empty<int>(),
                formula: new[] { StatFormulaOp.Const(1f), StatFormulaOp.Const(2f) });
            Check("Formula leaving 2 stack values rejected",
                !StatManager.RegisterStatDefinition(leftover));
            Check("Both rejections were reported, not just the first",
                log.ErrorCount == 2);

            // The static Validate API reports the failure with a message.
            bool badValid = StatFormulaEvaluator.Validate(new[] { StatFormulaOp.Add() }, out string error);
            Check("Validate() returns false for a bad formula", !badValid);
            Check("Validate() provides an error message", !string.IsNullOrEmpty(error));

            // A well-formed formula still validates and registers.
            bool goodValid = StatFormulaEvaluator.Validate(new[]
            {
                StatFormulaOp.Stat(StatIds.Strength),
                StatFormulaOp.Const(2f),
                StatFormulaOp.Mul(),
            }, out _);
            Check("Validate() returns true for a good formula", goodValid);
        }

        [Test, Category("Robustness")]
        [Description("Registration rejects dependency edges that would create a cycle, including self-dependency.")]
        public void Test_DependencyCycleRejected()
        {
            Log("--- Robustness: Dependency Cycle Rejected ---");
            // Each rejection must name the offending edge, not just return false. The
            // capture holds the diagnostics so they can be asserted rather than printed.
            using var log = new LogCapture();

            // A depends on B — accepted on its own (nothing depends on A yet).
            var cycleA = CreateDef(TestCycleAId, "Cycle A", 0f,
                dependencies: new[] { TestCycleBId },
                formula: new[]
                {
                    StatFormulaOp.Stat(TestCycleBId),
                    StatFormulaOp.Const(1f),
                    StatFormulaOp.Mul(),
                });
            Check("First edge (A depends on B) accepted",
                StatManager.RegisterStatDefinition(cycleA));
            Check("Accepted registration is silent", log.ErrorCount == 0);

            // B depends on A — closes the loop, must be rejected as a cycle.
            var cycleB = CreateDef(TestCycleBId, "Cycle B", 0f,
                dependencies: new[] { TestCycleAId },
                formula: new[]
                {
                    StatFormulaOp.Stat(TestCycleAId),
                    StatFormulaOp.Const(1f),
                    StatFormulaOp.Mul(),
                });
            Check("Closing edge (B depends on A) rejected as a cycle",
                !StatManager.RegisterStatDefinition(cycleB));
            Check("Cycle rejection names the offending edge",
                log.HasError($"stat {TestCycleBId}",
                             $"dependency on {TestCycleAId}",
                             "dependency cycle"));

            // Self-dependency is a degenerate cycle and must also be rejected.
            var selfDep = CreateDef(TestSelfDepId, "Self Dep", 0f,
                dependencies: new[] { TestSelfDepId },
                formula: new[]
                {
                    StatFormulaOp.Stat(TestSelfDepId),
                    StatFormulaOp.Const(1f),
                    StatFormulaOp.Mul(),
                });
            Check("Self-dependency rejected",
                !StatManager.RegisterStatDefinition(selfDep));
            Check("Self-dependency rejection names the stat as its own dependency",
                log.HasError($"stat {TestSelfDepId}",
                             $"dependency on {TestSelfDepId}",
                             "dependency cycle"));
        }

        [Test, Category("TimedModifiers")]
        [Description("RemoveAllFromSource also purges the matching timer so no ghost countdown remains.")]
        public void Test_RemoveAllFromSourceClearsTimer()
        {
            Log("--- Robustness: RemoveAllFromSource Clears Same-Source Timer ---");

            var handle = RegisterTestEntity();
            const long buffId = 88001;

            // Baseline is captured so the test doesn't depend on global timer state.
            int timerBaseline = StatManager.TimedModifierCount;

            // A timed buff and a permanent modifier, both from the SAME source.
            StatManager.AddTimedModifier(handle, StatIds.Strength,
                new StatModifier(0.5f, ModifierType.PercentAdd, buffId), 100f);
            handle.AddFlat(StatIds.MaxHealth, 20f, buffId);

            Check("Timer registered", StatManager.TimedModifierCount == timerBaseline + 1);
            Check("Strength buffed to 15", Approx(handle.GetValue(StatIds.Strength), 15f));
            Check("MaxHealth buffed to 120", Approx(handle.GetValue(StatIds.MaxHealth), 120f));

            // RemoveAllFromSource must strip BOTH the modifiers and the timer bookkeeping.
            int removed = handle.RemoveAllFromSource(buffId);
            Check("Removed 2 modifiers", removed == 2);
            Check("Timer bookkeeping cleared (no ghost timer)",
                StatManager.TimedModifierCount == timerBaseline);
            Check("Strength back to base 10", Approx(handle.GetValue(StatIds.Strength), 10f));
            Check("MaxHealth back to base 100", Approx(handle.GetValue(StatIds.MaxHealth), 100f));

            // Ticking afterward is a no-op and must not resurrect the buff.
            StatManager.TickTimedModifiers(200f);
            Check("Strength still base after tick", Approx(handle.GetValue(StatIds.Strength), 10f));
            Check("Timer count unchanged after tick",
                StatManager.TimedModifierCount == timerBaseline);

            StatManager.UnregisterEntity(handle);
        }

        [Test, Category("Events")]
        [Description("OnShieldChanged and OnPoolDepleted fire correctly; depletion fires once on the zero transition.")]
        public void Test_PoolEvents()
        {
            Log("--- Resource Pool: Shield & Depletion Events ---");

            var handle = RegisterTestEntityWithPools();

            int shieldEvents = 0;
            int depletedEvents = 0;
            float lastShieldNew = 0f;
            int lastDepletedPool = 0;

            Action<EntityStatHandle, int, float, float> onShield =
                (h, pool, o, n) => { if (pool == StatIds.MaxHealth) { shieldEvents++; lastShieldNew = n; } };
            Action<EntityStatHandle, int> onDepleted =
                (h, pool) => { if (pool == StatIds.MaxHealth) { depletedEvents++; lastDepletedPool = pool; } };

            ResourcePoolManager.OnShieldChanged += onShield;
            ResourcePoolManager.OnPoolDepleted  += onDepleted;

            // AddShield fires OnShieldChanged (0 -> 40).
            handle.AddShield(StatIds.MaxHealth, 40f);
            Check("Shield event on AddShield", shieldEvents == 1);
            Check("Shield event new value = 40", Approx(lastShieldNew, 40f));

            // Damage that only touches shield fires OnShieldChanged (40 -> 15), no depletion.
            handle.ApplyDamage(StatIds.MaxHealth, 25f);
            Check("Shield event on shield-only damage", shieldEvents == 2);
            Check("Shield reduced to 15", Approx(lastShieldNew, 15f));
            Check("No depletion yet", depletedEvents == 0);

            // Deplete: current 100 + shield 15 = 115; deal 200.
            handle.ApplyDamage(StatIds.MaxHealth, 200f);
            Check("Depletion event fired", depletedEvents == 1);
            Check("Depleted pool is MaxHealth", lastDepletedPool == StatIds.MaxHealth);
            Check("Current is 0 after overkill", Approx(handle.GetCurrent(StatIds.MaxHealth), 0f));

            // Further damage while already at 0 must NOT re-fire depletion.
            handle.ApplyDamage(StatIds.MaxHealth, 10f);
            Check("No duplicate depletion when already depleted", depletedEvents == 1);

            // ClearShield on empty shield fires nothing.
            int shieldEventsBefore = shieldEvents;
            handle.ClearShield(StatIds.MaxHealth);
            Check("ClearShield on empty shield: no event", shieldEvents == shieldEventsBefore);

            ResourcePoolManager.OnShieldChanged -= onShield;
            ResourcePoolManager.OnPoolDepleted  -= onDepleted;

            UnregisterTestEntityWithPools(handle);
        }

        [Test, Category("DynamicRegistration")]
        [Description("OverrideStatDefinition replaces an existing definition and re-clamps live entities; unregistered overrides are rejected.")]
        public void Test_OverrideDefinition()
        {
            Log("--- Robustness: Override Stat Definition ---");
            // The rejected-override path must explain itself; the capture asserts that,
            // and that the successful override path stays quiet.
            using var log = new LogCapture();

            // Register a fresh stat: base 10, clamped to a max of 50.
            var original = CreateDef(TestOverrideId, "Overridable", 10f, hasMax: true, max: 50f);
            Check("Original registered", StatManager.RegisterStatDefinition(original));

            // An entity registered under the ORIGINAL definition, pushed past the old max.
            int[] stats = { TestOverrideId };
            var existing = StatManager.RegisterEntity(stats);
            existing.AddFlat(TestOverrideId, 1000f, 70001);
            Check("Clamped at original max 50", Approx(existing.GetValue(TestOverrideId), 50f));

            // Override: raise the max to 200. Constraints apply to live entities immediately.
            var raised = CreateDef(TestOverrideId, "Overridable", 10f, hasMax: true, max: 200f);
            Check("Override succeeds", StatManager.OverrideStatDefinition(raised));
            Check("Existing entity re-clamps to new max 200",
                Approx(existing.GetValue(TestOverrideId), 200f));

            // A NEW entity also honors the overridden definition.
            var fresh = StatManager.RegisterEntity(stats);
            fresh.AddFlat(TestOverrideId, 1000f, 70002);
            Check("New entity clamps at new max 200", Approx(fresh.GetValue(TestOverrideId), 200f));
            Check("Nothing logged so far (the happy path is silent)", log.ErrorCount == 0);

            // Overriding a stat that was never registered is rejected.
            var ghost = CreateDef(TestOverrideGhostId, "Ghost", 0f);
            Check("Override of unregistered stat rejected",
                !StatManager.OverrideStatDefinition(ghost));
            Check("Rejected override explains the cause",
                log.HasError($"stat {TestOverrideGhostId}", "is not registered"));

            StatManager.UnregisterEntity(existing);
            StatManager.UnregisterEntity(fresh);
        }

        // ==================================================================
        // Common utilities
        // ==================================================================

        private void OnStatChanged(EntityStatHandle handle, int statId, float oldVal, float newVal)
        {
            _changeLog.Add((handle, statId, oldVal, newVal));
        }

        private static bool Approx(float a, float b, float tolerance = 0.001f)
        {
            return Mathf.Abs(a - b) < tolerance;
        }

        /// <summary>
        /// Redirects <see cref="StatLog"/> into a buffer for the lifetime of the scope, so
        /// error-path tests can assert on the diagnostic the system emits instead of letting
        /// it reach the Unity console — where it reads as a failure even when the test passes.
        ///
        /// Usage: <c>using var log = new LogCapture();</c> at the top of the test, then
        /// <c>log.HasError(...)</c> / <c>log.ErrorCount</c> after the call under test.
        /// </summary>
        private sealed class LogCapture : IDisposable
        {
            private readonly List<(LogType Type, string Message)> _entries = new();

            public LogCapture()
            {
                StatLog.Handler = (type, message) => _entries.Add((type, message));
            }

            /// <summary>How many errors have been captured so far.</summary>
            public int ErrorCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < _entries.Count; i++)
                        if (_entries[i].Type == LogType.Error) count++;
                    return count;
                }
            }

            /// <summary>
            /// True if some captured error contains every one of the given fragments.
            /// Fragment matching rather than whole-message equality, so a reworded
            /// diagnostic doesn't fail the test as long as it still identifies the cause.
            /// </summary>
            public bool HasError(params string[] fragments) => Has(LogType.Error, fragments);

            /// <summary>True if some captured warning contains every one of the fragments.</summary>
            public bool HasWarning(params string[] fragments) => Has(LogType.Warning, fragments);

            private bool Has(LogType type, string[] fragments)
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_entries[i].Type != type) continue;

                    bool all = true;
                    for (int f = 0; f < fragments.Length; f++)
                    {
                        if (_entries[i].Message.Contains(fragments[f])) continue;
                        all = false;
                        break;
                    }

                    if (all) return true;
                }
                return false;
            }

            /// <summary>Dumps everything captured — handy when an assertion here fails.</summary>
            public override string ToString()
            {
                if (_entries.Count == 0) return "LogCapture: (nothing captured)";

                var sb = new System.Text.StringBuilder("LogCapture:\n");
                for (int i = 0; i < _entries.Count; i++)
                    sb.Append("  [").Append(_entries[i].Type).Append("] ")
                      .AppendLine(_entries[i].Message);
                return sb.ToString();
            }

            public void Dispose() => StatLog.Reset();
        }

        /// <summary>Bridges the suite's (message, condition) assertion style onto NUnit.</summary>
        private static void Check(string testName, bool condition)
        {
            NUnit.Framework.Assert.IsTrue(condition, testName);
        }

        private static void Log(string msg)
        {
            Debug.Log($"[StatTests] {msg}");
        }
    }
}
