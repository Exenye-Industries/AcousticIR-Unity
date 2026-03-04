using UnityEngine;

namespace AcousticIR.Probes
{
    /// <summary>
    /// Marks a GameObject as the acoustic listener (receiver) for the zone system.
    /// Typically placed on the player camera or main character.
    /// Only one AcousticListener should be active at a time.
    /// </summary>
    [DisallowMultipleComponent]
    public class AcousticListener : MonoBehaviour
    {
        static AcousticListener current;

        /// <summary>
        /// The currently active AcousticListener in the scene.
        /// Returns null if none is active.
        /// </summary>
        public static AcousticListener Current => current;

        /// <summary>World-space listener position.</summary>
        public Vector3 Position => transform.position;

        void OnEnable()
        {
            if (current != null && current != this)
            {
                Debug.LogWarning(
                    $"[AcousticIR] Multiple AcousticListeners active. " +
                    $"Disabling previous on '{current.gameObject.name}'.", this);
            }
            current = this;
        }

        void OnDisable()
        {
            if (current == this)
                current = null;
        }
    }
}
