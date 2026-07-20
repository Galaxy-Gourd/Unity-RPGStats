# GourdStats

Stat, modifier and resource-pool system for Unity RPGs. Entities don't own stat objects — they hold an 8-byte
generational handle into two static managers that store every entity's stats in flat arrays. Stats can derive from
other stats via data-authored formulas, modifiers stack through a defined pipeline, buffs expire on a single batch
tick, and everything an entity has is capturable as a plain serializable struct.

Built for worlds with a lot of stat-bearing things in them: players, NPCs, creatures, destructible props, AI drives —
anything with numbers that other numbers depend on.

- **Handle-based** — no per-entity stat class, no null checks. A recycled slot invalidates stale handles automatically.
- **Data-driven derivation** — `MaxHealth = 100 + Endurance * 5` is authored in the Inspector, not compiled in.
- **Eager and consistent** — mutating a stat recalculates it and cascades through its dependents immediately.
  `GetValue` is an array read.
- **Moddable by construction** — the base game registers its content through the same API a mod does.
- **Save-ready** — full or delta capture per entity, including in-flight buff countdowns.

> **Scope.** This is a managed, main-thread system, not a DOTS/Burst one. It optimizes for correctness-on-read and low
> per-call allocation, and comfortably handles hundreds to low thousands of entities updated per frame. Recomputing
> tens of thousands of entities per frame across worker threads would want a `NativeArray`-backed layout instead —
> out of scope here. See [`Docs/Architecture.md`](Docs/Architecture.md).

## Install

Unity 2021.3 or newer. In the Package Manager, **+ → Add package from git URL**:

```
https://github.com/Galaxy-Gourd/Unity-RPGStats.git
```

Or add it to `Packages/manifest.json` directly:

```json
"com.galaxygourd.stats": "https://github.com/Galaxy-Gourd/Unity-RPGStats.git"
```

Everything lives in the `RPG.Stats` namespace.

## How It Works

**Entities register, they don't own.** An entity calls `StatManager.RegisterEntity(statIds)` and gets back an
`EntityStatHandle` — an index plus a generation counter, 8 bytes, passed by value. That handle is all the entity
stores. Unregistering recycles the slot and bumps the generation, so any stale handle still pointing at it fails
validation silently and returns defaults instead of reading another entity's data.

**Stats are ScriptableObject definitions.** A `StatDefinition` declares a stable integer ID, a default base value,
optional min/max/round constraints, and — optionally — dependencies and a formula. A definition with a formula is a
derived stat; one without is a plain stat. There is no separate type. Definitions are registered individually or
bulk-loaded from a `StatDefinitionDatabase` asset.

**Stat IDs are plain integers.** `StatIds` holds a starter set of constants; you define your own for whatever your
game needs. Content packs claiming high ranges (10000+) never collide with core stats and never touch the core
assembly.

**Values run a fixed pipeline.** Every evaluation goes through the same stages, in order:

```
base (stored, or formula result + stored)
  → + Σ Flat
  → × (1 + Σ PercentAdd)         ← all percent-adds sum into one multiplier
  → × (1 + p₁) × (1 + p₂) …      ← percent-mults compound sequentially
  → clamp / round
```

Within a stage, modifiers are sorted by their `Order` field, so evaluation is deterministic.

**Mutations are eager.** `AddModifier`, `SetBaseValue`, `RemoveAllFromSource` and timed expiry all recalculate the
affected stat immediately and cascade through the dependency graph — `Endurance → MaxHealth → HealthRegen` updates
in one call stack. There is no frame where a stat reads stale, so UI, AI and pool clamping all see the correct value
the moment it changes.

## Quick Start

```csharp
using RPG.Stats;

// 1. Boot the managers once, at startup, before any content loads.
StatManager.Boot(entityCapacity: 2048);
ResourcePoolManager.Boot(entityCapacity: 2048);

// 2. Register content. The base game is just the first plugin to do this.
StatManager.RegisterStatDefinitions(myStatDatabase);   // a StatDefinitionDatabase asset
ResourcePoolManager.RegisterPoolDefinition(
    new ResourcePoolManager.PoolDefinition(StatIds.Health, StatIds.MaxHealth));

// 3. Register an entity with the stats it should have.
int[] npcStats = { StatIds.Strength, StatIds.Endurance, StatIds.MaxHealth,
                   StatIds.PhysDamage, StatIds.Armor, StatIds.MoveSpeed };

EntityStatHandle npc = StatManager.RegisterEntity(npcStats);
ResourcePoolManager.RegisterEntity(npc);

// 4. Use it. Extension methods on the handle read like instance calls.
npc.AddFlat(StatIds.PhysDamage, 12f, sourceId: ironSwordId);
float damage = npc.GetValue(StatIds.PhysDamage);

// 5. Drive the batch ticks from your game loop.
void Update()
{
    StatManager.TickTimedModifiers(Time.deltaTime);
    ResourcePoolManager.TickRegen(StatIds.Health, healthRegenStatId, Time.deltaTime);
}
```

## Usage

### Modifiers and sources

Every `StatModifier` carries a `long SourceId` — the item, perk, spell or effect that applied it. That's what makes
removal a single call, regardless of how many stats the source touched.

```csharp
// Equip: the sword modifies two different stats.
handle.AddFlat(StatIds.PhysDamage, 12f, ironSwordId);
handle.AddPercentAdd(StatIds.Armor, 0.05f, ironSwordId);

// Unequip: one call strips everything that sword added, across every stat.
handle.RemoveAllFromSource(ironSwordId);

// A world event ends — strip it from every entity at once.
StatManager.RemoveSourceFromAllEntities(bloodMoonId);
```

Two rings each adding +5% `PercentAdd` give +10%. An enchantment adding +10% `PercentMult` on top compounds against
that result. Use `Flat` for +12 damage, `PercentAdd` for stacking buffs, `PercentMult` for multipliers that should
compound.

### Timed modifiers

Potions, poisons and temporary blessings are the same modifiers with a duration. One `TickTimedModifiers` call per
frame expires them across every entity in the world; expiry recalculates and cascades like any other mutation.

```csharp
handle.AddTimedPercentAdd(StatIds.Strength, 0.5f, strengthPotionId, duration: 120f);

// Dispel early — cancels timed entries from that source only.
handle.CancelTimedFromSource(curseId);
```

Remaining durations are captured in save data, so a potion with 47 seconds left resumes at 47 seconds on load.

### Derived stats

Author the formula on the `StatDefinition` as postfix (RPN) ops and list what it reads in `Dependencies`. The manager
builds the dependency graph at registration time; nothing needs manual wiring.

```csharp
// CarryWeight = 50 + (Strength * 10)
formula = new[]
{
    StatFormulaOp.Const(50f),
    StatFormulaOp.Stat(StatIds.Strength),
    StatFormulaOp.Const(10f),
    StatFormulaOp.Mul(),
    StatFormulaOp.Add(),
};
// Dependencies: [ StatIds.Strength ]
```

Evaluation uses a `stackalloc` scratch stack — no parsing, no allocation, no GC. The custom inspector shows a live
infix preview of the postfix ops and flags any stat the formula reads that isn't declared as a dependency. Full
authoring guide: [`Docs/FormulaGuide.md`](Docs/FormulaGuide.md).

When a relationship is too complex for postfix — weighted averages, conditionals, lookups into other systems —
register a C# evaluator instead. It takes priority over the formula, and can be removed at runtime to fall back to it.

```csharp
StatManager.RegisterCustomEvaluator(StatIds.Strength, query =>
{
    float melee = query.GetValue(StatIds.Melee);
    float grapple = query.GetValue(StatIds.Grappling);
    return Mathf.Lerp((melee + grapple) * 0.5f, Mathf.Max(melee, grapple), 0.3f);
});
```

Dependencies still come from the definition — the evaluator replaces the computation, not the cascade wiring.

### Resource pools

`ResourcePoolManager` handles current/max pairs on the same handle. Max is never stored: it reads live from the stat
that backs it, and when that stat drops (a Fortify Health buff expiring), current is clamped in the same call stack.

```csharp
float dealt = ResourcePoolManager.ApplyDamage(target, StatIds.Health, 40f); // shield absorbs first
ResourcePoolManager.Heal(target, StatIds.Health, 25f);                      // clamped at max

ResourcePoolManager.AddShield(target, StatIds.Health, 50f);   // temp HP, not clamped by max
ResourcePoolManager.TickShieldDecay(StatIds.Health, decayPerSecond: 5f, deltaTime);

ResourcePoolManager.OnPoolDepleted += (handle, poolId) => HandleDeath(handle);
```

`OnPoolChanged`, `OnShieldChanged` and `OnPoolDepleted` let UI and gameplay react without polling — `OnPoolDepleted`
fires once on the transition to zero, not every frame after it.

### Saving and loading

Both managers hand back plain serializable structs; your save system decides the wire format.

```csharp
var stats = StatManager.CaptureEntityState(handle);           // everything
var pools = ResourcePoolManager.CaptureEntityState(handle);

// ...serialize however you like (JsonUtility, MessagePack, binary)...

var restored = StatManager.RegisterEntity(sameStatIds);
ResourcePoolManager.RegisterEntity(restored);
StatManager.RestoreEntityState(restored, stats);
ResourcePoolManager.RestoreEntityState(restored, pools);
```

For chunk saves, use the delta path: `HasStatChanges(handle)` skips pristine entities entirely, and
`CaptureEntityStateDelta` records only stats that differ from their definition defaults. A city chunk where 5 of 200
NPCs saw combat writes 5 entries, not 200. `RestoreEntityState` handles the sparse data — absent stats keep their
registration defaults.

### Bulk operations

`RegisterEntities` pre-grows the arrays once and fills slots in a tight loop; `UnregisterEntities` scans the timed
modifier list once for the whole batch instead of once per entity. Both managers have matching bulk paths — use them
for chunk load/unload.

```csharp
var handles = new EntityStatHandle[npcCount];
StatManager.RegisterEntities(npcCount, npcStatIds, handles);
ResourcePoolManager.RegisterEntities(handles);
```

### Debugging

```csharp
Debug.Log(StatManager.GetStatBreakdown(handle, StatIds.Strength)); // every stage of the pipeline
Debug.Log(StatManager.GetEntitySnapshot(handle));                  // every stat + pool on the entity
Debug.Log(StatDebug.DiffSaveData(before, after));                  // what changed between two captures
```

`GetStatBreakdown` answers "why is this number this number" — stored base, formula result, flat total, each percent
stage, the final constrained value, and every contributing modifier with its source ID and remaining duration. It
allocates; it's for debug overlays and console commands, not hot paths. `DiffSaveData` works on captured structs, so
you can diff two save files offline without live entities.

Rejections and misuse warnings — a mod registering a cyclic dependency, a malformed formula, a modifier for a stat the
entity doesn't have — go through `StatLog`, which writes to the Unity console by default. Point it somewhere else to
route diagnostics into your own logging system, a mod loader's per-plugin log, or an in-game console:

```csharp
StatLog.Handler = (type, message) => MyGameLog.Write(type, message);
StatLog.Reset(); // back to the Unity console
```

## Implementation Components

| Type | Role |
|---|---|
| `StatManager` | Static owner of all stat data: storage, modifiers, evaluation, timed ticks, dependency graph, bulk ops, serialization |
| `ResourcePoolManager` | Current/max/shield pools on the same handle: damage, heal, regen, decay, auto-clamp, events |
| `EntityStatHandle` | 8-byte generational handle — an entity's entire stat footprint |
| `StatDefinition` | ScriptableObject: identity, default base, constraints, optional dependencies + formula |
| `StatDefinitionDatabase` | A plugin's definition manifest, bulk-registered in one call |
| `StatModifier` | Serializable value type: value, `ModifierType`, order, source ID |
| `StatFormulaOp` | One postfix operation, with factory helpers (`Const`, `Stat`, `Mul`, `Add`, …) |
| `StatIds` | Integer constants for a starter set of stats — extend or replace with your own |
| `StatManagerExtensions` / `ResourcePoolExtensions` | Handle extension methods for instance-style call sites |
| `StatDebug` | `StatBreakdown`, `EntityStatSnapshot`, `SnapshotDiff` and the diff utilities |
| `StatLog` | Redirectable sink for runtime diagnostics — defaults to the Unity console |
| `StatDefinitionEditor` | Custom inspector: conditional derivation UI, formula ops with contextual fields, live infix preview, dependency validation |

Both managers null their static state on `SubsystemRegistration`, so they're safe with domain reload disabled.

## Samples

Import from the Package Manager's **Samples** tab.

### Usage Example

A single `MonoBehaviour` walkthrough of the whole lifecycle: booting the managers, plugin-style content
registration, registering entities with different stat sets, equipment and potions via source IDs, dispelling,
batched multi-stat queries, save capture, and the per-frame timed tick.

## Docs

- [`Docs/Architecture.md`](Docs/Architecture.md) — data layout, evaluation order, dependency graph, derivation tiers, serialization, performance scope
- [`Docs/Examples.md`](Docs/Examples.md) — equipment, potions, poisons, DoTs, shields, perks, racials, level-up, enchantments, diseases, regen, AI drives, world events, chunk save/load
- [`Docs/FormulaGuide.md`](Docs/FormulaGuide.md) — postfix notation from scratch, conversion rules, op reference, and why it's built this way

## Tests

The package ships 49 EditMode tests under `Tests/`. To run them, add the package to the `testables` array of your
project's `Packages/manifest.json` — Unity only surfaces a package's tests when it's listed there:

```json
"testables": [ "com.galaxygourd.stats" ]
```

They then appear in **Window > General > Test Runner** under `GG.Stats.Tests`, covering registration and slot
recycling, handle generation safety, the full modifier pipeline and stacking rules, clamping, source and timed
removal, derived stats (single, multi-dependency, chained cascades, cycle and invalid-formula rejection), custom
evaluators, definition override, pools and shields (damage absorption, max-change clamping, regen, decay, events),
save/restore round trips, delta serialization, bulk registration, the debug breakdown/snapshot/diff surface, and a
scale pass.

Headless: `Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults results.xml`.

## License

MIT — see [`LICENSE.md`](LICENSE.md).
