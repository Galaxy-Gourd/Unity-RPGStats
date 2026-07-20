namespace RPG.Stats
{
    /// <summary>
    /// Extension methods on EntityStatHandle for clean call-site syntax.
    /// These are thin wrappers around StatManager static methods, so the handle
    /// reads like an instance: handle.GetValue(StatIds.Strength)
    /// </summary>
    public static class StatManagerExtensions
    {
        public static float GetValue(this EntityStatHandle handle, int statId) =>
            StatManager.GetValue(handle, statId);

        public static float GetBaseValue(this EntityStatHandle handle, int statId) =>
            StatManager.GetBaseValue(handle, statId);

        public static int GetValueInt(this EntityStatHandle handle, int statId) =>
            UnityEngine.Mathf.RoundToInt(StatManager.GetValue(handle, statId));

        public static bool HasStat(this EntityStatHandle handle, int statId) =>
            StatManager.HasStat(handle, statId);

        public static void SetBaseValue(this EntityStatHandle handle, int statId, float value) =>
            StatManager.SetBaseValue(handle, statId, value);

        public static void AddFlat(this EntityStatHandle handle, int statId, float value, long sourceId, int order = 0) =>
            StatManager.AddModifier(handle, statId, new StatModifier(value, ModifierType.Flat, order, sourceId));

        public static void AddPercentAdd(this EntityStatHandle handle, int statId, float value, long sourceId, int order = 0) =>
            StatManager.AddModifier(handle, statId, new StatModifier(value, ModifierType.PercentAdd, order, sourceId));

        public static void AddPercentMult(this EntityStatHandle handle, int statId, float value, long sourceId, int order = 0) =>
            StatManager.AddModifier(handle, statId, new StatModifier(value, ModifierType.PercentMult, order, sourceId));

        public static void AddTimedFlat(this EntityStatHandle handle, int statId,
            float value, long sourceId, float duration, int order = 0) =>
            StatManager.AddTimedModifier(handle, statId, new StatModifier(value, ModifierType.Flat, order, sourceId), duration);

        public static void AddTimedPercentAdd(this EntityStatHandle handle, int statId,
            float value, long sourceId, float duration, int order = 0) =>
            StatManager.AddTimedModifier(handle, statId, new StatModifier(value, ModifierType.PercentAdd, order, sourceId), duration);

        public static int RemoveAllFromSource(this EntityStatHandle handle, long sourceId) =>
            StatManager.RemoveAllFromSource(handle, sourceId);

        public static int CancelTimedFromSource(this EntityStatHandle handle, long sourceId) =>
            StatManager.CancelTimedFromSource(handle, sourceId);
    }
}
