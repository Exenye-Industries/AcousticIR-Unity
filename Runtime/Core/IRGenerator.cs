using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Converts ray arrivals into time-domain impulse responses.
    ///
    /// Purely data-driven: every arrival from the raytracer is placed as a
    /// spectrally-shaped impulse using pre-computed 6-band filter kernels.
    /// No synthetic noise — the reverb tail emerges naturally from the
    /// density and energy distribution of late arrivals.
    /// </summary>
    public static class IRGenerator
    {
        const int NumBands = 6;
        const int BandKernelLength = 256; // ~5.3ms at 48kHz

        // ====================================================================
        // MULTI-BAND FILTER INFRASTRUCTURE
        // ====================================================================

        struct BiquadCoeffs
        {
            public float b0, b1, b2, a1, a2;
        }

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

        static BiquadCoeffs GetBandFilter(int bandIndex, int sampleRate)
        {
            if (bandIndex == 0)
                return DesignLowpass(177f, sampleRate);
            if (bandIndex == NumBands - 1)
                return DesignHighpass(2828f, sampleRate);
            float[] centers = { 125f, 250f, 500f, 1000f, 2000f, 4000f };
            return DesignBandpass(centers[bandIndex], 1.414f, sampleRate);
        }

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
        /// </summary>
        static float[][] PrecomputeBandKernels(int sampleRate)
        {
            float[][] kernels = new float[NumBands][];

            for (int b = 0; b < NumBands; b++)
            {
                kernels[b] = new float[BandKernelLength];
                kernels[b][0] = 1f; // Unit impulse

                // Cascaded biquad (4th order = 24 dB/octave rolloff)
                BiquadCoeffs coeffs = GetBandFilter(b, sampleRate);
                ApplyBiquad(kernels[b], coeffs);
                ApplyBiquad(kernels[b], coeffs);

                // Normalize peak to 1.0
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
        // AMPLITUDE CONVERSION
        // ====================================================================

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
        /// Every arrival is placed as a spectrally-shaped multi-band impulse.
        /// No synthetic noise — the reverb tail comes purely from raytracing data.
        /// </summary>
        public static float[] Generate(
            NativeList<RayArrival> arrivals,
            int sampleRate = 48000,
            float irLengthSeconds = 2f,
            bool applyWindowing = true,
            float windowTailPortion = 0.1f,
            bool synthesizeLateTail = true, // kept for API compat, ignored
            float speedOfSound = 343f,
            int rayCount = 4096)
        {
            int sampleCount = (int)(irLengthSeconds * sampleRate);
            float[] ir = new float[sampleCount];

            if (arrivals.Length == 0)
                return ir;

            float[][] bandKernels = PrecomputeBandKernels(sampleRate);

            // === DIAGNOSTICS: Dump arrival data + kernel info to file ===
            DumpDiagnostics(arrivals, sampleRate, speedOfSound, bandKernels);

            // Place ALL arrivals as spectrally-shaped impulses
            AccumulateAllArrivals(ir, arrivals, sampleRate, speedOfSound, rayCount, bandKernels);

            // Normalize to -1dB peak
            NormalizePeak(ir, -1f);

            // Window the tail
            if (applyWindowing)
                AcousticMath.ApplyHannWindow(ir, windowTailPortion);

            return ir;
        }

        /// <summary>
        /// Places every arrival into the IR buffer.
        /// Direct sound (B0): consolidated broadband sinc impulse.
        /// All reflections (B1+): spectrally-shaped multi-band impulses.
        /// </summary>
        static void AccumulateAllArrivals(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, int rayCount, float[][] bandKernels)
        {
            const int sincHalfWidth = 4;

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
                float samplePos = directTime * sampleRate;
                float amplitude = directEnergySum / directCount;

                PlaceSincImpulse(ir, samplePos, amplitude, sincHalfWidth);

                Debug.Log($"[AcousticIR] Direct sound: {directCount} B0 arrivals → 1 impulse at " +
                          $"{directTime * 1000:F1}ms, amp={amplitude:F4}");
            }

            // === Pass 2: ALL reflections — spectrally shaped ===
            // NO distance attenuation here! The raytracer already handles 1/r²:
            // - Stochastic arrivals: implicit via receiver sphere hit probability
            // - NEE arrivals: explicit via solid angle weight (r²/2d²)
            int reflectionCount = 0;
            float maxTime = 0f;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                float samplePos = arrival.time * sampleRate;

                if (samplePos >= 0 && samplePos < ir.Length - BandKernelLength)
                {
                    PlaceMultiBandImpulse(ir, samplePos, arrival.bandEnergy, bandKernels);
                    reflectionCount++;
                }

                if (arrival.time > maxTime) maxTime = arrival.time;
            }

            Debug.Log($"[AcousticIR] Placed {reflectionCount} reflections as multi-band impulses " +
                      $"(last arrival at {maxTime * 1000:F0}ms)");
        }

        /// <summary>
        /// Places a spectrally-shaped impulse using pre-computed band kernels.
        /// Band energies are used directly — no additional distance attenuation.
        /// </summary>
        static void PlaceMultiBandImpulse(float[] buffer, float samplePos,
            AbsorptionCoefficients bandEnergy, float[][] bandKernels)
        {
            int center = (int)math.round(samplePos);

            for (int b = 0; b < NumBands; b++)
            {
                float bandAmp = GetBandValue(bandEnergy, b);
                if (bandAmp < 1e-8f) continue;

                for (int i = 0; i < BandKernelLength; i++)
                {
                    int bufIdx = center + i;
                    if (bufIdx >= 0 && bufIdx < buffer.Length)
                        buffer[bufIdx] += bandKernels[b][i] * bandAmp;
                }
            }
        }

        // ====================================================================
        // STEREO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a stereo impulse response from ray arrivals.
        /// Every arrival is placed with stereo imaging — no synthetic noise.
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
            bool synthesizeLateTail = true, // kept for API compat, ignored
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

            float[][] bandKernels = PrecomputeBandKernels(sampleRate);

            // === DIAGNOSTICS: Dump arrival data + kernel info to file ===
            DumpDiagnostics(arrivals, sampleRate, speedOfSound, bandKernels);

            // Place ALL arrivals with stereo imaging
            AccumulateAllStereoArrivals(irL, irR, arrivals, sampleRate, speedOfSound,
                rayCount, receiverForward, receiverRight, stereoConfig, bandKernels);

            // Normalize both channels together
            NormalizePeakStereo(irL, irR, -1f);

            // Window both channels
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
        /// Places all arrivals into stereo buffers with spectral shaping and stereo imaging.
        /// </summary>
        static void AccumulateAllStereoArrivals(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound,
            int rayCount, float3 receiverForward, float3 receiverRight,
            StereoConfig config, float[][] bandKernels)
        {
            const int sincHalfWidth = 4;

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
                float3 directDir = math.normalizesafe(directDirSum / directCount);
                float amplitude = directEnergySum / directCount;

                ComputeStereoGains(directDir, receiverForward, receiverRight,
                    config, speedOfSound, sampleRate,
                    out float gainL, out float gainR,
                    out int sampleOffsetL, out int sampleOffsetR);

                float samplePosL = directTime * sampleRate + sampleOffsetL;
                float samplePosR = directTime * sampleRate + sampleOffsetR;

                PlaceSincImpulse(irL, samplePosL, amplitude * gainL, sincHalfWidth);
                PlaceSincImpulse(irR, samplePosR, amplitude * gainR, sincHalfWidth);
            }

            // === Pass 2: ALL reflections — spectrally shaped stereo ===
            // NO distance attenuation — already handled by raytracer
            int reflectionCount = 0;
            float maxTime = 0f;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                ComputeStereoGains(arrival.direction, receiverForward, receiverRight,
                    config, speedOfSound, sampleRate,
                    out float gainL, out float gainR,
                    out int sampleOffsetL, out int sampleOffsetR);

                float samplePosL = arrival.time * sampleRate + sampleOffsetL;
                float samplePosR = arrival.time * sampleRate + sampleOffsetR;

                if (samplePosL >= 0 && samplePosL < irL.Length - BandKernelLength &&
                    samplePosR >= 0 && samplePosR < irR.Length - BandKernelLength)
                {
                    PlaceMultiBandImpulseStereo(irL, irR, samplePosL, samplePosR,
                        arrival.bandEnergy, gainL, gainR, bandKernels);
                    reflectionCount++;
                }

                if (arrival.time > maxTime) maxTime = arrival.time;
            }

            Debug.Log($"[AcousticIR] Stereo: {reflectionCount} reflections placed " +
                      $"({config.mode} mode, last at {maxTime * 1000:F0}ms)");
        }

        /// <summary>
        /// Places a spectrally-shaped stereo impulse using pre-computed band kernels.
        /// Band energies are used directly — no additional distance attenuation.
        /// </summary>
        static void PlaceMultiBandImpulseStereo(float[] bufferL, float[] bufferR,
            float samplePosL, float samplePosR,
            AbsorptionCoefficients bandEnergy,
            float gainL, float gainR, float[][] bandKernels)
        {
            int centerL = (int)math.round(samplePosL);
            int centerR = (int)math.round(samplePosR);

            for (int b = 0; b < NumBands; b++)
            {
                float bandAmp = GetBandValue(bandEnergy, b);
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

        // ====================================================================
        // DIAGNOSTICS
        // ====================================================================

        /// <summary>
        /// Dumps comprehensive arrival statistics and kernel analysis to a text file.
        /// This helps diagnose why the IR sounds wrong.
        /// </summary>
        static void DumpDiagnostics(NativeList<RayArrival> arrivals, int sampleRate,
            float speedOfSound, float[][] bandKernels)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== AcousticIR DIAGNOSTICS ==========");
            sb.AppendLine($"Total Arrivals: {arrivals.Length}");
            sb.AppendLine($"Sample Rate: {sampleRate}");
            sb.AppendLine();

            // --- 1. Band Kernel Energy Analysis ---
            sb.AppendLine("=== BAND KERNEL ANALYSIS (256 samples each) ===");
            string[] bandNames = { "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz" };
            for (int b = 0; b < NumBands; b++)
            {
                float peak = 0f, rmsSum = 0f;
                for (int i = 0; i < BandKernelLength; i++)
                {
                    float v = bandKernels[b][i];
                    if (math.abs(v) > peak) peak = math.abs(v);
                    rmsSum += v * v;
                }
                float rms = math.sqrt(rmsSum / BandKernelLength);
                float energy = rmsSum; // total energy = sum of squares
                sb.AppendLine($"  Band {bandNames[b]}: peak={peak:F4}, rms={rms:F6}, energy={energy:F6}");
            }
            sb.AppendLine();

            // --- 2. Arrivals per bounce count ---
            sb.AppendLine("=== ARRIVALS PER BOUNCE COUNT ===");
            int maxBounce = 0;
            for (int i = 0; i < arrivals.Length; i++)
                if (arrivals[i].bounceCount > maxBounce) maxBounce = arrivals[i].bounceCount;

            int[] bounceCounts = new int[maxBounce + 1];
            for (int i = 0; i < arrivals.Length; i++)
                bounceCounts[arrivals[i].bounceCount]++;

            for (int b = 0; b <= math.min(maxBounce, 20); b++)
                sb.AppendLine($"  Bounce {b}: {bounceCounts[b]} arrivals");
            if (maxBounce > 20)
                sb.AppendLine($"  ... (max bounce = {maxBounce})");
            sb.AppendLine();

            // --- 3. Time windows: arrival count + per-band energy stats ---
            sb.AppendLine("=== ENERGY PER TIME WINDOW ===");
            float[] windowEdges = { 0f, 0.01f, 0.05f, 0.1f, 0.5f, 1f, 2f, 5f, 10f, 20f };

            for (int w = 0; w < windowEdges.Length - 1; w++)
            {
                float tMin = windowEdges[w];
                float tMax = windowEdges[w + 1];
                int count = 0;
                float[] bandSum = new float[NumBands];
                float[] bandMin = new float[NumBands];
                float[] bandMax = new float[NumBands];
                for (int b = 0; b < NumBands; b++) { bandMin[b] = float.MaxValue; bandMax[b] = 0f; }

                for (int i = 0; i < arrivals.Length; i++)
                {
                    float t = arrivals[i].time;
                    if (t < tMin || t >= tMax) continue;
                    count++;

                    for (int b = 0; b < NumBands; b++)
                    {
                        float e = GetBandValue(arrivals[i].bandEnergy, b);
                        bandSum[b] += e;
                        if (e < bandMin[b]) bandMin[b] = e;
                        if (e > bandMax[b]) bandMax[b] = e;
                    }
                }

                sb.AppendLine($"  [{tMin:F2}s - {tMax:F2}s]: {count} arrivals");
                if (count > 0)
                {
                    sb.Append("    AVG energy: ");
                    for (int b = 0; b < NumBands; b++)
                        sb.Append($"{bandNames[b]}={bandSum[b] / count:F6}  ");
                    sb.AppendLine();

                    sb.Append("    MIN energy: ");
                    for (int b = 0; b < NumBands; b++)
                        sb.Append($"{bandNames[b]}={bandMin[b]:F6}  ");
                    sb.AppendLine();

                    sb.Append("    MAX energy: ");
                    for (int b = 0; b < NumBands; b++)
                        sb.Append($"{bandNames[b]}={bandMax[b]:F6}  ");
                    sb.AppendLine();

                    // HF/LF ratio
                    float avgLF = bandSum[0] / count; // 125Hz
                    float avgHF = bandSum[5] / count; // 4kHz
                    float ratio = avgLF > 1e-10f ? avgHF / avgLF : 0f;
                    sb.AppendLine($"    4kHz/125Hz ratio: {ratio:F4}");
                }
                sb.AppendLine();
            }

            // --- 4. First 20 arrivals (detailed) ---
            sb.AppendLine("=== FIRST 20 ARRIVALS (DETAILED) ===");
            int detailCount = math.min(arrivals.Length, 20);
            for (int i = 0; i < detailCount; i++)
            {
                var a = arrivals[i];
                sb.AppendLine($"  [{i}] t={a.time * 1000:F2}ms bounce={a.bounceCount} " +
                    $"125={a.bandEnergy.band125Hz:F6} 250={a.bandEnergy.band250Hz:F6} " +
                    $"500={a.bandEnergy.band500Hz:F6} 1k={a.bandEnergy.band1kHz:F6} " +
                    $"2k={a.bandEnergy.band2kHz:F6} 4k={a.bandEnergy.band4kHz:F6} " +
                    $"total={a.bandEnergy.TotalEnergy:F6}");
            }
            sb.AppendLine();

            // --- 5. Last 20 arrivals (detailed) ---
            sb.AppendLine("=== LAST 20 ARRIVALS (DETAILED) ===");
            int startIdx = math.max(0, arrivals.Length - 20);
            for (int i = startIdx; i < arrivals.Length; i++)
            {
                var a = arrivals[i];
                sb.AppendLine($"  [{i}] t={a.time * 1000:F2}ms bounce={a.bounceCount} " +
                    $"125={a.bandEnergy.band125Hz:F6} 250={a.bandEnergy.band250Hz:F6} " +
                    $"500={a.bandEnergy.band500Hz:F6} 1k={a.bandEnergy.band1kHz:F6} " +
                    $"2k={a.bandEnergy.band2kHz:F6} 4k={a.bandEnergy.band4kHz:F6} " +
                    $"total={a.bandEnergy.TotalEnergy:F6}");
            }

            // --- Write to file ---
            string path = System.IO.Path.Combine(Application.dataPath, "AcousticIR_Diagnostics.txt");
            System.IO.File.WriteAllText(path, sb.ToString());
            Debug.Log($"[AcousticIR] Diagnostics written to: {path}");
        }

        // ====================================================================
        // SHARED UTILITIES
        // ====================================================================

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
