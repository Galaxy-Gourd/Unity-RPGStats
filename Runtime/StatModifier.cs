using System;
using UnityEngine;

namespace RPG.Stats
{
    public enum ModifierType : byte
    {
        Flat        = 0,
        PercentAdd  = 1,
        PercentMult = 2,
    }

    /// <summary>
    /// Value type representing a single stat modification.
    /// Immutable from the public API. Uses plain serialized fields internally
    /// so JsonUtility (and other serializers) can deserialize correctly.
    /// Designed for storage in flat arrays with zero heap allocation.
    /// </summary>
    [Serializable]
    public struct StatModifier : IEquatable<StatModifier>
    {
        [SerializeField] private float        _value;
        [SerializeField] private ModifierType _type;
        [SerializeField] private int          _order;
        [SerializeField] private long         _sourceId;

        public float        Value    => _value;
        public ModifierType Type     => _type;
        public int          Order    => _order;
        public long         SourceId => _sourceId;

        public StatModifier(float value, ModifierType type, int order, long sourceId)
        {
            _value    = value;
            _type     = type;
            _order    = order;
            _sourceId = sourceId;
        }

        public StatModifier(float value, ModifierType type, long sourceId)
            : this(value, type, 0, sourceId) { }

        public bool Equals(StatModifier other) =>
            _value.Equals(other._value) && _type == other._type &&
            _order == other._order && _sourceId == other._sourceId;

        public override bool Equals(object obj) => obj is StatModifier other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_value, _type, _order, _sourceId);

        public override string ToString() =>
            $"[{_type}] {(_value >= 0 ? "+" : "")}{_value} (src:{_sourceId}, ord:{_order})";
    }
}