using AcousticIR.Core;
using AcousticIR.DSP;
using UnityEngine;

namespace AcousticIR.Probes
{
    /// <summary>
    /// Wraps a Unity AudioSource with acoustic IR routing.
    /// Loads an impulse response into the native convolution plugin
    /// attached to the AudioSource's output mixer.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class AcousticSource : MonoBehaviour
    {
        [Header("Convolution Plugin")]
        [Tooltip("Instance ID of the native convolution plugin effect. " +
                 "Read from the plugin's 'InstanceID' parameter in the Audio Mixer.")]
        [SerializeField] int pluginInstanceId = -1;

        [Header("IR Assignment")]
        [Tooltip("Baked IR to load into the convolution plugin on start.")]
        [SerializeField] IRData initialIR;

        [Tooltip("Auto-load the IR when the component is enabled.")]
        [SerializeField] bool loadOnEnable = true;

        AudioSource audioSource;
        IRData currentIR;
        bool irLoaded;

        /// <summary>The AudioSource this component wraps.</summary>
        public AudioSource AudioSource => audioSource;

        /// <summary>The currently loaded IR.</summary>
        public IRData CurrentIR => currentIR;

        /// <summary>Whether an IR is currently loaded in the plugin.</summary>
        public bool IsIRLoaded => irLoaded;

        /// <summary>Plugin instance ID for the native convolution effect.</summary>
        public int PluginInstanceId
        {
            get => pluginInstanceId;
            set => pluginInstanceId = value;
        }

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
        }

        void OnEnable()
        {
            if (loadOnEnable && initialIR != null)
                LoadIR(initialIR);
        }

        /// <summary>
        /// Loads an impulse response into the native convolution plugin.
        /// </summary>
        /// <param name="irData">The IR to load.</param>
        public void LoadIR(IRData irData)
        {
            if (irData == null || irData.Samples == null || irData.Samples.Length == 0)
            {
                Debug.LogWarning("[AcousticIR] Cannot load null or empty IR.", this);
                return;
            }

            if (pluginInstanceId < 0)
            {
                Debug.LogWarning(
                    "[AcousticIR] Plugin instance ID not set. " +
                    "Assign it from the Audio Mixer's convolution effect.", this);
                return;
            }

            ConvolutionBridge.LoadIR(pluginInstanceId, irData);
            currentIR = irData;
            irLoaded = true;
        }

        /// <summary>
        /// Loads raw IR samples into the native convolution plugin.
        /// </summary>
        public void LoadIR(float[] samples, int sampleRate)
        {
            if (pluginInstanceId < 0)
            {
                Debug.LogWarning("[AcousticIR] Plugin instance ID not set.", this);
                return;
            }

            ConvolutionBridge.LoadIR(pluginInstanceId, samples, sampleRate);
            currentIR = null;
            irLoaded = true;
        }

        /// <summary>
        /// Resets the convolution engine (clears all buffers).
        /// </summary>
        public void ResetConvolution()
        {
            if (pluginInstanceId >= 0)
            {
                ConvolutionBridge.Reset(pluginInstanceId);
                irLoaded = false;
                currentIR = null;
            }
        }
    }
}
