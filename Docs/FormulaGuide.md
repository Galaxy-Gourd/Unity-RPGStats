# Stat Formula System — How It Works and Why

## The Problem

You want to define formulas like these in data:

```
MaxHealth = 100 + (Endurance * 5)
CarryWeight = 50 + (Strength * 10)
HealthRegen = MaxHealth * 0.01
SpellCost = BaseCost * (1 - (SkillLevel * 0.005))
```

These need to be:
- **Data-driven**: Designers edit them in the Unity Inspector, not in C# code
- **Zero allocation**: Evaluated potentially thousands of times per frame across all NPCs
- **Serializable**: Saved as part of a ScriptableObject asset

## The Naive Approach (And Why We Didn't Use It)

The simplest approach would be a string formula parser:

```csharp
[SerializeField] string formula = "100 + (Endurance * 5)";
```

Problems:
- **Parsing strings at runtime is slow** — you'd need a tokenizer, an expression parser, operator precedence handling
- **Allocates garbage** — string splitting, substring creation, intermediate objects
- **Error-prone** — typos in stat names caught only at runtime, if at all
- **Hard to validate** — the Inspector shows a text box with no feedback on correctness

Another approach would be delegates or lambdas:

```csharp
Func<Entity, float> formula = (e) => 100 + e.GetStat(Endurance) * 5;
```

Clean in code, but:
- **Not serializable** — can't save a lambda to a ScriptableObject
- **Not data-driven** — designers need a programmer to write each formula
- **Not moddable** — compiled into the assembly

## What We Actually Do: Postfix Notation (RPN)

We store formulas as an array of simple operations. Each operation is a struct
with a type and (optionally) a number. The evaluator walks the array and uses
a small stack to track intermediate values.

This is called **Reverse Polish Notation** (RPN) or **postfix notation**.
You might know it from old HP calculators. The key idea:

> Instead of writing `3 + 4`, you write `3 4 +`
>
> "Put 3 on the stack. Put 4 on the stack. Add the top two things."

That's it. That's the whole concept.

## Step-by-Step Example

Let's trace through `CarryWeight = 50 + (Strength * 10)`.

### How you'd write this formula in the Inspector:

```
Op 0:  PushConstant   →  50
Op 1:  PushStat       →  StatIds.Strength (1)
Op 2:  PushConstant   →  10
Op 3:  Multiply
Op 4:  Add
```

### How you'd write this in code (using the factory helpers):

```csharp
formula = new StatFormulaOp[]
{
    StatFormulaOp.Const(50f),            // push 50
    StatFormulaOp.Stat(StatIds.Strength), // push current Strength value
    StatFormulaOp.Const(10f),            // push 10
    StatFormulaOp.Mul(),                 // pop 10 and Strength, push Strength*10
    StatFormulaOp.Add(),                 // pop Strength*10 and 50, push 50+Strength*10
};
```

### What happens inside the evaluator:

Assume Strength = 12 for this entity.

```
Step 0: PushConstant(50)
        Stack: [50]

Step 1: PushStat(Strength)    →  looks up Strength on this entity = 12
        Stack: [50, 12]

Step 2: PushConstant(10)
        Stack: [50, 12, 10]

Step 3: Multiply              →  pops 10 and 12, pushes 12 * 10 = 120
        Stack: [50, 120]

Step 4: Add                   →  pops 120 and 50, pushes 50 + 120 = 170
        Stack: [170]

Result: 170 (the one value left on the stack)
```

CarryWeight = 170. Done. No strings parsed, no objects allocated, no garbage generated.

## Converting Normal Math to Postfix

The mental model is: **push your ingredients first, then apply the operation.**

### Simple: `A + B`
```
Push A
Push B
Add
```

### With multiplication: `A + (B * C)`
```
Push A
Push B
Push C
Multiply    ← combines B and C
Add         ← combines A and (B*C)
```

### Chained: `(A + B) * C`
```
Push A
Push B
Add         ← combines A and B first
Push C
Multiply    ← combines (A+B) and C
```

### Real example: `HealthRegen = MaxHealth * 0.01`
```
PushStat(MaxHealth)
PushConstant(0.01)
Multiply
```

### Harder example: `SpellCost = BaseCost * (1 - (SkillLevel * 0.005))`
```
PushStatBase(SpellCost)     ← the spell's base cost from the definition
PushConstant(1)
PushStat(DestructionSkill)
PushConstant(0.005)
Multiply                    ← SkillLevel * 0.005
Subtract                    ← 1 - (SkillLevel * 0.005)
Multiply                    ← BaseCost * result
```

### The Rule of Thumb

Read your formula left to right. Whenever you hit a number or stat, push it.
Whenever you hit an operator (+, -, *, /), write the operation — it'll grab
the two most recent values from the stack.

For nested expressions like `(A + B) * (C + D)`:
```
Push A
Push B
Add         ← stack now has (A+B)
Push C
Push D
Add         ← stack now has (A+B), (C+D)
Multiply    ← stack now has (A+B)*(C+D)
```

## What PushStat vs PushStatBase Means

- **PushStat(Endurance)** → pushes the *final evaluated* Endurance value,
  including all active modifiers. If the entity has a Fortify Endurance
  potion active, this reflects that.

- **PushStatBase(Endurance)** → pushes the *raw base* Endurance value,
  ignoring modifiers. Useful when you want the formula to reference the
  "natural" attribute, not the buffed version.

Most formulas use PushStat (you want buffs to cascade). PushStatBase exists
for edge cases like computing how far a stat has been modified from its
natural value.

## Why This Design

### vs. String parsing
- No tokenizer, no parser, no regex, no string allocation
- Errors caught at authoring time (type-safe enum + int), not at runtime
- Array of structs = blittable, contiguous memory, cache-friendly

### vs. Delegates / hardcoded C#
- Fully serializable (array of structs with [Serializable])
- Data-driven (editable in Inspector, can be authored by designers)
- Moddable (a content pack can define new StatDefinitions with formulas)

### vs. Expression trees / ScriptableObject graphs
- No heap allocation per evaluation (stackalloc)
- No virtual dispatch per node (switch on an enum in a tight loop)
- No object graph to traverse (flat array, linear iteration)
- Simpler to serialize (no nested SO references)

### The tradeoff
- **Harder to author by hand than infix math** — `50 + Strength * 10` is
  more intuitive than `50, Strength, 10, Mul, Add`. This is real and
  acknowledged. The mitigation is the factory methods (StatFormulaOp.Const,
  .Stat, .Mul, .Add) which make code authoring readable, and eventually a
  custom editor drawer that could display the equivalent infix expression
  alongside the postfix ops.

### Zero allocation — why it matters
The evaluator uses `Span<float> stack = stackalloc float[16]` — a 64-byte
scratch buffer on the thread's stack. It's freed automatically when the
method returns. No `new`, no GC pressure, no garbage. When you have 2,000
NPCs each with 3 derived stats, that's 6,000 formula evaluations per
recalculation pass — zero bytes of garbage generated.

## What Happens Automatically

When you set up a StatDefinition with:
- **Formula**: the postfix ops shown above
- **Dependencies**: `[StatIds.Strength]`

The system does the rest:

1. At startup, StatManager builds a dependency graph from all StatDefinitions
2. When Strength changes on an entity (modifier added, base value set, etc.),
   the system marks CarryWeight as dirty on that same entity
3. Next time anything reads CarryWeight, the formula re-evaluates using
   Strength's current value
4. If CarryWeight itself is a dependency of something else, that cascades too

You never manually wire up "when Strength changes, recalculate CarryWeight."
The data declarations handle it.

## Quick Reference: All Operations

| Op Type       | What It Does                                      | Uses Constant? | Uses StatId? |
|---------------|---------------------------------------------------|----------------|--------------|
| PushConstant  | Push a number onto the stack                      | Yes            | No           |
| PushStat      | Push a stat's final (modified) value              | No             | Yes          |
| PushStatBase  | Push a stat's base (unmodified) value             | No             | Yes          |
| Add           | Pop two, push (a + b)                             | No             | No           |
| Subtract      | Pop two, push (a - b)  (deeper - top)             | No             | No           |
| Multiply      | Pop two, push (a × b)                             | No             | No           |
| Divide        | Pop two, push (a ÷ b)  (0 if b is 0)             | No             | No           |
| Min           | Replace top with min(top, constant)               | Yes            | No           |
| Max           | Replace top with max(top, constant)               | Yes            | No           |
| Floor         | Replace top with floor(top)                       | No             | No           |
| Ceil          | Replace top with ceil(top)                        | No             | No           |
| Round         | Replace top with round(top)                       | No             | No           |