using System.Collections.Generic;
using AcousticIR.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace AcousticIR.Probes
{
    /// <summary>
    /// Acoustic probe that bakes impulse responses from scene geometry.
    /// All settings are directly on this component - just place it, click Bake.
    /// Works with any Collider in the scene (no AcousticSurface needed for basic use).
    /// </summary>
    public class AcousticProbe : MonoBehaviour
    {
        [Header("Ray Parameters")]
        [Tooltip("Number of rays emitted from the source. More rays = denser IR but slower bake.\n" +
                 "Quick: 4K | Standard: 64K | High: 500K | Ultra: 2M | Extreme: 6M+\n" +
                 "Offline baking can take minutes at high ray counts.")]
        [Min(64)]
        [SerializeField] int rayCount = 65536;

        [Tooltip("Maximum bounces per ray. More bounces = longer reverb tail.\n" +
                 "Small room: 32 | Large room: 64-128 | Hall: 128-256 | Cave: 256-512")]
        [Range(1, 512)]
        [SerializeField] int maxBounces = 128;

        [Tooltip("Maximum total path length per ray in meters")]
        [Range(10f, 5000f)]
        [SerializeField] float maxDistance = 1000f;

        [Tooltip("Minimum total band energy before ray termination")]
        [Range(0.0001f, 0.1f)]
        [SerializeField] float energyThreshold = 0.001f;

        [Header("Receiver")]
        [Tooltip("Radius of the receiver capture sphere in meters")]
        [Range(0.1f, 2f)]
        [SerializeField] float receiverRadius = 0.5f;

        [Tooltip("Offset from this transform to the receiver position")]
        [SerializeField] Vector3 receiverOffset = new Vector3(0f, 0f, 2f);

        [Header("Physics")]
        [Tooltip("Speed of sound in m/s")]
        [Range(300f, 400f)]
        [SerializeField] float speedOfSound = 343f;

        [Header("Default Surface Material")]
        [Tooltip("Overall absorption strength for surfaces without AcousticSurface component.\n" +
                 "Absorption is frequency-dependent (HF absorbed more than LF, like real surfaces).\n" +
                 "0.05 = very reflective (tile/glass) | 0.1 = standard (concrete/plaster) | 0.3 = absorptive (carpet/curtain)")]
        [Range(0f, 1f)]
        [SerializeField] float defaultAbsorption = 0.1f;

        [Tooltip("Surface scattering (specular/diffuse blend).\n" +
                 "0.1 = smooth concrete | 0.2 = typical | 0.5 = rough | 0.8 = very diffuse")]
        [Range(0f, 1f)]
        [SerializeField] float defaultScattering = 0.2f;

        [Tooltip("Surface micro-roughness (jitter cone for imperfections).\n" +
                 "0.3 = polished | 0.5 = typical | 0.8 = very rough")]
        [Range(0f, 1f)]
        [SerializeField] float defaultDiffusion = 0.5f;

        [Header("Diffraction")]
        [Tooltip("Enable simplified stochastic diffraction.\n" +
                 "Allows low frequencies to bend around edges and obstacles.")]
        [SerializeField] bool enableDiffraction = true;

        [Header("Source Directivity")]
        [Tooltip("Directivity pattern of the sound source.\n" +
                 "Omni = equal in all directions | Cardioid = front-focused | Figure8 = front+back")]
        [SerializeField] DirectivityPattern directivity = DirectivityPattern.Omnidirectional;

        [Header("IR Output")]
        [Tooltip("Sample rate of the generated IR")]
        [SerializeField] int sampleRate = 48000;

        [Tooltip("Maximum IR length in seconds.\nSmall room: 1-2s | Large room: 3-4s | Hall: 4-6s | Cave: 6-15s")]
        [Range(0.5f, 20f)]
        [SerializeField] float irLength = 4f;

        [Tooltip("Apply Hann window to IR tail")]
        [SerializeField] bool applyWindowing = true;

        [Tooltip("Portion of IR tail to window")]
        [Range(0.05f, 0.3f)]
        [SerializeField] float windowTailPortion = 0.1f;

        [Tooltip("Synthesize stochastic late reverb tail")]
        [SerializeField] bool synthesizeLateTail = true;

        [Header("Stereo Configuration")]
        [Tooltip("Stereo microphone mode. Mono = classic single-channel IR.")]
        [SerializeField] StereoMode stereoMode = StereoMode.XY;

        [Tooltip("XY: Half-angle between microphones in degrees (90° total = standard)")]
        [Range(15f, 90f)]
        [SerializeField] float xyHalfAngle = 45f;

        [Tooltip("AB: Physical spacing between microphones in meters (0.17 = head width)")]
        [Range(0.05f, 1f)]
        [SerializeField] float abSpacing = 0.17f;

        [Tooltip("MS: Side signal width multiplier (0=mono, 1=standard, >1=extra wide)")]
        [Range(0f, 2f)]
        [SerializeField] float msWidth = 1f;

        [Header("Debug Visualization")]
        [Tooltip("Show debug rays in Scene view after baking")]
        [SerializeField] bool showDebugRays = true;

        [Tooltip("Number of debug rays to visualize (keep low for performance)")]
        [Range(16, 256)]
        [SerializeField] int debugRayCount = 64;

        [Header("Baked Result")]
        [SerializeField] IRData bakedIR;

        /// <summary>Debug ray segments from last bake (not serialized).</summary>
        [System.NonSerialized] public List<DebugRaySegment> lastDebugRays;

        /// <summary>The baked IR data, or null if not yet baked.</summary>
        public IRData BakedIR => bakedIR;

        /// <summary>World-space source position (this transform's position).</summary>
        public Vector3 SourcePosition => transform.position;

        /// <summary>World-space receiver position.</summary>
        public Vector3 ReceiverPosition => transform.position + transform.TransformDirection(receiverOffset);

        // Public accessors for editor/inspector
        public int RayCount => rayCount;
        public int MaxBounces => maxBounces;
        public int SampleRate => sampleRate;
        public float IRLength => irLength;
        public StereoMode StereoModeValue => stereoMode;
        public bool IsStereo => stereoMode != StereoMode.Mono;

        /// <summary>
        /// Bakes an impulse response from the current scene geometry.
        /// Works immediately - no config assets needed.
        /// </summary>
        public IRData Bake(IRData targetIR = null)
        {
            // FIRST: Ensure procedural geometry (voxel chunks etc.) has MeshColliders.
            // Must happen BEFORE CollectMaterials so the raytracer sees the colliders.
            PrepareSceneColliders();

            // Pre-bake geometry diagnostic
            DiagnoseGeometry(SourcePosition);

            // Build material mapping from AcousticSurface components (if any exist)
            var materialList = new List<MaterialData>();
            var colliderMapping = new Dictionary<int, int>();
            CollectMaterials(materialList, colliderMapping);

            // Default material for all surfaces without AcousticSurface
            // Uses frequency-dependent absorption (HF absorbed more than LF)
            // scaled by the user's overall absorption setting
            var baseAbsorption = AbsorptionCoefficients.DefaultSurface;
            float scale = defaultAbsorption / 0.1f; // normalize to user setting
            var defaultMaterial = new MaterialData
            {
                absorption = new AbsorptionCoefficients
                {
                    band125Hz = math.clamp(baseAbsorption.band125Hz * scale, 0f, 0.99f),
                    band250Hz = math.clamp(baseAbsorption.band250Hz * scale, 0f, 0.99f),
                    band500Hz = math.clamp(baseAbsorption.band500Hz * scale, 0f, 0.99f),
                    band1kHz  = math.clamp(baseAbsorption.band1kHz  * scale, 0f, 0.99f),
                    band2kHz  = math.clamp(baseAbsorption.band2kHz  * scale, 0f, 0.99f),
                    band4kHz  = math.clamp(baseAbsorption.band4kHz  * scale, 0f, 0.99f)
                },
                scattering = defaultScattering,
                diffusion = defaultDiffusion
            };

            // Set up raytracer
            var parameters = new RaytraceParams
            {
                sourcePosition = SourcePosition,
                receiverPosition = ReceiverPosition,
                receiverRadius = receiverRadius,
                maxBounces = maxBounces,
                maxDistance = maxDistance,
                speedOfSound = speedOfSound,
                energyThreshold = energyThreshold,
                sourceForward = transform.forward,
                directivityPattern = (int)directivity,
                enableDiffraction = enableDiffraction
            };

            using var raytracer = new AcousticRaytracer(
                parameters, materialList, colliderMapping, defaultMaterial);

            Debug.Log($"[AcousticIR] Baking IR: {rayCount} rays, {maxBounces} bounces, " +
                      $"source={SourcePosition}, receiver={ReceiverPosition}");

            var arrivals = raytracer.Trace(rayCount);

            Debug.Log($"[AcousticIR] Raytracing complete: {arrivals.Length} arrivals.");
            LogArrivalStatistics(arrivals);

            // Debug ray visualization - rendered via OnRenderObject (GL.Begin/GL.End)
            // Always visible in Scene AND Game view, no Gizmos toggle needed.
            if (showDebugRays && debugRayCount > 0)
            {
                lastDebugRays = raytracer.TraceDebug(debugRayCount);
                int receiverHits = 0;
                foreach (var seg in lastDebugRays)
                    if (seg.hitReceiver) receiverHits++;
                Debug.Log($"[AcousticIR] Debug rays: {lastDebugRays.Count} segments from {debugRayCount} rays " +
                          $"({receiverHits} hit receiver). Rays rendered via GL - visible in Scene AND Game view.");
            }

            // Generate IR
            if (targetIR == null)
                targetIR = ScriptableObject.CreateInstance<IRData>();

            if (stereoMode != StereoMode.Mono)
            {
                // Stereo generation with virtual microphone patterns
                var stereoConfig = new StereoConfig
                {
                    mode = stereoMode,
                    xyHalfAngleDeg = xyHalfAngle,
                    abSpacingMeters = abSpacing,
                    msWidth = msWidth
                };

                // Receiver orientation: forward = probe's forward, up = probe's up
                Unity.Mathematics.float3 fwd = transform.forward;
                Unity.Mathematics.float3 up = transform.up;

                var (left, right) = IRGenerator.GenerateStereo(
                    arrivals, fwd, up, stereoConfig,
                    sampleRate, irLength,
                    applyWindowing, windowTailPortion, synthesizeLateTail,
                    speedOfSound, rayCount);

                DiagnoseIR(left, sampleRate, "L");
                DiagnoseIR(right, sampleRate, "R");

                targetIR.SetStereoData(left, right, sampleRate, rayCount, maxBounces,
                    SourcePosition, ReceiverPosition, stereoMode);

                Debug.Log($"[AcousticIR] Stereo IR generated ({stereoMode}): " +
                          $"{left.Length} samples/channel ({irLength:F1}s @ {sampleRate}Hz)");
            }
            else
            {
                // Mono generation (with B0 consolidation and 1/r distance attenuation)
                float[] irSamples = IRGenerator.Generate(
                    arrivals, sampleRate, irLength,
                    applyWindowing, windowTailPortion, synthesizeLateTail,
                    speedOfSound, rayCount);

                DiagnoseIR(irSamples, sampleRate);

                targetIR.SetData(irSamples, sampleRate, rayCount, maxBounces,
                    SourcePosition, ReceiverPosition);

                Debug.Log($"[AcousticIR] Mono IR generated: {irSamples.Length} samples " +
                          $"({irLength:F1}s @ {sampleRate}Hz)");
            }

            arrivals.Dispose();

            bakedIR = targetIR;

            return targetIR;
        }

        static void CollectMaterials(List<MaterialData> materialList,
            Dictionary<int, int> colliderMapping)
        {
            var surfaces = FindObjectsByType<Materials.AcousticSurface>(
                FindObjectsSortMode.None);

            var materialIndices = new Dictionary<int, int>();

            foreach (var surface in surfaces)
            {
                if (surface.Material == null) continue;

                var collider = surface.GetComponent<Collider>();
                if (collider == null) continue;

                int matInstanceId = surface.Material.GetInstanceID();

                if (!materialIndices.TryGetValue(matInstanceId, out int matIndex))
                {
                    matIndex = materialList.Count;
                    materialList.Add(surface.Material.ToMaterialData());
                    materialIndices[matInstanceId] = matIndex;
                }

                colliderMapping[collider.GetInstanceID()] = matIndex;
            }
        }

        /// <summary>
        /// Finds and invokes PrepareCollidersForBake() on any MonoBehaviour in the scene.
        /// This allows voxel/procedural geometry systems to create MeshColliders on-demand.
        /// Uses reflection to avoid hard dependencies on specific projects.
        /// </summary>
        void PrepareSceneColliders()
        {
            var allBehaviours = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            int prepared = 0;

            foreach (var mb in allBehaviours)
            {
                if (mb == null) continue;
                var method = mb.GetType().GetMethod("PrepareCollidersForBake",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (method != null && method.GetParameters().Length == 0)
                {
                    try
                    {
                        method.Invoke(mb, new object[0]);
                        prepared++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[AcousticIR] PrepareCollidersForBake failed on {mb.GetType().Name}: {e.Message}");
                    }
                }
            }

            if (prepared > 0)
                Debug.Log($"[AcousticIR] Called PrepareCollidersForBake() on {prepared} component(s).");
        }

        /// <summary>
        /// Pre-bake diagnostic: checks if there's actually any geometry around the probe
        /// that rays can bounce off. Fires 26 test rays and counts colliders.
        /// </summary>
        void DiagnoseGeometry(Vector3 sourcePos)
        {
            // Count colliders in scene
            var allColliders = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            var meshColliders = FindObjectsByType<MeshCollider>(FindObjectsSortMode.None);

            // Find nearest colliders with OverlapSphere
            var nearby = Physics.OverlapSphere(sourcePos, maxDistance);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine("[AcousticIR] GEOMETRY DIAGNOSTIC");
            sb.AppendLine("========================================");
            sb.AppendLine($"  Scene colliders: {allColliders.Length} total, {meshColliders.Length} MeshColliders");
            sb.AppendLine($"  Within {maxDistance}m of source: {nearby.Length} colliders");

            // Fire test rays in 26 directions (6 axis + 8 corners + 12 edges)
            Vector3[] testDirs = {
                Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
                new Vector3(1,1,1).normalized, new Vector3(-1,1,1).normalized,
                new Vector3(1,-1,1).normalized, new Vector3(-1,-1,1).normalized,
                new Vector3(1,1,-1).normalized, new Vector3(-1,1,-1).normalized,
                new Vector3(1,-1,-1).normalized, new Vector3(-1,-1,-1).normalized,
            };
            int hitCount = 0;
            float nearestHit = float.MaxValue;
            float farthestHit = 0f;
            string nearestName = "";

            foreach (var dir in testDirs)
            {
                if (Physics.Raycast(sourcePos, dir, out RaycastHit hit, maxDistance))
                {
                    hitCount++;
                    if (hit.distance < nearestHit)
                    {
                        nearestHit = hit.distance;
                        nearestName = hit.collider.gameObject.name;
                    }
                    if (hit.distance > farthestHit) farthestHit = hit.distance;

                    // Draw test ray as yellow (hit) in Scene view
                    Debug.DrawLine(sourcePos, hit.point, Color.yellow, 30f);
                    Debug.DrawLine(hit.point, hit.point + hit.normal * 0.3f, Color.magenta, 30f);
                }
                else
                {
                    // Draw test ray as red (missed) in Scene view
                    Debug.DrawLine(sourcePos, sourcePos + dir * 15f, new Color(1f, 0f, 0f, 0.3f), 30f);
                }
            }

            sb.AppendLine($"  Test rays: {hitCount}/{testDirs.Length} hit geometry");
            if (hitCount > 0)
            {
                sb.AppendLine($"  Nearest surface: {nearestHit:F1}m ({nearestName})");
                sb.AppendLine($"  Farthest surface: {farthestHit:F1}m");
            }

            if (hitCount == 0)
            {
                sb.AppendLine("  *** NO GEOMETRY FOUND! Rays have nothing to bounce off! ***");
                sb.AppendLine("  Check: Are MeshColliders on the chunks? Is the probe inside geometry?");
                Debug.LogError(sb.ToString());
            }
            else if (hitCount < testDirs.Length / 2)
            {
                sb.AppendLine($"  WARNING: Only {hitCount}/{testDirs.Length} directions have geometry (open space)");
                Debug.LogWarning(sb.ToString());
            }
            else
            {
                Debug.Log(sb.ToString());
            }
        }

        void LogArrivalStatistics(Unity.Collections.NativeList<RayArrival> arrivals)
        {
            if (arrivals.Length == 0)
            {
                Debug.LogWarning("[AcousticIR] === NO ARRIVALS === Check scene geometry and receiver position!");
                return;
            }

            float minTime = float.MaxValue, maxTime = 0f;
            float minEnergy = float.MaxValue, maxEnergy = 0f;
            int[] bounceCounts = new int[maxBounces + 1];

            for (int i = 0; i < arrivals.Length; i++)
            {
                var a = arrivals[i];
                if (a.time < minTime) minTime = a.time;
                if (a.time > maxTime) maxTime = a.time;
                float e = a.bandEnergy.TotalEnergy;
                if (e < minEnergy) minEnergy = e;
                if (e > maxEnergy) maxEnergy = e;
                if (a.bounceCount >= 0 && a.bounceCount <= maxBounces)
                    bounceCounts[a.bounceCount]++;
            }

            // Time histogram (10 bins)
            int histBins = 10;
            float binWidth = (maxTime - minTime) / histBins;
            int[] histogram = new int[histBins];
            if (binWidth > 0f)
            {
                for (int i = 0; i < arrivals.Length; i++)
                {
                    int bin = Mathf.Clamp((int)((arrivals[i].time - minTime) / binWidth), 0, histBins - 1);
                    histogram[bin]++;
                }
            }
            else
            {
                histogram[0] = arrivals.Length;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"[AcousticIR] ARRIVAL STATISTICS ({arrivals.Length} arrivals)");
            sb.AppendLine("========================================");
            sb.AppendLine($"  Time range: {minTime * 1000:F1}ms - {maxTime * 1000:F1}ms (spread: {(maxTime - minTime) * 1000:F1}ms)");
            sb.AppendLine($"  Energy range: {minEnergy:F4} - {maxEnergy:F4} (total per band)");
            sb.Append("  Per bounce:");
            for (int b = 0; b <= maxBounces; b++)
            {
                if (bounceCounts[b] > 0)
                    sb.Append($" B{b}:{bounceCounts[b]}");
            }
            sb.AppendLine();

            // Time histogram
            sb.AppendLine("  Time distribution:");
            for (int h = 0; h < histBins; h++)
            {
                float tStart = (minTime + h * binWidth) * 1000f;
                float tEnd = (minTime + (h + 1) * binWidth) * 1000f;
                int count = histogram[h];
                string bar = new string('#', Mathf.Min(count, 50));
                sb.AppendLine($"    {tStart,7:F1}-{tEnd,7:F1}ms: {count,4} {bar}");
            }

            // First 10 arrivals
            sb.AppendLine("  First 10 arrivals:");
            int showCount = Mathf.Min(arrivals.Length, 10);
            for (int i = 0; i < showCount; i++)
            {
                var a = arrivals[i];
                sb.AppendLine($"    [{i}] time={a.time * 1000:F2}ms bounce={a.bounceCount} energy={a.bandEnergy.TotalEnergy:F4}");
            }
            sb.AppendLine("========================================");

            Debug.LogWarning(sb.ToString());
        }

        /// <summary>
        /// Analyzes the generated IR buffer to check for energy distribution issues.
        /// </summary>
        void DiagnoseIR(float[] ir, int sr, string channelLabel = null)
        {
            string prefix = channelLabel != null ? $"[{channelLabel}] " : "";
            float peak = 0f;
            int peakSample = 0;
            float totalEnergy = 0f;
            int nonZeroSamples = 0;
            int lastNonZeroSample = 0;

            for (int i = 0; i < ir.Length; i++)
            {
                float abs = Mathf.Abs(ir[i]);
                totalEnergy += abs * abs;
                if (abs > peak) { peak = abs; peakSample = i; }
                if (abs > 1e-6f) { nonZeroSamples++; lastNonZeroSample = i; }
            }

            // Energy in first 10ms vs rest
            int first10ms = Mathf.Min(sr / 100, ir.Length); // 10ms worth of samples
            float earlyEnergy = 0f;
            for (int i = 0; i < first10ms; i++)
                earlyEnergy += ir[i] * ir[i];
            float lateEnergy = totalEnergy - earlyEnergy;

            float earlyPct = totalEnergy > 0f ? (earlyEnergy / totalEnergy * 100f) : 0f;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[AcousticIR] IR DIAGNOSIS {prefix}:");
            sb.AppendLine($"  Peak: {peak:F6} at sample {peakSample} ({(float)peakSample / sr * 1000:F1}ms)");
            sb.AppendLine($"  Non-zero samples: {nonZeroSamples} / {ir.Length} ({100f * nonZeroSamples / ir.Length:F1}%)");
            sb.AppendLine($"  Last non-zero: sample {lastNonZeroSample} ({(float)lastNonZeroSample / sr * 1000:F1}ms)");
            sb.AppendLine($"  Energy: first 10ms={earlyPct:F1}%, rest={100f - earlyPct:F1}%");

            if (earlyPct > 90f)
                Debug.LogError($"[AcousticIR] {prefix}PROBLEM: {earlyPct:F0}% of IR energy is in the first 10ms! IR will sound like a click.");
            else
                Debug.Log(sb.ToString());
        }

        // ========================================================================
        // GL-BASED RAY VISUALIZATION
        // Uses OnRenderObject + GL.Begin/GL.End to render rays directly.
        // This is ALWAYS visible in Scene view AND Game view,
        // regardless of the Gizmos toggle.
        // ========================================================================

        static Material _glLineMaterial;

        static Material GLLineMaterial
        {
            get
            {
                if (_glLineMaterial == null)
                {
                    // Use the built-in colored shader (available in all render pipelines)
                    var shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader == null)
                    {
                        // Fallback for URP/HDRP where Hidden/Internal-Colored might not exist
                        shader = Shader.Find("Sprites/Default");
                    }
                    if (shader == null)
                    {
                        shader = Shader.Find("Unlit/Color");
                    }

                    _glLineMaterial = new Material(shader);
                    _glLineMaterial.hideFlags = HideFlags.HideAndDontSave;
                    _glLineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                    _glLineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                    _glLineMaterial.SetInt("_Cull", (int)CullMode.Off);
                    _glLineMaterial.SetInt("_ZWrite", 0);
                    // Always draw on top for visibility
                    _glLineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                }
                return _glLineMaterial;
            }
        }

        /// <summary>
        /// Called by Unity every frame on every camera.
        /// Draws debug rays using GL.Begin/GL.End which works
        /// ALWAYS (no Gizmos toggle dependency).
        /// </summary>
        void OnRenderObject()
        {
            if (lastDebugRays == null || lastDebugRays.Count == 0) return;

            var mat = GLLineMaterial;
            if (mat == null) return;

            mat.SetPass(0);

            GL.PushMatrix();
            // Use identity matrix - our coordinates are already in world space
            // GL transforms from world to clip automatically when no matrix is set
            GL.MultMatrix(Matrix4x4.identity);

            GL.Begin(GL.LINES);

            float maxEnergy = 0f;
            foreach (var seg in lastDebugRays)
                if (seg.energy > maxEnergy) maxEnergy = seg.energy;
            if (maxEnergy < 1e-8f) maxEnergy = 1f;

            foreach (var seg in lastDebugRays)
            {
                Color c;
                if (seg.hitReceiver)
                {
                    c = Color.cyan;
                }
                else
                {
                    float t = seg.energy / maxEnergy;
                    c = Color.Lerp(Color.red, Color.green, t);
                    c.a = 0.6f + 0.4f * t; // More transparent for low-energy rays
                }

                GL.Color(c);
                GL.Vertex(seg.start);
                GL.Vertex(seg.end);
            }

            GL.End();

            // Also draw source (green) and receiver (blue) markers
            DrawGLSphere(SourcePosition, 0.3f, new Color(0.2f, 0.9f, 0.2f, 0.8f));
            DrawGLSphere(ReceiverPosition, receiverRadius, new Color(0.2f, 0.4f, 0.9f, 0.3f));

            GL.PopMatrix();
        }

        /// <summary>
        /// Draws a wireframe sphere approximation using GL.LINES (octahedron + circles).
        /// </summary>
        static void DrawGLSphere(Vector3 center, float radius, Color color)
        {
            GL.Begin(GL.LINES);
            GL.Color(color);

            // Draw 3 orthogonal circles (16 segments each)
            const int segments = 16;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int i = 0; i < segments; i++)
                {
                    float a1 = (float)i / segments * Mathf.PI * 2f;
                    float a2 = (float)(i + 1) / segments * Mathf.PI * 2f;

                    Vector3 p1, p2;
                    if (axis == 0) // XY circle
                    {
                        p1 = center + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * radius;
                        p2 = center + new Vector3(Mathf.Cos(a2), Mathf.Sin(a2), 0) * radius;
                    }
                    else if (axis == 1) // XZ circle
                    {
                        p1 = center + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
                        p2 = center + new Vector3(Mathf.Cos(a2), 0, Mathf.Sin(a2)) * radius;
                    }
                    else // YZ circle
                    {
                        p1 = center + new Vector3(0, Mathf.Cos(a1), Mathf.Sin(a1)) * radius;
                        p2 = center + new Vector3(0, Mathf.Cos(a2), Mathf.Sin(a2)) * radius;
                    }

                    GL.Vertex(p1);
                    GL.Vertex(p2);
                }
            }
            GL.End();
        }

        void OnDrawGizmosSelected()
        {
            // Source (green sphere)
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.5f);
            Gizmos.DrawSphere(SourcePosition, 0.15f);
            Gizmos.DrawWireSphere(SourcePosition, 0.15f);

            // Receiver (blue sphere)
            Gizmos.color = new Color(0.2f, 0.4f, 0.9f, 0.5f);
            Gizmos.DrawWireSphere(ReceiverPosition, receiverRadius);
            Gizmos.DrawSphere(ReceiverPosition, 0.1f);

            // Connection line
            Gizmos.color = new Color(1f, 1f, 0.2f, 0.4f);
            Gizmos.DrawLine(SourcePosition, ReceiverPosition);
        }
    }
}
