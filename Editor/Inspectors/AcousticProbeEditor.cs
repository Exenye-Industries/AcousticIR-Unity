using AcousticIR.Core;
using AcousticIR.DSP;
using AcousticIR.Probes;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Inspectors
{
    [CustomEditor(typeof(AcousticProbe))]
    public class AcousticProbeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var probe = (AcousticProbe)target;

            EditorGUILayout.Space(10);

            // Big green Bake button - always works, no setup needed
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button($"Bake IR  ({probe.RayCount} rays, {probe.MaxBounces} bounces)", GUILayout.Height(35)))
            {
                BakeIR(probe);
            }
            GUI.backgroundColor = Color.white;

            // Show baked IR info + export
            if (probe.BakedIR != null && probe.BakedIR.SampleCount > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(
                    $"IR: {probe.BakedIR.LengthSeconds:F2}s | " +
                    $"{probe.BakedIR.SampleRate} Hz | " +
                    $"{probe.BakedIR.SampleCount:N0} samples | " +
                    $"{probe.BakedIR.RayCount} rays",
                    MessageType.Info);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Export WAV (32-bit float)"))
                    ExportWav(probe.BakedIR, false);
                if (GUILayout.Button("Export WAV (16-bit PCM)"))
                    ExportWav(probe.BakedIR, true);
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("Save IR as Asset"))
                    SaveAsAsset(probe.BakedIR, probe.name);
            }
        }

        void BakeIR(AcousticProbe probe)
        {
            EditorUtility.DisplayProgressBar("AcousticIR", "Baking impulse response...", 0.1f);
            try
            {
                var ir = probe.Bake();
                if (ir != null)
                {
                    EditorUtility.DisplayProgressBar("AcousticIR", "Bake complete!", 1f);
                    EditorUtility.SetDirty(probe);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void ExportWav(IRData irData, bool pcm16)
        {
            string defaultName = $"IR_{irData.SampleRate}Hz_{irData.LengthSeconds:F1}s";
            string path = EditorUtility.SaveFilePanel(
                "Export IR as WAV", Application.dataPath, defaultName, "wav");
            if (string.IsNullOrEmpty(path)) return;

            if (pcm16)
                WavExporter.ExportPCM16(irData.Samples, irData.SampleRate, path);
            else
                WavExporter.ExportFloat32(irData.Samples, irData.SampleRate, path);
        }

        void SaveAsAsset(IRData irData, string probeName)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save IR Data Asset", $"IR_{probeName}", "asset",
                "Choose where to save the IR data asset");
            if (string.IsNullOrEmpty(path)) return;

            var copy = Instantiate(irData);
            copy.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AcousticIR] Saved IR asset: {path}");
        }
    }
}
