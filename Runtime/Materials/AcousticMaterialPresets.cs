using UnityEngine;

namespace AcousticIR.Materials
{
    /// <summary>
    /// Factory for creating common acoustic material presets.
    /// Absorption values based on standard acoustic engineering reference data (Sabine coefficients).
    /// </summary>
    public static class AcousticMaterialPresets
    {
        //                                125    250    500    1k     2k     4k     diff
        public static readonly float[] Concrete =     { 0.01f, 0.01f, 0.02f, 0.02f, 0.02f, 0.03f, 0.10f };
        public static readonly float[] Brick =        { 0.03f, 0.03f, 0.03f, 0.04f, 0.05f, 0.07f, 0.20f };
        public static readonly float[] WoodPanel =    { 0.28f, 0.22f, 0.17f, 0.09f, 0.10f, 0.11f, 0.30f };
        public static readonly float[] WoodFloor =    { 0.15f, 0.11f, 0.10f, 0.07f, 0.06f, 0.07f, 0.25f };
        public static readonly float[] Glass =        { 0.35f, 0.25f, 0.18f, 0.12f, 0.07f, 0.04f, 0.05f };
        public static readonly float[] Carpet =       { 0.08f, 0.24f, 0.57f, 0.69f, 0.71f, 0.73f, 0.60f };
        public static readonly float[] HeavyCurtain = { 0.14f, 0.35f, 0.55f, 0.72f, 0.70f, 0.65f, 0.80f };
        public static readonly float[] Metal =        { 0.01f, 0.01f, 0.01f, 0.02f, 0.02f, 0.03f, 0.05f };
        public static readonly float[] Plaster =      { 0.01f, 0.02f, 0.02f, 0.03f, 0.04f, 0.05f, 0.15f };
        public static readonly float[] AcousticTile = { 0.10f, 0.30f, 0.70f, 0.80f, 0.75f, 0.65f, 0.50f };
        public static readonly float[] Soil =         { 0.15f, 0.25f, 0.40f, 0.55f, 0.60f, 0.60f, 0.70f };
        public static readonly float[] Water =        { 0.01f, 0.01f, 0.01f, 0.02f, 0.02f, 0.03f, 0.02f };
        public static readonly float[] Foliage =      { 0.03f, 0.06f, 0.11f, 0.17f, 0.27f, 0.31f, 0.90f };
        public static readonly float[] OpenAir =      { 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 0.00f };

        /// <summary>
        /// Applies a preset to an AcousticMaterial instance.
        /// </summary>
        /// <param name="material">Target material to configure.</param>
        /// <param name="preset">Preset array (7 values: 6 bands + diffusion).</param>
        public static void ApplyPreset(AcousticMaterial material, float[] preset)
        {
            if (preset.Length != 7)
            {
                Debug.LogError("[AcousticIR] Preset must have exactly 7 values.");
                return;
            }
            material.SetAbsorption(preset[0], preset[1], preset[2],
                preset[3], preset[4], preset[5], preset[6]);
        }
    }
}
