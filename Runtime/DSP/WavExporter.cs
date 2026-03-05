using System;
using System.IO;
using UnityEngine;

namespace AcousticIR.DSP
{
    /// <summary>
    /// Exports impulse response data as WAV files.
    /// Supports 32-bit float and 16-bit PCM formats.
    /// Handles file locking gracefully with automatic retry.
    /// </summary>
    public static class WavExporter
    {
        /// <summary>
        /// Exports IR samples as a 32-bit float WAV file.
        /// If the file is locked (e.g. by REAPER), automatically tries
        /// a timestamped alternative filename.
        /// </summary>
        /// <param name="samples">Mono IR samples (-1 to 1).</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="filePath">Full output file path.</param>
        public static void ExportFloat32(float[] samples, int sampleRate, string filePath)
        {
            string actualPath = ResolveWritablePath(filePath);

            using var stream = new FileStream(actualPath, FileMode.Create, FileAccess.Write, FileShare.Read);
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

            Debug.Log($"[AcousticIR] Exported WAV: {actualPath} ({samples.Length} samples, {sampleRate}Hz, 32-bit float)");
        }

        /// <summary>
        /// Exports stereo IR samples as a 32-bit float WAV file.
        /// Interleaves L/R samples for standard stereo WAV format.
        /// </summary>
        /// <param name="left">Left channel IR samples (-1 to 1).</param>
        /// <param name="right">Right channel IR samples (-1 to 1).</param>
        /// <param name="sampleRate">Sample rate in Hz.</param>
        /// <param name="filePath">Full output file path.</param>
        public static void ExportStereoFloat32(float[] left, float[] right, int sampleRate, string filePath)
        {
            if (left.Length != right.Length)
            {
                Debug.LogError("[AcousticIR] Stereo export: L/R channels must have equal length!");
                return;
            }

            string actualPath = ResolveWritablePath(filePath);

            using var stream = new FileStream(actualPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);

            int channels = 2;
            int bitsPerSample = 32;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int dataSize = left.Length * channels * (bitsPerSample / 8);

            // RIFF header
            writer.Write(new char[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + dataSize);
            writer.Write(new char[] { 'W', 'A', 'V', 'E' });

            // fmt chunk
            writer.Write(new char[] { 'f', 'm', 't', ' ' });
            writer.Write(16);
            writer.Write((short)3);         // Format: IEEE float
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk (interleaved: L0, R0, L1, R1, ...)
            writer.Write(new char[] { 'd', 'a', 't', 'a' });
            writer.Write(dataSize);

            for (int i = 0; i < left.Length; i++)
            {
                writer.Write(left[i]);
                writer.Write(right[i]);
            }

            Debug.Log($"[AcousticIR] Exported stereo WAV: {actualPath} " +
                      $"({left.Length} frames, {sampleRate}Hz, 32-bit float, stereo)");
        }

        /// <summary>
        /// Exports IR samples as a 16-bit PCM WAV file.
        /// If the file is locked, automatically tries a timestamped alternative.
        /// </summary>
        public static void ExportPCM16(float[] samples, int sampleRate, string filePath)
        {
            string actualPath = ResolveWritablePath(filePath);

            using var stream = new FileStream(actualPath, FileMode.Create, FileAccess.Write, FileShare.Read);
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

            Debug.Log($"[AcousticIR] Exported WAV: {actualPath} ({samples.Length} samples, {sampleRate}Hz, 16-bit PCM)");
        }

        /// <summary>
        /// Checks if the file can be written to. If the file is locked by another process
        /// (e.g. REAPER, a media player, etc.), generates an alternative filename with a timestamp.
        /// </summary>
        static string ResolveWritablePath(string originalPath)
        {
            // If file doesn't exist yet, we're good
            if (!File.Exists(originalPath))
                return originalPath;

            // Try to open the existing file to check if it's locked
            try
            {
                using var test = new FileStream(originalPath, FileMode.Open, FileAccess.Write, FileShare.None);
                // File is not locked, we can overwrite it
                return originalPath;
            }
            catch (IOException)
            {
                // File is locked - generate alternative path with timestamp
                string dir = Path.GetDirectoryName(originalPath);
                string name = Path.GetFileNameWithoutExtension(originalPath);
                string ext = Path.GetExtension(originalPath);
                string timestamp = DateTime.Now.ToString("HHmmss");
                string altPath = Path.Combine(dir ?? "", $"{name}_{timestamp}{ext}");

                Debug.LogWarning($"[AcousticIR] File locked: {originalPath}\n" +
                                 $"  Saving to: {altPath}\n" +
                                 $"  (Close the program using the file to overwrite the original)");
                return altPath;
            }
        }
    }
}
