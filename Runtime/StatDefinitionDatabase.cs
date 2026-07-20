using System.Collections.Generic;
using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Compact constraint data extracted from a StatDefinition.
    /// Stored by the manager in a flat lookup for fast clamping during evaluation.
    /// </summary>
    public readonly struct StatConstraints
    {
        public readonly float MinValue;
        public readonly float MaxValue;
        public readonly bool  HasMin;
        public readonly bool  HasMax;
        public readonly bool  RoundToInt;

        public StatConstraints(StatDefinition def)
        {
            MinValue   = def.MinValue;
            MaxValue   = def.MaxValue;
            HasMin     = def.HasMinValue;
            HasMax     = def.HasMaxValue;
            RoundToInt = def.RoundToInt;
        }

        public float Apply(float value)
        {
            if (HasMin && value < MinValue) value = MinValue;
            if (HasMax && value > MaxValue) value = MaxValue;
            if (RoundToInt) value = Mathf.Round(value);
            return value;
        }
    }

    [CreateAssetMenu(fileName = "StatDefinitionDatabase", menuName = "RPG/Stats/Stat Database")]
    public class StatDefinitionDatabase : ScriptableObject
    {
        [SerializeField] private StatDefinition[] definitions;

        private Dictionary<int, StatDefinition> _lookup;
        private Dictionary<int, StatConstraints> _constraints;

        public void Initialize()
        {
            int count = definitions?.Length ?? 0;
            _lookup      = new Dictionary<int, StatDefinition>(count);
            _constraints = new Dictionary<int, StatConstraints>(count);

            if (definitions == null) return;

            for (int i = 0; i < definitions.Length; i++)
            {
                var def = definitions[i];
                if (def == null) continue;

                if (!_lookup.TryAdd(def.StatId, def))
                {
                    StatLog.Error($"StatDatabase: Duplicate statId {def.StatId} " +
                                   $"('{def.DisplayName}' vs '{_lookup[def.StatId].DisplayName}').");
                    continue;
                }

                _constraints[def.StatId] = new StatConstraints(def);
            }
        }

        public bool TryGetDefinition(int statId, out StatDefinition def) =>
            _lookup.TryGetValue(statId, out def);

        public bool TryGetConstraints(int statId, out StatConstraints constraints) =>
            _constraints.TryGetValue(statId, out constraints);

        public StatDefinition GetDefinition(int statId)
        {
            if (TryGetDefinition(statId, out var def)) return def;
            StatLog.Error($"StatDatabase: No definition for statId {statId}.");
            return null;
        }

        public float GetDefaultBaseValue(int statId)
        {
            return TryGetDefinition(statId, out var def) ? def.DefaultBaseValue : 0f;
        }

        public IReadOnlyDictionary<int, StatConstraints> AllConstraints => _constraints;

        /// <summary>All registered definitions. Useful for iteration during bulk registration.</summary>
        public IEnumerable<StatDefinition> All => _lookup?.Values ?? System.Linq.Enumerable.Empty<StatDefinition>();

        /// <summary>
        /// If statId has a formula, returns it. Otherwise null.
        /// </summary>
        public StatFormulaOp[] GetFormula(int statId)
        {
            if (TryGetDefinition(statId, out var def) && def.HasFormula)
                return def.Formula;
            return null;
        }

        public bool RegisterDefinition(StatDefinition definition)
        {
            _lookup      ??= new Dictionary<int, StatDefinition>();
            _constraints ??= new Dictionary<int, StatConstraints>();

            if (!_lookup.TryAdd(definition.StatId, definition)) return false;
            _constraints[definition.StatId] = new StatConstraints(definition);
            return true;
        }

        /// <summary>
        /// Replace the definition (and cached constraints) for an already-registered stat.
        /// Returns false if the stat ID isn't registered. Used by
        /// StatManager.OverrideStatDefinition for mod rebalancing.
        /// </summary>
        public bool OverrideDefinition(StatDefinition definition)
        {
            if (_lookup == null || !_lookup.ContainsKey(definition.StatId)) return false;
            _lookup[definition.StatId]      = definition;
            _constraints[definition.StatId] = new StatConstraints(definition);
            return true;
        }
    }
}