using AcousticIR.Core;
using UnityEngine;

namespace AcousticIR.Config
{
    /// <summary>
    /// Configuration for the acoustic raytracing simulation.
    /// Create via Assets > Create > AcousticIR > Raytrace Config.
    /// </summary>
    [CreateAssetMenu(fileName = "RaytraceConfig", menuName = "AcousticIR/Raytrace Config")]
    public class RaytraceConfig : ScriptableObject
    {
        [Header("Ray Parameters")]
        [Tooltip("Number of rays emitted from the source position")]
        [Range(64, 10000000)]
        [SerializeField] int rayCount = 32768;

        [Tooltip("Maximum number of bounces per ray")]
        [Range(1, 512)]
        [SerializeField] int maxBounces = 64;

        [Tooltip("Maximum total path length per ray in meters")]
        [Range(10f, 5000f)]
        [SerializeField] float maxDistance = 500f;

        [Tooltip("Minimum total band energy before ray termination (lower = more accurate, slower)")]
        [Range(0.0001f, 0.1f)]
        [SerializeField] float energyThreshold = 0.001f;

        [Header("Receiver")]
        [Tooltip("Radius of the receiver capture sphere in meters")]
        [Range(0.1f, 2f)]
        [SerializeField] float receiverRadius = 0.5f;

        [Header("Physics")]
        [Tooltip("Speed of sound in m/s")]
        [Range(300f, 400f)]
        [SerializeField] float speedOfSound = 343f;

        [Header("Default Material")]
        [Tooltip("Absorption used for surfaces without an AcousticSurface component")]
        [Range(0f, 1f)]
        [SerializeField] float defaultAbsorption = 0.1f;

        [Tooltip("Diffusion used for surfaces without an AcousticSurface component")]
        [Range(0f, 1f)]
        [SerializeField] float defaultDiffusion = 0.5f;

        public int RayCount => rayCount;
        public int MaxBounces => maxBounces;
        public float MaxDistance => maxDistance;
        public float EnergyThreshold => energyThreshold;
        public float ReceiverRadius => receiverRadius;
        public float SpeedOfSound => speedOfSound;

        /// <summary>
        /// Builds the RaytraceParams struct for the simulation.
        /// </summary>
        public RaytraceParams ToParams(Vector3 sourcePos, Vector3 receiverPos)
        {
            return new RaytraceParams
            {
                sourcePosition = sourcePos,
                receiverPosition = receiverPos,
                receiverRadius = receiverRadius,
                maxBounces = maxBounces,
                maxDistance = maxDistance,
                speedOfSound = speedOfSound,
                energyThreshold = energyThreshold
            };
        }

        /// <summary>
        /// Returns the default MaterialData for untagged surfaces.
        /// </summary>
        public MaterialData DefaultMaterial
        {
            get
            {
                var baseAbs = AbsorptionCoefficients.DefaultSurface;
                float scale = defaultAbsorption / 0.1f;
                return new MaterialData
                {
                    absorption = new AbsorptionCoefficients
                    {
                        band125Hz = Mathf.Clamp01(baseAbs.band125Hz * scale),
                        band250Hz = Mathf.Clamp01(baseAbs.band250Hz * scale),
                        band500Hz = Mathf.Clamp01(baseAbs.band500Hz * scale),
                        band1kHz  = Mathf.Clamp01(baseAbs.band1kHz  * scale),
                        band2kHz  = Mathf.Clamp01(baseAbs.band2kHz  * scale),
                        band4kHz  = Mathf.Clamp01(baseAbs.band4kHz  * scale)
                    },
                    diffusion = defaultDiffusion
                };
            }
        }
    }
}
