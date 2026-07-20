using System;
using UnityEngine;

namespace RPG.Stats.Example
{
    /// <summary>
    /// Example showing the centralized StatManager architecture.
    /// 
    /// Key differences from the old OOP approach:
    ///   - No per-entity class instances. Entities just hold an EntityStatHandle (8 bytes).
    ///   - All data lives in StatManager's flat arrays.
    ///   - Timed modifiers across ALL entities are batch-ticked in one call.
    ///   - Extension methods on the handle give clean instance-style syntax.
    /// </summary>
    public class StatSystemUsageExample : MonoBehaviour
    {
        [SerializeField] private StatDefinitionDatabase database;

        private EntityStatHandle _player;
        private EntityStatHandle _npc1;
        private EntityStatHandle _npc2;

        // Source IDs (in practice: GGIDs from your entity/item system).
        private const long IronSwordId       = 5001;
        private const long StrengthPotionId  = 8001;
        private const long BlessingOfMightId = 9001;
        private const long CurseOfWeaknessId = 9002;

        private void Start()
        {
            // --- Boot infrastructure (once, before any content) ---
            StatManager.Boot(entityCapacity: 1024);
            ResourcePoolManager.Boot(entityCapacity: 1024);
            StatManager.OnStatChanged += OnAnyStatChanged;

            // --- Load content (base game is just another plugin) ---
            StatManager.RegisterStatDefinitions(database);
            ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(StatIds.MaxHealth, StatIds.MaxHealth));
            ResourcePoolManager.RegisterPoolDefinition(
                new ResourcePoolManager.PoolDefinition(StatIds.MaxStamina, StatIds.MaxStamina));

            // --- Register entities. Each gets a lightweight handle. ---
            int[] playerStats = {
                StatIds.Strength, StatIds.Dexterity, StatIds.Intelligence,
                StatIds.Endurance, StatIds.Cunning, StatIds.Discipline,
                StatIds.MaxHealth, StatIds.MaxStamina, StatIds.MaxMagicka,
                StatIds.PhysDamage, StatIds.MagicDamage,
                StatIds.Armor, StatIds.MagicResist,
                StatIds.MoveSpeed, StatIds.CarryWeight,
            };

            // NPCs might have a smaller stat set.
            int[] basicNpcStats = {
                StatIds.Strength, StatIds.MaxHealth,
                StatIds.PhysDamage, StatIds.Armor, StatIds.MoveSpeed,
            };

            _player = StatManager.RegisterEntity(playerStats);
            _npc1   = StatManager.RegisterEntity(basicNpcStats);
            _npc2   = StatManager.RegisterEntity(basicNpcStats);

            // --- Equip sword on player ---
            _player.AddFlat(StatIds.PhysDamage, 15f, IronSwordId);
            _player.AddPercentAdd(StatIds.PhysDamage, 0.05f, IronSwordId);
            Debug.Log($"Player PhysDamage: {_player.GetValue(StatIds.PhysDamage)}");

            // --- Player drinks strength potion: +50% for 120s ---
            _player.AddTimedPercentAdd(StatIds.Strength, 0.50f, StrengthPotionId, 120f);
            Debug.Log($"Player Strength: {_player.GetValue(StatIds.Strength)}");

            // --- NPC gets a shrine blessing ---
            _npc1.AddPercentMult(StatIds.MaxHealth, 0.10f, BlessingOfMightId);

            // --- Enemy curses NPC2: -20% MoveSpeed for 5s ---
            _npc2.AddTimedPercentAdd(StatIds.MoveSpeed, -0.20f, CurseOfWeaknessId, 5f);

            // --- Unequip sword (strips all modifiers from that source) ---
            _player.RemoveAllFromSource(IronSwordId);

            // --- Dispel curse early ---
            _npc2.CancelTimedFromSource(CurseOfWeaknessId);

            // --- Batch query: get multiple stats at once ---
            Span<int> queryIds = stackalloc int[] { StatIds.Strength, StatIds.MaxHealth, StatIds.PhysDamage };
            Span<float> results = stackalloc float[3];
            StatManager.GetValues(_player, queryIds, results);
            Debug.Log($"Player batch: STR={results[0]}, HP={results[1]}, DMG={results[2]}");

            // --- Save ---
            var saveData = StatManager.CaptureEntityState(_player);
            // Serialize saveData with MessagePack / JSON / etc.

            // --- Load (after re-registering the entity) ---
            // var loadedHandle = StatManager.RegisterEntity(playerStats);
            // StatManager.RestoreEntityState(loadedHandle, saveData);

            Debug.Log($"Active entities: {StatManager.ActiveEntityCount}");
            Debug.Log($"Active timed modifiers: {StatManager.TimedModifierCount}");
        }

        private void Update()
        {
            // Single call ticks ALL timed modifiers across ALL entities.
            StatManager.TickTimedModifiers(Time.deltaTime);
        }

        private void OnDestroy()
        {
            // Unregister entities when they leave the world.
            StatManager.UnregisterEntity(_player);
            StatManager.UnregisterEntity(_npc1);
            StatManager.UnregisterEntity(_npc2);

            // Full shutdown when the game exits.
            // StatManager.Shutdown();
        }

        private static void OnAnyStatChanged(EntityStatHandle handle, int statId, float oldVal, float newVal)
        {
            Debug.Log($"[{handle}] Stat {statId}: {oldVal:F1} -> {newVal:F1}");
        }
    }
}