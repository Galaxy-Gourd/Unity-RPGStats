using System;
using System.Collections.Generic;
using System.Text;

namespace RPG.Stats
{
    // ------------------------------------------------------------------
    // Debug data structures
    // ------------------------------------------------------------------

    /// <summary>
    /// Full breakdown of how a single stat's value was computed.
    /// Returned by StatManager.GetStatBreakdown(). Allocates — intended
    /// for debug tools, editor overlays, and console commands, not hot paths.
    /// </summary>
    public struct StatBreakdown
    {
        public int     StatId;
        public string  StatName;         // from StatDefinition.DisplayName, or null
        public bool    IsDerived;

        // Pipeline stages
        public float   StoredBaseValue;   // the raw base value in the slot
        public float   FormulaResult;     // derived stat formula output (0 for non-derived)
        public float   EffectiveBase;     // storedBase + formulaResult (input to modifier pipeline)

        public float   FlatTotal;         // sum of all flat modifiers
        public float   AfterFlat;         // effectiveBase + flatTotal

        public float   PercentAddTotal;   // sum of all PercentAdd values
        public float   AfterPercentAdd;   // afterFlat * (1 + percentAddTotal)

        public float   AfterPercentMult;  // after each PercentMult applied sequentially

        public float   FinalValue;        // after constraints (clamp, round)

        // Modifier detail
        public StatModifier[] Modifiers;

        // Timed modifier detail (subset of Modifiers that have active countdowns)
        public TimedModifierInfo[] TimedModifiers;

        public override string ToString()
        {
            var sb = new StringBuilder(256);
            sb.AppendLine($"=== {StatName ?? $"Stat#{StatId}"} (id:{StatId}) ===");
            sb.AppendLine($"  Stored base:     {StoredBaseValue}");
            if (IsDerived)
                sb.AppendLine($"  Formula result:  {FormulaResult}");
            sb.AppendLine($"  Effective base:  {EffectiveBase}");

            if (Modifiers != null && Modifiers.Length > 0)
            {
                sb.AppendLine($"  Modifiers ({Modifiers.Length}):");
                for (int i = 0; i < Modifiers.Length; i++)
                {
                    var m = Modifiers[i];
                    string timed = "";
                    if (TimedModifiers != null)
                    {
                        for (int t = 0; t < TimedModifiers.Length; t++)
                        {
                            if (TimedModifiers[t].Modifier.Equals(m))
                            {
                                timed = $" [{TimedModifiers[t].RemainingTime:F1}s remaining]";
                                break;
                            }
                        }
                    }
                    sb.AppendLine($"    [{m.Type}] {(m.Value >= 0 ? "+" : "")}{m.Value} " +
                                  $"(src:{m.SourceId}, ord:{m.Order}){timed}");
                }
            }

            sb.AppendLine($"  After flat:      {AfterFlat} (+{FlatTotal})");
            sb.AppendLine($"  After %add:      {AfterPercentAdd} (×{1f + PercentAddTotal:F3})");
            sb.AppendLine($"  After %mult:     {AfterPercentMult}");
            sb.AppendLine($"  Final:           {FinalValue}");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Info about a timed modifier on a specific stat.
    /// </summary>
    public struct TimedModifierInfo
    {
        public StatModifier Modifier;
        public float        RemainingTime;
        public float        TotalDuration;
    }

    /// <summary>
    /// Full snapshot of all stats on an entity, for debug display.
    /// </summary>
    public struct EntityStatSnapshot
    {
        public EntityStatHandle Handle;
        public StatBreakdown[]  Stats;
        public PoolSnapshot[]   Pools;

        public override string ToString()
        {
            var sb = new StringBuilder(1024);
            sb.AppendLine($"=== Entity {Handle} ===");
            if (Stats != null)
                for (int i = 0; i < Stats.Length; i++)
                    sb.Append(Stats[i].ToString());
            if (Pools != null)
                for (int i = 0; i < Pools.Length; i++)
                    sb.AppendLine(Pools[i].ToString());
            return sb.ToString();
        }
    }

    /// <summary>
    /// Snapshot of a single resource pool for debug display.
    /// </summary>
    public struct PoolSnapshot
    {
        public int    PoolId;
        public float  Current;
        public float  Max;
        public float  Shield;
        public float  Ratio;

        public override string ToString() =>
            $"  Pool {PoolId}: {Current:F1} / {Max:F1} + {Shield:F1}sh ({Ratio:P0})";
    }

    // ------------------------------------------------------------------
    // Snapshot diff
    // ------------------------------------------------------------------

    /// <summary>
    /// Difference between two entity snapshots. Shows what changed.
    /// </summary>
    public struct SnapshotDiff
    {
        public StatDiffEntry[] StatDiffs;
        public PoolDiffEntry[] PoolDiffs;

        public bool HasChanges =>
            (StatDiffs != null && StatDiffs.Length > 0) ||
            (PoolDiffs != null && PoolDiffs.Length > 0);

        public override string ToString()
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("=== Snapshot Diff ===");

            if (StatDiffs != null)
            {
                for (int i = 0; i < StatDiffs.Length; i++)
                    sb.AppendLine(StatDiffs[i].ToString());
            }

            if (PoolDiffs != null)
            {
                for (int i = 0; i < PoolDiffs.Length; i++)
                    sb.AppendLine(PoolDiffs[i].ToString());
            }

            if (!HasChanges)
                sb.AppendLine("  (no changes)");

            return sb.ToString();
        }
    }

    public struct StatDiffEntry
    {
        public int    StatId;
        public string StatName;

        public float  OldBase;
        public float  NewBase;

        // Note: final (post-modifier) values are intentionally not diffed here.
        // Save data carries base values + modifiers but not the constraint/formula
        // context needed to recompute a final value offline, so a reliable final
        // diff isn't possible from serialized snapshots alone.

        public int    OldModifierCount;
        public int    NewModifierCount;

        // Modifiers added/removed between snapshots.
        public StatModifier[] Added;
        public StatModifier[] Removed;

        public override string ToString()
        {
            var sb = new StringBuilder(128);
            string name = StatName ?? $"#{StatId}";

            if (OldBase != NewBase)
                sb.AppendLine($"  {name}: base {OldBase} -> {NewBase}");

            if (Added != null)
                for (int i = 0; i < Added.Length; i++)
                    sb.AppendLine($"  {name}: + {Added[i]}");
            if (Removed != null)
                for (int i = 0; i < Removed.Length; i++)
                    sb.AppendLine($"  {name}: - {Removed[i]}");

            return sb.ToString();
        }
    }

    public struct PoolDiffEntry
    {
        public int    PoolId;
        public float  OldCurrent;
        public float  NewCurrent;
        public float  OldShield;
        public float  NewShield;

        public override string ToString()
        {
            var sb = new StringBuilder(64);
            // ReSharper disable CompareOfFloatsByEqualityOperator
            if (OldCurrent != NewCurrent)
                sb.AppendLine($"  Pool {PoolId}: current {OldCurrent:F1} -> {NewCurrent:F1}");
            if (OldShield != NewShield)
                sb.AppendLine($"  Pool {PoolId}: shield {OldShield:F1} -> {NewShield:F1}");
            // ReSharper restore CompareOfFloatsByEqualityOperator
            return sb.ToString();
        }
    }

    // ------------------------------------------------------------------
    // Static debug utilities
    // ------------------------------------------------------------------

    /// <summary>
    /// Static utility methods for debug inspection. All methods allocate
    /// and are intended for tooling, not gameplay hot paths.
    /// </summary>
    public static class StatDebug
    {
        /// <summary>
        /// Diff two save data snapshots. Useful for "what changed between these two saves?"
        /// Works on serialized data — no live entity required.
        /// </summary>
        public static SnapshotDiff DiffSaveData(
            StatManager.FullEntitySaveData before,
            StatManager.FullEntitySaveData after)
        {
            var statDiffs = new List<StatDiffEntry>();

            // Index both by statId.
            var beforeStats = IndexSaveStats(before);
            var afterStats  = IndexSaveStats(after);

            // Check all stats in after (may be new or changed).
            foreach (var kvp in afterStats)
            {
                int statId = kvp.Key;
                var afterEntry = kvp.Value;

                if (beforeStats.TryGetValue(statId, out var beforeEntry))
                {
                    // Stat existed before — check for differences.
                    var diff = DiffStatEntries(statId, beforeEntry, afterEntry);
                    if (diff.HasValue)
                        statDiffs.Add(diff.Value);
                }
                else
                {
                    // New stat.
                    statDiffs.Add(new StatDiffEntry
                    {
                        StatId           = statId,
                        OldBase          = 0f,
                        NewBase          = afterEntry.BaseValue,
                        OldModifierCount = 0,
                        NewModifierCount = afterEntry.Modifiers?.Length ?? 0,
                        Added            = afterEntry.Modifiers,
                    });
                }
            }

            // Check for stats removed.
            foreach (var kvp in beforeStats)
            {
                if (!afterStats.ContainsKey(kvp.Key))
                {
                    var entry = kvp.Value;
                    statDiffs.Add(new StatDiffEntry
                    {
                        StatId           = kvp.Key,
                        OldBase          = entry.BaseValue,
                        NewBase          = 0f,
                        OldModifierCount = entry.Modifiers?.Length ?? 0,
                        NewModifierCount = 0,
                        Removed          = entry.Modifiers,
                    });
                }
            }

            return new SnapshotDiff
            {
                StatDiffs = statDiffs.Count > 0 ? statDiffs.ToArray() : null,
            };
        }

        /// <summary>
        /// Diff two pool save snapshots.
        /// </summary>
        public static SnapshotDiff DiffPoolData(
            ResourcePoolManager.EntityPoolSaveData before,
            ResourcePoolManager.EntityPoolSaveData after)
        {
            var poolDiffs = new List<PoolDiffEntry>();

            var beforePools = IndexSavePools(before);
            var afterPools  = IndexSavePools(after);

            foreach (var kvp in afterPools)
            {
                var a = kvp.Value;
                if (beforePools.TryGetValue(kvp.Key, out var b))
                {
                    // ReSharper disable CompareOfFloatsByEqualityOperator
                    if (b.CurrentValue != a.CurrentValue || b.ShieldValue != a.ShieldValue)
                    {
                        poolDiffs.Add(new PoolDiffEntry
                        {
                            PoolId     = kvp.Key,
                            OldCurrent = b.CurrentValue,
                            NewCurrent = a.CurrentValue,
                            OldShield  = b.ShieldValue,
                            NewShield  = a.ShieldValue,
                        });
                    }
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                }
            }

            return new SnapshotDiff
            {
                PoolDiffs = poolDiffs.Count > 0 ? poolDiffs.ToArray() : null,
            };
        }

        // ------------------------------------------------------------------
        // Internal helpers
        // ------------------------------------------------------------------

        private static Dictionary<int, StatManager.EntityStatSaveData> IndexSaveStats(
            StatManager.FullEntitySaveData data)
        {
            var dict = new Dictionary<int, StatManager.EntityStatSaveData>();
            if (data.Stats == null) return dict;
            for (int i = 0; i < data.Stats.Length; i++)
                dict[data.Stats[i].StatId] = data.Stats[i];
            return dict;
        }

        private static Dictionary<int, ResourcePoolManager.PoolSaveEntry> IndexSavePools(
            ResourcePoolManager.EntityPoolSaveData data)
        {
            var dict = new Dictionary<int, ResourcePoolManager.PoolSaveEntry>();
            if (data.Pools == null) return dict;
            for (int i = 0; i < data.Pools.Length; i++)
                dict[data.Pools[i].PoolId] = data.Pools[i];
            return dict;
        }

        private static StatDiffEntry? DiffStatEntries(
            int statId,
            StatManager.EntityStatSaveData before,
            StatManager.EntityStatSaveData after)
        {
            // ReSharper disable CompareOfFloatsByEqualityOperator
            bool baseChanged = before.BaseValue != after.BaseValue;
            // ReSharper restore CompareOfFloatsByEqualityOperator

            var beforeMods = before.Modifiers ?? Array.Empty<StatModifier>();
            var afterMods  = after.Modifiers  ?? Array.Empty<StatModifier>();

            // Find added and removed modifiers.
            var beforeSet = new List<StatModifier>(beforeMods);
            var afterSet  = new List<StatModifier>(afterMods);

            var added   = new List<StatModifier>();
            var removed = new List<StatModifier>();

            // Match and remove common modifiers.
            for (int i = afterSet.Count - 1; i >= 0; i--)
            {
                int match = -1;
                for (int j = 0; j < beforeSet.Count; j++)
                {
                    if (afterSet[i].Equals(beforeSet[j]))
                    {
                        match = j;
                        break;
                    }
                }

                if (match >= 0)
                {
                    afterSet.RemoveAt(i);
                    beforeSet.RemoveAt(match);
                }
            }

            // Remaining in afterSet are added; remaining in beforeSet are removed.
            added.AddRange(afterSet);
            removed.AddRange(beforeSet);

            if (!baseChanged && added.Count == 0 && removed.Count == 0)
                return null;

            return new StatDiffEntry
            {
                StatId           = statId,
                OldBase          = before.BaseValue,
                NewBase          = after.BaseValue,
                OldModifierCount = beforeMods.Length,
                NewModifierCount = afterMods.Length,
                Added            = added.Count > 0 ? added.ToArray() : null,
                Removed          = removed.Count > 0 ? removed.ToArray() : null,
            };
        }
    }
}