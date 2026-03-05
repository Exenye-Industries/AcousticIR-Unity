using Unity.Mathematics;

namespace AcousticIR.Core
{
    /// <summary>
    /// Static math utilities for acoustic raytracing.
    /// All methods are Burst-compatible (no managed allocations).
    /// When called from a [BurstCompile] job, these are automatically Burst-compiled.
    /// </summary>
    public static class AcousticMath
    {
        /// <summary>
        /// Golden ratio used for Fibonacci sphere sampling.
        /// </summary>
        const float GoldenRatio = 1.6180339887498949f;

        /// <summary>
        /// Generates a uniformly distributed point on a unit sphere using Fibonacci spiral.
        /// Deterministic and produces even coverage with any point count.
        /// </summary>
        /// <param name="index">Ray index (0 to totalPoints-1).</param>
        /// <param name="totalPoints">Total number of points to distribute.</param>
        /// <returns>Unit vector direction on the sphere.</returns>
        public static float3 FibonacciSpherePoint(int index, int totalPoints)
        {
            float theta = 2f * math.PI * index / GoldenRatio;
            float phi = math.acos(1f - 2f * (index + 0.5f) / totalPoints);

            float sinPhi = math.sin(phi);
            return new float3(
                sinPhi * math.cos(theta),
                math.cos(phi),
                sinPhi * math.sin(theta)
            );
        }

        /// <summary>
        /// Specular reflection: r = d - 2(d.n)n.
        /// Standard mirror reflection off a surface.
        /// </summary>
        public static float3 Reflect(float3 direction, float3 normal)
        {
            return direction - 2f * math.dot(direction, normal) * normal;
        }

        /// <summary>
        /// Cosine-weighted diffuse reflection in the hemisphere around the normal.
        /// Physically correct Lambertian scattering distribution.
        /// </summary>
        public static float3 DiffuseReflect(float3 normal, ref Random rng)
        {
            float u1 = rng.NextFloat();
            float u2 = rng.NextFloat();

            float r = math.sqrt(u1);
            float theta = 2f * math.PI * u2;

            // Build tangent space from normal
            float3 up = math.abs(normal.y) < 0.999f
                ? new float3(0f, 1f, 0f)
                : new float3(1f, 0f, 0f);
            float3 tangent = math.normalize(math.cross(up, normal));
            float3 bitangent = math.cross(normal, tangent);

            return math.normalize(
                tangent * (r * math.cos(theta)) +
                bitangent * (r * math.sin(theta)) +
                normal * math.sqrt(math.max(0f, 1f - u1))
            );
        }

        /// <summary>
        /// Hybrid reflection blending between specular and diffuse based on material diffusion.
        /// diffusion=0: perfect mirror, diffusion=1: fully Lambertian.
        /// </summary>
        public static float3 HybridReflect(float3 direction, float3 normal,
            float diffusion, ref Random rng)
        {
            float3 specular = Reflect(direction, normal);
            float3 diffuse = DiffuseReflect(normal, ref rng);
            return math.normalize(math.lerp(specular, diffuse, diffusion));
        }

        /// <summary>
        /// Calculates air absorption per meter for each frequency band.
        /// Higher frequencies are absorbed more by air.
        /// Based on ISO 9613-1 simplified model at 20C, 50% humidity.
        /// </summary>
        public static AbsorptionCoefficients AirAbsorption(float distance)
        {
            // Absorption coefficients in dB/meter (simplified)
            // These increase with frequency as expected physically
            const float air125 = 0.0003f;
            const float air250 = 0.0011f;
            const float air500 = 0.0027f;
            const float air1k = 0.0055f;
            const float air2k = 0.0115f;
            const float air4k = 0.0380f;

            // Convert dB attenuation to linear factor: 10^(-dB/20)
            return new AbsorptionCoefficients
            {
                band125Hz = math.pow(10f, -air125 * distance / 20f),
                band250Hz = math.pow(10f, -air250 * distance / 20f),
                band500Hz = math.pow(10f, -air500 * distance / 20f),
                band1kHz = math.pow(10f, -air1k * distance / 20f),
                band2kHz = math.pow(10f, -air2k * distance / 20f),
                band4kHz = math.pow(10f, -air4k * distance / 20f)
            };
        }

        /// <summary>
        /// Applies distance attenuation (inverse square law) and air absorption
        /// to the energy of a ray segment.
        /// </summary>
        public static AbsorptionCoefficients AttenuateByDistance(
            AbsorptionCoefficients energy, float distance)
        {
            // Inverse square law: energy falls off with 1/r^2
            // We use 1/(1+r)^2 to avoid division by zero at very short distances
            float distanceFactor = 1f / ((1f + distance) * (1f + distance));

            // Air absorption (frequency-dependent)
            AbsorptionCoefficients airAbs = AirAbsorption(distance);

            return new AbsorptionCoefficients
            {
                band125Hz = energy.band125Hz * distanceFactor * airAbs.band125Hz,
                band250Hz = energy.band250Hz * distanceFactor * airAbs.band250Hz,
                band500Hz = energy.band500Hz * distanceFactor * airAbs.band500Hz,
                band1kHz = energy.band1kHz * distanceFactor * airAbs.band1kHz,
                band2kHz = energy.band2kHz * distanceFactor * airAbs.band2kHz,
                band4kHz = energy.band4kHz * distanceFactor * airAbs.band4kHz
            };
        }

        /// <summary>
        /// Checks whether a ray segment (from origin in direction, traveling distance)
        /// passes through or near a sphere at receiverPos with given radius.
        /// Returns the closest approach distance if within radius, -1 otherwise.
        /// </summary>
        public static float CheckReceiverIntersection(
            float3 rayOrigin, float3 rayDirection, float rayLength,
            float3 receiverPos, float receiverRadius)
        {
            float3 toReceiver = receiverPos - rayOrigin;
            float projection = math.dot(toReceiver, rayDirection);

            // Receiver is behind the ray
            if (projection < 0f)
                return -1f;

            // Receiver is beyond the ray hit point
            if (projection > rayLength)
                return -1f;

            // Closest point on the ray to the receiver center
            float3 closestPoint = rayOrigin + rayDirection * projection;
            float distSq = math.distancesq(closestPoint, receiverPos);

            if (distSq <= receiverRadius * receiverRadius)
                return projection; // Return distance along ray to closest approach

            return -1f;
        }

        /// <summary>
        /// Applies only air absorption (frequency-dependent) without geometric distance falloff.
        /// In stochastic ray tracing, geometric spreading (1/r²) is implicitly handled by
        /// the receiver sphere geometry - fewer rays hit the receiver at greater distances.
        /// Applying explicit 1/r² would double-count the distance attenuation.
        /// </summary>
        public static AbsorptionCoefficients ApplyAirAbsorption(
            AbsorptionCoefficients energy, float distance)
        {
            AbsorptionCoefficients airAbs = AirAbsorption(distance);
            return new AbsorptionCoefficients
            {
                band125Hz = energy.band125Hz * airAbs.band125Hz,
                band250Hz = energy.band250Hz * airAbs.band250Hz,
                band500Hz = energy.band500Hz * airAbs.band500Hz,
                band1kHz = energy.band1kHz * airAbs.band1kHz,
                band2kHz = energy.band2kHz * airAbs.band2kHz,
                band4kHz = energy.band4kHz * airAbs.band4kHz
            };
        }

        /// <summary>
        /// Applies a Hann window to the tail portion of an IR buffer.
        /// </summary>
        public static void ApplyHannWindow(float[] buffer, float tailPortion)
        {
            int windowStart = (int)(buffer.Length * (1f - tailPortion));
            int windowLength = buffer.Length - windowStart;

            for (int i = 0; i < windowLength; i++)
            {
                float t = (float)i / windowLength;
                float window = 0.5f * (1f + math.cos(math.PI * t));
                buffer[windowStart + i] *= window;
            }
        }

        /// <summary>
        /// Sinc function for sub-sample interpolation.
        /// sinc(x) = sin(pi*x) / (pi*x), sinc(0) = 1
        /// </summary>
        public static float Sinc(float x)
        {
            if (math.abs(x) < 1e-6f)
                return 1f;
            float pix = math.PI * x;
            return math.sin(pix) / pix;
        }
    }
}
