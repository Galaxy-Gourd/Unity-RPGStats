namespace RPG.Stats
{
    /// <summary>
    /// Extension methods for resource pool operations on EntityStatHandle.
    /// Clean call-site syntax: handle.GetCurrentHP(), handle.ApplyDamage(poolId, 25f)
    /// </summary>
    public static class ResourcePoolExtensions
    {
        public static float GetCurrent(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.GetCurrent(handle, poolId);

        public static float GetPoolMax(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.GetMax(handle, poolId);

        public static float GetPoolRatio(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.GetRatio(handle, poolId);

        public static float ApplyDamage(this EntityStatHandle handle, int poolId, float amount) =>
            ResourcePoolManager.ApplyDamage(handle, poolId, amount);

        public static float Heal(this EntityStatHandle handle, int poolId, float amount) =>
            ResourcePoolManager.Heal(handle, poolId, amount);

        public static bool IsDepleted(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.IsDepleted(handle, poolId);

        public static void RestoreAllPools(this EntityStatHandle handle) =>
            ResourcePoolManager.RestoreAll(handle);

        public static float GetShield(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.GetShield(handle, poolId);

        public static float GetEffectiveTotal(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.GetEffectiveTotal(handle, poolId);

        public static float AddShield(this EntityStatHandle handle, int poolId, float amount) =>
            ResourcePoolManager.AddShield(handle, poolId, amount);

        public static void SetShield(this EntityStatHandle handle, int poolId, float value) =>
            ResourcePoolManager.SetShield(handle, poolId, value);

        public static float ClearShield(this EntityStatHandle handle, int poolId) =>
            ResourcePoolManager.ClearShield(handle, poolId);
    }
}