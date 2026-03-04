using System.Runtime.InteropServices;
using UnityEngine;

namespace AcousticIR.DSP
{
    /// <summary>
    /// P/Invoke bridge to the native AudioPluginAcousticIR convolution engine.
    /// Provides methods to load impulse responses into the native plugin instances.
    /// </summary>
    public static class ConvolutionBridge
    {
        const string DllName = "AudioPluginAcousticIR";

        [DllImport(DllName)]
        static extern void AcousticIR_LoadIR(
            int instanceId, float[] irData, int irLength, int sampleRate);

        [DllImport(DllName)]
        static extern void AcousticIR_Reset(int instanceId);

        [DllImport(DllName)]
        static extern int AcousticIR_GetLatestInstanceId();

        /// <summary>
        /// Loads an impulse response into the native convolution plugin instance.
        /// </summary>
        /// <param name="instanceId">Plugin instance ID (read from InstanceID parameter).</param>
        /// <param name="irSamples">Mono IR samples.</param>
        /// <param name="sampleRate">Sample rate of the IR.</param>
        public static void LoadIR(int instanceId, float[] irSamples, int sampleRate)
        {
            if (irSamples == null || irSamples.Length == 0)
            {
                Debug.LogWarning("[AcousticIR] Cannot load empty IR.");
                return;
            }

            AcousticIR_LoadIR(instanceId, irSamples, irSamples.Length, sampleRate);
            Debug.Log($"[AcousticIR] Loaded IR into instance {instanceId}: " +
                      $"{irSamples.Length} samples @ {sampleRate}Hz");
        }

        /// <summary>
        /// Loads an IRData asset into the native convolution plugin.
        /// </summary>
        public static void LoadIR(int instanceId, Core.IRData irData)
        {
            if (irData == null || irData.Samples == null)
            {
                Debug.LogWarning("[AcousticIR] IRData is null or empty.");
                return;
            }

            LoadIR(instanceId, irData.Samples, irData.SampleRate);
        }

        /// <summary>
        /// Resets the convolution engine state (clears buffers).
        /// </summary>
        public static void Reset(int instanceId)
        {
            AcousticIR_Reset(instanceId);
        }

        /// <summary>
        /// Gets the instance ID of the most recently created plugin instance.
        /// </summary>
        public static int GetLatestInstanceId()
        {
            return AcousticIR_GetLatestInstanceId();
        }
    }
}
