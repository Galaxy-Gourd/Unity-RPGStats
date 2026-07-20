using System;
using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Operation type for a single step in a postfix (RPN) stat formula.
    /// Formulas are authored as an array of StatFormulaOp and evaluated
    /// against a fixed-size stack with zero heap allocation.
    /// </summary>
    public enum FormulaOpType : byte
    {
        /// <summary>Push a constant float value onto the stack.</summary>
        PushConstant = 0,

        /// <summary>
        /// Push the final evaluated value of another stat onto the stack.
        /// The stat is identified by the int stored in StatId.
        /// If the entity doesn't have that stat, pushes 0.
        /// </summary>
        PushStat = 1,

        /// <summary>
        /// Push the base value (before modifiers) of another stat.
        /// Useful when the formula should read the raw base, not the modified value.
        /// </summary>
        PushStatBase = 2,

        /// <summary>Pop two values, push (a + b).</summary>
        Add = 10,

        /// <summary>Pop two values, push (a - b). Note: a is deeper, b is top.</summary>
        Subtract = 11,

        /// <summary>Pop two values, push (a * b).</summary>
        Multiply = 12,

        /// <summary>Pop two values, push (a / b). Division by zero returns 0.</summary>
        Divide = 13,

        /// <summary>Pop one value, push min(top, Constant).</summary>
        Min = 20,

        /// <summary>Pop one value, push max(top, Constant).</summary>
        Max = 21,

        /// <summary>Pop one value, push floor(top).</summary>
        Floor = 22,

        /// <summary>Pop one value, push ceil(top).</summary>
        Ceil = 23,

        /// <summary>Pop one value, push round(top).</summary>
        Round = 24,
    }

    /// <summary>
    /// A single operation in a stat formula. Serializable, value type.
    /// 
    /// For PushConstant / Min / Max: use the Constant field.
    /// For PushStat / PushStatBase: use the StatId field.
    /// For arithmetic ops: no extra data needed.
    /// </summary>
    [Serializable]
    public struct StatFormulaOp
    {
        public FormulaOpType OpType;
        public float Constant;
        public int   StatId;

        // --- Factory methods for readable formula construction ---

        public static StatFormulaOp Const(float value) =>
            new() { OpType = FormulaOpType.PushConstant, Constant = value };

        public static StatFormulaOp Stat(int statId) =>
            new() { OpType = FormulaOpType.PushStat, StatId = statId };

        public static StatFormulaOp StatBase(int statId) =>
            new() { OpType = FormulaOpType.PushStatBase, StatId = statId };

        public static StatFormulaOp Add() =>
            new() { OpType = FormulaOpType.Add };

        public static StatFormulaOp Sub() =>
            new() { OpType = FormulaOpType.Subtract };

        public static StatFormulaOp Mul() =>
            new() { OpType = FormulaOpType.Multiply };

        public static StatFormulaOp Div() =>
            new() { OpType = FormulaOpType.Divide };

        public static StatFormulaOp Min(float value) =>
            new() { OpType = FormulaOpType.Min, Constant = value };

        public static StatFormulaOp Max(float value) =>
            new() { OpType = FormulaOpType.Max, Constant = value };

        public static StatFormulaOp Floor() =>
            new() { OpType = FormulaOpType.Floor };

        public static StatFormulaOp Ceil() =>
            new() { OpType = FormulaOpType.Ceil };

        public static StatFormulaOp Round() =>
            new() { OpType = FormulaOpType.Round };
    }

    /// <summary>
    /// Evaluates a formula against an entity's stats. Uses a small fixed stack
    /// on the... stack (stackalloc), so zero heap allocation per evaluation.
    /// 
    /// Takes an entityIndex directly and calls back into StatManager's internal
    /// methods for stat lookups, avoiding delegate allocation.
    /// </summary>
    public static class StatFormulaEvaluator
    {
        private const int MaxStackDepth = 16;

        /// <summary>
        /// Function pointers for stat lookup, set once by StatManager.Boot().
        /// Avoids circular type dependency while keeping zero-allocation evaluation.
        /// Internal: these are plumbing wired up by StatManager, not a public API.
        /// </summary>
        internal static Func<int, int, float> GetStatValueFn;
        internal static Func<int, int, float> GetBaseValueFn;

        // Latched so a malformed formula reaching Evaluate logs once, not every frame.
        private static bool _malformedLogged;

        /// <summary>Reset one-shot diagnostics latches. Called by StatManager on boot/reset.</summary>
        internal static void ResetDiagnostics() => _malformedLogged = false;

        /// <summary>
        /// Statically validate a formula: every op has its operands and the program
        /// leaves exactly one value on the stack, never exceeding the fixed stack depth.
        /// Call at registration so malformed data is rejected up front instead of
        /// throwing inside the recalculation path. Returns true for null/empty (no-op).
        /// </summary>
        public static bool Validate(StatFormulaOp[] ops, out string error)
        {
            error = null;
            if (ops == null || ops.Length == 0) return true;

            int sp = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                switch (ops[i].OpType)
                {
                    case FormulaOpType.PushConstant:
                    case FormulaOpType.PushStat:
                    case FormulaOpType.PushStatBase:
                        if (sp >= MaxStackDepth)
                        {
                            error = $"stack overflow at op {i} ({ops[i].OpType}) — exceeds max depth {MaxStackDepth}";
                            return false;
                        }
                        sp++;
                        break;

                    case FormulaOpType.Add:
                    case FormulaOpType.Subtract:
                    case FormulaOpType.Multiply:
                    case FormulaOpType.Divide:
                        if (sp < 2)
                        {
                            error = $"stack underflow at op {i} ({ops[i].OpType}) — needs 2 operands, has {sp}";
                            return false;
                        }
                        sp--; // pop 2, push 1
                        break;

                    case FormulaOpType.Min:
                    case FormulaOpType.Max:
                    case FormulaOpType.Floor:
                    case FormulaOpType.Ceil:
                    case FormulaOpType.Round:
                        if (sp < 1)
                        {
                            error = $"stack underflow at op {i} ({ops[i].OpType}) — needs 1 operand, has {sp}";
                            return false;
                        }
                        break; // pop 1, push 1 (net zero)

                    default:
                        error = $"unknown op {ops[i].OpType} at op {i}";
                        return false;
                }
            }

            if (sp != 1)
            {
                error = $"formula leaves {sp} values on the stack, expected exactly 1 (missing an operator?)";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Evaluate a formula for a given entity. Zero heap allocation.
        /// </summary>
        /// <param name="ops">The postfix formula operations.</param>
        /// <param name="entityIndex">Index into StatManager's entity array.</param>
        public static float Evaluate(StatFormulaOp[] ops, int entityIndex)
        {
            if (ops == null || ops.Length == 0) return 0f;

            Span<float> stack = stackalloc float[MaxStackDepth];
            int sp = 0; // stack pointer (next free slot)

            for (int i = 0; i < ops.Length; i++)
            {
                ref var op = ref ops[i];

                switch (op.OpType)
                {
                    case FormulaOpType.PushConstant:
                        if (sp >= MaxStackDepth) return ReportMalformed(i, "stack overflow");
                        stack[sp++] = op.Constant;
                        break;

                    case FormulaOpType.PushStat:
                        if (sp >= MaxStackDepth) return ReportMalformed(i, "stack overflow");
                        stack[sp++] = GetStatValueFn(entityIndex, op.StatId);
                        break;

                    case FormulaOpType.PushStatBase:
                        if (sp >= MaxStackDepth) return ReportMalformed(i, "stack overflow");
                        stack[sp++] = GetBaseValueFn(entityIndex, op.StatId);
                        break;

                    case FormulaOpType.Add:
                    {
                        if (sp < 2) return ReportMalformed(i, "stack underflow");
                        float b = stack[--sp];
                        float a = stack[--sp];
                        stack[sp++] = a + b;
                        break;
                    }

                    case FormulaOpType.Subtract:
                    {
                        if (sp < 2) return ReportMalformed(i, "stack underflow");
                        float b = stack[--sp];
                        float a = stack[--sp];
                        stack[sp++] = a - b;
                        break;
                    }

                    case FormulaOpType.Multiply:
                    {
                        if (sp < 2) return ReportMalformed(i, "stack underflow");
                        float b = stack[--sp];
                        float a = stack[--sp];
                        stack[sp++] = a * b;
                        break;
                    }

                    case FormulaOpType.Divide:
                    {
                        if (sp < 2) return ReportMalformed(i, "stack underflow");
                        float b = stack[--sp];
                        float a = stack[--sp];
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        stack[sp++] = b == 0f ? 0f : a / b;
                        break;
                    }

                    case FormulaOpType.Min:
                        if (sp < 1) return ReportMalformed(i, "stack underflow");
                        stack[sp - 1] = Mathf.Min(stack[sp - 1], op.Constant);
                        break;

                    case FormulaOpType.Max:
                        if (sp < 1) return ReportMalformed(i, "stack underflow");
                        stack[sp - 1] = Mathf.Max(stack[sp - 1], op.Constant);
                        break;

                    case FormulaOpType.Floor:
                        if (sp < 1) return ReportMalformed(i, "stack underflow");
                        stack[sp - 1] = Mathf.Floor(stack[sp - 1]);
                        break;

                    case FormulaOpType.Ceil:
                        if (sp < 1) return ReportMalformed(i, "stack underflow");
                        stack[sp - 1] = Mathf.Ceil(stack[sp - 1]);
                        break;

                    case FormulaOpType.Round:
                        if (sp < 1) return ReportMalformed(i, "stack underflow");
                        stack[sp - 1] = Mathf.Round(stack[sp - 1]);
                        break;
                }
            }

            return sp > 0 ? stack[0] : 0f;
        }

        /// <summary>
        /// Backstop for a formula that reached evaluation despite failing validation
        /// (e.g., mutated after registration). Logs once and returns a safe 0 instead
        /// of throwing an IndexOutOfRange in the recalculation path.
        /// </summary>
        private static float ReportMalformed(int opIndex, string reason)
        {
            if (!_malformedLogged)
            {
                _malformedLogged = true;
                StatLog.Error(
                    $"StatFormulaEvaluator: malformed formula at op {opIndex} ({reason}). " +
                    "This formula was not caught by Validate() at registration. Returning 0.");
            }
            return 0f;
        }
    }
}