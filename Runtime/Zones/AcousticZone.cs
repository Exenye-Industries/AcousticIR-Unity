using AcousticIR.Config;
using AcousticIR.Core;
using AcousticIR.Probes;
using UnityEngine;

namespace AcousticIR.Zones
{
    /// <summary>
    /// Defines an acoustic zone using a trigger collider.
    /// When the AcousticListener enters this zone, the assigned IR is activated.
    /// Zones can overlap; the one with the highest priority wins.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class AcousticZone : MonoBehaviour
    {
        [Header("IR Assignment")]
        [Tooltip("Baked impulse response for this zone.")]
        [SerializeField] IRData irData;

        [Header("Zone Settings")]
        [Tooltip("Higher priority zones override lower ones when overlapping.")]
        [SerializeField] int priority;

        [Tooltip("Fade time in seconds when entering/leaving this zone.")]
        [Range(0.1f, 5f)]
        [SerializeField] float fadeTime = 1f;

        [Tooltip("Volume multiplier for the IR in this zone (0-1).")]
        [Range(0f, 1f)]
        [SerializeField] float volume = 1f;

        [Header("Runtime Baking (Optional)")]
        [Tooltip("If set, the zone can rebake its IR at runtime using this config.")]
        [SerializeField] RaytraceConfig runtimeRaytraceConfig;

        [SerializeField] IRGenerationConfig runtimeIRConfig;

        [Tooltip("Source position for runtime baking (if null, uses zone center).")]
        [SerializeField] Transform sourceOverride;

        /// <summary>The IR data for this zone.</summary>
        public IRData IR
        {
            get => irData;
            set => irData = value;
        }

        /// <summary>Zone priority for overlap resolution. Higher = wins.</summary>
        public int Priority => priority;

        /// <summary>Crossfade duration in seconds.</summary>
        public float FadeTime => fadeTime;

        /// <summary>Volume multiplier.</summary>
        public float Volume => volume;

        /// <summary>Whether this zone has a valid IR assigned.</summary>
        public bool HasIR => irData != null && irData.SampleCount > 0;

        /// <summary>Can this zone rebake at runtime?</summary>
        public bool CanRebake => runtimeRaytraceConfig != null && runtimeIRConfig != null;

        /// <summary>The source position for runtime baking.</summary>
        public Vector3 BakeSourcePosition =>
            sourceOverride != null ? sourceOverride.position : transform.position;

        /// <summary>Raytrace config for runtime baking.</summary>
        public RaytraceConfig RuntimeRaytraceConfig => runtimeRaytraceConfig;

        /// <summary>IR generation config for runtime baking.</summary>
        public IRGenerationConfig RuntimeIRConfig => runtimeIRConfig;

        void Reset()
        {
            // Ensure the collider is set to trigger
            var col = GetComponent<Collider>();
            if (col != null)
                col.isTrigger = true;
        }

        void OnValidate()
        {
            var col = GetComponent<Collider>();
            if (col != null && !col.isTrigger)
            {
                Debug.LogWarning(
                    "[AcousticIR] AcousticZone collider should be set to 'Is Trigger'.", this);
            }
        }

        void OnDrawGizmos()
        {
            // Draw zone bounds with color based on priority
            Gizmos.color = HasIR
                ? new Color(0.2f, 0.6f, 1f, 0.1f)
                : new Color(1f, 0.3f, 0.2f, 0.1f);

            var col = GetComponent<Collider>();
            if (col is BoxCollider box)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }
            else if (col is SphereCollider sphere)
            {
                Gizmos.DrawSphere(
                    transform.position + sphere.center,
                    sphere.radius * transform.lossyScale.x);
                Gizmos.DrawWireSphere(
                    transform.position + sphere.center,
                    sphere.radius * transform.lossyScale.x);
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.3f);

            var col = GetComponent<Collider>();
            if (col is BoxCollider box)
            {
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }

            // Draw priority label in scene view
#if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position,
                $"Zone: P{priority}" + (HasIR ? " [IR]" : " [No IR]"));
#endif
        }
    }
}
