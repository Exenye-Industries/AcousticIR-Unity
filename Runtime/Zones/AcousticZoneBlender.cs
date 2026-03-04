using AcousticIR.Core;
using AcousticIR.Probes;
using UnityEngine;

namespace AcousticIR.Zones
{
    /// <summary>
    /// Handles smooth crossfading between impulse responses when switching zones.
    /// Uses a gain-ramp approach: fades out the current IR while fading in the new one.
    /// The native convolution plugin handles the actual audio processing.
    /// </summary>
    public class AcousticZoneBlender : MonoBehaviour
    {
        [Header("Blending")]
        [Tooltip("Minimum fade time in seconds (prevents clicks from instant switches).")]
        [Range(0.05f, 1f)]
        [SerializeField] float minimumFadeTime = 0.1f;

        AcousticSource targetSource;
        IRData currentIR;
        IRData targetIR;
        float targetVolume = 1f;

        // Crossfade state
        float fadeProgress = 1f; // 1 = complete, 0 = just started
        float fadeDuration = 1f;
        bool isFading;

        /// <summary>Whether a crossfade is currently in progress.</summary>
        public bool IsFading => isFading;

        /// <summary>Current crossfade progress (0 to 1).</summary>
        public float FadeProgress => fadeProgress;

        /// <summary>The currently active IR.</summary>
        public IRData CurrentIR => currentIR;

        /// <summary>
        /// Initializes the blender with a target AcousticSource.
        /// </summary>
        public void Initialize(AcousticSource source)
        {
            targetSource = source;
        }

        /// <summary>
        /// Initiates a crossfade to a new impulse response.
        /// </summary>
        /// <param name="newIR">The new IR to crossfade to.</param>
        /// <param name="fadeTime">Duration of the crossfade in seconds.</param>
        /// <param name="volume">Target volume for the new IR (0-1).</param>
        public void CrossfadeTo(IRData newIR, float fadeTime, float volume = 1f)
        {
            if (newIR == null || targetSource == null)
                return;

            // Same IR - just update volume
            if (newIR == currentIR && !isFading)
            {
                targetVolume = volume;
                return;
            }

            targetIR = newIR;
            targetVolume = volume;
            fadeDuration = Mathf.Max(fadeTime, minimumFadeTime);
            fadeProgress = 0f;
            isFading = true;

            // Immediately load the new IR into the plugin.
            // The native plugin handles the actual crossfade at the audio thread level
            // by double-buffering the IR data.
            targetSource.LoadIR(newIR);
        }

        /// <summary>
        /// Immediately switches to an IR without crossfading.
        /// </summary>
        public void SetImmediate(IRData newIR, float volume = 1f)
        {
            if (targetSource == null) return;

            currentIR = newIR;
            targetIR = null;
            targetVolume = volume;
            isFading = false;
            fadeProgress = 1f;

            if (newIR != null)
                targetSource.LoadIR(newIR);
        }

        void Update()
        {
            if (!isFading)
                return;

            fadeProgress += Time.deltaTime / fadeDuration;

            if (fadeProgress >= 1f)
            {
                fadeProgress = 1f;
                isFading = false;
                currentIR = targetIR;
                targetIR = null;
            }
        }
    }
}
