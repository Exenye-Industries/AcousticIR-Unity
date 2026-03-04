using System.Collections.Generic;
using AcousticIR.Config;
using AcousticIR.Core;
using UnityEngine;

namespace AcousticIR.Probes
{
    /// <summary>
    /// Acoustic probe component that defines a source/receiver pair for IR baking.
    /// Place in the scene, configure source and receiver positions, then bake.
    /// The baked IR can be assigned to AcousticZones or used directly.
    /// </summary>
    public class AcousticProbe : MonoBehaviour
    {
        [Header("Positions")]
        [Tooltip("Source position (where the sound originates). If null, uses this transform.")]
        [SerializeField] Transform sourceTransform;

        [Tooltip("Receiver position (where the listener is). If null, uses this transform.")]
        [SerializeField] Transform receiverTransform;

        [Tooltip("Offset from source transform (if no separate transform is set)")]
        [SerializeField] Vector3 sourceOffset = Vector3.zero;

        [Tooltip("Offset from receiver transform (if no separate transform is set)")]
        [SerializeField] Vector3 receiverOffset = new Vector3(0f, 0f, 2f);

        [Header("Configuration")]
        [Tooltip("Raytracing configuration")]
        [SerializeField] RaytraceConfig raytraceConfig;

        [Tooltip("IR generation configuration")]
        [SerializeField] IRGenerationConfig irConfig;

        [Header("Baked Result")]
        [SerializeField] IRData bakedIR;

        /// <summary>The baked IR data, or null if not yet baked.</summary>
        public IRData BakedIR => bakedIR;

        /// <summary>World-space source position.</summary>
        public Vector3 SourcePosition =>
            sourceTransform != null
                ? sourceTransform.position + sourceOffset
                : transform.position + sourceOffset;

        /// <summary>World-space receiver position.</summary>
        public Vector3 ReceiverPosition =>
            receiverTransform != null
                ? receiverTransform.position + receiverOffset
                : transform.position + receiverOffset;

        /// <summary>
        /// Bakes an impulse response from the current scene geometry.
        /// Collects all AcousticSurface components to build the material mapping.
        /// </summary>
        /// <param name="targetIR">IRData asset to store the result. If null, creates a new instance.</param>
        /// <returns>The baked IRData.</returns>
        public IRData Bake(IRData targetIR = null)
        {
            if (raytraceConfig == null)
            {
                Debug.LogError("[AcousticIR] No RaytraceConfig assigned to AcousticProbe.", this);
                return null;
            }

            if (irConfig == null)
            {
                Debug.LogError("[AcousticIR] No IRGenerationConfig assigned to AcousticProbe.", this);
                return null;
            }

            // Build material mapping from all AcousticSurface components in scene
            var materialList = new List<MaterialData>();
            var colliderMapping = new Dictionary<int, int>();
            CollectMaterials(materialList, colliderMapping);

            // Set up raytracer
            var parameters = raytraceConfig.ToParams(SourcePosition, ReceiverPosition);
            using var raytracer = new AcousticRaytracer(
                parameters, materialList, colliderMapping, raytraceConfig.DefaultMaterial);

            // Trace rays
            Debug.Log($"[AcousticIR] Baking IR: {raytraceConfig.RayCount} rays, " +
                      $"{raytraceConfig.MaxBounces} max bounces...");

            var arrivals = raytracer.Trace(raytraceConfig.RayCount);

            Debug.Log($"[AcousticIR] Raytracing complete: {arrivals.Length} arrivals captured.");

            // Generate IR from arrivals
            float[] irSamples = IRGenerator.Generate(
                arrivals,
                irConfig.SampleRate,
                irConfig.IRLength,
                irConfig.ApplyWindowing,
                irConfig.WindowTailPortion,
                irConfig.SynthesizeLateTail);

            arrivals.Dispose();

            // Store result
            if (targetIR == null)
                targetIR = ScriptableObject.CreateInstance<IRData>();

            targetIR.SetData(irSamples, irConfig.SampleRate,
                raytraceConfig.RayCount, raytraceConfig.MaxBounces,
                SourcePosition, ReceiverPosition);

            bakedIR = targetIR;

            Debug.Log($"[AcousticIR] IR generated: {irSamples.Length} samples " +
                      $"({irConfig.IRLength:F1}s @ {irConfig.SampleRate}Hz)");

            return targetIR;
        }

        /// <summary>
        /// Collects all AcousticSurface components in the scene and builds
        /// the material list and collider-to-material mapping.
        /// </summary>
        static void CollectMaterials(List<MaterialData> materialList,
            Dictionary<int, int> colliderMapping)
        {
            // Find all AcousticSurface components
            // Using FindObjectsByType for Unity 6 compatibility
            var surfaces = FindObjectsByType<Materials.AcousticSurface>(
                FindObjectsSortMode.None);

            // Build unique material list and mapping
            var materialIndices = new Dictionary<int, int>(); // material instance ID -> list index

            foreach (var surface in surfaces)
            {
                if (surface.Material == null)
                    continue;

                var collider = surface.GetComponent<Collider>();
                if (collider == null)
                    continue;

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
            // Source sphere (green)
            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.5f);
            Gizmos.DrawSphere(SourcePosition, 0.15f);
            Gizmos.DrawWireSphere(SourcePosition, 0.15f);

            // Receiver sphere (blue)
            Gizmos.color = new Color(0.2f, 0.4f, 0.9f, 0.5f);
            float receiverRadius = raytraceConfig != null ? raytraceConfig.ReceiverRadius : 0.5f;
            Gizmos.DrawWireSphere(ReceiverPosition, receiverRadius);
            Gizmos.DrawSphere(ReceiverPosition, 0.1f);

            // Line connecting source and receiver
            Gizmos.color = new Color(1f, 1f, 0.2f, 0.4f);
            Gizmos.DrawLine(SourcePosition, ReceiverPosition);
        }
    }
}
