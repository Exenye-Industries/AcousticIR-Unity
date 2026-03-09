using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Converts ray arrivals into time-domain impulse responses.
    ///
    /// Architecture:
    /// - Early reflections (0 to mixing time): Spectrally-shaped impulses using
    ///   pre-computed 6-band kernels (4th-order cascaded biquad filters)
    /// - Late reverberation (mixing time to end): Per-band Velvet Noise synthesis
    ///   with frequency-dependent RT60 decay + air absorption (HF decays faster)
    /// - Smooth quadratic crossfade at the transition point
    /// </summary>
    public static class IRGenerator
    {
        // ====================================================================
        // CONSTANTS
        // ====================================================================

        const int NumBands = 6;
        const int BandKernelLength = 256; // ~5.3ms at 48kHz, enough for LP@177Hz to settle
        const float VelvetImpulseDensity = 2000f; // Impulses per second

        /// <summary>
        /// Per-band RT60 minimum multipliers (relative to maxArrivalTime).
        /// LF bands get longer minimum decay, HF bands shorter.
        /// Prevents HF from ringing as long as LF.
        /// </summary>
        static readonly float[] BandRT60Multipliers = { 2.5f, 2.2f, 1.8f, 1.4f, 1.0f, 0.7f };

        /// <summary>
        /// Air absorption extra decay in dB/second.
        /// Derived from ISO 9613-1 coefficients × speed of sound.
        /// Applied on top of material-based RT60 to ensure HF dies faster.
        /// </summary>
        static readonly float[] AirAbsDecayDbPerSec = { 0.103f, 0.378f, 0.927f, 1.890f, 3.953f, 13.054f };

        // ====================================================================
        // MULTI-BAND FILTER INFRASTRUCTURE
        // 6 octave bands covering DC to Nyquist:
        //   Band 0: LP at 177 Hz   (DC–177 Hz, represents 125 Hz octave)
        //   Band 1: BP at 250 Hz   (177–354 Hz)
        //   Band 2: BP at 500 Hz   (354–707 Hz)
        //   Band 3: BP at 1000 Hz  (707–1414 Hz)
        //   Band 4: BP at 2000 Hz  (1414–2828 Hz)
        //   Band 5: HP at 2828 Hz  (2828 Hz–Nyquist, represents 4 kHz octave)
        // ====================================================================

        struct BiquadCoeffs
        {
            public float b0, b1, b2, a1, a2;
        }

        /// <summary>2nd-order Butterworth lowpass.</summary>
        static BiquadCoeffs DesignLowpass(float cutoffHz, int sampleRate)
        {
            float w0 = 2f * math.PI * cutoffHz / sampleRate;
            float cosW0 = math.cos(w0);
            float alpha = math.sin(w0) / (2f * 0.7071f);
            float a0 = 1f + alpha;
            return new BiquadCoeffs
            {
                b0 = (1f - cosW0) * 0.5f / a0,
                b1 = (1f - cosW0) / a0,
                b2 = (1f - cosW0) * 0.5f / a0,
                a1 = -2f * cosW0 / a0,
                a2 = (1f - alpha) / a0
            };
        }

        /// <summary>2nd-order Butterworth highpass.</summary>
        static BiquadCoeffs DesignHighpass(float cutoffHz, int sampleRate)
        {
            float w0 = 2f * math.PI * cutoffHz / sampleRate;
            float cosW0 = math.cos(w0);
            float alpha = math.sin(w0) / (2f * 0.7071f);
            float a0 = 1f + alpha;
            return new BiquadCoeffs
            {
                b0 = (1f + cosW0) * 0.5f / a0,
                b1 = -(1f + cosW0) / a0,
                b2 = (1f + cosW0) * 0.5f / a0,
                a1 = -2f * cosW0 / a0,
                a2 = (1f - alpha) / a0
            };
        }

        /// <summary>2nd-order bandpass (constant peak gain = 1).</summary>
        static BiquadCoeffs DesignBandpass(float centerHz, float q, int sampleRate)
        {
            float w0 = 2f * math.PI * centerHz / sampleRate;
            float alpha = math.sin(w0) / (2f * q);
            float a0 = 1f + alpha;
            return new BiquadCoeffs
            {
                b0 = alpha / a0,
                b1 = 0f,
                b2 = -alpha / a0,
                a1 = -2f * math.cos(w0) / a0,
                a2 = (1f - alpha) / a0
            };
        }

        /// <summary>Applies a biquad filter to a buffer (in-place, causal).</summary>
        static void ApplyBiquad(float[] buffer, BiquadCoeffs c, int startSample = 0)
        {
            float x1 = 0f, x2 = 0f, y1 = 0f, y2 = 0f;
            for (int i = startSample; i < buffer.Length; i++)
            {
                float x = buffer[i];
                float y = c.b0 * x + c.b1 * x1 + c.b2 * x2 - c.a1 * y1 - c.a2 * y2;
                x2 = x1; x1 = x;
                y2 = y1; y1 = y;
                buffer[i] = y;
            }
        }

        /// <summary>Returns the appropriate filter for the given band index.</summary>
        static BiquadCoeffs GetBandFilter(int bandIndex, int sampleRate)
        {
            if (bandIndex == 0)
                return DesignLowpass(177f, sampleRate);
            if (bandIndex == NumBands - 1)
                return DesignHighpass(2828f, sampleRate);
            float[] centers = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
            return DesignBandpass(centers[bandIndex], 1.414f, sampleRate);
        }

        /// <summary>Extracts a single band value from AbsorptionCoefficients.</summary>
        static float GetBandValue(AbsorptionCoefficients coeff, int bandIndex)
        {
            switch (bandIndex)
            {
                case 0: return coeff.band125Hz;
                case 1: return coeff.band250Hz;
                case 2: return coeff.band500Hz;
                case 3: return coeff.band1kHz;
                case 4: return coeff.band2kHz;
                case 5: return coeff.band4kHz;
                default: return 0f;
            }
        }

        // ====================================================================
        // PRE-COMPUTED BAND KERNELS
        // ====================================================================

        /// <summary>
        /// Pre-computes normalized 4th-order band filter kernels.
        /// Each kernel is the impulse response of two cascaded biquads,
        /// normalized so the peak amplitude = 1.0.
        /// This fixes the old per-impulse bandpass issue where LF bands
        /// produced tiny peaks (b0 ≈ 0.01 for 250Hz BP at 48kHz).
        /// </summary>
        static float[][] PrecomputeBandKernels(int sampleRate)
        {
            float[][] kernels = new float[NumBands][];

            for (int b = 0; b < NumBands; b++)
            {
                kernels[b] = new float[BandKernelLength];
                kernels[b][0] = 1f; // Unit impulse at start

                // Cascaded biquad (4th order = 24 dB/octave rolloff)
                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(kernels[b], coeffs);
                ApplyBiquad(kernels[b], coeffs);

                // Normalize peak to 1.0 so band energy scaling works correctly
                float peak = 0f;
                for (int i = 0; i < BandKernelLength; i++)
                    if (math.abs(kernels[b][i]) > peak)
                        peak = math.abs(kernels[b][i]);

                if (peak > 1e-15f)
                {
                    float norm = 1f / peak;
                    for (int i = 0; i < BandKernelLength; i++)
                        kernels[b][i] *= norm;
                }
            }

            return kernels;
        }

        // ====================================================================
        // MIXING TIME ESTIMATION
        // ====================================================================

        /// <summary>
        /// Estimates the mixing time: the transition point where discrete
        /// early reflections blend into diffuse late reverberation.
        /// Uses the broadband energy histogram peak as reference.
        /// Result clamped to [40ms, 120ms].
        /// </summary>
        static float EstimateMixingTime(NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound)
        {
            const int binMs = 5;

            float maxTime = 0f;
            for (int i = 0; i < arrivals.Length; i++)
                if (arrivals[i].bounceCount > 0 && arrivals[i].time > maxTime)
                    maxTime = arrivals[i].time;

            if (maxTime < 0.02f) return 40f;

            int numBins = (int)(maxTime * 1000f / binMs) + 2;
            float[] broadbandBins = new float[numBins];

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0) continue;
                int bin = (int)(arrivals[i].time * 1000f / binMs);
                if (bin < 0 || bin >= numBins) continue;

                float totalDist = arrivals[i].time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDist, 0.1f);
                float amp = BandEnergyToAmplitude(arrivals[i].bandEnergy) * distAtten;
                broadbandBins[bin] += amp * amp;
            }

            float peakEnergy = 0f;
            int peakBin = 2;
            for (int bin = 2; bin < numBins; bin++)
            {
                if (broadbandBins[bin] > peakEnergy)
                {
                    peakEnergy = broadbandBins[bin];
                    peakBin = bin;
                }
            }

            return math.clamp(peakBin * binMs * 0.8f, 40f, 120f);
        }

        /// <summary>
        /// Converts 6-band energy to a single amplitude value.
        /// Uses simplified A-weighting for perceptual accuracy.
        /// </summary>
        static float BandEnergyToAmplitude(AbsorptionCoefficients bandEnergy)
        {
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

        // ====================================================================
        // MONO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a mono impulse response from ray arrivals.
        /// Early reflections are spectrally shaped, late tail uses per-band Velvet Noise.
        /// </summary>
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

            // Pre-compute band kernels for spectral shaping
            float[][] bandKernels = PrecomputeBandKernels(sampleRate);

            // Estimate mixing time (early/late boundary)
            float mixingTimeMs = EstimateMixingTime(arrivals, sampleRate, speedOfSound);

            // Phase 1: Early reflections (spectrally shaped)
            AccumulateArrivals(ir, arrivals, sampleRate, speedOfSound, rayCount,
                mixingTimeMs, bandKernels);

            // Phase 2: Late reverb tail (per-band Velvet Noise)
            if (synthesizeLateTail)
                SynthesizeLateTail(ir, arrivals, sampleRate, speedOfSound, mixingTimeMs);

            // Phase 3: Normalize to -1dB peak
            NormalizePeak(ir, -1f);

            // Phase 4: Window the tail
            if (applyWindowing)
                AcousticMath.ApplyHannWindow(ir, windowTailPortion);

            return ir;
        }

        /// <summary>
        /// Accumulates early reflections as spectrally-shaped impulses.
        /// Direct sound (B0) is placed as a broadband impulse.
        /// Reflections before the mixing time are placed using 6-band kernels.
        /// Reflections after the mixing time are handled by the tail synthesis.
        /// </summary>
        static void AccumulateArrivals(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, int rayCount,
            float mixingTimeMs, float[][] bandKernels)
        {
            const int sincHalfWidth = 4;
            float mixingTimeS = mixingTimeMs / 1000f;

            // === Pass 1: Direct sound (bounce 0) — broadband ===
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

            if (directCount > 0)
            {
                float directTime = directTimeSum / directCount;
                float directDistance = directTime * speedOfSound;
                float distAtten = 1f / Mathf.Max(directDistance, 0.1f);
                float samplePos = directTime * sampleRate;
                float amplitude = (directEnergySum / directCount) * distAtten;

                PlaceSincImpulse(ir, samplePos, amplitude, sincHalfWidth);

                Debug.Log($"[AcousticIR] Direct sound: {directCount} B0 arrivals → 1 impulse at " +
                          $"{directTime * 1000:F1}ms, distance={directDistance:F1}m, amp={amplitude:F4}");
            }

            // === Pass 2: Early reflections (spectrally shaped via band kernels) ===
            int earlyCount = 0;
            int lateCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                if (arrival.time > mixingTimeS)
                {
                    lateCount++;
                    continue;
                }

                float totalDistance = arrival.time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDistance, 0.1f);
                float samplePos = arrival.time * sampleRate;

                PlaceMultiBandImpulse(ir, samplePos, arrival.bandEnergy, distAtten, bandKernels);
                earlyCount++;
            }

            Debug.Log($"[AcousticIR] Early reflections: {earlyCount} spectrally shaped " +
                      $"(mixing@{mixingTimeMs:F0}ms), {lateCount} deferred to tail");
        }

        /// <summary>
        /// Places a spectrally-shaped impulse using pre-computed band kernels.
        /// Each band is scaled by its energy and added to the buffer.
        /// The kernel normalization ensures correct energy balance across bands
        /// regardless of filter characteristics at different frequencies.
        /// </summary>
        static void PlaceMultiBandImpulse(float[] buffer, float samplePos,
            AbsorptionCoefficients bandEnergy, float distAtten, float[][] bandKernels)
        {
            int center = (int)math.round(samplePos);

            for (int b = 0; b < NumBands; b++)
            {
                float bandAmp = GetBandValue(bandEnergy, b) * distAtten;
                if (bandAmp < 1e-8f) continue;

                for (int i = 0; i < BandKernelLength; i++)
                {
                    int bufIdx = center + i;
                    if (bufIdx >= 0 && bufIdx < buffer.Length)
                        buffer[bufIdx] += bandKernels[b][i] * bandAmp;
                }
            }
        }

        /// <summary>
        /// Per-band late reverb tail synthesis using Velvet Noise.
        ///
        /// Each band gets:
        /// - Independent RT60 estimated from arrival energy histogram
        /// - Band-dependent RT60 minimum (LF longer, HF shorter)
        /// - Additional air absorption decay (HF dies faster)
        /// - Velvet Noise: sparse ±1 impulses at ~2000/s (smoother than Gaussian)
        /// - 4th-order cascaded biquad filtering (sharper band isolation)
        /// </summary>
        static void SynthesizeLateTail(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, float mixingTimeMs)
        {
            const int binMs = 5;
            int binSamples = binMs * sampleRate / 1000;
            int numBins = ir.Length / binSamples + 1;

            // === Build per-band energy histograms ===
            float[][] bandBinEnergy = new float[NumBands][];
            for (int b = 0; b < NumBands; b++)
                bandBinEnergy[b] = new float[numBins];

            float maxArrivalTime = 0f;
            int reflectionCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0) continue;

                if (arrivals[i].time > maxArrivalTime)
                    maxArrivalTime = arrivals[i].time;

                int bin = (int)(arrivals[i].time * 1000f / binMs);
                if (bin < 0 || bin >= numBins) continue;

                float totalDist = arrivals[i].time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDist, 0.1f);

                for (int b = 0; b < NumBands; b++)
                {
                    float bandAmp = GetBandValue(arrivals[i].bandEnergy, b) * distAtten;
                    bandBinEnergy[b][bin] += bandAmp * bandAmp;
                }
                reflectionCount++;
            }

            if (reflectionCount == 0 || maxArrivalTime < 0.02f)
                return;

            // Normalize by bin size → energy density per sample
            for (int b = 0; b < NumBands; b++)
                for (int bin = 0; bin < numBins; bin++)
                    bandBinEnergy[b][bin] /= binSamples;

            // Mixing time from parameter
            int mixingStartSample = (int)(mixingTimeMs / 1000f * sampleRate);
            int mixingEndSample = (int)((mixingTimeMs + 50f) / 1000f * sampleRate);
            float irLenS = (float)ir.Length / sampleRate;

            float overallRT60 = 0f;
            int activeBands = 0;
            float gridSize = (float)sampleRate / VelvetImpulseDensity;

            for (int b = 0; b < NumBands; b++)
            {
                // Find peak energy for this band
                float peakEnergy = 0f;
                int peakBin = 2;
                for (int bin = 2; bin < numBins; bin++)
                {
                    if (bandBinEnergy[b][bin] > peakEnergy)
                    {
                        peakEnergy = bandBinEnergy[b][bin];
                        peakBin = bin;
                    }
                }

                if (peakEnergy < 1e-14f) continue;

                float peakTimeS = peakBin * binMs / 1000f;

                // === RT60 estimation via exponential regression ===
                float bandRT60;
                {
                    float noiseFloor = peakEnergy * 1e-4f;
                    float sumT = 0f, sumLogE = 0f, sumTT = 0f, sumTLogE = 0f;
                    int n = 0;

                    for (int bin = peakBin; bin < numBins; bin++)
                    {
                        if (bandBinEnergy[b][bin] > noiseFloor)
                        {
                            float t = (bin - peakBin) * binMs / 1000f;
                            float logE = math.log(bandBinEnergy[b][bin]);
                            sumT += t; sumLogE += logE;
                            sumTT += t * t; sumTLogE += t * logE;
                            n++;
                        }
                    }

                    if (n >= 5)
                    {
                        float denom = n * sumTT - sumT * sumT;
                        if (math.abs(denom) > 1e-12f)
                        {
                            float slope = (n * sumTLogE - sumT * sumLogE) / denom;
                            bandRT60 = slope < -0.1f ? -13.816f / slope : irLenS;
                        }
                        else
                            bandRT60 = irLenS;
                    }
                    else
                        bandRT60 = maxArrivalTime * 4.0f;

                    // Per-band RT60 minimum (band-dependent, not uniform!)
                    float minRT60 = maxArrivalTime * BandRT60Multipliers[b];
                    bandRT60 = math.max(bandRT60, minRT60);
                    bandRT60 = math.clamp(bandRT60, 0.3f, irLenS);
                }

                overallRT60 = math.max(overallRT60, bandRT60);
                activeBands++;

                // Decay rate: material absorption + air absorption
                float materialDecayRate = -13.816f / bandRT60;
                float airDecayRate = -AirAbsDecayDbPerSec[b] / 20f * Mathf.Log(10f);
                float totalDecayRate = materialDecayRate + airDecayRate;
                float peakAmp = math.sqrt(peakEnergy);

                // === Velvet Noise generation ===
                float[] bandNoise = new float[ir.Length];
                var rng = new Unity.Mathematics.Random((uint)(12345 + b * 7919));

                for (float gs = mixingStartSample; gs < ir.Length; gs += gridSize)
                {
                    int gsInt = (int)gs;
                    int geInt = math.min((int)(gs + gridSize), ir.Length);
                    int gLen = geInt - gsInt;
                    if (gLen <= 0) continue;

                    int impulsePos = gsInt + (int)(rng.NextFloat() * gLen);
                    if (impulsePos >= ir.Length) continue;

                    float time = (float)impulsePos / sampleRate;
                    float timeSincePeak = math.max(0f, time - peakTimeS);
                    float envelope = peakAmp * math.exp(totalDecayRate * 0.5f * timeSincePeak);
                    if (envelope < 1e-20f) continue;

                    // Crossfade from discrete arrivals to noise tail
                    float crossfade = 1f;
                    if (impulsePos < mixingEndSample)
                    {
                        crossfade = (float)(impulsePos - mixingStartSample)
                            / (float)(mixingEndSample - mixingStartSample);
                        crossfade = math.clamp(crossfade, 0f, 1f);
                        crossfade = crossfade * crossfade; // quadratic ease-in
                    }

                    float polarity = rng.NextFloat() > 0.5f ? 1f : -1f;
                    bandNoise[impulsePos] = polarity * envelope * crossfade;
                }

                // 4th-order cascaded biquad (24 dB/octave rolloff)
                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(bandNoise, coeffs, mixingStartSample);
                ApplyBiquad(bandNoise, coeffs, mixingStartSample);

                // Accumulate to IR
                for (int s = mixingStartSample; s < ir.Length; s++)
                    ir[s] += bandNoise[s];
            }

            Debug.Log($"[AcousticIR] Per-band tail (velvet): {activeBands} bands, " +
                      $"max RT60≈{overallRT60:F2}s, mixing@{mixingTimeMs:F0}ms, " +
                      $"from {reflectionCount} reflections (last at {maxArrivalTime * 1000:F0}ms)");
        }

        // ====================================================================
        // STEREO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a stereo impulse response from ray arrivals.
        /// Early reflections are spectrally shaped with stereo imaging.
        /// Late tail uses decorrelated per-band Velvet Noise.
        /// </summary>
        public static (float[] left, float[] right) GenerateStereo(
            NativeList<RayArrival> arrivals,
            float3 receiverForward,
            float3 receiverUp,
            StereoConfig stereoConfig,
            int sampleRate = 48000,
            float irLengthSeconds = 2f,
            bool applyWindowing = true,
            float windowTailPortion = 0.1f,
            bool synthesizeLateTail = true,
            float speedOfSound = 343f,
            int rayCount = 4096)
        {
            int sampleCount = (int)(irLengthSeconds * sampleRate);
            float[] irL = new float[sampleCount];
            float[] irR = new float[sampleCount];

            if (arrivals.Length == 0)
                return (irL, irR);

            // Build orthonormal receiver basis
            receiverForward = math.normalize(receiverForward);
            receiverUp = math.normalize(receiverUp);
            float3 receiverRight = math.normalize(math.cross(receiverForward, receiverUp));
            receiverUp = math.cross(receiverRight, receiverForward);

            // Pre-compute band kernels
            float[][] bandKernels = PrecomputeBandKernels(sampleRate);

            // Estimate mixing time
            float mixingTimeMs = EstimateMixingTime(arrivals, sampleRate, speedOfSound);

            // Phase 1: Early reflections (spectrally shaped stereo)
            AccumulateStereoArrivals(irL, irR, arrivals, sampleRate, speedOfSound,
                rayCount, receiverForward, receiverRight, stereoConfig,
                mixingTimeMs, bandKernels);

            // Phase 2: Late reverb tail (decorrelated per-band Velvet Noise)
            if (synthesizeLateTail)
                SynthesizeLateTailStereo(irL, irR, arrivals, sampleRate, speedOfSound, mixingTimeMs);

            // Phase 3: Normalize both channels together
            NormalizePeakStereo(irL, irR, -1f);

            // Phase 4: Window both channels
            if (applyWindowing)
            {
                AcousticMath.ApplyHannWindow(irL, windowTailPortion);
                AcousticMath.ApplyHannWindow(irR, windowTailPortion);
            }

            Debug.Log($"[AcousticIR] Stereo IR generated: {stereoConfig.mode} mode, " +
                      $"{sampleCount} samples/channel ({irLengthSeconds:F1}s @ {sampleRate}Hz)");

            return (irL, irR);
        }

        /// <summary>
        /// Accumulates early stereo reflections with spectral shaping.
        /// </summary>
        static void AccumulateStereoArrivals(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound,
            int rayCount, float3 receiverForward, float3 receiverRight,
            StereoConfig config, float mixingTimeMs, float[][] bandKernels)
        {
            const int sincHalfWidth = 4;
            float mixingTimeS = mixingTimeMs / 1000f;

            // === Pass 1: Direct sound (broadband stereo) ===
            float directTimeSum = 0f;
            float3 directDirSum = float3.zero;
            float directEnergySum = 0f;
            int directCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0)
                {
                    directTimeSum += arrivals[i].time;
                    directDirSum += arrivals[i].direction;
                    directEnergySum += BandEnergyToAmplitude(arrivals[i].bandEnergy);
                    directCount++;
                }
            }

            if (directCount > 0)
            {
                float directTime = directTimeSum / directCount;
                float directDistance = directTime * speedOfSound;
                float distAtten = 1f / Mathf.Max(directDistance, 0.1f);
                float3 directDir = math.normalizesafe(directDirSum / directCount);
                float amplitude = (directEnergySum / directCount) * distAtten;

                ComputeStereoGains(directDir, receiverForward, receiverRight,
                    config, speedOfSound, sampleRate,
                    out float gainL, out float gainR,
                    out int sampleOffsetL, out int sampleOffsetR);

                float samplePosL = directTime * sampleRate + sampleOffsetL;
                float samplePosR = directTime * sampleRate + sampleOffsetR;

                PlaceSincImpulse(irL, samplePosL, amplitude * gainL, sincHalfWidth);
                PlaceSincImpulse(irR, samplePosR, amplitude * gainR, sincHalfWidth);
            }

            // === Pass 2: Early reflections (spectrally shaped stereo) ===
            int earlyCount = 0;
            int lateCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                if (arrival.time > mixingTimeS)
                {
                    lateCount++;
                    continue;
                }

                float totalDistance = arrival.time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDistance, 0.1f);

                ComputeStereoGains(arrival.direction, receiverForward, receiverRight,
                    config, speedOfSound, sampleRate,
                    out float gainL, out float gainR,
                    out int sampleOffsetL, out int sampleOffsetR);

                float samplePosL = arrival.time * sampleRate + sampleOffsetL;
                float samplePosR = arrival.time * sampleRate + sampleOffsetR;

                PlaceMultiBandImpulseStereo(irL, irR, samplePosL, samplePosR,
                    arrival.bandEnergy, distAtten, gainL, gainR, bandKernels);

                earlyCount++;
            }

            Debug.Log($"[AcousticIR] Stereo early: {earlyCount} spectrally shaped " +
                      $"({config.mode} mode, mixing@{mixingTimeMs:F0}ms), {lateCount} deferred");
        }

        /// <summary>
        /// Places a spectrally-shaped stereo impulse using pre-computed band kernels.
        /// </summary>
        static void PlaceMultiBandImpulseStereo(float[] bufferL, float[] bufferR,
            float samplePosL, float samplePosR,
            AbsorptionCoefficients bandEnergy, float distAtten,
            float gainL, float gainR, float[][] bandKernels)
        {
            int centerL = (int)math.round(samplePosL);
            int centerR = (int)math.round(samplePosR);

            for (int b = 0; b < NumBands; b++)
            {
                float bandAmp = GetBandValue(bandEnergy, b) * distAtten;
                if (bandAmp < 1e-8f) continue;

                float ampL = bandAmp * gainL;
                float ampR = bandAmp * gainR;

                for (int i = 0; i < BandKernelLength; i++)
                {
                    float kv = bandKernels[b][i];
                    int idxL = centerL + i;
                    int idxR = centerR + i;
                    if (idxL >= 0 && idxL < bufferL.Length)
                        bufferL[idxL] += kv * ampL;
                    if (idxR >= 0 && idxR < bufferR.Length)
                        bufferR[idxR] += kv * ampR;
                }
            }
        }

        /// <summary>
        /// Per-band decorrelated stereo late reverb tail using Velvet Noise.
        /// Independent L/R noise seeds produce natural diffuse reverb imaging.
        /// </summary>
        static void SynthesizeLateTailStereo(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound,
            float mixingTimeMs)
        {
            const int binMs = 5;
            int binSamples = binMs * sampleRate / 1000;
            int numBins = irL.Length / binSamples + 1;

            // Build per-band energy histograms
            float[][] bandBinEnergy = new float[NumBands][];
            for (int b = 0; b < NumBands; b++)
                bandBinEnergy[b] = new float[numBins];

            float maxArrivalTime = 0f;
            int reflectionCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                if (arrivals[i].bounceCount == 0) continue;

                if (arrivals[i].time > maxArrivalTime)
                    maxArrivalTime = arrivals[i].time;

                int bin = (int)(arrivals[i].time * 1000f / binMs);
                if (bin < 0 || bin >= numBins) continue;

                float totalDist = arrivals[i].time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDist, 0.1f);

                for (int b = 0; b < NumBands; b++)
                {
                    float bandAmp = GetBandValue(arrivals[i].bandEnergy, b) * distAtten;
                    bandBinEnergy[b][bin] += bandAmp * bandAmp;
                }
                reflectionCount++;
            }

            if (reflectionCount == 0 || maxArrivalTime < 0.02f)
                return;

            for (int b = 0; b < NumBands; b++)
                for (int bin = 0; bin < numBins; bin++)
                    bandBinEnergy[b][bin] /= binSamples;

            int mixingStartSample = (int)(mixingTimeMs / 1000f * sampleRate);
            int mixingEndSample = (int)((mixingTimeMs + 50f) / 1000f * sampleRate);
            float irLenS = (float)irL.Length / sampleRate;

            float overallRT60 = 0f;
            int activeBands = 0;
            float gridSize = (float)sampleRate / VelvetImpulseDensity;

            for (int b = 0; b < NumBands; b++)
            {
                float peakEnergy = 0f;
                int peakBin = 2;
                for (int bin = 2; bin < numBins; bin++)
                {
                    if (bandBinEnergy[b][bin] > peakEnergy)
                    {
                        peakEnergy = bandBinEnergy[b][bin];
                        peakBin = bin;
                    }
                }

                if (peakEnergy < 1e-14f) continue;

                float peakTimeS = peakBin * binMs / 1000f;

                // RT60 estimation
                float bandRT60;
                {
                    float noiseFloor = peakEnergy * 1e-4f;
                    float sumT = 0f, sumLogE = 0f, sumTT = 0f, sumTLogE = 0f;
                    int n = 0;

                    for (int bin = peakBin; bin < numBins; bin++)
                    {
                        if (bandBinEnergy[b][bin] > noiseFloor)
                        {
                            float t = (bin - peakBin) * binMs / 1000f;
                            float logE = math.log(bandBinEnergy[b][bin]);
                            sumT += t; sumLogE += logE;
                            sumTT += t * t; sumTLogE += t * logE;
                            n++;
                        }
                    }

                    if (n >= 5)
                    {
                        float denom = n * sumTT - sumT * sumT;
                        if (math.abs(denom) > 1e-12f)
                        {
                            float slope = (n * sumTLogE - sumT * sumLogE) / denom;
                            bandRT60 = slope < -0.1f ? -13.816f / slope : irLenS;
                        }
                        else
                            bandRT60 = irLenS;
                    }
                    else
                        bandRT60 = maxArrivalTime * 4.0f;

                    // Per-band RT60 minimum
                    float minRT60 = maxArrivalTime * BandRT60Multipliers[b];
                    bandRT60 = math.max(bandRT60, minRT60);
                    bandRT60 = math.clamp(bandRT60, 0.3f, irLenS);
                }

                overallRT60 = math.max(overallRT60, bandRT60);
                activeBands++;

                float materialDecayRate = -13.816f / bandRT60;
                float airDecayRate = -AirAbsDecayDbPerSec[b] / 20f * Mathf.Log(10f);
                float totalDecayRate = materialDecayRate + airDecayRate;
                float peakAmp = math.sqrt(peakEnergy);

                // Decorrelated L/R Velvet Noise
                float[] bandNoiseL = new float[irL.Length];
                float[] bandNoiseR = new float[irR.Length];
                var rngL = new Unity.Mathematics.Random((uint)(12345 + b * 7919));
                var rngR = new Unity.Mathematics.Random((uint)(67890 + b * 6271));

                for (float gs = mixingStartSample; gs < irL.Length; gs += gridSize)
                {
                    int gsInt = (int)gs;
                    int geInt = math.min((int)(gs + gridSize), irL.Length);
                    int gLen = geInt - gsInt;
                    if (gLen <= 0) continue;

                    int posL = gsInt + (int)(rngL.NextFloat() * gLen);
                    int posR = gsInt + (int)(rngR.NextFloat() * gLen);

                    float time = (float)gsInt / sampleRate + (gridSize * 0.5f / sampleRate);
                    float timeSincePeak = math.max(0f, time - peakTimeS);
                    float envelope = peakAmp * math.exp(totalDecayRate * 0.5f * timeSincePeak);
                    if (envelope < 1e-20f) continue;

                    float crossfade = 1f;
                    if (gsInt < mixingEndSample)
                    {
                        crossfade = (float)(gsInt - mixingStartSample)
                            / (float)(mixingEndSample - mixingStartSample);
                        crossfade = math.clamp(crossfade, 0f, 1f);
                        crossfade = crossfade * crossfade;
                    }

                    float polarityL = rngL.NextFloat() > 0.5f ? 1f : -1f;
                    float polarityR = rngR.NextFloat() > 0.5f ? 1f : -1f;

                    if (posL >= 0 && posL < irL.Length)
                        bandNoiseL[posL] = polarityL * envelope * crossfade;
                    if (posR >= 0 && posR < irR.Length)
                        bandNoiseR[posR] = polarityR * envelope * crossfade;
                }

                // 4th-order cascaded biquad
                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(bandNoiseL, coeffs, mixingStartSample);
                ApplyBiquad(bandNoiseL, coeffs, mixingStartSample);
                ApplyBiquad(bandNoiseR, coeffs, mixingStartSample);
                ApplyBiquad(bandNoiseR, coeffs, mixingStartSample);

                for (int s = mixingStartSample; s < irL.Length; s++)
                {
                    irL[s] += bandNoiseL[s];
                    irR[s] += bandNoiseR[s];
                }
            }

            Debug.Log($"[AcousticIR] Stereo per-band tail (velvet): {activeBands} bands, " +
                      $"max RT60≈{overallRT60:F2}s, mixing@{mixingTimeMs:F0}ms");
        }

        // ====================================================================
        // SHARED UTILITIES
        // ====================================================================

        /// <summary>
        /// Places a single impulse into a buffer using sinc interpolation.
        /// </summary>
        static void PlaceSincImpulse(float[] buffer, float samplePos,
            float amplitude, int sincHalfWidth)
        {
            int centerSample = (int)samplePos;
            float fraction = samplePos - centerSample;

            for (int k = -sincHalfWidth; k <= sincHalfWidth; k++)
            {
                int sampleIdx = centerSample + k;
                if (sampleIdx < 0 || sampleIdx >= buffer.Length) continue;
                float sincValue = AcousticMath.Sinc(k - fraction);
                buffer[sampleIdx] += amplitude * sincValue;
            }
        }

        /// <summary>
        /// Computes left/right gains and sample offsets based on stereo mode.
        /// </summary>
        static void ComputeStereoGains(float3 arrivalDirection,
            float3 receiverForward, float3 receiverRight,
            StereoConfig config, float speedOfSound, int sampleRate,
            out float gainL, out float gainR,
            out int sampleOffsetL, out int sampleOffsetR)
        {
            sampleOffsetL = 0;
            sampleOffsetR = 0;

            float3 incoming = -math.normalizesafe(arrivalDirection);

            switch (config.mode)
            {
                case StereoMode.XY:
                    ComputeXYGains(incoming, receiverForward, receiverRight,
                        config.xyHalfAngleDeg, out gainL, out gainR);
                    break;

                case StereoMode.AB:
                    gainL = 1f;
                    gainR = 1f;
                    ComputeABOffsets(incoming, receiverRight,
                        config.abSpacingMeters, speedOfSound, sampleRate,
                        out sampleOffsetL, out sampleOffsetR);
                    break;

                case StereoMode.MS:
                    ComputeMSGains(incoming, receiverForward, receiverRight,
                        config.msWidth, out gainL, out gainR);
                    break;

                default:
                    gainL = 1f;
                    gainR = 1f;
                    break;
            }
        }

        static void ComputeXYGains(float3 incoming, float3 forward, float3 right,
            float halfAngleDeg, out float gainL, out float gainR)
        {
            float halfAngleRad = math.radians(halfAngleDeg);
            float cosHalf = math.cos(halfAngleRad);
            float sinHalf = math.sin(halfAngleRad);

            float3 leftMicDir = forward * cosHalf - right * sinHalf;
            float3 rightMicDir = forward * cosHalf + right * sinHalf;

            float cosL = math.dot(incoming, leftMicDir);
            float cosR = math.dot(incoming, rightMicDir);

            gainL = 0.5f * (1f + cosL);
            gainR = 0.5f * (1f + cosR);
        }

        static void ComputeABOffsets(float3 incoming, float3 right,
            float spacingMeters, float speedOfSound, int sampleRate,
            out int sampleOffsetL, out int sampleOffsetR)
        {
            float sinAzimuth = math.dot(incoming, right);
            float halfSpacing = spacingMeters * 0.5f;
            float ictd = halfSpacing * sinAzimuth / speedOfSound;
            int sampleShift = (int)math.round(ictd * sampleRate);
            sampleOffsetL = sampleShift;
            sampleOffsetR = -sampleShift;
        }

        static void ComputeMSGains(float3 incoming, float3 forward, float3 right,
            float width, out float gainL, out float gainR)
        {
            float cosFwd = math.dot(incoming, forward);
            float mid = 0.5f * (1f + cosFwd);
            float side = -math.dot(incoming, right);

            gainL = math.max(mid + width * side, 0f);
            gainR = math.max(mid - width * side, 0f);
        }

        /// <summary>Normalizes mono IR to target dB peak.</summary>
        static void NormalizePeak(float[] ir, float targetDb)
        {
            float peak = 0f;
            for (int i = 0; i < ir.Length; i++)
            {
                float abs = math.abs(ir[i]);
                if (abs > peak) peak = abs;
            }

            if (peak < 1e-10f) return;

            float gain = math.pow(10f, targetDb / 20f) / peak;
            for (int i = 0; i < ir.Length; i++)
                ir[i] *= gain;
        }

        /// <summary>Normalizes stereo IR to target dB peak (preserves L/R balance).</summary>
        static void NormalizePeakStereo(float[] irL, float[] irR, float targetDb)
        {
            float peak = 0f;
            for (int i = 0; i < irL.Length; i++)
            {
                float absL = math.abs(irL[i]);
                float absR = math.abs(irR[i]);
                if (absL > peak) peak = absL;
                if (absR > peak) peak = absR;
            }

            if (peak < 1e-10f) return;

            float gain = math.pow(10f, targetDb / 20f) / peak;
            for (int i = 0; i < irL.Length; i++)
            {
                irL[i] *= gain;
                irR[i] *= gain;
            }
        }
    }
}
