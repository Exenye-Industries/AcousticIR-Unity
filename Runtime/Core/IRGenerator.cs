using Unity.Collections;
using Unity.Mathematics;

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
        /// <returns>Float array of IR samples, normalized to peak at -1dB.</returns>
        public static float[] Generate(
            NativeList<RayArrival> arrivals,
            int sampleRate = 48000,
            float irLengthSeconds = 2f,
            bool applyWindowing = true,
            float windowTailPortion = 0.1f,
            bool synthesizeLateTail = true)
        {
            int sampleCount = (int)(irLengthSeconds * sampleRate);
            float[] ir = new float[sampleCount];

            if (arrivals.Length == 0)
                return ir;

            // Phase 1: Accumulate ray arrivals into the IR buffer
            AccumulateArrivals(ir, arrivals, sampleRate);

            // Phase 2: Synthesize late reverb tail (stochastic noise with decay)
            if (synthesizeLateTail)
                SynthesizeLateTail(ir, arrivals, sampleRate);

            // Phase 3: Normalize to -1dB peak
            NormalizePeak(ir, -1f);

            // Phase 4: Apply windowing to the tail
            if (applyWindowing)
                AcousticMath.ApplyHannWindow(ir, windowTailPortion);

            return ir;
        }

        /// <summary>
        /// Accumulates ray arrivals into the IR buffer using sinc interpolation
        /// for sub-sample accuracy.
        /// </summary>
        static void AccumulateArrivals(float[] ir, NativeList<RayArrival> arrivals, int sampleRate)
        {
            // Sinc interpolation kernel half-width (in samples)
            const int sincHalfWidth = 4;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];

                // Convert arrival time to fractional sample position
                float samplePos = arrival.time * sampleRate;
                int centerSample = (int)samplePos;
                float fraction = samplePos - centerSample;

                // Convert band energy to a single amplitude value.
                // Weight by perceptual importance (A-weighting simplified):
                // Lower bands are less perceptually important, higher bands more.
                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy);

                // Apply sinc interpolation kernel around the center sample
                for (int k = -sincHalfWidth; k <= sincHalfWidth; k++)
                {
                    int sampleIdx = centerSample + k;
                    if (sampleIdx < 0 || sampleIdx >= ir.Length)
                        continue;

                    float sincValue = AcousticMath.Sinc(k - fraction);
                    ir[sampleIdx] += amplitude * sincValue;
                }
            }
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
        /// </summary>
        static void SynthesizeLateTail(float[] ir, NativeList<RayArrival> arrivals, int sampleRate)
        {
            // Find the energy decay curve from arrivals
            float maxArrivalTime = 0f;
            float totalEnergy = 0f;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].time > maxArrivalTime)
                    maxArrivalTime = arrivals[i].time;
                totalEnergy += arrivals[i].bandEnergy.TotalEnergy;
            }

            if (totalEnergy < 1e-6f || maxArrivalTime < 0.05f)
                return;

            // Early reflection cutoff: 80ms
            // Only synthesize late tail after this point
            const float earlyReflectionCutoff = 0.08f;
            int startSample = (int)(earlyReflectionCutoff * sampleRate);

            // Estimate RT60 from the arrival data (very rough)
            // Find when the accumulated energy drops to 60dB below the peak
            float avgEnergyPerArrival = totalEnergy / arrivals.Length;
            float estimatedRT60 = maxArrivalTime * 1.5f; // Heuristic: extend beyond last arrival
            estimatedRT60 = math.clamp(estimatedRT60, 0.2f, (float)ir.Length / sampleRate);

            // Decay rate: energy should drop 60dB over RT60
            float decayRate = -6.908f / estimatedRT60; // ln(0.001) = -6.908

            // Density increases with time (Poisson process)
            var rng = new Unity.Mathematics.Random(42);
            float baseDensity = 200f; // Impulses per second at the start

            for (int s = startSample; s < ir.Length; s++)
            {
                float time = (float)s / sampleRate;
                float envelope = math.exp(decayRate * time) * avgEnergyPerArrival * 0.3f;

                if (envelope < 1e-7f)
                    break;

                // Density increases linearly with time (natural echo density growth)
                float density = baseDensity * (1f + time * 5f);
                float probability = density / sampleRate;

                if (rng.NextFloat() < probability)
                {
                    // Random amplitude with envelope
                    float noise = (rng.NextFloat() * 2f - 1f) * envelope;
                    ir[s] += noise;
                }
            }
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
