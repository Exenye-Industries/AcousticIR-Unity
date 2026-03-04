using AcousticIR.Core;
using AcousticIR.DSP;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Windows
{
    /// <summary>
    /// Editor window for previewing and auditioning impulse response data.
    /// Displays the IR waveform, metadata, and provides audio preview playback.
    /// </summary>
    public class IRPreviewWindow : EditorWindow
    {
        [SerializeField] IRData irData;

        AudioClip previewClip;
        AudioSource previewSource;
        Texture2D waveformTexture;
        bool isPlaying;
        float zoomLevel = 1f;
        Vector2 scrollPos;
        int waveformWidth = 512;
        int waveformHeight = 128;

        [MenuItem("Window/AcousticIR/IR Preview")]
        static void ShowWindow()
        {
            var window = GetWindow<IRPreviewWindow>("IR Preview");
            window.minSize = new Vector2(400, 350);
        }

        void OnEnable()
        {
            // Try to get selected IRData
            if (Selection.activeObject is IRData selected)
                SetIRData(selected);
        }

        void OnSelectionChange()
        {
            if (Selection.activeObject is IRData selected)
            {
                SetIRData(selected);
                Repaint();
            }
        }

        void OnDisable()
        {
            StopPreview();
            if (waveformTexture != null)
                DestroyImmediate(waveformTexture);
        }

        /// <summary>
        /// Sets the IR data to preview. Can be called externally.
        /// </summary>
        public void SetIRData(IRData data)
        {
            irData = data;
            RegenerateWaveform();
        }

        void OnGUI()
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("IR Preview", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // IR data field
            EditorGUI.BeginChangeCheck();
            irData = (IRData)EditorGUILayout.ObjectField(
                "IR Data", irData, typeof(IRData), false);
            if (EditorGUI.EndChangeCheck())
                RegenerateWaveform();

            if (irData == null || irData.SampleCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "Select an IRData asset to preview.\n" +
                    "You can also select one in the Project window.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.Space(5);

            // Metadata
            DrawMetadata();

            EditorGUILayout.Space(10);

            // Waveform display
            DrawWaveform();

            EditorGUILayout.Space(10);

            // Playback controls
            DrawPlaybackControls();

            EditorGUILayout.Space(10);

            // Export buttons
            DrawExportButtons();
        }

        void DrawMetadata()
        {
            EditorGUILayout.LabelField("IR Information", EditorStyles.boldLabel);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Length", $"{irData.LengthSeconds:F3} seconds");
            EditorGUILayout.LabelField("Sample Rate", $"{irData.SampleRate} Hz");
            EditorGUILayout.LabelField("Samples", $"{irData.SampleCount:N0}");
            EditorGUILayout.LabelField("Rays Used", $"{irData.RayCount:N0}");
            EditorGUILayout.LabelField("Max Bounces", $"{irData.MaxBounces}");
            EditorGUILayout.LabelField("Source Pos", irData.SourcePosition.ToString("F2"));
            EditorGUILayout.LabelField("Receiver Pos", irData.ReceiverPosition.ToString("F2"));

            float distance = Vector3.Distance(irData.SourcePosition, irData.ReceiverPosition);
            EditorGUILayout.LabelField("Distance", $"{distance:F2} m");
            EditorGUILayout.EndVertical();
        }

        void DrawWaveform()
        {
            EditorGUILayout.LabelField("Waveform", EditorStyles.boldLabel);

            // Zoom control
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Zoom", GUILayout.Width(40));
            zoomLevel = GUILayout.HorizontalSlider(zoomLevel, 0.1f, 10f);
            if (GUILayout.Button("Reset", GUILayout.Width(50)))
            {
                zoomLevel = 1f;
                RegenerateWaveform();
            }
            EditorGUILayout.EndHorizontal();

            if (waveformTexture == null)
                RegenerateWaveform();

            if (waveformTexture != null)
            {
                // Draw waveform in a scroll view for zoomed display
                float displayWidth = waveformWidth * zoomLevel;
                scrollPos = EditorGUILayout.BeginScrollView(scrollPos,
                    GUILayout.Height(waveformHeight + 20));

                Rect waveRect = GUILayoutUtility.GetRect(
                    displayWidth, waveformHeight,
                    GUILayout.Width(displayWidth));

                // Background
                EditorGUI.DrawRect(waveRect, new Color(0.1f, 0.1f, 0.12f));

                // Waveform texture
                GUI.DrawTexture(waveRect, waveformTexture, ScaleMode.StretchToFill);

                // Center line
                float centerY = waveRect.y + waveRect.height * 0.5f;
                EditorGUI.DrawRect(
                    new Rect(waveRect.x, centerY - 0.5f, waveRect.width, 1f),
                    new Color(0.4f, 0.4f, 0.4f, 0.5f));

                // Time markers
                DrawTimeMarkers(waveRect);

                EditorGUILayout.EndScrollView();
            }
        }

        void DrawTimeMarkers(Rect waveRect)
        {
            if (irData == null) return;

            float totalTime = irData.LengthSeconds;
            float markerInterval = totalTime > 2f ? 0.5f : (totalTime > 1f ? 0.2f : 0.1f);

            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
            };

            for (float t = 0f; t <= totalTime; t += markerInterval)
            {
                float x = waveRect.x + (t / totalTime) * waveRect.width;

                // Tick mark
                EditorGUI.DrawRect(
                    new Rect(x, waveRect.yMax - 12, 1, 12),
                    new Color(0.5f, 0.5f, 0.5f, 0.5f));

                // Time label
                GUI.Label(
                    new Rect(x - 20, waveRect.yMax - 14, 40, 14),
                    $"{t:F1}s", style);
            }
        }

        void DrawPlaybackControls()
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (isPlaying)
            {
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Stop", GUILayout.Height(28)))
                    StopPreview();
            }
            else
            {
                GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
                if (GUILayout.Button("Play IR", GUILayout.Height(28)))
                    PlayPreview();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndHorizontal();
        }

        void DrawExportButtons()
        {
            EditorGUILayout.LabelField("Export", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Export WAV (32-bit float)"))
            {
                string path = EditorUtility.SaveFilePanel(
                    "Export IR as WAV", Application.dataPath,
                    $"IR_{irData.SampleRate}Hz", "wav");
                if (!string.IsNullOrEmpty(path))
                    WavExporter.ExportFloat32(irData.Samples, irData.SampleRate, path);
            }

            if (GUILayout.Button("Export WAV (16-bit PCM)"))
            {
                string path = EditorUtility.SaveFilePanel(
                    "Export IR as WAV", Application.dataPath,
                    $"IR_{irData.SampleRate}Hz", "wav");
                if (!string.IsNullOrEmpty(path))
                    WavExporter.ExportPCM16(irData.Samples, irData.SampleRate, path);
            }

            EditorGUILayout.EndHorizontal();
        }

        void RegenerateWaveform()
        {
            if (irData == null || irData.Samples == null || irData.Samples.Length == 0)
            {
                waveformTexture = null;
                return;
            }

            if (waveformTexture != null)
                DestroyImmediate(waveformTexture);

            waveformTexture = new Texture2D(waveformWidth, waveformHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            float[] samples = irData.Samples;
            var pixels = new Color[waveformWidth * waveformHeight];

            // Clear background
            Color bgColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = bgColor;

            // Draw waveform
            float samplesPerPixel = (float)samples.Length / waveformWidth;
            int halfHeight = waveformHeight / 2;

            for (int x = 0; x < waveformWidth; x++)
            {
                int startSample = (int)(x * samplesPerPixel);
                int endSample = Mathf.Min((int)((x + 1) * samplesPerPixel), samples.Length);

                float min = 0f, max = 0f;
                for (int s = startSample; s < endSample; s++)
                {
                    if (samples[s] < min) min = samples[s];
                    if (samples[s] > max) max = samples[s];
                }

                // Map to pixel coordinates
                int yMin = halfHeight + (int)(min * halfHeight);
                int yMax = halfHeight + (int)(max * halfHeight);

                yMin = Mathf.Clamp(yMin, 0, waveformHeight - 1);
                yMax = Mathf.Clamp(yMax, 0, waveformHeight - 1);

                // Draw column
                for (int y = yMin; y <= yMax; y++)
                {
                    float intensity = 1f - Mathf.Abs((float)(y - halfHeight) / halfHeight);
                    Color waveColor = Color.Lerp(
                        new Color(0.2f, 0.5f, 1f, 1f),
                        new Color(0.4f, 0.9f, 1f, 1f),
                        intensity);
                    pixels[y * waveformWidth + x] = waveColor;
                }
            }

            waveformTexture.SetPixels(pixels);
            waveformTexture.Apply();
        }

        void PlayPreview()
        {
            if (irData == null) return;

            previewClip = irData.ToAudioClip("IR_Preview");
            if (previewClip == null) return;

            // Use Unity's internal preview audio utility
            // This works in the editor without needing a scene AudioSource
            var assembly = typeof(AudioImporter).Assembly;
            var audioUtilType = assembly.GetType("UnityEditor.AudioUtil");
            if (audioUtilType != null)
            {
                var playClipMethod = audioUtilType.GetMethod("PlayPreviewClip",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(AudioClip), typeof(int), typeof(bool) },
                    null);

                if (playClipMethod != null)
                {
                    playClipMethod.Invoke(null, new object[] { previewClip, 0, false });
                    isPlaying = true;

                    // Auto-stop after clip finishes
                    EditorApplication.delayCall += () =>
                    {
                        float delay = previewClip.length;
                        EditorApplication.delayCall += () =>
                        {
                            if (isPlaying)
                                StopPreview();
                        };
                    };
                }
            }
        }

        void StopPreview()
        {
            isPlaying = false;

            var assembly = typeof(AudioImporter).Assembly;
            var audioUtilType = assembly.GetType("UnityEditor.AudioUtil");
            if (audioUtilType != null)
            {
                var stopMethod = audioUtilType.GetMethod("StopAllPreviewClips",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                stopMethod?.Invoke(null, null);
            }

            if (previewClip != null)
            {
                DestroyImmediate(previewClip);
                previewClip = null;
            }
        }
    }
}
