using UnityEngine;

namespace AcousticIR.Materials
{
    /// <summary>
    /// Attach to any GameObject with a Collider to define its acoustic material.
    /// During raytracing, hit colliders are checked for this component to determine
    /// absorption and diffusion properties.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class AcousticSurface : MonoBehaviour
    {
        [Tooltip("Acoustic material for this surface. Determines absorption and scattering.")]
        [SerializeField] AcousticMaterial material;

        /// <summary>The assigned acoustic material.</summary>
        public AcousticMaterial Material => material;
    }
}
