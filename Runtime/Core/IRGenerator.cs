using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Converts ray arrivals from the acoustic raytracer into a time-domain
    /// impulse response buffer. Handles sub-sample interpolation, normalization,
    /// optional late tail synthesis, and windowing.
    /// </summary>
    public static class IRGenerator
    {
        /// <summary>
        /// Generates a mono impulse response from ray arrivals.
        /// </summary>
        /// <param name="arrivals">Ray arrivals from AcousticRaytracer.Trace().</param>
        /// <param name="sampleRate">Output sample rate (e.g. 48000).</param>
        /// <param name="irLengthSeconds">Maximum IR length in seconds.</param>
        /// <param name="applyWindowing">Whether to apply a Hann window to the tail.</param>
        /// <param name="windowTailPortion">Portion of the IR tail to window (0-1).</param>
        /// <param name="synthesizeLateTail">Whether to add stochastic late reverb tail.</param>
        /// <param name="speedOfSound">Speed of sound in m/s (for distance calculation).</param>
        /// <param name="rayCount">Number of rays used (for Monte Carlo normalization).</param>
        /// <returns>Float array of IR samples, normalized to peak at -1dB.</returns>
        public static float[] Generate(
            NativeList<RayArrival> arrivals,
            int sampleRate = 48000,
            float irLengthSeconds = 2f,
            bool applyWindowing = true,
            float windowTailPortion = 0.1f,
            bool synthesizeLateTail = true,
            float speedOfSound = 343f,
            int rayCount = 4096)
        {
            int sampleCount = (int)(irLengthSeconds * sampleRate);
            float[] ir = new float[sampleCount];

            if (arrivals.Length == 0)
                return ir;

            // Phase 1: Accumulate ray arrivals into the IR buffer
            // Separates direct sound (B0) from reflections (B1+) to avoid
            // coherent pileup of thousands of B0 arrivals at the same sample.
            AccumulateArrivals(ir, arrivals, sampleRate, speedOfSound, rayCount);

            // Phase 2: Synthesize late reverb tail (stochastic noise with decay)
            if (synthesizeLateTail)
                SynthesizeLateTail(ir, arrivals, sampleRate, speedOfSound);

            // Phase 3: Normalize to -1dB peak
            NormalizePeak(ir, -1f);

            // Phase 4: Apply windowing to the tail
            if (applyWindowing)
                AcousticMath.ApplyHannWindow(ir, windowTailPortion);

            return ir;
        }

        /// <summary>
        /// Accumulates ray arrivals into the IR buffer.
        ///
        /// Key design decisions:
        /// - Bounce-0 arrivals (direct sound) are CONSOLIDATED into a single impulse.
        ///   Many rays hit the receiver before bouncing (all at the same time), which would
        ///   create a massive spike drowning out reflections. Direct sound is one physical event.
        /// - Bounce 1+ arrivals (reflections) are placed individually with sinc interpolation.
        ///   Each represents a unique reflection path through the scene.
        /// - All arrivals are attenuated by 1/totalDistance (inverse distance law for amplitude).
        ///   This is applied HERE (not per-segment in the raytracer) to avoid double-counting
        ///   while the receiver sphere geometry handles the angular component of 1/r².
        /// </summary>
        static void AccumulateArrivals(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, int rayCount)
        {
            const int sincHalfWidth = 4;

            // === Pass 1: Collect direct sound (bounce 0) statistics ===
            float directTimeSum = 0f;
            float directEnergySum = 0f;
            int directCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0)
                {
                    directTimeSum += arrivals[i].time;
                    directEnergySum += BandEnergyToAmplitude(arrivals[i].bandEnergy);
                    directCount++;
                }
            }

            // === Place direct sound as a single consolidated impulse ===
            if (directCount > 0)
            {
                float directTime = directTimeSum / directCount;
                float directDistance = directTime * speedOfSound;

                // Average amplitude of B0 arrivals, attenuated by 1/distance
                float directAmplitude = (directEnergySum / directCount)
                    / Mathf.Max(directDistance, 0.1f);

                float samplePos = directTime * sampleRate;
                int centerSample = (int)samplePos;
                float fraction = samplePos - centerSample;

                for (int k = -sincHalfWidth; k <= sincHalfWidth; k++)
                {
                    int sampleIdx = centerSample + k;
                    if (sampleIdx < 0 || sampleIdx >= ir.Length) continue;
                    float sincValue = AcousticMath.Sinc(k - fraction);
                    ir[sampleIdx] += directAmplitude * sincValue;
                }

                Debug.Log($"[AcousticIR] Direct sound: {directCount} B0 arrivals → 1 impulse at " +
                          $"{directTime * 1000:F1}ms, amplitude={directAmplitude:F4}, distance={directDistance:F1}m");
            }

            // === Pass 2: Accumulate reflections (bounce >= 1) individually ===
            int reflectionCount = 0;
            float reflectionEnergySum = 0f;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue; // Already handled above

                float samplePos = arrival.time * sampleRate;
                int centerSample = (int)samplePos;
                float fraction = samplePos - centerSample;

                // Amplitude: A-weighted band energy, attenuated by 1/totalDistance
                float totalDistance = arrival.time * speedOfSound;
                float distanceAtten = 1f / Mathf.Max(totalDistance, 0.1f);
                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy) * distanceAtten;

                reflectionEnergySum += amplitude * amplitude;
                reflectionCount++;

                // Sinc interpolation for sub-sample accuracy
                for (int k = -sincHalfWidth; k <= sincHalfWidth; k++)
                {
                    int sampleIdx = centerSample + k;
                    if (sampleIdx < 0 || sampleIdx >= ir.Length) continue;
                    float sincValue = AcousticMath.Sinc(k - fraction);
                    ir[sampleIdx] += amplitude * sincValue;
                }
            }

            Debug.Log($"[AcousticIR] Reflections: {reflectionCount} arrivals placed, " +
                      $"total RMS energy={Mathf.Sqrt(reflectionEnergySum):F4}");
        }

        /// <summary>
        /// Converts 6-band energy to a single amplitude value.
        /// Uses simplified A-weighting for perceptual accuracy.
        /// </summary>
        static float BandEnergyToAmplitude(AbsorptionCoefficients bandEnergy)
        {
            // A-weighting relative levels at each octave band (approximate dB)
            // 125Hz: -16.1, 250Hz: -8.6, 500Hz: -3.2, 1kHz: 0, 2kHz: +1.2, 4kHz: +1.0
            // Converted to linear weights (normalized so 1kHz = 1.0)
            const float w125 = 0.157f;
            const float w250 = 0.372f;
            const float w500 = 0.692f;
            const float w1k = 1.000f;
            const float w2k = 1.148f;
            const float w4k = 1.122f;
            const float totalWeight = w125 + w250 + w500 + w1k + w2k + w4k;

            float weighted =
                bandEnergy.band125Hz * w125 +
                bandEnergy.band250Hz * w250 +
                bandEnergy.band500Hz * w500 +
                bandEnergy.band1kHz * w1k +
                bandEnergy.band2kHz * w2k +
                bandEnergy.band4kHz * w4k;

            return weighted / totalWeight;
        }

        /// <summary>
        /// Synthesizes a stochastic late reverberation tail.
        /// Uses Poisson-distributed noise impulses with an exponential decay
        /// envelope matched to the energy decay from the raytracing data.
        /// Only considers B1+ reflections (not direct sound) for energy estimation.
        /// </summary>
        static void SynthesizeLateTail(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound)
        {
            // Find the energy decay curve from REFLECTION arrivals only (skip B0)
            float maxArrivalTime = 0f;
            float totalReflectionEnergy = 0f;
            int reflectionCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0) continue; // Skip direct sound

                if (arrivals[i].time > maxArrivalTime)
                    maxArrivalTime = arrivals[i].time;

                // Use distance-attenuated energy (same as AccumulateArrivals)
                float totalDist = arrivals[i].time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDist, 0.1f);
                float amp = BandEnergyToAmplitude(arrivals[i].bandEnergy) * distAtten;
                totalReflectionEnergy += amp * amp;
                reflectionCount++;
            }

            if (totalReflectionEnergy < 1e-8f || maxArrivalTime < 0.05f || reflectionCount == 0)
                return;

            float avgReflectionAmplitude = Mathf.Sqrt(totalReflectionEnergy / reflectionCount);

            // Early reflection cutoff: 80ms
            // Only synthesize late tail after this point
            const float earlyReflectionCutoff = 0.08f;
            int startSample = (int)(earlyReflectionCutoff * sampleRate);

            // Estimate RT60 from the arrival data
            // Heuristic: extend 2x beyond last arrival for natural tail
            float estimatedRT60 = maxArrivalTime * 2.0f;
            estimatedRT60 = math.clamp(estimatedRT60, 0.3f, (float)ir.Length / sampleRate);

            // Decay rate: energy should drop 60dB over RT60
            float decayRate = -6.908f / estimatedRT60; // ln(0.001) = -6.908

            // Density increases with time (Poisson process)
            var rng = new Unity.Mathematics.Random(42);
            float baseDensity = 300f; // Impulses per second at the start

            for (int s = startSample; s < ir.Length; s++)
            {
                float time = (float)s / sampleRate;
                float envelope = math.exp(decayRate * time) * avgReflectionAmplitude * 0.5f;

                if (envelope < 1e-8f)
                    break;

                // Density increases linearly with time (natural echo density growth)
                float density = baseDensity * (1f + time * 8f);
                float probability = density / sampleRate;

                if (rng.NextFloat() < probability)
                {
                    // Random amplitude with envelope
                    float noise = (rng.NextFloat() * 2f - 1f) * envelope;
                    ir[s] += noise;
                }
            }

            Debug.Log($"[AcousticIR] Late tail: RT60≈{estimatedRT60:F2}s, " +
                      $"avgReflectionAmp={avgReflectionAmplitude:F4}, " +
                      $"from {reflectionCount} reflections (max arrival {maxArrivalTime * 1000:F0}ms)");
        }

        /// <summary>
        /// Normalizes the IR buffer so the peak value equals the target dB level.
        /// </summary>
        static void NormalizePeak(float[] ir, float targetDb)
        {
            float peak = 0f;
            for (int i = 0; i < ir.Length; i++)
            {
                float abs = math.abs(ir[i]);
                if (abs > peak)
                    peak = abs;
            }

            if (peak < 1e-10f)
                return;

            float targetLinear = math.pow(10f, targetDb / 20f);
            float gain = targetLinear / peak;

            for (int i = 0; i < ir.Length; i++)
                ir[i] *= gain;
        }
    }
}
