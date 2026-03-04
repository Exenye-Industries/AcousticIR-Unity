using AcousticIR.Config;
using AcousticIR.Core;
using AcousticIR.Probes;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Windows
{
    /// <summary>
    /// Editor window for batch-baking impulse responses from all AcousticProbes in the scene.
    /// Provides quality presets, progress tracking, and batch operations.
    /// </summary>
    public class AcousticBakeWindow : EditorWindow
    {
        // Quality presets
        enum BakeQuality { Draft, Medium, High, Ultra }

        [SerializeField] BakeQuality quality = BakeQuality.Medium;
        [SerializeField] RaytraceConfig customRaytraceConfig;
        [SerializeField] IRGenerationConfig customIRConfig;
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
            window.minSize = new Vector2(350, 400);
        }

        void OnEnable()
        {
            RefreshProbeList();
        }

        void OnFocus()
        {
            RefreshProbeList();
        }

        void RefreshProbeList()
        {
            probesInScene = FindObjectsByType<AcousticProbe>(FindObjectsSortMode.InstanceID);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Acoustic IR Bake", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // Quality preset
            DrawQualitySettings();

            EditorGUILayout.Space(10);

            // Probe list
            DrawProbeList();

            EditorGUILayout.Space(10);

            // Export settings
            DrawExportSettings();

            EditorGUILayout.Space(10);

            // Bake buttons
            DrawBakeButtons();
        }

        void DrawQualitySettings()
        {
            EditorGUILayout.LabelField("Quality Settings", EditorStyles.boldLabel);

            quality = (BakeQuality)EditorGUILayout.EnumPopup("Preset", quality);

            EditorGUILayout.HelpBox(GetQualityDescription(quality), MessageType.Info);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Custom Config Override", EditorStyles.miniLabel);
            customRaytraceConfig = (RaytraceConfig)EditorGUILayout.ObjectField(
                "Raytrace Config", customRaytraceConfig, typeof(RaytraceConfig), false);
            customIRConfig = (IRGenerationConfig)EditorGUILayout.ObjectField(
                "IR Config", customIRConfig, typeof(IRGenerationConfig), false);
        }

        void DrawProbeList()
        {
            EditorGUILayout.LabelField(
                $"Probes in Scene ({(probesInScene != null ? probesInScene.Length : 0)})",
                EditorStyles.boldLabel);

            if (probesInScene == null || probesInScene.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No AcousticProbes found in the scene.\n" +
                    "Add probes via GameObject > AcousticIR > Acoustic Probe.",
                    MessageType.Warning);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.MaxHeight(200));

            for (int i = 0; i < probesInScene.Length; i++)
            {
                var probe = probesInScene[i];
                if (probe == null) continue;

                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                // Status icon
                bool hasBakedIR = probe.BakedIR != null && probe.BakedIR.SampleCount > 0;
                GUILayout.Label(hasBakedIR ? "OK" : "--", GUILayout.Width(24));

                // Probe name
                if (GUILayout.Button(probe.name, EditorStyles.linkLabel))
                {
                    Selection.activeGameObject = probe.gameObject;
                    EditorGUIUtility.PingObject(probe);
                }

                // IR info
                if (hasBakedIR)
                {
                    GUILayout.Label(
                        $"{probe.BakedIR.LengthSeconds:F1}s | {probe.BakedIR.RayCount} rays",
                        EditorStyles.miniLabel, GUILayout.Width(120));
                }
                else
                {
                    GUILayout.Label("Not baked", EditorStyles.miniLabel, GUILayout.Width(120));
                }

                // Individual bake button
                GUI.enabled = !isBaking;
                if (GUILayout.Button("Bake", GUILayout.Width(50)))
                {
                    BakeSingleProbe(probe);
                }
                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawExportSettings()
        {
            EditorGUILayout.LabelField("Export Settings", EditorStyles.boldLabel);

            autoExportWav = EditorGUILayout.Toggle("Auto-Export WAV", autoExportWav);

            if (autoExportWav)
            {
                EditorGUILayout.BeginHorizontal();
                exportFolder = EditorGUILayout.TextField("Export Folder", exportFolder);
                if (GUILayout.Button("...", GUILayout.Width(30)))
                {
                    string folder = EditorUtility.OpenFolderPanel(
                        "Select WAV Export Folder", exportFolder, "");
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
            {
                BakeAllProbes();
            }
            GUI.backgroundColor = Color.white;

            GUI.enabled = !isBaking;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Probe List"))
                RefreshProbeList();
            if (GUILayout.Button("Select All Probes"))
                Selection.objects = GetProbeGameObjects();
            EditorGUILayout.EndHorizontal();

            GUI.enabled = true;

            // Progress during bake
            if (isBaking)
            {
                EditorGUILayout.Space(5);
                Rect progressRect = EditorGUILayout.GetControlRect(false, 20);
                EditorGUI.ProgressBar(progressRect, bakeProgress,
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
                    {
                        string path = $"{exportFolder}/IR_{probe.name}.wav";
                        DSP.WavExporter.ExportFloat32(ir.Samples, ir.SampleRate, path);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
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
                        $"Baking {probe.name} ({i + 1}/{probesInScene.Length})...",
                        bakeProgress);

                    var ir = probe.Bake();
                    if (ir != null)
                    {
                        EditorUtility.SetDirty(probe);

                        if (autoExportWav && !string.IsNullOrEmpty(exportFolder))
                        {
                            string path = $"{exportFolder}/IR_{probe.name}.wav";
                            DSP.WavExporter.ExportFloat32(ir.Samples, ir.SampleRate, path);
                        }
                    }
                }

                Debug.Log($"[AcousticIR] Batch bake complete: {probesInScene.Length} probes processed.");
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

        static string GetQualityDescription(BakeQuality q)
        {
            return q switch
            {
                BakeQuality.Draft => "256 rays, 4 bounces - Fast preview (~1s)",
                BakeQuality.Medium => "2048 rays, 8 bounces - Good quality (~5s)",
                BakeQuality.High => "8192 rays, 12 bounces - High quality (~20s)",
                BakeQuality.Ultra => "32768 rays, 16 bounces - Reference quality (~2min)",
                _ => ""
            };
        }
    }
}
