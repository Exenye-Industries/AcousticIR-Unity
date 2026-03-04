using AcousticIR.Materials;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for AcousticMaterial with preset buttons and absorption curve preview.
    /// </summary>
    [CustomEditor(typeof(AcousticMaterial))]
    public class AcousticMaterialEditor : UnityEditor.Editor
    {
        static readonly string[] bandLabels = { "125", "250", "500", "1k", "2k", "4k" };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var material = (AcousticMaterial)target;

            // Absorption curve visualization
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Absorption Curve", EditorStyles.boldLabel);

            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(80));
            DrawAbsorptionCurve(rect, material);

            // Presets
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Concrete")) ApplyPreset(material, AcousticMaterialPresets.Concrete);
            if (GUILayout.Button("Brick")) ApplyPreset(material, AcousticMaterialPresets.Brick);
            if (GUILayout.Button("Wood")) ApplyPreset(material, AcousticMaterialPresets.WoodPanel);
            if (GUILayout.Button("Glass")) ApplyPreset(material, AcousticMaterialPresets.Glass);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Carpet")) ApplyPreset(material, AcousticMaterialPresets.Carpet);
            if (GUILayout.Button("Curtain")) ApplyPreset(material, AcousticMaterialPresets.HeavyCurtain);
            if (GUILayout.Button("Metal")) ApplyPreset(material, AcousticMaterialPresets.Metal);
            if (GUILayout.Button("Plaster")) ApplyPreset(material, AcousticMaterialPresets.Plaster);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Soil")) ApplyPreset(material, AcousticMaterialPresets.Soil);
            if (GUILayout.Button("Foliage")) ApplyPreset(material, AcousticMaterialPresets.Foliage);
            if (GUILayout.Button("Tile")) ApplyPreset(material, AcousticMaterialPresets.AcousticTile);
            if (GUILayout.Button("Open Air")) ApplyPreset(material, AcousticMaterialPresets.OpenAir);
            EditorGUILayout.EndHorizontal();
        }

        void ApplyPreset(AcousticMaterial material, float[] preset)
        {
            Undo.RecordObject(material, "Apply Acoustic Preset");
            AcousticMaterialPresets.ApplyPreset(material, preset);
            EditorUtility.SetDirty(material);
        }

        void DrawAbsorptionCurve(Rect rect, AcousticMaterial material)
        {
            float[] values = {
                material.Absorption125Hz,
                material.Absorption250Hz,
                material.Absorption500Hz,
                material.Absorption1kHz,
                material.Absorption2kHz,
                material.Absorption4kHz
            };

            // Background
            EditorGUI.DrawRect(rect, new Color(0.15f, 0.15f, 0.15f));

            // Grid lines
            Handles.color = new Color(0.3f, 0.3f, 0.3f);
            for (int i = 1; i < 4; i++)
            {
                float y = rect.y + rect.height * (1f - i * 0.25f);
                Handles.DrawLine(
                    new Vector3(rect.x, y),
                    new Vector3(rect.xMax, y));
            }

            // Bars
            float barWidth = rect.width / values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                float x = rect.x + i * barWidth + 2;
                float barHeight = values[i] * rect.height;
                float y = rect.yMax - barHeight;

                // Bar color: green (low absorption) to red (high absorption)
                Color barColor = Color.Lerp(
                    new Color(0.2f, 0.7f, 0.3f),
                    new Color(0.8f, 0.2f, 0.2f),
                    values[i]);
                EditorGUI.DrawRect(new Rect(x, y, barWidth - 4, barHeight), barColor);

                // Label
                GUI.Label(new Rect(x, rect.yMax - 14, barWidth - 4, 14),
                    bandLabels[i], EditorStyles.miniLabel);
            }
        }
    }
}
