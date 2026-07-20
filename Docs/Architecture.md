# Stat System — Architecture Reference

## Overview

A centralized, handle-based stat system for RPGs with many concurrent stat-bearing entities. All stat data is owned by two static managers (`StatManager` and `ResourcePoolManager`), stored in a top-level array of per-entity slots indexed by generational handles.

**Performance profile & scope.** This is a managed, main-thread system — not a DOTS/Burst one. The top-level entity array is contiguous, but each slot holds managed collections (a `Dictionary<int,int>` for stat-id lookup plus per-stat arrays and modifier lists), so stat reads are dictionary lookups and the storage is not a cache-packed struct-of-arrays. The design optimizes for eager correctness (values are never stale) and low per-call allocation, and comfortably handles hundreds to low thousands of entities updated per frame. Workloads that need tens of thousands of entities recomputed every frame across worker threads would want a `NativeArray`/Burst-backed layout instead; that is out of scope for this implementation.

## Core Concepts

### Entity Registration

Entities (players, NPCs, objects, anything with stats) don't own their stat data. They register with `StatManager` and receive an `EntityStatHandle` — an 8-byte readonly struct containing an array index and a generation counter. The handle is what the entity stores. All stat operations go through the handle.

When an entity is unregistered, its slot is recycled. The generation counter increments, so any stale handles still pointing to that slot fail validation silently and return defaults. This prevents use-after-free bugs without requiring null checks or wrapper objects.

### Stat Definitions

Stats are defined as ScriptableObject assets (`StatDefinition`). Each defines a unique integer ID, display name, default base value, optional min/max clamps, and an optional round-to-integer flag. These are pure data — they describe what a stat is, not what value it has on any particular entity.

Stats with derivation have optional dependency and formula fields. When populated, the stat is derived — its base value is computed from the formula. When empty, the stat is a plain base stat. There is no separate type. See the Formula Guide for notation details.

All definitions are registered via `StatManager.RegisterStatDefinition()` or bulk-loaded from a `StatDefinitionDatabase` asset via `StatManager.RegisterStatDefinitions()`. The base game is just another plugin — it registers its definitions through the same API as mods. There is no separate "core" initialization path.

### Plugin Loading Pattern

The system separates infrastructure boot from content registration:

1. **Boot** — `StatManager.Boot()` and `ResourcePoolManager.Boot()` allocate arrays and wire up internal callbacks. Called once at engine startup, before any plugins.
2. **Register content** — Each plugin calls `RegisterStatDefinition()` / `RegisterStatDefinitions()` and `RegisterPoolDefinition()` to contribute its stats and pools. The base game goes first, then mods in load order.
3. **Register entities** — After all plugins have loaded, entities are created with stat sets that can include IDs from any plugin.

This means a mod can define new attributes, derived stats with formulas referencing core stats, and resource pools — all through the same API the base game uses.

### Stat IDs

Stats are identified by plain integers, not enums. Core stats live in the `StatIds` static class as constants (1–999 range). This allows mods to define stats in higher ranges (10000+) without touching the core assembly. The system doesn't care what the number means — a stat is a stat.

## StatManager

### Data Layout

StatManager owns a flat array of `EntitySlot` structs. Each slot contains parallel arrays for a single entity's stats:

- `StatIdsBySlot[]` — maps local slot index to stat ID
- `StatIdToSlot` — dictionary mapping stat ID to local slot index
- `BaseValues[]` — the raw base value per stat
- `CachedValues[]` — the fully evaluated final value per stat
- `Modifiers[]` — a `List<StatModifier>` per stat (lazily allocated, null until first modifier)

### Modifier Pipeline

When a stat is evaluated, the pipeline runs in this order:

1. **Base value** — for normal stats, the stored base. For derived stats, the formula result plus the stored base.
2. **Flat modifiers** — summed and added to base. `base + flat1 + flat2 + ...`
3. **PercentAdd modifiers** — all summed into one multiplier, applied once. `× (1 + sum_of_all_percent_add)`
4. **PercentMult modifiers** — each applied sequentially. `× (1 + pct1) × (1 + pct2) × ...`
5. **Constraints** — min/max clamp and optional rounding from the stat definition.

Modifiers within the same type are sorted by an `Order` field for deterministic evaluation.

### Eager Evaluation

All mutations (`AddModifier`, `RemoveModifier`, `SetBaseValue`, `RemoveAllFromSource`, timed expiration) immediately recalculate the affected stat and cascade through the dependency graph. `GetValue()` is a direct array read with zero computation.

This means stat values are always consistent — there is no frame where a stat reads stale. Resource pool auto-clamping, UI updates via `OnStatChanged`, and AI queries all see correct values immediately after any mutation.

### Dependency Graph

Built incrementally as `StatDefinition` (with derivation fields populated) assets are registered via `RegisterStatDefinition()`. Stored as `Dictionary<int, int[]>` mapping each stat to the stats that depend on it. When a stat's final value changes during recalculation, all dependents are recursively recalculated. Multi-level chains (Endurance → MaxHealth → HealthRegen) cascade automatically. A mod registering a new derived stat that depends on a core stat updates the graph at registration time — no rebuild needed.

### Derivation: Three Tiers

When a stat recalculates, the system determines its base value using the first match in this priority order:

1. **Custom C# evaluator** — a delegate registered via `RegisterCustomEvaluator(statId, evaluator)`. Full procedural control: conditionals, loops, external system lookups. Used for complex formulas that would be unreadable in postfix notation (weighted skill averages, conditional attribute composition, etc.).
2. **Postfix formula** — from the `StatDefinition` (with derivation fields populated) asset. Data-driven, zero-allocation, inspector-editable. Used for straightforward arithmetic relationships between stats.
3. **Stored base value** — the raw float in the slot. Used for non-derived stats.

All three feed into the same pipeline: the evaluator/formula produces a "derived base," the stored base value is added on top (allowing level-up bonuses to stack with the formula), then the modifier pipeline runs (Flat → PercentAdd → PercentMult → constraints).

Custom evaluators receive a `StatQuery` struct — a lightweight read-only accessor with `GetValue()`, `GetBaseValue()`, and `HasStat()` methods that safely read other stats on the same entity, triggering dependency recalculation as needed. The dependency graph must still be declared on the `StatDefinition` (with derivation fields populated) for cascade to work — the custom evaluator replaces the formula computation, not the dependency wiring.

Custom evaluators can be registered and removed at runtime. Removing one causes the stat to fall back to its postfix formula (if any) or plain base value. This supports mods overriding core stat derivations without modifying the original definition asset.

### Timed Modifiers

Stored in a single flat `List<TimedEntry>` across all entities. `TickTimedModifiers(deltaTime)` is called once per frame from whatever drives the simulation. Expired modifiers are removed from their stat and the list in the same pass. Each entry stores the entity index, generation, stat slot, the modifier itself, and the remaining time — fully serializable.

### Source-Based Removal

Every `StatModifier` carries a `long SourceId`. Calling `RemoveAllFromSource(handle, sourceId)` strips every modifier from that source across every stat on the entity in one pass. This is the primary mechanism for equipment unequip, buff dispel, and effect removal.

### Serialization

Two capture modes:

**Full capture** — `CaptureEntityState(handle)` returns a `FullEntitySaveData` struct containing every stat's base value, all active modifiers, and timed modifier countdowns. Use when you need a complete snapshot regardless of whether anything changed.

**Delta capture** — `CaptureEntityStateDelta(handle)` returns only stats that differ from their definition defaults (base value changed or modifiers present). An untouched NPC produces empty arrays — zero bytes in the change map. `HasStatChanges(handle)` is a fast check to skip serialization entirely for pristine entities.

Both produce the same `FullEntitySaveData` struct. `RestoreEntityState` handles sparse delta data correctly — stats not present in the save keep their registration defaults. The consuming save system serializes these structs however it wants (JSON, MessagePack, binary, etc.).

### Bulk Registration

`RegisterEntities(count, statIds, outHandles)` pre-grows the entity array once and allocates all slots in a tight loop. Much faster than individual registration when loading a chunk with hundreds of NPCs. `UnregisterEntities(handles)` scans the timed modifier list once for all entities in the batch rather than once per entity — the key win when unloading a chunk where some NPCs had active timed effects.

`ResourcePoolManager.RegisterEntities(handles)` and `UnregisterEntities(handles)` provide matching bulk paths.

## Debug Tooling

### Stat Breakdown

`GetStatBreakdown(handle, statId)` returns a `StatBreakdown` struct showing every stage of the evaluation pipeline: stored base value, formula result (for derived stats), effective base, flat total, value after flat, PercentAdd sum, value after PercentAdd, value after each PercentMult, final constrained value, the full modifier list, and timed modifier info with remaining durations. Allocates — intended for debug tools, editor overlays, and console commands, not hot paths.

### Entity Snapshot

`GetEntitySnapshot(handle)` returns a `StatBreakdown` for every stat on the entity plus a `PoolSnapshot` for every resource pool (current, max, shield, ratio). Full picture of an entity's state in one call. Both `StatBreakdown` and `EntityStatSnapshot` have `ToString()` overrides producing formatted multi-line readouts.

### Snapshot Diff

`StatDebug.DiffSaveData(before, after)` and `StatDebug.DiffPoolData(before, after)` take two serialized snapshots and return a `SnapshotDiff` listing every stat that changed: base value changes, modifier additions, modifier removals, pool current changes, shield changes. Works on save data, not live entities — diff two save files offline to answer "what happened to this NPC between these saves."

## ResourcePoolManager

A companion manager for current/max resource pairs (health, stamina, magicka). Operates on the same `EntityStatHandle` — no separate handle system.

### Pool Definitions

Registered at initialization as `PoolDefinition` structs, each mapping a pool ID to a max stat ID on StatManager. Pools are created per-entity only for definitions whose max stat exists on that entity.

### Current, Shield, and Max

Each pool tracks three values:

- **Current** — the primary resource value. Clamped to `[0, max]`. Reduced by damage, increased by healing.
- **Shield** — a temporary buffer absorbed before current when taking damage. Not clamped by max. Not affected by max stat changes. Decays independently.
- **Max** — read from StatManager. Not stored separately. Always queries the live stat value.

### Auto-Clamp

ResourcePoolManager subscribes to `StatManager.OnStatChanged`. When a max stat decreases (e.g., Fortify Health expires), current is clamped to the new max immediately in the same call stack. Shield is never clamped by this mechanism.

### Events

Three events let UI and gameplay react without polling:

- `OnPoolChanged(handle, poolId, oldCurrent, newCurrent)` — current value changed (damage, heal, regen, set, auto-clamp).
- `OnShieldChanged(handle, poolId, oldShield, newShield)` — shield changed (add, set, clear, decay, absorption).
- `OnPoolDepleted(handle, poolId)` — fired once when current transitions from above zero to `<= 0`. Does not re-fire while depleted. Use for death/knock-out handling instead of polling `IsDepleted`.

### Regen

`TickRegen(poolId, regenStatId, deltaTime)` batch-iterates all entities, reads their regen rate stat, and adds to current capped at max. One call in the game loop handles regen for every entity in the world.

### Shield Decay

`TickShieldDecay(poolId, decayPerSecond, deltaTime)` batch-iterates all entities and reduces shield toward zero. Separate from regen — different tick rates, different design intent.

## Custom Editor

### StatDefinitionEditor

A unified inspector for all stat definitions. Draws identity, base value, and constraints with conditional visibility (min/max fields only show when toggled on). Shows the resolved stat name from `StatIds` constants next to the integer ID. When derivation fields are populated (dependencies and/or formula), the inspector shows the dependency list with add/remove controls, the formula operation list with per-op-type contextual fields, reorder/delete buttons, quick-add buttons, and a live infix preview that reconstructs the equivalent mathematical expression from the postfix operations. Validates that all stat references in the formula appear in the dependency list. Summary bar at the bottom shows the stat's full configuration at a glance, including whether it's derived.

## File Map

| File | Purpose |
|---|---|
| `StatManager.cs` | Central stat storage, modifiers, evaluation, timed modifiers, bulk ops, serialization |
| `ResourcePoolManager.cs` | Current/max pools, shield, damage, heal, regen, auto-clamp, bulk ops |
| `StatModifier.cs` | Serializable struct: value, type, order, sourceId |
| `StatDefinition.cs` | ScriptableObject: stat identity, base value, constraints, optional derivation |
| `StatDefinitionDatabase.cs` | Per-plugin definition manifest and lookup/constraint cache |
| `StatFormula.cs` | Postfix formula ops, zero-allocation evaluator |
| `EntityStatHandle.cs` | Lightweight generational handle struct |
| `StatIds.cs` | Integer constants for core stat IDs |
| `StatManagerExtensions.cs` | Handle extension methods for stat operations |
| `ResourcePoolExtensions.cs` | Handle extension methods for pool/shield operations |
| `StatDebug.cs` | Debug structures (StatBreakdown, SnapshotDiff) and diff utilities |
| `Editor/StatDefinitionEditor.cs` | Custom inspector with conditional derivation UI, formula preview, and dependency validation |
| `Tests/StatSystemTests.cs` | Comprehensive EditMode test suite (Unity Test Framework) |
| `Samples~/UsageExample/` | Importable usage-example sample (via Package Manager) |
| `Docs/Architecture.md` | This document |
| `Docs/Examples.md` | Common RPG patterns and code examples |
| `Docs/FormulaGuide.md` | Postfix formula authoring guide |

## Subsystem Registration

Both managers use `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` to null all static state on domain reload. Safe for Enter Play Mode Settings with domain reload disabled.