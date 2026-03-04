using System;
using System.IO;
using UnityEngine;

namespace AcousticIR.DSP
{
    /// <summary>
    /// Exports impulse response data as WAV files.
    /// Supports 32-bit float and 16-bit PCM formats.
    /// </summary>
    public static class WavExporter
    {
        /// <summary>
        /// Exports IR samples as a 32-bit float WAV file.
        /// </summary>
        /// <param name="samples">Mono IR samples (-1 to 1).</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="filePath">Full output file path.</param>
        public static void ExportFloat32(float[] samples, int sampleRate, string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            int channels = 1;
            int bitsPerSample = 32;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = samples.Length * blockAlign;

            // RIFF header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize); // File size - 8
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);              // Chunk size
            writer.Write((short)3);        // Format: IEEE float
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            for (int i = 0; i < samples.Length; i++)
                writer.Write(samples[i]);

            Debug.Log($"[AcousticIR] Exported WAV: {filePath} ({samples.Length} samples, {sampleRate}Hz, 32-bit float)");
        }

        /// <summary>
        /// Exports IR samples as a 16-bit PCM WAV file.
        /// </summary>
        public static void ExportPCM16(float[] samples, int sampleRate, string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Create);
            using var writer = new BinaryWriter(stream);

            int channels = 1;
            int bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = samples.Length * blockAlign;

            // RIFF header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)1);        // Format: PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            for (int i = 0; i < samples.Length; i++)
            {
                float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                short pcm = (short)(clamped * short.MaxValue);
                writer.Write(pcm);
            }

            Debug.Log($"[AcousticIR] Exported WAV: {filePath} ({samples.Length} samples, {sampleRate}Hz, 16-bit PCM)");
        }
    }
}
