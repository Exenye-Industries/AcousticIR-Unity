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

        [Header("Baked Result")]
        [SerializeField] IRData bakedIR;

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

            var arrivals = raytracer.Trace(rayCount);

            Debug.Log($"[AcousticIR] Raytracing complete: {arrivals.Length} arrivals.");

            // Generate IR
            float[] irSamples = IRGenerator.Generate(
                arrivals, sampleRate, irLength,
                applyWindowing, windowTailPortion, synthesizeLateTail);

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

        void OnDrawGizmosSelected()
        {
            // Source (green)
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.5f);
            Gizmos.DrawSphere(SourcePosition, 0.15f);
            Gizmos.DrawWireSphere(SourcePosition, 0.15f);

            // Receiver (blue)
            Gizmos.color = new Color(0.2f, 0.4f, 0.9f, 0.5f);
            Gizmos.DrawWireSphere(ReceiverPosition, receiverRadius);
            Gizmos.DrawSphere(ReceiverPosition, 0.1f);

            // Connection line
            Gizmos.color = new Color(1f, 1f, 0.2f, 0.4f);
            Gizmos.DrawLine(SourcePosition, ReceiverPosition);
        }
    }
}
