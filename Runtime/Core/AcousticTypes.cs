using UnityEngine;
using Unity.Mathematics;

namespace AcousticIR.Core
{
    /// <summary>
    /// Stereo microphone configuration for IR generation.
    /// </summary>
    public enum StereoMode
    {
        /// <summary>Single omnidirectional receiver. Classic mono IR.</summary>
        Mono,

        /// <summary>
        /// XY Coincident Pair: Two cardioid microphones at ±angle from forward.
        /// Good mono compatibility, natural stereo image.
        /// Default: ±45° (90° total angle).
        /// </summary>
        XY,

        /// <summary>
        /// AB Spaced Pair: Two omnidirectional microphones with physical spacing.
        /// Wide stereo image from inter-channel time differences.
        /// Default: 17cm spacing (head width).
        /// </summary>
        AB,

        /// <summary>
        /// MS Mid-Side: One cardioid (mid) + one figure-8 (side).
        /// Perfect mono compatibility, adjustable stereo width in post.
        /// L = Mid + Width*Side, R = Mid - Width*Side.
        /// </summary>
        MS
    }

    /// <summary>
    /// Parameters for stereo microphone configuration.
    /// </summary>
    [System.Serializable]
    public struct StereoConfig
    {
        /// <summary>Stereo microphone mode.</summary>
        public StereoMode mode;

        /// <summary>
        /// XY: Half-angle between microphones in degrees (e.g. 45 = 90° total spread).
        /// MS: Not used.
        /// AB: Not used.
        /// </summary>
        [Range(15f, 90f)]
        public float xyHalfAngleDeg;

        /// <summary>
        /// AB: Physical spacing between microphones in meters.
        /// Default: 0.17m (approximate head width).
        /// XY/MS: Not used.
        /// </summary>
        [Range(0.05f, 1f)]
        public float abSpacingMeters;

        /// <summary>
        /// MS: Side signal width multiplier.
        /// 0 = pure mono, 1 = standard width, >1 = extra wide.
        /// XY/AB: Not used.
        /// </summary>
        [Range(0f, 2f)]
        public float msWidth;

        /// <summary>Default stereo configuration (XY at 90°).</summary>
        public static StereoConfig Default => new StereoConfig
        {
            mode = StereoMode.XY,
            xyHalfAngleDeg = 45f,
            abSpacingMeters = 0.17f,
            msWidth = 1f
        };
    }

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

        /// <summary>
        /// Realistic default surface absorption (painted concrete/plaster).
        /// Low frequencies reflect well, high frequencies are absorbed more.
        /// This is physically correct - no real surface has uniform absorption.
        /// </summary>
        public static AbsorptionCoefficients DefaultSurface => new AbsorptionCoefficients
        {
            band125Hz = 0.02f,
            band250Hz = 0.03f,
            band500Hz = 0.04f,
            band1kHz  = 0.06f,
            band2kHz  = 0.08f,
            band4kHz  = 0.12f
        };
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
    /// Debug visualization data for a single ray segment.
    /// Used to draw ray paths in the Scene view.
    /// </summary>
    public struct DebugRaySegment
    {
        public Vector3 start;
        public Vector3 end;
        public float energy;
        public int rayIndex;
        public int bounce;
        public bool hitReceiver;
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
