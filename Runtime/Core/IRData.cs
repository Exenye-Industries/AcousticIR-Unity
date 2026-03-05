using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// ScriptableObject that stores a baked impulse response.
    /// Supports both mono and stereo IRs.
    /// Contains the raw IR samples plus metadata about how it was generated.
    /// </summary>
    [CreateAssetMenu(fileName = "IRData", menuName = "AcousticIR/IR Data")]
    public class IRData : ScriptableObject
    {
        [Header("IR Samples")]
        [HideInInspector]
        [SerializeField] float[] samples;

        [Header("Metadata")]
        [SerializeField] int sampleRate = 48000;
        [SerializeField] int channels = 1;
        [SerializeField] float lengthSeconds;
        [SerializeField] int rayCount;
        [SerializeField] int maxBounces;
        [SerializeField] Vector3 sourcePosition;
        [SerializeField] Vector3 receiverPosition;
        [SerializeField] StereoMode stereoMode = StereoMode.Mono;

        /// <summary>
        /// Raw IR samples. For mono: sequential samples. For stereo: interleaved L/R.
        /// </summary>
        public float[] Samples => samples;

        /// <summary>Sample rate of the IR.</summary>
        public int SampleRate => sampleRate;

        /// <summary>Number of channels (1 = mono, 2 = stereo).</summary>
        public int Channels => channels;

        /// <summary>IR length in seconds.</summary>
        public float LengthSeconds => lengthSeconds;

        /// <summary>Whether this is a stereo IR.</summary>
        public bool IsStereo => channels == 2;

        /// <summary>Total number of samples per channel.</summary>
        public int SampleCount => samples != null ? samples.Length / channels : 0;

        /// <summary>Number of rays used during generation.</summary>
        public int RayCount => rayCount;

        /// <summary>Maximum bounces used during generation.</summary>
        public int MaxBounces => maxBounces;

        /// <summary>Source position used during generation.</summary>
        public Vector3 SourcePosition => sourcePosition;

        /// <summary>Receiver position used during generation.</summary>
        public Vector3 ReceiverPosition => receiverPosition;

        /// <summary>Stereo mode used during generation.</summary>
        public StereoMode StereoModeUsed => stereoMode;

        /// <summary>
        /// Sets the IR data for mono. Called by the bake process.
        /// </summary>
        public void SetData(float[] irSamples, int rate, int rays, int bounces,
            Vector3 srcPos, Vector3 rcvPos)
        {
            samples = irSamples;
            sampleRate = rate;
            channels = 1;
            lengthSeconds = irSamples.Length / (float)rate;
            rayCount = rays;
            maxBounces = bounces;
            sourcePosition = srcPos;
            receiverPosition = rcvPos;
            stereoMode = StereoMode.Mono;
        }

        /// <summary>
        /// Sets the IR data for stereo. L/R channels are interleaved.
        /// </summary>
        public void SetStereoData(float[] left, float[] right, int rate,
            int rays, int bounces, Vector3 srcPos, Vector3 rcvPos,
            StereoMode mode)
        {
            // Interleave L/R: [L0, R0, L1, R1, ...]
            int frameCount = left.Length;
            samples = new float[frameCount * 2];
            for (int i = 0; i < frameCount; i++)
            {
                samples[i * 2] = left[i];
                samples[i * 2 + 1] = right[i];
            }

            sampleRate = rate;
            channels = 2;
            lengthSeconds = frameCount / (float)rate;
            rayCount = rays;
            maxBounces = bounces;
            sourcePosition = srcPos;
            receiverPosition = rcvPos;
            stereoMode = mode;
        }

        /// <summary>
        /// Extracts the left channel from a stereo IR.
        /// Returns the full samples array for mono.
        /// </summary>
        public float[] GetLeftChannel()
        {
            if (channels == 1) return samples;

            int frameCount = samples.Length / 2;
            float[] left = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
                left[i] = samples[i * 2];
            return left;
        }

        /// <summary>
        /// Extracts the right channel from a stereo IR.
        /// Returns the full samples array for mono.
        /// </summary>
        public float[] GetRightChannel()
        {
            if (channels == 1) return samples;

            int frameCount = samples.Length / 2;
            float[] right = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
                right[i] = samples[i * 2 + 1];
            return right;
        }

        /// <summary>
        /// Creates an AudioClip from this IR data.
        /// Supports both mono and stereo.
        /// </summary>
        public AudioClip ToAudioClip(string clipName = "IR")
        {
            if (samples == null || samples.Length == 0)
                return null;

            int frameCount = samples.Length / channels;
            var clip = AudioClip.Create(clipName, frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
