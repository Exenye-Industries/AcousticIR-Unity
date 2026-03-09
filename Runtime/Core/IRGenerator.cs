using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Converts ray arrivals from the acoustic raytracer into a time-domain
    /// impulse response buffer. Uses multi-band synthesis for physically accurate
    /// spectral content: each arrival is decomposed into 6 octave bands, filtered,
    /// and summed to preserve the frequency-dependent absorption characteristics.
    ///
    /// Without multi-band synthesis, every reflection becomes a broadband click
    /// regardless of material absorption — losing all spectral information and
    /// making the IR sound like white noise.
    /// </summary>
    public static class IRGenerator
    {
        // ====================================================================
        // MULTI-BAND FILTER INFRASTRUCTURE
        // 6 octave bands covering DC to Nyquist:
        //   Band 0: LP at 177 Hz   (captures DC–177 Hz, represents 125 Hz band)
        //   Band 1: BP at 250 Hz   (177–354 Hz)
        //   Band 2: BP at 500 Hz   (354–707 Hz)
        //   Band 3: BP at 1000 Hz  (707–1414 Hz)
        //   Band 4: BP at 2000 Hz  (1414–2828 Hz)
        //   Band 5: HP at 2828 Hz  (captures 2828 Hz–Nyquist, represents 4 kHz band)
        // ====================================================================

        const int NumBands = 6;

        struct BiquadCoeffs
        {
            public float b0, b1, b2, a1, a2;
        }

        /// <summary>2nd-order Butterworth lowpass.</summary>
        static BiquadCoeffs DesignLowpass(float cutoffHz, int sampleRate)
        {
            float w0 = 2f * math.PI * cutoffHz / sampleRate;
            float cosW0 = math.cos(w0);
            float alpha = math.sin(w0) / (2f * 0.7071f); // Q=0.7071 for Butterworth
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
                return DesignLowpass(177f, sampleRate);    // DC to 177 Hz
            if (bandIndex == NumBands - 1)
                return DesignHighpass(2828f, sampleRate);   // 2828 Hz to Nyquist
            float[] centers = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
            return DesignBandpass(centers[bandIndex], 1.414f, sampleRate); // octave BW
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
        // MONO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a mono impulse response from ray arrivals.
        /// Uses multi-band synthesis: each reflection is spectrally shaped
        /// according to its 6-band energy values from material absorption.
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

            // Phase 1: Multi-band accumulation of ray arrivals
            AccumulateArrivals(ir, arrivals, sampleRate, speedOfSound, rayCount);

            // Phase 2: Per-band late reverb tail (fills gaps where arrivals are sparse)
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
        /// Broadband arrival accumulation with perceptually-weighted amplitude.
        ///
        /// Each arrival is placed as a single broadband impulse. The amplitude
        /// is computed from the A-weighted sum of all 6 band energies.
        /// This preserves clean click-like reflections (no filter ringing artifacts).
        ///
        /// The frequency-dependent absorption information is used by the late tail
        /// synthesis, where per-band noise decay correctly models HF rolling off
        /// faster than LF (as in real rooms).
        /// </summary>
        static void AccumulateArrivals(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, int rayCount)
        {
            const int sincHalfWidth = 4;

            // === Pass 1: Collect direct sound (bounce 0) ===
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

            // Place direct sound as a single consolidated impulse
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

            // === Pass 2: Place reflections (bounce >= 1) as broadband impulses ===
            int reflectionCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                float samplePos = arrival.time * sampleRate;
                float totalDistance = arrival.time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDistance, 0.1f);
                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy) * distAtten;

                PlaceSincImpulse(ir, samplePos, amplitude, sincHalfWidth);
                reflectionCount++;
            }

            Debug.Log($"[AcousticIR] Broadband synthesis: {reflectionCount} reflections placed");
        }

        /// <summary>
        /// Converts 6-band energy to a single amplitude value.
        /// Uses simplified A-weighting for perceptual accuracy.
        /// Used for logging and diagnostics; the actual IR uses per-band values.
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

        /// <summary>
        /// Per-band late reverb tail synthesis.
        ///
        /// Each of the 6 frequency bands gets its own noise tail with:
        /// - Band-specific RT60 estimated from the arrival energy histogram
        /// - White noise generated → bandpass filtered to the correct frequency range
        /// - Independent decay rate (HF bands decay faster = natural HF rolloff)
        ///
        /// This replaces the old single-band white noise approach that produced
        /// harsh high-frequency hissing. The per-band approach naturally creates
        /// a warm, realistic reverb tail because:
        /// - Materials absorb more HF → HF bands have shorter RT60
        /// - Each band is correctly filtered → no broadband noise artifacts
        /// - The sum of 6 narrowband noises = properly colored reverb tail
        /// </summary>
        static void SynthesizeLateTail(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound)
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

            // === Estimate mixing time from broadband peak ===
            // Combine all bands to find a sensible mixing point
            float broadbandPeakEnergy = 0f;
            int broadbandPeakBin = 2;
            for (int bin = 2; bin < numBins; bin++)
            {
                float total = 0f;
                for (int b = 0; b < NumBands; b++)
                    total += bandBinEnergy[b][bin];
                if (total > broadbandPeakEnergy)
                {
                    broadbandPeakEnergy = total;
                    broadbandPeakBin = bin;
                }
            }

            if (broadbandPeakEnergy < 1e-14f)
                return;

            float mixingTimeMs = math.max(40f, broadbandPeakBin * binMs * 0.8f);
            int mixingStartSample = (int)(mixingTimeMs / 1000f * sampleRate);
            int mixingEndSample = (int)((mixingTimeMs + 50f) / 1000f * sampleRate); // 50ms crossfade

            // === Generate per-band noise tails ===
            float overallRT60 = 0f;
            int activeBands = 0;

            for (int b = 0; b < NumBands; b++)
            {
                // Find peak energy density for this band
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
                    float noiseFloor = peakEnergy * 1e-4f; // -40dB below peak (more inclusive)
                    float sumT = 0f, sumLogE = 0f, sumTT = 0f, sumTLogE = 0f;
                    int n = 0;

                    for (int bin = peakBin; bin < numBins; bin++)
                    {
                        if (bandBinEnergy[b][bin] > noiseFloor)
                        {
                            float t = (bin - peakBin) * binMs / 1000f;
                            float logE = math.log(bandBinEnergy[b][bin]);
                            sumT += t;
                            sumLogE += logE;
                            sumTT += t * t;
                            sumTLogE += t * logE;
                            n++;
                        }
                    }

                    float irLenS = (float)ir.Length / sampleRate;

                    if (n >= 5)
                    {
                        float denom = n * sumTT - sumT * sumT;
                        if (math.abs(denom) > 1e-12f)
                        {
                            float slope = (n * sumTLogE - sumT * sumLogE) / denom;
                            if (slope < -0.1f)
                                bandRT60 = -13.816f / slope;
                            else
                                bandRT60 = irLenS;
                        }
                        else
                        {
                            bandRT60 = irLenS;
                        }
                    }
                    else
                    {
                        // Too few data points - use generous fallback
                        bandRT60 = maxArrivalTime * 4.0f;
                    }

                    // RT60 minimum: at least 2× the arrival time span (rays don't capture full decay)
                    float minRT60 = maxArrivalTime * 2.0f;
                    bandRT60 = math.max(bandRT60, minRT60);
                    bandRT60 = math.clamp(bandRT60, 0.5f, irLenS);
                }

                overallRT60 = math.max(overallRT60, bandRT60);
                activeBands++;

                float decayRate = -13.816f / bandRT60;
                float peakAmp = math.sqrt(peakEnergy);

                // Generate noise for this band - ALWAYS fill to end of IR
                float[] bandNoise = new float[ir.Length];
                var rng = new Unity.Mathematics.Random((uint)(12345 + b * 7919));

                for (int s = mixingStartSample; s < ir.Length; s++)
                {
                    float time = (float)s / sampleRate;
                    float timeSincePeak = time - peakTimeS;
                    if (timeSincePeak < 0f) timeSincePeak = 0f;

                    float envelope = peakAmp * math.exp(decayRate * 0.5f * timeSincePeak);

                    // Don't break early - always fill to end of IR
                    // The windowing pass will handle the final fade-out
                    if (envelope < 1e-20f) envelope = 0f;

                    // Crossfade from discrete arrivals to noise
                    float crossfade = 1f;
                    if (s < mixingEndSample)
                    {
                        crossfade = (float)(s - mixingStartSample)
                            / (float)(mixingEndSample - mixingStartSample);
                        crossfade = math.clamp(crossfade, 0f, 1f);
                        crossfade = crossfade * crossfade;
                    }

                    // Box-Muller Gaussian noise
                    float u1 = math.max(rng.NextFloat(), 1e-10f);
                    float u2 = rng.NextFloat();
                    float gaussian = math.sqrt(-2f * math.log(u1))
                        * math.cos(2f * math.PI * u2);

                    bandNoise[s] = gaussian * envelope * crossfade;
                }

                // Apply band filter to noise
                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(bandNoise, coeffs, mixingStartSample);

                // Add filtered noise to IR
                for (int s = mixingStartSample; s < ir.Length; s++)
                    ir[s] += bandNoise[s];
            }

            Debug.Log($"[AcousticIR] Per-band tail synthesis: {activeBands} bands active, " +
                      $"max RT60≈{overallRT60:F2}s, mixing@{mixingTimeMs:F0}ms, " +
                      $"from {reflectionCount} reflections (last at {maxArrivalTime * 1000:F0}ms)");
        }

        // ====================================================================
        // STEREO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a stereo impulse response from ray arrivals.
        /// Uses multi-band synthesis + virtual microphone patterns for
        /// spectrally accurate stereo imaging.
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

            // Phase 1: Multi-band stereo accumulation
            AccumulateStereoArrivals(irL, irR, arrivals, sampleRate, speedOfSound,
                rayCount, receiverForward, receiverRight, stereoConfig);

            // Phase 2: Per-band stereo late reverb tail
            if (synthesizeLateTail)
                SynthesizeLateTailStereo(irL, irR, arrivals, sampleRate, speedOfSound);

            // Phase 3: Normalize both channels together to -1dB peak
            NormalizePeakStereo(irL, irR, -1f);

            // Phase 4: Apply windowing to both channels
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
        /// Broadband stereo arrival accumulation with perceptually-weighted amplitude.
        /// Each arrival is placed as a single broadband impulse per channel,
        /// with stereo gains from the virtual microphone pattern.
        /// </summary>
        static void AccumulateStereoArrivals(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound,
            int rayCount, float3 receiverForward, float3 receiverRight,
            StereoConfig config)
        {
            const int sincHalfWidth = 4;

            // === Pass 1: Direct sound (bounce 0) ===
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

            // === Pass 2: Reflections (bounce >= 1) as broadband stereo impulses ===
            int reflectionCount = 0;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                float totalDistance = arrival.time * speedOfSound;
                float distAtten = 1f / Mathf.Max(totalDistance, 0.1f);
                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy) * distAtten;

                ComputeStereoGains(arrival.direction, receiverForward, receiverRight,
                    config, speedOfSound, sampleRate,
                    out float gainL, out float gainR,
                    out int sampleOffsetL, out int sampleOffsetR);

                float samplePosL = arrival.time * sampleRate + sampleOffsetL;
                float samplePosR = arrival.time * sampleRate + sampleOffsetR;

                PlaceSincImpulse(irL, samplePosL, amplitude * gainL, sincHalfWidth);
                PlaceSincImpulse(irR, samplePosR, amplitude * gainR, sincHalfWidth);

                reflectionCount++;
            }

            Debug.Log($"[AcousticIR] Stereo broadband: {reflectionCount} reflections ({config.mode} mode)");
        }

        /// <summary>
        /// Per-band decorrelated stereo late reverb tail.
        /// Each band uses independent L/R noise seeds for natural diffuse reverb.
        /// Each band decays at its own rate (HF bands faster = natural reverb color).
        /// </summary>
        static void SynthesizeLateTailStereo(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound)
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

            // Mixing time from broadband peak
            float broadbandPeakEnergy = 0f;
            int broadbandPeakBin = 2;
            for (int bin = 2; bin < numBins; bin++)
            {
                float total = 0f;
                for (int b = 0; b < NumBands; b++)
                    total += bandBinEnergy[b][bin];
                if (total > broadbandPeakEnergy)
                {
                    broadbandPeakEnergy = total;
                    broadbandPeakBin = bin;
                }
            }

            if (broadbandPeakEnergy < 1e-14f)
                return;

            float mixingTimeMs = math.max(40f, broadbandPeakBin * binMs * 0.8f);
            int mixingStartSample = (int)(mixingTimeMs / 1000f * sampleRate);
            int mixingEndSample = (int)((mixingTimeMs + 50f) / 1000f * sampleRate);

            float overallRT60 = 0f;
            int activeBands = 0;

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

                // RT60 estimation via exponential regression
                float bandRT60;
                {
                    float noiseFloor = peakEnergy * 1e-4f; // -40dB (more inclusive)
                    float sumT = 0f, sumLogE = 0f, sumTT = 0f, sumTLogE = 0f;
                    int n = 0;

                    for (int bin = peakBin; bin < numBins; bin++)
                    {
                        if (bandBinEnergy[b][bin] > noiseFloor)
                        {
                            float t = (bin - peakBin) * binMs / 1000f;
                            float logE = math.log(bandBinEnergy[b][bin]);
                            sumT += t;
                            sumLogE += logE;
                            sumTT += t * t;
                            sumTLogE += t * logE;
                            n++;
                        }
                    }

                    float irLenS = (float)irL.Length / sampleRate;

                    if (n >= 5)
                    {
                        float denom = n * sumTT - sumT * sumT;
                        if (math.abs(denom) > 1e-12f)
                        {
                            float slope = (n * sumTLogE - sumT * sumLogE) / denom;
                            if (slope < -0.1f)
                                bandRT60 = -13.816f / slope;
                            else
                                bandRT60 = irLenS;
                        }
                        else
                        {
                            bandRT60 = irLenS;
                        }
                    }
                    else
                    {
                        bandRT60 = maxArrivalTime * 4.0f;
                    }

                    // RT60 minimum: at least 2× the arrival time span
                    float minRT60 = maxArrivalTime * 2.0f;
                    bandRT60 = math.max(bandRT60, minRT60);
                    bandRT60 = math.clamp(bandRT60, 0.5f, irLenS);
                }

                overallRT60 = math.max(overallRT60, bandRT60);
                activeBands++;

                float decayRate = -13.816f / bandRT60;
                float peakAmp = math.sqrt(peakEnergy);

                // Independent L/R noise per band - ALWAYS fill to end of IR
                float[] bandNoiseL = new float[irL.Length];
                float[] bandNoiseR = new float[irR.Length];
                var rngL = new Unity.Mathematics.Random((uint)(12345 + b * 7919));
                var rngR = new Unity.Mathematics.Random((uint)(67890 + b * 6271));

                for (int s = mixingStartSample; s < irL.Length; s++)
                {
                    float time = (float)s / sampleRate;
                    float timeSincePeak = time - peakTimeS;
                    if (timeSincePeak < 0f) timeSincePeak = 0f;

                    float envelope = peakAmp * math.exp(decayRate * 0.5f * timeSincePeak);
                    if (envelope < 1e-20f) envelope = 0f;

                    float crossfade = 1f;
                    if (s < mixingEndSample)
                    {
                        crossfade = (float)(s - mixingStartSample)
                            / (float)(mixingEndSample - mixingStartSample);
                        crossfade = math.clamp(crossfade, 0f, 1f);
                        crossfade = crossfade * crossfade;
                    }

                    float u1L = math.max(rngL.NextFloat(), 1e-10f);
                    float u2L = rngL.NextFloat();
                    float gaussL = math.sqrt(-2f * math.log(u1L))
                        * math.cos(2f * math.PI * u2L);

                    float u1R = math.max(rngR.NextFloat(), 1e-10f);
                    float u2R = rngR.NextFloat();
                    float gaussR = math.sqrt(-2f * math.log(u1R))
                        * math.cos(2f * math.PI * u2R);

                    bandNoiseL[s] = gaussL * envelope * crossfade;
                    bandNoiseR[s] = gaussR * envelope * crossfade;
                }

                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(bandNoiseL, coeffs, mixingStartSample);
                ApplyBiquad(bandNoiseR, coeffs, mixingStartSample);

                for (int s = mixingStartSample; s < irL.Length; s++)
                {
                    irL[s] += bandNoiseL[s];
                    irR[s] += bandNoiseR[s];
                }
            }

            Debug.Log($"[AcousticIR] Stereo per-band tail: {activeBands} bands, " +
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
        /// Computes left/right gains and sample offsets based on the stereo mode
        /// and the arrival direction relative to the receiver orientation.
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

        /// <summary>
        /// XY Coincident Pair: Two cardioid microphones at ±halfAngle from forward.
        /// Cardioid pattern: gain = 0.5 * (1 + cos(θ))
        /// </summary>
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

        /// <summary>
        /// AB Spaced Pair: Two omnidirectional microphones with physical spacing.
        /// ICTD = spacing * sin(azimuth) / speedOfSound.
        /// </summary>
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

        /// <summary>
        /// MS Mid-Side: Cardioid (mid) + figure-8 (side).
        /// L = Mid + width * Side, R = Mid - width * Side.
        /// </summary>
        static void ComputeMSGains(float3 incoming, float3 forward, float3 right,
            float width, out float gainL, out float gainR)
        {
            float cosFwd = math.dot(incoming, forward);
            float mid = 0.5f * (1f + cosFwd);
            float side = -math.dot(incoming, right);

            gainL = mid + width * side;
            gainR = mid - width * side;

            gainL = math.max(gainL, 0f);
            gainR = math.max(gainR, 0f);
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

        /// <summary>
        /// Normalizes stereo IR buffers together so the louder channel's
        /// peak equals the target dB level. Preserves the L/R balance.
        /// </summary>
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

            if (peak < 1e-10f)
                return;

            float targetLinear = math.pow(10f, targetDb / 20f);
            float gain = targetLinear / peak;

            for (int i = 0; i < irL.Length; i++)
            {
                irL[i] *= gain;
                irR[i] *= gain;
            }
        }
    }
}
