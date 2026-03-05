using System.Collections.Generic;
using AcousticIR.Core;
using UnityEngine;

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
        [Tooltip("Number of rays emitted from the source position")]
        [Range(64, 65536)]
        [SerializeField] int rayCount = 4096;

        [Tooltip("Maximum number of bounces per ray")]
        [Range(1, 32)]
        [SerializeField] int maxBounces = 8;

        [Tooltip("Maximum total path length per ray in meters")]
        [Range(10f, 500f)]
        [SerializeField] float maxDistance = 100f;

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
        [Tooltip("Absorption for surfaces without AcousticSurface component")]
        [Range(0f, 1f)]
        [SerializeField] float defaultAbsorption = 0.1f;

        [Tooltip("Diffusion for surfaces without AcousticSurface component")]
        [Range(0f, 1f)]
        [SerializeField] float defaultDiffusion = 0.3f;

        [Header("IR Output")]
        [Tooltip("Sample rate of the generated IR")]
        [SerializeField] int sampleRate = 48000;

        [Tooltip("Maximum IR length in seconds")]
        [Range(0.5f, 6f)]
        [SerializeField] float irLength = 2f;

        [Tooltip("Apply Hann window to IR tail")]
        [SerializeField] bool applyWindowing = true;

        [Tooltip("Portion of IR tail to window")]
        [Range(0.05f, 0.3f)]
        [SerializeField] float windowTailPortion = 0.1f;

        [Tooltip("Synthesize stochastic late reverb tail")]
        [SerializeField] bool synthesizeLateTail = true;

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

        /// <summary>
        /// Bakes an impulse response from the current scene geometry.
        /// Works immediately - no config assets needed.
        /// </summary>
        public IRData Bake(IRData targetIR = null)
        {
            // Build material mapping from AcousticSurface components (if any exist)
            var materialList = new List<MaterialData>();
            var colliderMapping = new Dictionary<int, int>();
            CollectMaterials(materialList, colliderMapping);

            // Default material for all surfaces without AcousticSurface
            var defaultMaterial = new MaterialData
            {
                absorption = AbsorptionCoefficients.Uniform(defaultAbsorption),
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
                energyThreshold = energyThreshold
            };

            using var raytracer = new AcousticRaytracer(
                parameters, materialList, colliderMapping, defaultMaterial);

            Debug.Log($"[AcousticIR] Baking IR: {rayCount} rays, {maxBounces} bounces, " +
                      $"source={SourcePosition}, receiver={ReceiverPosition}");

            // Pre-bake geometry diagnostic
            DiagnoseGeometry(SourcePosition);

            var arrivals = raytracer.Trace(rayCount);

            Debug.Log($"[AcousticIR] Raytracing complete: {arrivals.Length} arrivals.");
            LogArrivalStatistics(arrivals);

            // Debug ray visualization - uses Debug.DrawLine (visible in Scene view without selection)
            if (showDebugRays && debugRayCount > 0)
            {
                lastDebugRays = raytracer.TraceDebug(debugRayCount);
                DrawDebugRaysImmediate(lastDebugRays);
                Debug.Log($"[AcousticIR] Debug rays: {lastDebugRays.Count} segments from {debugRayCount} rays drawn in Scene view (visible for 30s).");
            }

            // Generate IR
            float[] irSamples = IRGenerator.Generate(
                arrivals, sampleRate, irLength,
                applyWindowing, windowTailPortion, synthesizeLateTail);

            // Diagnose IR content
            DiagnoseIR(irSamples, sampleRate);

            arrivals.Dispose();

            // Store result
            if (targetIR == null)
                targetIR = ScriptableObject.CreateInstance<IRData>();

            targetIR.SetData(irSamples, sampleRate, rayCount, maxBounces,
                SourcePosition, ReceiverPosition);

            bakedIR = targetIR;

            Debug.Log($"[AcousticIR] IR generated: {irSamples.Length} samples " +
                      $"({irLength:F1}s @ {sampleRate}Hz)");

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
        void DiagnoseIR(float[] ir, int sr)
        {
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
            sb.AppendLine("[AcousticIR] IR DIAGNOSIS:");
            sb.AppendLine($"  Peak: {peak:F6} at sample {peakSample} ({(float)peakSample / sr * 1000:F1}ms)");
            sb.AppendLine($"  Non-zero samples: {nonZeroSamples} / {ir.Length} ({100f * nonZeroSamples / ir.Length:F1}%)");
            sb.AppendLine($"  Last non-zero: sample {lastNonZeroSample} ({(float)lastNonZeroSample / sr * 1000:F1}ms)");
            sb.AppendLine($"  Energy: first 10ms={earlyPct:F1}%, rest={100f - earlyPct:F1}%");

            if (earlyPct > 90f)
                Debug.LogError($"[AcousticIR] PROBLEM: {earlyPct:F0}% of IR energy is in the first 10ms! IR will sound like a click.");
            else
                Debug.Log(sb.ToString());
        }

        /// <summary>
        /// Draws debug rays immediately using Debug.DrawLine.
        /// These are visible in Scene view for 30 seconds without needing to select anything.
        /// Green = high energy, Red = low energy, Cyan = hit receiver.
        /// </summary>
        static void DrawDebugRaysImmediate(List<DebugRaySegment> segments)
        {
            if (segments == null || segments.Count == 0) return;

            float maxEnergy = 0f;
            foreach (var seg in segments)
                if (seg.energy > maxEnergy) maxEnergy = seg.energy;
            if (maxEnergy < 1e-8f) maxEnergy = 1f;

            int receiverHits = 0;
            foreach (var seg in segments)
            {
                float t = seg.energy / maxEnergy;
                Color color;

                if (seg.hitReceiver)
                {
                    color = Color.cyan;
                    receiverHits++;
                }
                else
                {
                    // Green (high energy) -> Red (low energy)
                    color = Color.Lerp(Color.red, Color.green, t);
                }

                Debug.DrawLine(seg.start, seg.end, color, 30f);
            }

            Debug.Log($"[AcousticIR] Drew {segments.Count} ray segments ({receiverHits} hit receiver). Look at Scene view!");
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
