using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// ScriptableObject that stores a baked impulse response.
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
        [SerializeField] float lengthSeconds;
        [SerializeField] int rayCount;
        [SerializeField] int maxBounces;
        [SerializeField] Vector3 sourcePosition;
        [SerializeField] Vector3 receiverPosition;

        /// <summary>Raw IR samples (mono, normalized).</summary>
        public float[] Samples => samples;

        /// <summary>Sample rate of the IR.</summary>
        public int SampleRate => sampleRate;

        /// <summary>IR length in seconds.</summary>
        public float LengthSeconds => lengthSeconds;

        /// <summary>Total number of samples.</summary>
        public int SampleCount => samples != null ? samples.Length : 0;

        /// <summary>Number of rays used during generation.</summary>
        public int RayCount => rayCount;

        /// <summary>Maximum bounces used during generation.</summary>
        public int MaxBounces => maxBounces;

        /// <summary>Source position used during generation.</summary>
        public Vector3 SourcePosition => sourcePosition;

        /// <summary>Receiver position used during generation.</summary>
        public Vector3 ReceiverPosition => receiverPosition;

        /// <summary>
        /// Sets the IR data. Called by the bake process.
        /// </summary>
        public void SetData(float[] irSamples, int rate, int rays, int bounces,
            Vector3 srcPos, Vector3 rcvPos)
        {
            samples = irSamples;
            sampleRate = rate;
            lengthSeconds = irSamples.Length / (float)rate;
            rayCount = rays;
            maxBounces = bounces;
            sourcePosition = srcPos;
            receiverPosition = rcvPos;
        }

        /// <summary>
        /// Creates an AudioClip from this IR data.
        /// </summary>
        public AudioClip ToAudioClip(string clipName = "IR")
        {
            if (samples == null || samples.Length == 0)
                return null;

            var clip = AudioClip.Create(clipName, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
