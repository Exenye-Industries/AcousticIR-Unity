using UnityEngine;

namespace AcousticIR.Config
{
    /// <summary>
    /// Configuration for impulse response generation from raytracing results.
    /// Controls output format, quality, and post-processing.
    /// </summary>
    [CreateAssetMenu(fileName = "IRGenerationConfig", menuName = "AcousticIR/IR Generation Config")]
    public class IRGenerationConfig : ScriptableObject
    {
        [Header("Output Format")]
        [Tooltip("Sample rate of the generated IR")]
        [SerializeField] int sampleRate = 48000;

        [Tooltip("Maximum IR length in seconds")]
        [Range(0.5f, 6f)]
        [SerializeField] float irLength = 2f;

        [Header("Processing")]
        [Tooltip("Apply Hann window to IR tail to prevent abrupt cutoff")]
        [SerializeField] bool applyWindowing = true;

        [Tooltip("Portion of IR tail to apply windowing to (0-1)")]
        [Range(0.05f, 0.3f)]
        [SerializeField] float windowTailPortion = 0.1f;

        [Tooltip("Synthesize stochastic late reverberation tail for denser sound")]
        [SerializeField] bool synthesizeLateTail = true;

        public int SampleRate => sampleRate;
        public float IRLength => irLength;
        public bool ApplyWindowing => applyWindowing;
        public float WindowTailPortion => windowTailPortion;
        public bool SynthesizeLateTail => synthesizeLateTail;
    }
}
