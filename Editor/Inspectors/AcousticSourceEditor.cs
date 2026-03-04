using AcousticIR.Probes;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Inspectors
{
    /// <summary>
    /// Custom inspector for AcousticSource with plugin instance auto-detection.
    /// </summary>
    [CustomEditor(typeof(AcousticSource))]
    public class AcousticSourceEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var source = (AcousticSource)target;

            EditorGUILayout.Space(10);

            // Status
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

                string irStatus = source.IsIRLoaded
                    ? (source.CurrentIR != null
                        ? $"Loaded: {source.CurrentIR.LengthSeconds:F2}s"
                        : "Loaded (raw samples)")
                    : "No IR loaded";

                EditorGUILayout.HelpBox(
                    $"Plugin Instance: {source.PluginInstanceId}\n" +
                    $"IR Status: {irStatus}",
                    source.IsIRLoaded ? MessageType.Info : MessageType.Warning);
            }

            // Plugin ID help
            if (source.PluginInstanceId < 0)
            {
                EditorGUILayout.HelpBox(
                    "Plugin Instance ID is not set.\n\n" +
                    "1. Add the 'AcousticIR Convolution' effect to an Audio Mixer group.\n" +
                    "2. Read the 'InstanceID' parameter from the mixer.\n" +
                    "3. Set it here or call PluginInstanceId from script.\n\n" +
                    "Alternatively, use ConvolutionBridge.GetLatestInstanceId() at runtime.",
                    MessageType.Info);

                if (Application.isPlaying && GUILayout.Button("Auto-Detect Plugin Instance"))
                {
                    int id = DSP.ConvolutionBridge.GetLatestInstanceId();
                    if (id >= 0)
                    {
                        Undo.RecordObject(source, "Set Plugin Instance ID");
                        source.PluginInstanceId = id;
                        EditorUtility.SetDirty(source);
                        Debug.Log($"[AcousticIR] Auto-detected plugin instance ID: {id}");
                    }
                    else
                    {
                        Debug.LogWarning("[AcousticIR] No plugin instance found. " +
                                         "Make sure the AcousticIR effect is on an Audio Mixer group.");
                    }
                }
            }
        }
    }
}
