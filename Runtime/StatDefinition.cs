using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Data-driven definition of a stat. One asset per stat type in the project.
    /// Defines identity, default values, clamping behavior, and optional derivation.
    /// 
    /// A stat with no formula is a base stat (Strength, MaxHealth, etc.).
    /// A stat with a formula and dependencies is a derived stat (CarryWeight = f(Strength), etc.).
    /// There is no separate type — derivation is a property, not a category.
    /// </summary>
    [CreateAssetMenu(fileName = "NewStatDefinition", menuName = "RPG/Stats/Stat Definition")]
    public class StatDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable integer ID. Must be unique. Never change after shipping.")]
        [SerializeField] private int statId;

        [SerializeField] private string displayName;
        [SerializeField, TextArea(2, 4)] private string description;

        [Header("Base Value & Constraints")]
        [Tooltip("The starting base value before any modifiers or formula. " +
                 "For derived stats, this is added on top of the formula result " +
                 "(set to 0 if the formula should be the sole contributor).")]
        [SerializeField] private float defaultBaseValue;

        [SerializeField] private bool  hasMinValue;
        [SerializeField] private float minValue;
        [SerializeField] private bool  hasMaxValue;
        [SerializeField] private float maxValue = 100f;
        [SerializeField] private bool  roundToInt;

        [Header("Derivation (Optional)")]
        [Tooltip("Which stats this formula reads. When any of these change, " +
                 "this stat recalculates automatically.\n\n" +
                 "Leave empty for base stats (no derivation).")]
        [SerializeField] private int[] dependencies;

        [Tooltip("Postfix (RPN) formula that computes the derived base value.\n" +
                 "Leave empty for base stats.\n\n" +
                 "Example for MaxHealth = 100 + (Endurance * 5):\n" +
                 "  PushConstant(100), PushStat(Endurance), PushConstant(5), Multiply, Add")]
        [SerializeField] private StatFormulaOp[] formula;

        // --- Public API ---

        public int    StatId           => statId;
        public string DisplayName      => displayName;
        public string Description      => description;
        public float  DefaultBaseValue => defaultBaseValue;
        public bool   HasMinValue      => hasMinValue;
        public float  MinValue         => minValue;
        public bool   HasMaxValue      => hasMaxValue;
        public float  MaxValue         => maxValue;
        public bool   RoundToInt       => roundToInt;

        /// <summary>Stat IDs this stat depends on. Null or empty for base stats.</summary>
        public int[] Dependencies => dependencies;

        /// <summary>The postfix formula operations. Null or empty for base stats.</summary>
        public StatFormulaOp[] Formula => formula;

        /// <summary>Whether this stat has derivation logic (formula or custom evaluator).</summary>
        public bool HasFormula => formula != null && formula.Length > 0;

        /// <summary>Whether this stat declares dependencies on other stats.</summary>
        public bool HasDependencies => dependencies != null && dependencies.Length > 0;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (hasMinValue && hasMaxValue && minValue > maxValue)
            {
                Debug.LogWarning($"StatDefinition '{displayName}': min > max. Clamping.", this);
                minValue = maxValue;
            }

            // Warn if formula references a stat not in the dependency list.
            if (dependencies != null && formula != null)
            {
                var depSet = new System.Collections.Generic.HashSet<int>(dependencies);
                for (int i = 0; i < formula.Length; i++)
                {
                    var op = formula[i];
                    if (op.OpType is FormulaOpType.PushStat or FormulaOpType.PushStatBase)
                    {
                        if (!depSet.Contains(op.StatId))
                        {
                            Debug.LogWarning(
                                $"StatDefinition '{displayName}': formula references statId {op.StatId} " +
                                "but it's not in the dependencies array. Add it or changes won't cascade.",
                                this);
                        }
                    }
                }
            }
        }
#endif
    }
}