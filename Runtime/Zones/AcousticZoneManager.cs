using System.Collections.Generic;
using AcousticIR.Probes;
using UnityEngine;

namespace AcousticIR.Zones
{
    /// <summary>
    /// Tracks the AcousticListener's position and determines which AcousticZone is active.
    /// When the active zone changes, triggers a crossfade via AcousticZoneBlender.
    /// </summary>
    public class AcousticZoneManager : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The zone blender that handles IR crossfades. Auto-created if null.")]
        [SerializeField] AcousticZoneBlender blender;

        [Tooltip("The acoustic source to route IRs to. If null, searches scene.")]
        [SerializeField] AcousticSource targetSource;

        [Header("Settings")]
        [Tooltip("How often to check zone overlap (seconds). Lower = more responsive.")]
        [Range(0.05f, 1f)]
        [SerializeField] float updateInterval = 0.1f;

        [Tooltip("Fallback IR when no zone is active (e.g. outdoors/open air).")]
        [SerializeField] Core.IRData fallbackIR;

        readonly HashSet<AcousticZone> activeZones = new();
        AcousticZone currentZone;
        float nextUpdateTime;

        /// <summary>The currently active zone (highest priority among overlapping).</summary>
        public AcousticZone CurrentZone => currentZone;

        /// <summary>All zones the listener is currently inside.</summary>
        public IReadOnlyCollection<AcousticZone> ActiveZones => activeZones;

        void Start()
        {
            if (blender == null)
            {
                blender = GetComponent<AcousticZoneBlender>();
                if (blender == null)
                    blender = gameObject.AddComponent<AcousticZoneBlender>();
            }

            if (targetSource == null)
                targetSource = FindAnyObjectByType<AcousticSource>();

            if (targetSource != null)
                blender.Initialize(targetSource);
        }

        void Update()
        {
            if (Time.time < nextUpdateTime)
                return;
            nextUpdateTime = Time.time + updateInterval;

            EvaluateActiveZone();
        }

        /// <summary>
        /// Called by AcousticZone trigger events or manually.
        /// Registers a zone as containing the listener.
        /// </summary>
        public void EnterZone(AcousticZone zone)
        {
            if (zone == null) return;
            activeZones.Add(zone);
            EvaluateActiveZone();
        }

        /// <summary>
        /// Called when the listener leaves a zone.
        /// </summary>
        public void ExitZone(AcousticZone zone)
        {
            if (zone == null) return;
            activeZones.Remove(zone);
            EvaluateActiveZone();
        }

        /// <summary>
        /// Forces a re-evaluation of the active zone.
        /// </summary>
        public void ForceUpdate()
        {
            EvaluateActiveZone();
        }

        void EvaluateActiveZone()
        {
            // Find highest-priority zone with a valid IR
            AcousticZone bestZone = null;
            int bestPriority = int.MinValue;

            foreach (var zone in activeZones)
            {
                if (zone == null || !zone.isActiveAndEnabled)
                    continue;

                if (zone.Priority > bestPriority)
                {
                    bestPriority = zone.Priority;
                    bestZone = zone;
                }
            }

            // Clean up destroyed zones
            activeZones.RemoveWhere(z => z == null || !z.isActiveAndEnabled);

            if (bestZone == currentZone)
                return;

            var previousZone = currentZone;
            currentZone = bestZone;

            OnZoneChanged(previousZone, currentZone);
        }

        void OnZoneChanged(AcousticZone from, AcousticZone to)
        {
            if (blender == null || targetSource == null)
                return;

            if (to != null && to.HasIR)
            {
                blender.CrossfadeTo(to.IR, to.FadeTime, to.Volume);
                Debug.Log($"[AcousticIR] Zone changed: {(from != null ? from.name : "none")} " +
                          $"-> {to.name} (P{to.Priority})");
            }
            else if (fallbackIR != null)
            {
                float fadeTime = from != null ? from.FadeTime : 1f;
                blender.CrossfadeTo(fallbackIR, fadeTime, 1f);
                Debug.Log("[AcousticIR] Zone changed: reverted to fallback IR.");
            }
            else
            {
                Debug.Log("[AcousticIR] No active zone and no fallback IR.");
            }
        }

        /// <summary>
        /// Trigger-based zone detection. Attach this to the AcousticListener
        /// or ensure the listener has a Rigidbody + Collider for trigger detection.
        /// </summary>
        void OnTriggerEnter(Collider other)
        {
            var zone = other.GetComponent<AcousticZone>();
            if (zone != null)
                EnterZone(zone);
        }

        void OnTriggerExit(Collider other)
        {
            var zone = other.GetComponent<AcousticZone>();
            if (zone != null)
                ExitZone(zone);
        }
    }
}
