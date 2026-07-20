using System;

namespace RPG.Stats
{
    /// <summary>
    /// Lightweight handle that an entity holds as its reference into the StatManager.
    /// This is what a character, NPC, object, etc. stores instead of a StatContainer class.
    /// 
    /// Contains an index (slot in the manager's arrays) and a generation counter
    /// to detect use-after-free (entity was unregistered, slot recycled).
    /// 
    /// Cost: 8 bytes. No heap allocation. Pass by value.
    /// </summary>
    public readonly struct EntityStatHandle : IEquatable<EntityStatHandle>
    {
        /// <summary>Index into StatManager's entity arrays.</summary>
        public readonly int Index;

        /// <summary>Generation counter. Must match the manager's generation for this slot.</summary>
        public readonly int Generation;

        public bool IsValid => Generation > 0;

        public EntityStatHandle(int index, int generation)
        {
            Index      = index;
            Generation = generation;
        }

        public static readonly EntityStatHandle Invalid = default;

        public bool Equals(EntityStatHandle other) =>
            Index == other.Index && Generation == other.Generation;

        public override bool Equals(object obj) => obj is EntityStatHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(Index, Generation);

        public static bool operator ==(EntityStatHandle a, EntityStatHandle b) => a.Equals(b);
        public static bool operator !=(EntityStatHandle a, EntityStatHandle b) => !a.Equals(b);

        public override string ToString() => $"StatHandle({Index}:{Generation})";
    }
}
