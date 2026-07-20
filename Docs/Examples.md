# Stat System — Common RPG Patterns

Practical examples showing how to implement standard RPG mechanics with the stat system. All examples assume `StatManager` and `ResourcePoolManager` are initialized.

> **Note on stat IDs:** these examples reference illustrative names like `StatIds.HealthRegen`, `StatIds.CritChance`, `StatIds.FireResist`, and `StatIds.BladeSkill` to keep the patterns readable. The shipped `StatIds` class defines only a starter set (see `StatIds.cs`) — define your own constants (or load definitions from a `StatDefinitionDatabase`) for any stat your game needs. The IDs here stand in for whatever you name them; the code patterns are what matter.

## Setup

```csharp
// === Engine startup (once, before any plugins): ===
StatManager.Boot(entityCapacity: 2048);
ResourcePoolManager.Boot(entityCapacity: 2048);

// === Each plugin registers its content: ===

// Plugin can load from a StatDefinitionDatabase asset (bulk):
StatManager.RegisterStatDefinitions(myPluginDatabase);

// Or register individual definitions:
StatManager.RegisterStatDefinition(myCustomStatDef);

// Pool definitions are registered per-plugin too:
ResourcePoolManager.RegisterPoolDefinition(
    new ResourcePoolManager.PoolDefinition(StatIds.MaxHealth, StatIds.MaxHealth));
ResourcePoolManager.RegisterPoolDefinition(
    new ResourcePoolManager.PoolDefinition(StatIds.MaxStamina, StatIds.MaxStamina));
ResourcePoolManager.RegisterPoolDefinition(
    new ResourcePoolManager.PoolDefinition(StatIds.MaxMagicka, StatIds.MaxMagicka));

// The base game is just another plugin — same calls, no special path.

// === Per entity (after all plugins have loaded): ===
int[] npcStats = { StatIds.Strength, StatIds.MaxHealth, StatIds.Armor, StatIds.MoveSpeed, ... };
EntityStatHandle handle = StatManager.RegisterEntity(npcStats);
ResourcePoolManager.RegisterEntity(handle);

// Store handle on your entity/agent data.
```

## Equipment

Equipment adds modifiers when equipped and removes them by source ID when unequipped. The equipment's GGID (or any stable long identifier) serves as the source.

```csharp
long ironSwordGGID = 50001;

// Equip: sword gives +12 flat damage and +5% additive crit.
StatManager.AddModifier(handle, StatIds.PhysDamage,
    new StatModifier(12f, ModifierType.Flat, ironSwordGGID));
StatManager.AddModifier(handle, StatIds.CritChance,
    new StatModifier(0.05f, ModifierType.PercentAdd, ironSwordGGID));

// Unequip: one call strips everything the sword added, across all stats.
StatManager.RemoveAllFromSource(handle, ironSwordGGID);
```

Multiple equipment pieces stack naturally. Two rings each adding +5% PercentAdd to MaxHealth results in +10% total (additive stacking). An enchantment adding +10% PercentMult on top of that compounds multiplicatively.

## Potions (Timed Buffs)

Potions are timed modifiers — applied immediately, removed automatically when the duration expires.

```csharp
long strengthPotionId = 80001;
float duration = 120f; // 2 minutes

// Fortify Strength: +50% for 2 minutes.
StatManager.AddTimedModifier(handle, StatIds.Strength,
    new StatModifier(0.5f, ModifierType.PercentAdd, strengthPotionId), duration);

// That's it. After 120 seconds of TickTimedModifiers(), the modifier is gone.
// Derived stats (CarryWeight, etc.) update automatically via the dependency graph.
```

Stacking multiple potions: each gets its own source ID (the potion item's GGID). Two Fortify Strength potions at +50% each result in +100% additive. If you want "only the strongest applies," enforce that in your item-use logic before calling AddTimedModifier.

## Poisons

Poisons applied to weapons work the same way — timed modifiers on the target with the poison as the source.

```csharp
long poisonId = 80050;

// On weapon hit, apply to target:
StatManager.AddTimedModifier(targetHandle, StatIds.MoveSpeed,
    new StatModifier(-0.3f, ModifierType.PercentAdd, poisonId), 10f); // -30% speed for 10s

// Optionally also deal damage over time (see Damage Over Time section below).
```

## Damage and Healing

Direct HP manipulation goes through ResourcePoolManager. Shield is absorbed first on damage.

```csharp
// Calculate damage (your combat system determines the final number).
float baseDamage = StatManager.GetValue(attackerHandle, StatIds.PhysDamage);
float targetArmor = StatManager.GetValue(targetHandle, StatIds.Armor);
float finalDamage = CalculateDamage(baseDamage, targetArmor); // your formula

// Apply — shield absorbs first, remainder hits current HP.
float actualDealt = ResourcePoolManager.ApplyDamage(targetHandle, StatIds.MaxHealth, finalDamage);

// Check for death.
if (ResourcePoolManager.IsDepleted(targetHandle, StatIds.MaxHealth))
    HandleDeath(targetHandle);

// Healing — clamped at max, doesn't affect shield.
ResourcePoolManager.Heal(targetHandle, StatIds.MaxHealth, 25f);
```

## Damage Over Time (DoT)

A DoT is a timed modifier that periodically deals damage. The stat system handles the modifier; your effect system handles the periodic damage tick.

```csharp
long burnEffectId = 90010;

// Apply a -2 flat to a "BurnDPS" tracking stat, or just track the DoT in your effect system.
// The stat system's role is tracking the debuff's duration and any stat penalties:
StatManager.AddTimedModifier(targetHandle, StatIds.FireResist,
    new StatModifier(-0.1f, ModifierType.PercentAdd, burnEffectId), 8f); // -10% fire resist while burning

// Your effect tick (separate from stat system):
// if (burnActive) ResourcePoolManager.ApplyDamage(target, StatIds.MaxHealth, burnDPS * deltaTime);
```

## Drain and Restore

Drain reduces the base value directly — not a modifier. It persists until explicitly cured.

```csharp
// Drain 20 points of Strength (e.g., from a curse).
float current = StatManager.GetBaseValue(handle, StatIds.Strength);
StatManager.SetBaseValue(handle, StatIds.Strength, current - 20f);

// Restore (cure disease, rest at shrine, etc.):
// You need to know the original base. Store it before draining, or store the drain
// amount separately and add it back.
StatManager.SetBaseValue(handle, StatIds.Strength, current); // restore to original
```

For a more robust drain system, consider storing drain amounts as a separate tracked value per stat (similar to how shield works for pools). The stat system's base value mechanism handles the core mechanic.

## Shields / Temporary HP

Shield sits on top of current HP. Damage hits shield first. Shield ignores max-stat clamping.

```csharp
long wardSpellId = 70001;

// Mage casts a ward: 50 temporary HP on the health pool.
ResourcePoolManager.AddShield(handle, StatIds.MaxHealth, 50f);

// Shield stacks additively by default. A second ward adds more.
ResourcePoolManager.AddShield(handle, StatIds.MaxHealth, 30f); // now 80 total shield

// Or replace (non-stacking barrier that refreshes):
ResourcePoolManager.SetShield(handle, StatIds.MaxHealth, 50f); // exactly 50, regardless of previous

// Shield decays over time (optional — call from game loop):
ResourcePoolManager.TickShieldDecay(StatIds.MaxHealth, decayPerSecond: 5f, deltaTime);

// Dispel all shield:
ResourcePoolManager.ClearShield(handle, StatIds.MaxHealth);
```

## Passive Abilities / Perks

A perk that's always active is just a modifier with the perk as source, added when the perk is acquired and removed if the perk is ever lost.

```csharp
long heavyArmorPerkId = 60001;

// "Juggernaut" perk: +25% armor permanently.
StatManager.AddModifier(handle, StatIds.Armor,
    new StatModifier(0.25f, ModifierType.PercentAdd, heavyArmorPerkId));

// If perk is ever removed (respec):
StatManager.RemoveAllFromSource(handle, heavyArmorPerkId);
```

## Conditional Perks

Perks that only apply in certain situations ("+20% damage to undead") don't live in the stat system — they live in the gameplay/combat pipeline. The stat system provides the base values; the combat calculation applies situational multipliers.

```csharp
// In your damage calculation:
float damage = StatManager.GetValue(attackerHandle, StatIds.PhysDamage);

if (HasPerk(attackerHandle, Perks.SlayerOfUndead) && targetIsUndead)
    damage *= 1.2f; // +20% — never touches StatManager

ResourcePoolManager.ApplyDamage(targetHandle, StatIds.MaxHealth, damage);
```

## Racial Bonuses

Applied at entity creation, racial bonuses are modifiers with a "race" source ID.

```csharp
long nordRaceId = 1001;

// Nord racial: +50 flat health, +50% frost resist.
StatManager.AddModifier(handle, StatIds.MaxHealth,
    new StatModifier(50f, ModifierType.Flat, nordRaceId));
StatManager.AddModifier(handle, StatIds.FrostResist,
    new StatModifier(0.5f, ModifierType.PercentAdd, nordRaceId));

// These persist for the entity's lifetime. RemoveAllFromSource would strip them
// (useful for vampirism transformation, etc.).
```

## Level Up

Level-ups modify base values directly. Modifiers aren't appropriate here — these are permanent character growth, not buffs.

```csharp
// Player chooses to increase Health on level up.
float currentBase = StatManager.GetBaseValue(handle, StatIds.MaxHealth);
StatManager.SetBaseValue(handle, StatIds.MaxHealth, currentBase + 10f);

// If MaxHealth is a derived stat (formula includes Level or Endurance),
// the formula handles scaling automatically. You might only need to
// bump the base attribute:
float endBase = StatManager.GetBaseValue(handle, StatIds.Endurance);
StatManager.SetBaseValue(handle, StatIds.Endurance, endBase + 1f);
// MaxHealth (derived from Endurance) recalculates eagerly via the dependency graph.
```

## Enchantments

Enchantments are modifiers sourced to the enchanted item. When the item is unequipped, both the item's base modifiers and its enchantment modifiers are stripped by source.

```csharp
long enchantedRingGGID = 55001;

// Ring itself: +5 flat magicka.
StatManager.AddModifier(handle, StatIds.MaxMagicka,
    new StatModifier(5f, ModifierType.Flat, enchantedRingGGID));

// Enchantment on the ring: +15% spell damage.
StatManager.AddModifier(handle, StatIds.MagicDamage,
    new StatModifier(0.15f, ModifierType.PercentAdd, enchantedRingGGID));

// Unequip strips both — same source.
StatManager.RemoveAllFromSource(handle, enchantedRingGGID);
```

If the enchantment has a separate identity from the item (e.g., enchantments can be moved between items), give the enchantment its own source ID and manage its lifecycle independently.

## Diseases

A disease is a modifier with no automatic expiration. Applied when contracted, removed when cured.

```csharp
long brainRotId = 91001;

// Contract Brain Rot: -25 flat magicka.
StatManager.AddModifier(handle, StatIds.MaxMagicka,
    new StatModifier(-25f, ModifierType.Flat, brainRotId));

// Cure (shrine, potion, spell):
StatManager.RemoveAllFromSource(handle, brainRotId);
```

## Regen

Regen is a derived stat that feeds into the pool manager's batch tick.

Define the stat:
```
HealthRegen (derived):
  Formula: PushStat(MaxHealth), PushConstant(0.01), Multiply
  Dependencies: [MaxHealth]
  // HealthRegen = MaxHealth * 0.01 (1% of max per second)
```

Tick in game loop:
```csharp
// One call handles regen for every entity in the world.
ResourcePoolManager.TickRegen(StatIds.MaxHealth, StatIds.HealthRegen, Time.deltaTime);
ResourcePoolManager.TickRegen(StatIds.MaxStamina, StatIds.StaminaRegen, Time.deltaTime);
ResourcePoolManager.TickRegen(StatIds.MaxMagicka, StatIds.MagickaRegen, Time.deltaTime);
```

Buffs to MaxHealth automatically cascade to HealthRegen via the dependency graph, which means regen rate adjusts in real time.

## Custom Evaluators (Complex Derived Stats)

Some derived stats have formulas too complex for postfix notation — weighted averages, conditional branches, lookups into external systems. Register a C# delegate instead.

### Attribute from Weighted Skill Average

Strength is derived from three skills. The formula weights toward the highest skill and factors in race bonuses.

```csharp
// Define Strength as a StatDefinition with dependencies on the three skills.
// Leave the postfix formula empty — the custom evaluator handles everything.
// Dependencies: [BladeSkill, HeavyArmorSkill, AthleticsSkill]

StatManager.RegisterCustomEvaluator(StatIds.Strength, query => {
    float blade  = query.GetValue(StatIds.BladeSkill);
    float heavy  = query.GetValue(StatIds.HeavyArmorSkill);
    float athlet = query.GetValue(StatIds.AthleticsSkill);

    // Weighted average: 70% straight average, 30% pulled toward the highest.
    float max = Mathf.Max(blade, Mathf.Max(heavy, athlet));
    float avg = (blade + heavy + athlet) / 3f;
    return Mathf.Lerp(avg, max, 0.3f);
});
```

The dependency graph is still declared on the `StatDefinition` asset — that's what tells the system "when BladeSkill changes, recalculate Strength." The custom evaluator replaces the formula computation, not the dependency wiring.

### Conditional Derivation Based on Character State

An attribute that scales differently depending on focus slots or character composition:

```csharp
StatManager.RegisterCustomEvaluator(StatIds.Intelligence, query => {
    float destruction = query.GetValue(StatIds.DestructionSkill);
    float enchanting  = query.GetValue(StatIds.EnchantingSkill);
    float alchemy     = query.GetValue(StatIds.AlchemySkill);

    // If the character has high enchanting, it contributes more.
    float enchantWeight = enchanting > 50f ? 0.5f : 0.25f;
    float destroWeight  = 0.35f;
    float alchemyWeight = 1f - enchantWeight - destroWeight;

    return destruction * destroWeight
         + enchanting  * enchantWeight
         + alchemy     * alchemyWeight;
});
```

### Mod Overriding a Core Formula

A mod can replace the base game's derivation for a stat. The mod registers its custom evaluator during plugin init — since custom evaluators take priority over postfix formulas, it overrides without modifying the original definition asset.

```csharp
// Mod: "Rebalanced Attributes"
// Override Strength to be purely average-based with no weighting.
StatManager.RegisterCustomEvaluator(StatIds.Strength, query => {
    float blade  = query.GetValue(StatIds.BladeSkill);
    float heavy  = query.GetValue(StatIds.HeavyArmorSkill);
    float athlet = query.GetValue(StatIds.AthleticsSkill);
    return (blade + heavy + athlet) / 3f;
});
```

### Key Points

- Custom evaluators receive a `StatQuery` with `GetValue()`, `GetBaseValue()`, and `HasStat()` for safe access to the entity's stats.
- The stored base value is added on top of the evaluator's return value. Set `defaultBaseValue` to 0 on the definition if the evaluator should be the sole contributor.
- Modifiers still stack on top: a Fortify Strength enchantment works identically whether Strength uses a postfix formula or a custom evaluator.
- `RemoveCustomEvaluator(statId)` reverts to the postfix formula. Useful for mods that can be toggled on/off.

### Overriding a Definition's Data (constraints, formula, dependencies)

Custom evaluators override *derivation*. To rebalance a core stat's **constraints, default, postfix formula, or dependencies**, override the definition itself. `RegisterStatDefinition` is add-only (it rejects an already-used ID); `OverrideStatDefinition` replaces an existing one.

```csharp
// Mod: raise the Armor cap from 100 to 250 and re-derive it.
// Build a StatDefinition asset (or clone the core one) with the new values, then:
StatManager.OverrideStatDefinition(rebalancedArmorDef);
```

- Constraints, formula, and dependencies take effect **immediately** — every live entity with that stat is recalculated (cascading to dependents).
- The new **default base value** applies only to entities registered *after* the override; already-registered entities keep the base they were seeded with (use `SetBaseValue` to retro-adjust).
- Returns `false` if the stat isn't registered yet, or if the new formula/dependencies fail validation (bad formula, dependency cycle).

## AI Drives

Internal simulation values use the same system. An NPC "socialize" drive that builds over time:

```csharp
// Register with AI-range stat IDs.
const int Socialize = 5001;
const int Hunger = 5002;

// Tick drives in your AI update:
float current = StatManager.GetBaseValue(npcHandle, Socialize);
StatManager.SetBaseValue(npcHandle, Socialize, current + driveRate * deltaTime);

// Context modifiers: NPC is at a tavern, socializing urge builds faster.
long tavernContextId = 40001;
StatManager.AddModifier(npcHandle, Socialize,
    new StatModifier(0.5f, ModifierType.PercentAdd, tavernContextId));
// Removed when NPC leaves the tavern area.

// AI reads the drive to make decisions:
float socializeUrge = StatManager.GetValue(npcHandle, Socialize);
if (socializeUrge > threshold)
    SelectSocializeBehavior(npcHandle);
```

## Saving and Loading

The stat system produces plain serializable structs from `CaptureEntityState`. Your save system is responsible for writing them to disk however it wants — JSON, MessagePack, binary, etc. On load, you re-register the entity with the same stat set and restore the saved state.

### Save Data Structures

```csharp
// StatManager produces:
StatManager.FullEntitySaveData
    .Stats[]              // per-stat: statId, baseValue, active modifiers
    .TimedModifiers[]     // per-timed: statId, modifier, remainingTime, totalDuration

// ResourcePoolManager produces:
ResourcePoolManager.EntityPoolSaveData
    .Pools[]              // per-pool: poolId, currentValue, shieldValue
```

### Full Save Flow

```csharp
/// <summary>
/// Capture everything about an entity's stat/pool state for serialization.
/// Call this as part of your save pipeline.
/// </summary>
public EntitySaveBundle CaptureEntity(EntityStatHandle handle)
{
    return new EntitySaveBundle
    {
        StatData = StatManager.CaptureEntityState(handle),
        PoolData = ResourcePoolManager.CaptureEntityState(handle),
    };
}

// Your save wrapper:
[System.Serializable]
public struct EntitySaveBundle
{
    public StatManager.FullEntitySaveData StatData;
    public ResourcePoolManager.EntityPoolSaveData PoolData;
}
```

### Full Load Flow

```csharp
/// <summary>
/// Rebuild an entity from saved data. The entity must be re-registered
/// with the same stat set it had when saved.
/// </summary>
public EntityStatHandle RestoreEntity(int[] statIds, EntitySaveBundle savedData)
{
    // 1. Register the entity fresh (gets a new handle).
    EntityStatHandle handle = StatManager.RegisterEntity(statIds);
    ResourcePoolManager.RegisterEntity(handle);

    // 2. Overwrite defaults with saved state.
    //    - Base values are restored.
    //    - All active modifiers are re-added (equipment, perks, diseases, etc.).
    //    - Timed modifier countdowns resume from where they were saved.
    StatManager.RestoreEntityState(handle, savedData.StatData);

    //    - Current pool values are restored and clamped to current max.
    //    - Shield values are restored (not clamped to max).
    ResourcePoolManager.RestoreEntityState(handle, savedData.PoolData);

    return handle;
}
```

### JSON Serialization Example (using Unity's JsonUtility)

```csharp
// Save to JSON:
var bundle = CaptureEntity(handle);
string json = JsonUtility.ToJson(bundle, prettyPrint: true);
File.WriteAllText(savePath, json);

// Load from JSON:
string json = File.ReadAllText(savePath);
var bundle = JsonUtility.FromJson<EntitySaveBundle>(json);
var handle = RestoreEntity(entityStatIds, bundle);
```

### What Gets Saved

Everything needed to reconstruct the entity's stat state exactly as it was:

- **Base values** — including level-up bonuses, drain effects, anything that modified the base.
- **Active modifiers** — every flat, percent-add, and percent-mult modifier with its source ID, value, type, and order. Equipment modifiers, disease penalties, perk bonuses — all preserved.
- **Timed modifier countdowns** — a potion with 47 seconds remaining at save resumes at 47 seconds on load. The modifier itself is included in the modifier list above; the timed entry just tracks the countdown.
- **Pool current values** — HP at 65/100 saves as 65. On restore, clamped to current max (in case max changed due to mod load order differences).
- **Shield values** — temporary HP buffer, restored as-is (not clamped to max).

### What Doesn't Get Saved (By Design)

- **Stat definitions** — these come from the database asset and mod registrations, not from per-entity state.
- **The dependency graph and formulas** — rebuilt from definitions at startup. Derived stats recalculate correctly on load because the modifiers on their dependencies are restored.
- **Unity GameObjects** — the stat system is decoupled from scene objects. Your entity spawning system handles instantiation; the stat system just restores the numbers.

### Edge Cases

**Mod removed between save and load:** If a saved entity has modifiers for a stat that no longer exists (mod was unloaded), `RestoreEntityState` skips entries whose stat ID isn't in the entity's current stat set. No crash, no corruption — the modifiers silently disappear.

**Mod added between save and load:** New stats start at their definition's default base value with no modifiers. The entity won't have saved state for the new stat, so it behaves as if freshly registered.

**Max stat changed between save and load:** Pool current values are clamped to the current max on restore. If a balance patch reduced MaxHealth from 100 to 80, a saved HP of 95 loads as 80.

## World Events / Global Effects

A world event that buffs every entity uses `RemoveSourceFromAllEntities` for cleanup.

```csharp
long bloodMoonId = 99001;

// Blood moon rises: all entities get +20% damage.
// Apply to each entity as they're loaded or registered.
StatManager.AddModifier(entityHandle, StatIds.PhysDamage,
    new StatModifier(0.2f, ModifierType.PercentAdd, bloodMoonId));

// Blood moon ends: strip from everyone at once.
StatManager.RemoveSourceFromAllEntities(bloodMoonId);
```

## Dispelling

Dispelling a buff or debuff uses the timed-modifier cancellation API.

```csharp
long curseSourceId = 92001;

// A "Purify" spell removes all effects from a specific source:
StatManager.CancelTimedFromSource(handle, curseSourceId);

// Or remove ALL modifiers from a source (timed and permanent):
StatManager.RemoveAllFromSource(handle, curseSourceId);
```

## Game Loop Integration

```csharp
void Update()
{
    float dt = Time.deltaTime;

    // Tick all timed modifiers (buff/debuff expiration).
    StatManager.TickTimedModifiers(dt);

    // Tick regen on all pools.
    ResourcePoolManager.TickRegen(StatIds.MaxHealth, StatIds.HealthRegen, dt);
    ResourcePoolManager.TickRegen(StatIds.MaxStamina, StatIds.StaminaRegen, dt);
    ResourcePoolManager.TickRegen(StatIds.MaxMagicka, StatIds.MagickaRegen, dt);

    // Tick shield decay (if applicable).
    ResourcePoolManager.TickShieldDecay(StatIds.MaxHealth, shieldDecayRate, dt);
}
```

## Delta Serialization (Chunk Save Optimization)

Most NPCs in a loaded chunk won't have any stat modifications. Delta serialization skips pristine entities entirely and only records stats that differ from their definition defaults.

```csharp
// In your chunk save pipeline, per entity:
if (StatManager.HasStatChanges(handle) || ResourcePoolManager.HasPoolChanges(handle))
{
    // Only write this entity to the change map.
    var statDelta = StatManager.CaptureEntityStateDelta(handle);
    var poolDelta = ResourcePoolManager.CaptureEntityStateDelta(handle);
    changeMap.AddEntry(entityGGID, statDelta, poolDelta);
}
// Untouched entities: no entry in the change map.
// They'll reconstruct from their template definition on next load.
```

```csharp
// On load, RestoreEntityState handles sparse delta data.
// Stats not in the delta keep their registration defaults.
var handle = StatManager.RegisterEntity(templateStatIds);
ResourcePoolManager.RegisterEntity(handle);

if (changeMap.TryGetEntry(entityGGID, out var statDelta, out var poolDelta))
{
    StatManager.RestoreEntityState(handle, statDelta);
    ResourcePoolManager.RestoreEntityState(handle, poolDelta);
}
// else: entity stays at template defaults — nothing to restore.
```

For a city chunk with 200 NPCs where only 5 were involved in combat, the change map contains 5 entries instead of 200.

## Bulk Registration (Chunk Loading)

When loading a chunk with many entities of the same type, bulk registration avoids repeated array resizing and handle validation.

```csharp
int[] npcStatIds = { StatIds.Strength, StatIds.MaxHealth, StatIds.Armor, StatIds.MoveSpeed };
int npcCount = chunkData.npcCount;

// Allocate handle buffer.
var handles = new EntityStatHandle[npcCount];

// One call registers all stat entities — pre-grows arrays once.
StatManager.RegisterEntities(npcCount, npcStatIds, handles);

// One call registers all pools — pre-grows pool arrays once.
ResourcePoolManager.RegisterEntities(handles);

// Distribute handles to spawned GameObjects / agent data.
for (int i = 0; i < npcCount; i++)
    chunkData.npcs[i].statHandle = handles[i];
```

```csharp
// Bulk unregister on chunk unload. Scans timed modifier list once
// for all entities instead of once per entity.
ResourcePoolManager.UnregisterEntities(handles);
StatManager.UnregisterEntities(handles);
```

## Debug Tools

### Stat Breakdown (Why Is This Stat This Value?)

```csharp
// Get full pipeline breakdown for a single stat.
var breakdown = StatManager.GetStatBreakdown(handle, StatIds.Strength);

// Shows: base=10, +5 flat (sword), +50% pctAdd (potion), ×1.1 pctMult (blessing) = 24.75
Debug.Log(breakdown);

// breakdown.Modifiers contains every active modifier with source IDs.
// breakdown.TimedModifiers shows remaining durations.
// breakdown.FinalValue matches StatManager.GetValue(handle, statId).
```

### Entity Snapshot (Full State Dump)

```csharp
// Dump every stat and pool on an entity.
var snapshot = StatManager.GetEntitySnapshot(handle);
Debug.Log(snapshot);

// Useful for in-game debug console:
// /inspect npc_1234 -> prints all stats with breakdowns + pool states.
```

### Snapshot Diff (What Changed Between Saves?)

```csharp
// Capture before and after states.
var beforeStats = StatManager.CaptureEntityState(handle);
var beforePools = ResourcePoolManager.CaptureEntityState(handle);

// ... something happens ...

var afterStats = StatManager.CaptureEntityState(handle);
var afterPools = ResourcePoolManager.CaptureEntityState(handle);

// Diff.
var statDiff = StatDebug.DiffSaveData(beforeStats, afterStats);
var poolDiff = StatDebug.DiffPoolData(beforePools, afterPools);

// Shows: Strength base 10 -> 15, +1 modifier added (src:50001), HP 100 -> 65
Debug.Log(statDiff);
Debug.Log(poolDiff);

// Works on serialized data — diff two save files offline without live entities.
```