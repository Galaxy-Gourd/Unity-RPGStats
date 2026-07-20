#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace RPG.Stats.Editor
{
    [CustomEditor(typeof(StatDefinition))]
    [CanEditMultipleObjects]
    public class StatDefinitionEditor : UnityEditor.Editor
    {
        // Base fields
        private SerializedProperty _statId;
        private SerializedProperty _displayName;
        private SerializedProperty _description;
        private SerializedProperty _defaultBaseValue;
        private SerializedProperty _hasMinValue;
        private SerializedProperty _minValue;
        private SerializedProperty _hasMaxValue;
        private SerializedProperty _maxValue;
        private SerializedProperty _roundToInt;

        // Derivation fields
        private SerializedProperty _dependencies;
        private SerializedProperty _formula;

        private static Dictionary<int, string> _statNames;
        private static bool _statNamesCached;

        private static GUIStyle _headerStyle;
        private static GUIStyle _idLabelStyle;
        private static GUIStyle _previewStyle;
        private static GUIStyle _errorStyle;

        private void OnEnable()
        {
            _statId           = serializedObject.FindProperty("statId");
            _displayName      = serializedObject.FindProperty("displayName");
            _description      = serializedObject.FindProperty("description");
            _defaultBaseValue = serializedObject.FindProperty("defaultBaseValue");
            _hasMinValue      = serializedObject.FindProperty("hasMinValue");
            _minValue         = serializedObject.FindProperty("minValue");
            _hasMaxValue      = serializedObject.FindProperty("hasMaxValue");
            _maxValue         = serializedObject.FindProperty("maxValue");
            _roundToInt       = serializedObject.FindProperty("roundToInt");
            _dependencies     = serializedObject.FindProperty("dependencies");
            _formula          = serializedObject.FindProperty("formula");

            EnsureStatNameCache();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            // --- Identity ---
            EditorGUILayout.LabelField("Identity", _headerStyle);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_statId, GUILayout.ExpandWidth(true));
            string knownName = GetStatName(_statId.intValue);
            if (knownName != null)
                EditorGUILayout.LabelField(knownName, _idLabelStyle, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_displayName);
            EditorGUILayout.PropertyField(_description);

            // --- Value ---
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Base Value", _headerStyle);

            string baseTooltip = (_formula.arraySize > 0)
                ? "Added on top of the formula result. Set to 0 if the formula should be the sole contributor."
                : "Default value before any modifiers.";
            EditorGUILayout.PropertyField(_defaultBaseValue, new GUIContent("Default", baseTooltip));

            // --- Constraints ---
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Constraints", _headerStyle);

            DrawConstraintToggle(_hasMinValue, _minValue, "Has Minimum", "Min Value");
            DrawConstraintToggle(_hasMaxValue, _maxValue, "Has Maximum", "Max Value");
            EditorGUILayout.PropertyField(_roundToInt, new GUIContent("Round to Integer"));

            if (_hasMinValue.boolValue && _hasMaxValue.boolValue &&
                !_hasMinValue.hasMultipleDifferentValues && !_hasMaxValue.hasMultipleDifferentValues &&
                _minValue.floatValue > _maxValue.floatValue)
            {
                EditorGUILayout.HelpBox(
                    $"Min ({_minValue.floatValue}) is greater than Max ({_maxValue.floatValue}).",
                    MessageType.Warning);
            }

            // --- Derivation ---
            EditorGUILayout.Space(8);
            bool hasDeps = _dependencies.arraySize > 0;
            bool hasFormula = _formula.arraySize > 0;
            bool isDerived = hasDeps || hasFormula;

            string derivLabel = isDerived ? "Derivation" : "Derivation (none — base stat)";
            EditorGUILayout.LabelField(derivLabel, _headerStyle);

            DrawDependencies();
            EditorGUILayout.Space(4);
            DrawFormula();

            if (hasFormula)
            {
                EditorGUILayout.Space(4);
                DrawFormulaPreview();
            }

            // --- Summary ---
            if (!serializedObject.isEditingMultipleObjects)
            {
                EditorGUILayout.Space(4);
                DrawSummary(isDerived);
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ==================================================================
        // Constraint toggle
        // ==================================================================

        private void DrawConstraintToggle(SerializedProperty toggle, SerializedProperty value,
            string toggleLabel, string valueLabel)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(toggle, new GUIContent(toggleLabel), GUILayout.Width(180));

            if (toggle.boolValue && !toggle.hasMultipleDifferentValues)
                EditorGUILayout.PropertyField(value, new GUIContent(valueLabel));
            else if (!toggle.boolValue)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("—", GUILayout.Width(40));
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ==================================================================
        // Dependencies
        // ==================================================================

        private void DrawDependencies()
        {
            EditorGUILayout.LabelField("Dependencies",
                "Stats this formula reads. Changes to these trigger recalculation. Leave empty for base stats.");
            EditorGUI.indentLevel++;

            int depCount = _dependencies.arraySize;
            for (int i = 0; i < depCount; i++)
            {
                EditorGUILayout.BeginHorizontal();

                var elem = _dependencies.GetArrayElementAtIndex(i);
                string label = GetStatName(elem.intValue) ?? $"#{elem.intValue}";

                EditorGUILayout.LabelField($"[{i}]", GUILayout.Width(28));
                elem.intValue = EditorGUILayout.IntField(elem.intValue, GUILayout.Width(60));
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

                if (GUILayout.Button("×", GUILayout.Width(20)))
                {
                    _dependencies.DeleteArrayElementAtIndex(i);
                    break;
                }

                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button("+ Add Dependency", GUILayout.Width(140)))
            {
                _dependencies.InsertArrayElementAtIndex(depCount);
                _dependencies.GetArrayElementAtIndex(depCount).intValue = 0;
            }

            EditorGUI.indentLevel--;
        }

        // ==================================================================
        // Formula
        // ==================================================================

        private void DrawFormula()
        {
            EditorGUILayout.LabelField("Formula (Postfix / RPN)",
                "Operations evaluated top-to-bottom using a stack. Leave empty for base stats.");
            EditorGUI.indentLevel++;

            int count = _formula.arraySize;
            int deleteIndex = -1;
            int moveUpIndex = -1;
            int moveDownIndex = -1;

            for (int i = 0; i < count; i++)
            {
                var elem = _formula.GetArrayElementAtIndex(i);
                var opTypeProp   = elem.FindPropertyRelative("OpType");
                var constantProp = elem.FindPropertyRelative("Constant");
                var statIdProp   = elem.FindPropertyRelative("StatId");

                var opType = (FormulaOpType)opTypeProp.enumValueIndex;

                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.LabelField($"{i}:", GUILayout.Width(22));

                var newOpType = (FormulaOpType)EditorGUILayout.EnumPopup(opType, GUILayout.Width(110));
                if (newOpType != opType)
                {
                    opTypeProp.enumValueIndex = (int)newOpType;
                    opType = newOpType;
                }

                switch (opType)
                {
                    case FormulaOpType.PushConstant:
                        EditorGUILayout.LabelField("=", GUILayout.Width(12));
                        constantProp.floatValue = EditorGUILayout.FloatField(constantProp.floatValue, GUILayout.Width(60));
                        break;

                    case FormulaOpType.PushStat:
                    case FormulaOpType.PushStatBase:
                        EditorGUILayout.LabelField("id:", GUILayout.Width(16));
                        statIdProp.intValue = EditorGUILayout.IntField(statIdProp.intValue, GUILayout.Width(50));
                        string name = GetStatName(statIdProp.intValue) ?? $"#{statIdProp.intValue}";
                        EditorGUILayout.LabelField(name, EditorStyles.miniLabel, GUILayout.Width(80));
                        break;

                    case FormulaOpType.Min:
                    case FormulaOpType.Max:
                        EditorGUILayout.LabelField("=", GUILayout.Width(12));
                        constantProp.floatValue = EditorGUILayout.FloatField(constantProp.floatValue, GUILayout.Width(60));
                        break;

                    default:
                        EditorGUILayout.LabelField(GetOpSymbol(opType), EditorStyles.boldLabel, GUILayout.Width(30));
                        break;
                }

                GUI.enabled = i > 0;
                if (GUILayout.Button("▲", GUILayout.Width(22))) moveUpIndex = i;
                GUI.enabled = i < count - 1;
                if (GUILayout.Button("▼", GUILayout.Width(22))) moveDownIndex = i;
                GUI.enabled = true;
                if (GUILayout.Button("×", GUILayout.Width(20))) deleteIndex = i;

                EditorGUILayout.EndHorizontal();
            }

            if (deleteIndex >= 0)
                _formula.DeleteArrayElementAtIndex(deleteIndex);
            if (moveUpIndex > 0)
                _formula.MoveArrayElement(moveUpIndex, moveUpIndex - 1);
            if (moveDownIndex >= 0 && moveDownIndex < count - 1)
                _formula.MoveArrayElement(moveDownIndex, moveDownIndex + 1);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Constant", GUILayout.Width(90)))
                AddOp(FormulaOpType.PushConstant);
            if (GUILayout.Button("+ Stat", GUILayout.Width(70)))
                AddOp(FormulaOpType.PushStat);
            if (GUILayout.Button("+ Add", GUILayout.Width(60)))
                AddOp(FormulaOpType.Add);
            if (GUILayout.Button("+ Mul", GUILayout.Width(60)))
                AddOp(FormulaOpType.Multiply);
            if (GUILayout.Button("+ Sub", GUILayout.Width(60)))
                AddOp(FormulaOpType.Subtract);
            if (GUILayout.Button("+ Div", GUILayout.Width(60)))
                AddOp(FormulaOpType.Divide);
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel--;
        }

        private void AddOp(FormulaOpType type)
        {
            int idx = _formula.arraySize;
            _formula.InsertArrayElementAtIndex(idx);
            var elem = _formula.GetArrayElementAtIndex(idx);
            elem.FindPropertyRelative("OpType").enumValueIndex = (int)type;
            elem.FindPropertyRelative("Constant").floatValue = 0f;
            elem.FindPropertyRelative("StatId").intValue = 0;
        }

        // ==================================================================
        // Infix preview + stack validation
        // ==================================================================

        private void DrawFormulaPreview()
        {
            EditorGUILayout.LabelField("Preview", _headerStyle);

            int count = _formula.arraySize;
            if (count == 0) return;

            var stack = new List<string>();
            string error = null;

            for (int i = 0; i < count; i++)
            {
                var elem = _formula.GetArrayElementAtIndex(i);
                var opType     = (FormulaOpType)elem.FindPropertyRelative("OpType").enumValueIndex;
                float constant = elem.FindPropertyRelative("Constant").floatValue;
                int statId     = elem.FindPropertyRelative("StatId").intValue;

                switch (opType)
                {
                    case FormulaOpType.PushConstant:
                        stack.Add(FormatConstant(constant));
                        break;
                    case FormulaOpType.PushStat:
                        stack.Add(GetStatName(statId) ?? $"#{statId}");
                        break;
                    case FormulaOpType.PushStatBase:
                        stack.Add($"base({GetStatName(statId) ?? $"#{statId}"})");
                        break;
                    case FormulaOpType.Add:
                    case FormulaOpType.Subtract:
                    case FormulaOpType.Multiply:
                    case FormulaOpType.Divide:
                    {
                        if (stack.Count < 2) { error = $"Stack underflow at op {i} ({opType})"; goto Done; }
                        string b = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        string a = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        string op = opType switch
                        {
                            FormulaOpType.Add      => "+",
                            FormulaOpType.Subtract => "-",
                            FormulaOpType.Multiply => "*",
                            FormulaOpType.Divide   => "/",
                            _ => "?"
                        };
                        bool needParensA = a.Contains('+') || a.Contains('-');
                        bool needParensB = b.Contains('+') || b.Contains('-');
                        if (opType is FormulaOpType.Multiply or FormulaOpType.Divide)
                        {
                            if (needParensA) a = $"({a})";
                            if (needParensB) b = $"({b})";
                        }
                        stack.Add($"{a} {op} {b}");
                        break;
                    }
                    case FormulaOpType.Min:
                    {
                        if (stack.Count < 1) { error = $"Stack underflow at op {i} (Min)"; goto Done; }
                        string val = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add($"min({val}, {FormatConstant(constant)})");
                        break;
                    }
                    case FormulaOpType.Max:
                    {
                        if (stack.Count < 1) { error = $"Stack underflow at op {i} (Max)"; goto Done; }
                        string val = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add($"max({val}, {FormatConstant(constant)})");
                        break;
                    }
                    case FormulaOpType.Floor:
                    case FormulaOpType.Ceil:
                    case FormulaOpType.Round:
                    {
                        if (stack.Count < 1) { error = $"Stack underflow at op {i} ({opType})"; goto Done; }
                        string val = stack[^1]; stack.RemoveAt(stack.Count - 1);
                        stack.Add($"{opType.ToString().ToLower()}({val})");
                        break;
                    }
                }
            }

            Done:

            if (error != null)
            {
                EditorGUILayout.LabelField(error, _errorStyle);
            }
            else if (stack.Count == 0)
            {
                EditorGUILayout.LabelField("(empty stack — formula produces no value)", _errorStyle);
            }
            else if (stack.Count > 1)
            {
                EditorGUILayout.LabelField($"Stack has {stack.Count} values — expected 1. Missing an operator?", _errorStyle);
                for (int i = 0; i < stack.Count; i++)
                    EditorGUILayout.LabelField($"  [{i}]: {stack[i]}", EditorStyles.miniLabel);
            }
            else
            {
                string displayNameVal = _displayName.stringValue;
                if (string.IsNullOrEmpty(displayNameVal))
                    displayNameVal = $"Stat_{_statId.intValue}";

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField($"{displayNameVal} = {stack[0]}", _previewStyle);
                EditorGUILayout.EndVertical();
            }

            // Dependency cross-check.
            var depSet = new HashSet<int>();
            for (int i = 0; i < _dependencies.arraySize; i++)
                depSet.Add(_dependencies.GetArrayElementAtIndex(i).intValue);

            for (int i = 0; i < count; i++)
            {
                var elem = _formula.GetArrayElementAtIndex(i);
                var opType = (FormulaOpType)elem.FindPropertyRelative("OpType").enumValueIndex;
                if (opType is FormulaOpType.PushStat or FormulaOpType.PushStatBase)
                {
                    int refId = elem.FindPropertyRelative("StatId").intValue;
                    if (!depSet.Contains(refId))
                    {
                        string refName = GetStatName(refId) ?? $"#{refId}";
                        EditorGUILayout.LabelField(
                            $"⚠ Formula references {refName} (id:{refId}) but it's not in Dependencies.",
                            _errorStyle);
                    }
                }
            }
        }

        // ==================================================================
        // Summary
        // ==================================================================

        private void DrawSummary(bool isDerived)
        {
            string name = string.IsNullOrEmpty(_displayName.stringValue)
                ? $"Stat #{_statId.intValue}"
                : _displayName.stringValue;

            string range;
            bool hasMin = _hasMinValue.boolValue;
            bool hasMax = _hasMaxValue.boolValue;

            if (hasMin && hasMax)
                range = $"[{_minValue.floatValue}, {_maxValue.floatValue}]";
            else if (hasMin)
                range = $"[{_minValue.floatValue}, ∞)";
            else if (hasMax)
                range = $"(-∞, {_maxValue.floatValue}]";
            else
                range = "(-∞, ∞)";

            string rounding = _roundToInt.boolValue ? ", rounded" : "";
            string derived = isDerived ? ", derived" : "";

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"{name}  |  base: {_defaultBaseValue.floatValue}  |  range: {range}{rounding}{derived}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        // ==================================================================
        // Utilities
        // ==================================================================

        private static string FormatConstant(float value)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (value == Mathf.Floor(value) && Mathf.Abs(value) < 100000)
                return value.ToString("0");
            return value.ToString("0.###");
        }

        private static string GetOpSymbol(FormulaOpType op) => op switch
        {
            FormulaOpType.Add      => "+",
            FormulaOpType.Subtract => "−",
            FormulaOpType.Multiply => "×",
            FormulaOpType.Divide   => "÷",
            FormulaOpType.Floor    => "⌊⌋",
            FormulaOpType.Ceil     => "⌈⌉",
            FormulaOpType.Round    => "≈",
            _ => op.ToString()
        };

        private static string GetStatName(int statId)
        {
            EnsureStatNameCache();
            return _statNames.TryGetValue(statId, out var name) ? name : null;
        }

        private static void EnsureStatNameCache()
        {
            if (_statNamesCached) return;
            _statNamesCached = true;
            _statNames = new Dictionary<int, string>();

            var fields = typeof(StatIds).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var field in fields)
            {
                if (field.IsLiteral && field.FieldType == typeof(int))
                {
                    int val = (int)field.GetRawConstantValue();
                    _statNames[val] = field.Name;
                }
            }

            var guids = AssetDatabase.FindAssets("t:StatDefinition");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var def = AssetDatabase.LoadAssetAtPath<StatDefinition>(path);
                if (def != null && !_statNames.ContainsKey(def.StatId))
                    _statNames[def.StatId] = def.DisplayName ?? $"#{def.StatId}";
            }
        }

        private static void EnsureStyles()
        {
            _headerStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
            _idLabelStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.5f, 0.8f, 0.5f) },
            };
            _previewStyle ??= new GUIStyle(EditorStyles.label)
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                wordWrap  = true,
            };
            _errorStyle ??= new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(1f, 0.4f, 0.3f) },
                wordWrap = true,
            };
        }
    }
}
#endif