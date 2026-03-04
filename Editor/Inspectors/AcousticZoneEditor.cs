using AcousticIR.Zones;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Inspectors
{
    [CustomEditor(typeof(AcousticZone))]
    public class AcousticZoneEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var zone = (AcousticZone)target;

            EditorGUILayout.Space(10);

            // Trigger validation
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
                    "No IR assigned. Use an AcousticProbe to bake one, then drag the IR asset here.",
                    MessageType.Warning);
            }
        }
    }
}
