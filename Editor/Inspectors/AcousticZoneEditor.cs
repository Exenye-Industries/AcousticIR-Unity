using AcousticIR.Zones;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for AcousticZone with IR info and quick-assign helpers.
    /// </summary>
    [CustomEditor(typeof(AcousticZone))]
    public class AcousticZoneEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var zone = (AcousticZone)target;

            EditorGUILayout.Space(10);

            // Validation
            var col = zone.GetComponent<Collider>();
            if (col != null && !col.isTrigger)
            {
                EditorGUILayout.HelpBox(
                    "The Collider must be set to 'Is Trigger' for zone detection.",
                    MessageType.Error);

                if (GUILayout.Button("Fix: Set Collider to Trigger"))
                {
                    Undo.RecordObject(col, "Set Collider Trigger");
                    col.isTrigger = true;
                    EditorUtility.SetDirty(col);
                }
            }

            // IR status
            EditorGUILayout.Space(5);
            if (zone.HasIR)
            {
                EditorGUILayout.HelpBox(
                    $"IR: {zone.IR.LengthSeconds:F2}s | " +
                    $"{zone.IR.SampleRate}Hz | " +
                    $"{zone.IR.SampleCount:N0} samples\n" +
                    $"Baked with: {zone.IR.RayCount} rays, {zone.IR.MaxBounces} bounces",
                    MessageType.Info);

                if (GUILayout.Button("Open IR Preview"))
                {
                    var window = EditorWindow.GetWindow<Windows.IRPreviewWindow>("IR Preview");
                    window.SetIRData(zone.IR);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No IR assigned. Drag an IRData asset to the 'IR Data' field, " +
                    "or use an AcousticProbe to bake one.",
                    MessageType.Warning);
            }

            // Runtime bake info
            if (zone.CanRebake)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Runtime Baking", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "This zone is configured for runtime rebaking.\n" +
                    $"Source: {zone.BakeSourcePosition}",
                    MessageType.Info);
            }
        }
    }
}
