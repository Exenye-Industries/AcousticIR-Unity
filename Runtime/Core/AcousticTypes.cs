using Unity.Mathematics;

namespace AcousticIR.Core
{
    /// <summary>
    /// 6 octave band absorption coefficients (125Hz, 250Hz, 500Hz, 1kHz, 2kHz, 4kHz).
    /// All values range from 0.0 (fully reflective) to 1.0 (fully absorptive).
    /// </summary>
    public struct AbsorptionCoefficients
    {
        public float band125Hz;
        public float band250Hz;
        public float band500Hz;
        public float band1kHz;
        public float band2kHz;
        public float band4kHz;

        /// <summary>
        /// Returns the average absorption across all bands.
        /// </summary>
        public float Average =>
            (band125Hz + band250Hz + band500Hz + band1kHz + band2kHz + band4kHz) / 6f;

        /// <summary>
        /// Multiplies each band by (1 - absorption) to get reflected energy per band.
        /// </summary>
        public AbsorptionCoefficients Reflect(AbsorptionCoefficients incoming)
        {
            return new AbsorptionCoefficients
            {
                band125Hz = incoming.band125Hz * (1f - band125Hz),
                band250Hz = incoming.band250Hz * (1f - band250Hz),
                band500Hz = incoming.band500Hz * (1f - band500Hz),
                band1kHz = incoming.band1kHz * (1f - band1kHz),
                band2kHz = incoming.band2kHz * (1f - band2kHz),
                band4kHz = incoming.band4kHz * (1f - band4kHz)
            };
        }

        /// <summary>
        /// Returns the total energy (sum of all bands).
        /// </summary>
        public float TotalEnergy =>
            band125Hz + band250Hz + band500Hz + band1kHz + band2kHz + band4kHz;

        /// <summary>
        /// Creates coefficients with uniform energy across all bands.
        /// </summary>
        public static AbsorptionCoefficients Uniform(float value)
        {
            return new AbsorptionCoefficients
            {
                band125Hz = value,
                band250Hz = value,
                band500Hz = value,
                band1kHz = value,
                band2kHz = value,
                band4kHz = value
            };
        }

        /// <summary>
        /// Full energy (1.0 in all bands). Used as initial ray energy.
        /// </summary>
        public static AbsorptionCoefficients FullEnergy => Uniform(1f);
    }

    /// <summary>
    /// Compact material data for Burst jobs. No managed references.
    /// </summary>
    public struct MaterialData
    {
        public AbsorptionCoefficients absorption;

        /// <summary>
        /// Surface diffusion: 0 = perfect mirror (specular), 1 = fully diffuse (Lambertian).
        /// </summary>
        public float diffusion;
    }

    /// <summary>
    /// A ray arrival at the receiver sphere.
    /// Represents one complete path from source to receiver via reflections.
    /// </summary>
    public struct RayArrival
    {
        /// <summary>Arrival time in seconds (based on total path length / speed of sound).</summary>
        public float time;

        /// <summary>Remaining energy per frequency band after all absorptions and distance attenuation.</summary>
        public AbsorptionCoefficients bandEnergy;

        /// <summary>Direction the ray was traveling when it hit the receiver.</summary>
        public float3 direction;

        /// <summary>Number of surface bounces before reaching the receiver.</summary>
        public int bounceCount;
    }

    /// <summary>
    /// State of a single active ray during the bounce iteration.
    /// </summary>
    public struct ActiveRay
    {
        /// <summary>Current position of the ray.</summary>
        public float3 origin;

        /// <summary>Current direction of the ray.</summary>
        public float3 direction;

        /// <summary>Remaining energy per frequency band.</summary>
        public AbsorptionCoefficients bandEnergy;

        /// <summary>Total distance traveled so far (meters).</summary>
        public float totalDistance;

        /// <summary>Number of bounces completed.</summary>
        public int bounceCount;

        /// <summary>Random seed for this ray (used for diffuse reflection).</summary>
        public uint randomSeed;
    }

    /// <summary>
    /// Parameters for the raytracing simulation. Passed to Burst jobs.
    /// </summary>
    public struct RaytraceParams
    {
        public float3 sourcePosition;
        public float3 receiverPosition;

        /// <summary>Radius of the receiver capture sphere (meters).</summary>
        public float receiverRadius;

        /// <summary>Maximum number of bounces per ray.</summary>
        public int maxBounces;

        /// <summary>Maximum total path length per ray (meters).</summary>
        public float maxDistance;

        /// <summary>Speed of sound in m/s (default 343).</summary>
        public float speedOfSound;

        /// <summary>Minimum total band energy before ray termination.</summary>
        public float energyThreshold;
    }

    /// <summary>
    /// Result from processing a single bounce hit.
    /// Used to communicate between RaycastCommand results and the Burst processing job.
    /// </summary>
    public struct BounceResult
    {
        /// <summary>Whether this ray hit a surface (false = escaped into open space).</summary>
        public bool didHit;

        /// <summary>Whether this ray reached the receiver sphere.</summary>
        public bool reachedReceiver;

        /// <summary>Whether this ray is still alive (has enough energy to continue).</summary>
        public bool isAlive;

        /// <summary>Updated ray state after this bounce.</summary>
        public ActiveRay updatedRay;

        /// <summary>Arrival data if the ray reached the receiver.</summary>
        public RayArrival arrival;
    }
}
