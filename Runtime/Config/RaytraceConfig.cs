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
        [Range(64, 65536)]
        [SerializeField] int rayCount = 4096;

        [Tooltip("Maximum number of bounces per ray")]
        [Range(1, 32)]
        [SerializeField] int maxBounces = 8;

        [Tooltip("Maximum total path length per ray in meters")]
        [Range(10f, 500f)]
        [SerializeField] float maxDistance = 100f;

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
        [SerializeField] float defaultDiffusion = 0.3f;

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
        public MaterialData DefaultMaterial => new MaterialData
        {
            absorption = AbsorptionCoefficients.Uniform(defaultAbsorption),
            diffusion = defaultDiffusion
        };
    }
}
