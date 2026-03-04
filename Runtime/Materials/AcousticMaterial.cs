using AcousticIR.Core;
using UnityEngine;

namespace AcousticIR.Materials
{
    /// <summary>
    /// Defines the acoustic properties of a surface material.
    /// Absorption coefficients control how much sound energy is absorbed at each frequency band.
    /// Diffusion controls the scattering behavior (specular vs. diffuse reflection).
    /// </summary>
    [CreateAssetMenu(fileName = "AcousticMaterial", menuName = "AcousticIR/Acoustic Material")]
    public class AcousticMaterial : ScriptableObject
    {
        [Header("Absorption (0 = reflective, 1 = absorptive)")]
        [Tooltip("125 Hz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption125Hz = 0.1f;

        [Tooltip("250 Hz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption250Hz = 0.15f;

        [Tooltip("500 Hz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption500Hz = 0.2f;

        [Tooltip("1 kHz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption1kHz = 0.25f;

        [Tooltip("2 kHz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption2kHz = 0.3f;

        [Tooltip("4 kHz absorption coefficient")]
        [Range(0f, 1f)]
        [SerializeField] float absorption4kHz = 0.35f;

        [Header("Scattering")]
        [Tooltip("Surface diffusion: 0 = mirror reflection, 1 = fully diffuse (Lambertian)")]
        [Range(0f, 1f)]
        [SerializeField] float diffusion = 0.3f;

        public float Absorption125Hz => absorption125Hz;
        public float Absorption250Hz => absorption250Hz;
        public float Absorption500Hz => absorption500Hz;
        public float Absorption1kHz => absorption1kHz;
        public float Absorption2kHz => absorption2kHz;
        public float Absorption4kHz => absorption4kHz;
        public float Diffusion => diffusion;

        /// <summary>
        /// Converts to a blittable MaterialData struct for use in Burst jobs.
        /// </summary>
        public MaterialData ToMaterialData()
        {
            return new MaterialData
            {
                absorption = new AbsorptionCoefficients
                {
                    band125Hz = absorption125Hz,
                    band250Hz = absorption250Hz,
                    band500Hz = absorption500Hz,
                    band1kHz = absorption1kHz,
                    band2kHz = absorption2kHz,
                    band4kHz = absorption4kHz
                },
                diffusion = diffusion
            };
        }

        /// <summary>
        /// Sets all absorption values at once. Useful for presets.
        /// </summary>
        public void SetAbsorption(float hz125, float hz250, float hz500,
            float hz1k, float hz2k, float hz4k, float diff)
        {
            absorption125Hz = hz125;
            absorption250Hz = hz250;
            absorption500Hz = hz500;
            absorption1kHz = hz1k;
            absorption2kHz = hz2k;
            absorption4kHz = hz4k;
            diffusion = diff;
        }
    }
}
