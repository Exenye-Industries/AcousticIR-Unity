using AcousticIR.Probes;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Windows
{
    /// <summary>
    /// Editor window for batch-baking all AcousticProbes in the scene.
    /// Each probe has its own settings - this just provides batch bake + export.
    /// </summary>
    public class AcousticBakeWindow : EditorWindow
    {
        [SerializeField] string exportFolder;
        [SerializeField] bool autoExportWav;

        AcousticProbe[] probesInScene;
        Vector2 scrollPos;
        bool isBaking;
        int currentProbeIndex;
        float bakeProgress;

        [MenuItem("Window/AcousticIR/Bake Window")]
        static void ShowWindow()
        {
            var window = GetWindow<AcousticBakeWindow>("Acoustic Bake");
            window.minSize = new Vector2(350, 300);
        }

        void OnEnable() => RefreshProbeList();
        void OnFocus() => RefreshProbeList();

        void RefreshProbeList()
        {
            probesInScene = FindObjectsByType<AcousticProbe>(FindObjectsSortMode.InstanceID);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Acoustic IR Bake", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            DrawProbeList();
            EditorGUILayout.Space(10);
            DrawExportSettings();
            EditorGUILayout.Space(10);
            DrawBakeButtons();
        }

        void DrawProbeList()
        {
            EditorGUILayout.LabelField(
                $"Probes in Scene ({(probesInScene != null ? probesInScene.Length : 0)})",
                EditorStyles.boldLabel);

            if (probesInScene == null || probesInScene.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No AcousticProbes in scene.\nGameObject > AcousticIR > Acoustic Probe",
                    MessageType.Warning);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.MaxHeight(200));

            for (int i = 0; i < probesInScene.Length; i++)
            {
                var probe = probesInScene[i];
                if (probe == null) continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                bool hasBakedIR = probe.BakedIR != null && probe.BakedIR.SampleCount > 0;
                GUILayout.Label(hasBakedIR ? "OK" : "--", GUILayout.Width(24));

                if (GUILayout.Button(probe.name, EditorStyles.linkLabel))
                {
                    Selection.activeGameObject = probe.gameObject;
                    EditorGUIUtility.PingObject(probe);
                }

                if (hasBakedIR)
                    GUILayout.Label($"{probe.BakedIR.LengthSeconds:F1}s | {probe.BakedIR.RayCount} rays",
                        EditorStyles.miniLabel, GUILayout.Width(120));
                else
                    GUILayout.Label($"{probe.RayCount} rays", EditorStyles.miniLabel, GUILayout.Width(120));

                GUI.enabled = !isBaking;
                if (GUILayout.Button("Bake", GUILayout.Width(50)))
                    BakeSingleProbe(probe);
                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawExportSettings()
        {
            autoExportWav = EditorGUILayout.Toggle("Auto-Export WAV", autoExportWav);

            if (autoExportWav)
            {
                EditorGUILayout.BeginHorizontal();
                exportFolder = EditorGUILayout.TextField("Export Folder", exportFolder);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    string folder = EditorUtility.OpenFolderPanel("WAV Export Folder", exportFolder, "");
                    if (!string.IsNullOrEmpty(folder))
                        exportFolder = folder;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        void DrawBakeButtons()
        {
            GUI.enabled = !isBaking && probesInScene != null && probesInScene.Length > 0;

            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
            if (GUILayout.Button("Bake All Probes", GUILayout.Height(35)))
                BakeAllProbes();
            GUI.backgroundColor = Color.white;

            GUI.enabled = !isBaking;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh")) RefreshProbeList();
            if (GUILayout.Button("Select All Probes")) Selection.objects = GetProbeGameObjects();
            EditorGUILayout.EndHorizontal();

            GUI.enabled = true;

            if (isBaking)
            {
                EditorGUILayout.Space(5);
                Rect r = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(r, bakeProgress,
                    $"Baking probe {currentProbeIndex + 1}/{probesInScene.Length}...");
            }
        }

        void BakeSingleProbe(AcousticProbe probe)
        {
            EditorUtility.DisplayProgressBar("AcousticIR", $"Baking {probe.name}...", 0.2f);
            try
            {
                var ir = probe.Bake();
                if (ir != null)
                {
                    EditorUtility.SetDirty(probe);
                    if (autoExportWav && !string.IsNullOrEmpty(exportFolder))
                        DSP.WavExporter.ExportFloat32(ir.Samples, ir.SampleRate,
                            $"{exportFolder}/IR_{probe.name}.wav");
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        void BakeAllProbes()
        {
            isBaking = true;
            try
            {
                for (int i = 0; i < probesInScene.Length; i++)
                {
                    currentProbeIndex = i;
                    bakeProgress = (float)i / probesInScene.Length;
                    var probe = probesInScene[i];
                    if (probe == null) continue;

                    EditorUtility.DisplayProgressBar("AcousticIR",
                        $"Baking {probe.name} ({i + 1}/{probesInScene.Length})...", bakeProgress);

                    var ir = probe.Bake();
                    if (ir != null)
                    {
                        EditorUtility.SetDirty(probe);
                        if (autoExportWav && !string.IsNullOrEmpty(exportFolder))
                            DSP.WavExporter.ExportFloat32(ir.Samples, ir.SampleRate,
                                $"{exportFolder}/IR_{probe.name}.wav");
                    }
                }
                Debug.Log($"[AcousticIR] Batch bake complete: {probesInScene.Length} probes.");
            }
            finally
            {
                isBaking = false;
                bakeProgress = 0f;
                EditorUtility.ClearProgressBar();
            }
        }

        GameObject[] GetProbeGameObjects()
        {
            if (probesInScene == null) return new GameObject[0];
            var gos = new GameObject[probesInScene.Length];
            for (int i = 0; i < probesInScene.Length; i++)
                gos[i] = probesInScene[i].gameObject;
            return gos;
        }
    }
}
