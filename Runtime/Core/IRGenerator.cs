using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Converts ray arrivals into time-domain impulse responses.
    ///
    /// Each arrival is placed as a single sinc-interpolated impulse.
    /// The amplitude is derived from the 6-band energy using A-weighting.
    /// The reverb character emerges naturally from the time/energy distribution
    /// of arrivals — no synthetic noise or per-arrival frequency filtering.
    /// </summary>
    public static class IRGenerator
    {
        const int NumBands = 6;

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
        /// Each arrival is placed as a single sinc-interpolated impulse.
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

            DumpDiagnostics(arrivals, sampleRate, speedOfSound);

            // Place all arrivals as sinc impulses
            AccumulateAllArrivals(ir, arrivals, sampleRate, speedOfSound, rayCount);

            // Normalize to -1dB peak
            NormalizePeak(ir, -1f);

            // Window the tail
            if (applyWindowing)
                AcousticMath.ApplyHannWindow(ir, windowTailPortion);

            return ir;
        }

        /// <summary>
        /// Places every arrival as a single sinc-interpolated impulse.
        /// Direct sound (B0): consolidated into one impulse.
        /// Reflections (B1+): individual impulses.
        /// </summary>
        static void AccumulateAllArrivals(float[] ir, NativeList<RayArrival> arrivals,
            int sampleRate, float speedOfSound, int rayCount)
        {
            const int sincHalfWidth = 4;

            // === Pass 1: Direct sound (bounce 0) — consolidate into one impulse ===
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

            // === Pass 2: All reflections — individual sinc impulses ===
            int reflectionCount = 0;
            float maxTime = 0f;
            float minAmp = float.MaxValue, maxAmp = 0f;

            for (int i = 0; i < arrivals.Length; i++)
            {
                RayArrival arrival = arrivals[i];
                if (arrival.bounceCount == 0) continue;

                float samplePos = arrival.time * sampleRate;
                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy);

                if (samplePos >= 0 && samplePos < ir.Length - sincHalfWidth)
                {
                    PlaceSincImpulse(ir, samplePos, amplitude, sincHalfWidth);
                    reflectionCount++;

                    if (amplitude < minAmp) minAmp = amplitude;
                    if (amplitude > maxAmp) maxAmp = amplitude;
                }

                if (arrival.time > maxTime) maxTime = arrival.time;
            }

            Debug.Log($"[AcousticIR] Placed {reflectionCount} reflections as sinc impulses " +
                      $"(amp range: {minAmp:F6} - {maxAmp:F4}, last at {maxTime * 1000:F0}ms)");
        }

        // ====================================================================
        // STEREO IR GENERATION
        // ====================================================================

        /// <summary>
        /// Generates a stereo impulse response from ray arrivals.
        /// Each arrival is placed with stereo imaging as a single sinc impulse.
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

            DumpDiagnostics(arrivals, sampleRate, speedOfSound);

            // Place all arrivals with stereo imaging
            AccumulateAllStereoArrivals(irL, irR, arrivals, sampleRate, speedOfSound,
                rayCount, receiverForward, receiverRight, stereoConfig);

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
        /// Places all arrivals into stereo buffers with individual sinc impulses.
        /// </summary>
        static void AccumulateAllStereoArrivals(float[] irL, float[] irR,
            NativeList<RayArrival> arrivals, int sampleRate, float speedOfSound,
            int rayCount, float3 receiverForward, float3 receiverRight,
            StereoConfig config)
        {
            const int sincHalfWidth = 4;

            // === Pass 1: Direct sound (consolidate stereo) ===
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

            // === Pass 2: All reflections — individual sinc stereo impulses ===
            int reflectionCount = 0;

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

                float amplitude = BandEnergyToAmplitude(arrival.bandEnergy);

                if (samplePosL >= 0 && samplePosL < irL.Length - sincHalfWidth &&
                    samplePosR >= 0 && samplePosR < irR.Length - sincHalfWidth)
                {
                    PlaceSincImpulse(irL, samplePosL, amplitude * gainL, sincHalfWidth);
                    PlaceSincImpulse(irR, samplePosR, amplitude * gainR, sincHalfWidth);
                    reflectionCount++;
                }
            }

            Debug.Log($"[AcousticIR] Stereo: {reflectionCount} reflections placed " +
                      $"({config.mode} mode)");
        }

        // ====================================================================
        // DIAGNOSTICS
        // ====================================================================

        static void DumpDiagnostics(NativeList<RayArrival> arrivals, int sampleRate,
            float speedOfSound)
        {
            var sb = new StringBuilder();
            sb.AppendLine("========== AcousticIR DIAGNOSTICS ==========");
            sb.AppendLine($"Total Arrivals: {arrivals.Length}");
            sb.AppendLine($"Sample Rate: {sampleRate}");
            sb.AppendLine();

            // --- Arrivals per bounce count ---
            sb.AppendLine("=== ARRIVALS PER BOUNCE COUNT ===");
            int maxBounce = 0;
            for (int i = 0; i < arrivals.Length; i++)
                if (arrivals[i].bounceCount > maxBounce) maxBounce = arrivals[i].bounceCount;

            int[] bounceCounts = new int[maxBounce + 1];
            for (int i = 0; i < arrivals.Length; i++)
                bounceCounts[arrivals[i].bounceCount]++;

            for (int b = 0; b <= math.min(maxBounce, 30); b++)
                if (bounceCounts[b] > 0)
                    sb.AppendLine($"  Bounce {b}: {bounceCounts[b]} arrivals");
            sb.AppendLine();

            // --- Energy per time window ---
            sb.AppendLine("=== ENERGY PER TIME WINDOW ===");
            string[] bandNames = { "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz" };
            float[] windowEdges = { 0f, 0.01f, 0.05f, 0.1f, 0.5f, 1f, 2f, 5f, 10f, 20f };

            for (int w = 0; w < windowEdges.Length - 1; w++)
            {
                float tMin = windowEdges[w];
                float tMax = windowEdges[w + 1];
                int count = 0;
                float[] bandSum = new float[NumBands];

                for (int i = 0; i < arrivals.Length; i++)
                {
                    float t = arrivals[i].time;
                    if (t < tMin || t >= tMax) continue;
                    count++;

                    for (int b = 0; b < NumBands; b++)
                        bandSum[b] += GetBandValue(arrivals[i].bandEnergy, b);
                }

                if (count == 0) continue;

                sb.AppendLine($"  [{tMin:F2}s - {tMax:F2}s]: {count} arrivals");
                sb.Append("    AVG: ");
                for (int b = 0; b < NumBands; b++)
                    sb.Append($"{bandNames[b]}={bandSum[b] / count:F6}  ");
                sb.AppendLine();

                float avgLF = bandSum[0] / count;
                float avgHF = bandSum[5] / count;
                float ratio = avgLF > 1e-10f ? avgHF / avgLF : 0f;
                sb.AppendLine($"    4kHz/125Hz ratio: {ratio:F4}");
                sb.AppendLine();
            }

            // --- First 20 + Last 20 arrivals ---
            sb.AppendLine("=== FIRST 20 ARRIVALS ===");
            int detailCount = math.min(arrivals.Length, 20);
            for (int i = 0; i < detailCount; i++)
            {
                var a = arrivals[i];
                sb.AppendLine($"  [{i}] t={a.time * 1000:F2}ms B{a.bounceCount} " +
                    $"125={a.bandEnergy.band125Hz:F6} 4k={a.bandEnergy.band4kHz:F6} " +
                    $"amp={BandEnergyToAmplitude(a.bandEnergy):F6}");
            }
            sb.AppendLine();

            sb.AppendLine("=== LAST 20 ARRIVALS ===");
            int startIdx = math.max(0, arrivals.Length - 20);
            for (int i = startIdx; i < arrivals.Length; i++)
            {
                var a = arrivals[i];
                sb.AppendLine($"  [{i}] t={a.time * 1000:F2}ms B{a.bounceCount} " +
                    $"125={a.bandEnergy.band125Hz:F6} 4k={a.bandEnergy.band4kHz:F6} " +
                    $"amp={BandEnergyToAmplitude(a.bandEnergy):F6}");
            }

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
