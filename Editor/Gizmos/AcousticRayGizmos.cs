using AcousticIR.Probes;
using AcousticIR.Zones;
using UnityEditor;
using UnityEngine;

namespace AcousticIR.Editor.Gizmos
{
    /// <summary>
    /// Scene view gizmo drawing for acoustic components.
    /// Draws zone boundaries, probe connections, and debug overlays.
    /// </summary>
    [InitializeOnLoad]
    public static class AcousticRayGizmos
    {
        static bool showZoneBounds = true;
        static bool showProbeConnections = true;

        static AcousticRayGizmos()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        static void OnSceneGUI(SceneView sceneView)
        {
            if (!showZoneBounds && !showProbeConnections)
                return;

            if (showProbeConnections)
                DrawProbeConnections();

            if (showZoneBounds)
                DrawZoneLabels();
        }

        static void DrawProbeConnections()
        {
            var probes = Object.FindObjectsByType<AcousticProbe>(FindObjectsSortMode.None);

            foreach (var probe in probes)
            {
                if (probe == null) continue;

                Vector3 src = probe.SourcePosition;
                Vector3 rcv = probe.ReceiverPosition;

                // Dashed line between source and receiver
                Handles.color = new Color(1f, 0.8f, 0.2f, 0.6f);
                Handles.DrawDottedLine(src, rcv, 4f);

                // Distance label at midpoint
                Vector3 mid = (src + rcv) * 0.5f;
                float dist = Vector3.Distance(src, rcv);
                Handles.Label(mid, $"{dist:F1}m", EditorStyles.miniLabel);
            }
        }

        static void DrawZoneLabels()
        {
            var zones = Object.FindObjectsByType<AcousticZone>(FindObjectsSortMode.None);

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                normal = { textColor = new Color(0.8f, 0.9f, 1f) }
            };

            foreach (var zone in zones)
            {
                if (zone == null) continue;

                string label = $"{zone.name}\n" +
                               $"P{zone.Priority}" +
                               (zone.HasIR ? $" | {zone.IR.LengthSeconds:F1}s" : " | No IR");

                Handles.Label(zone.transform.position + Vector3.up * 0.5f, label, style);
            }
        }

        // Menu toggles for the gizmo options
        [MenuItem("AcousticIR/Gizmos/Show Zone Bounds")]
        static void ToggleZoneBounds()
        {
            showZoneBounds = !showZoneBounds;
            Menu.SetChecked("AcousticIR/Gizmos/Show Zone Bounds", showZoneBounds);
            SceneView.RepaintAll();
        }

        [MenuItem("AcousticIR/Gizmos/Show Zone Bounds", true)]
        static bool ToggleZoneBoundsValidate()
        {
            Menu.SetChecked("AcousticIR/Gizmos/Show Zone Bounds", showZoneBounds);
            return true;
        }

        [MenuItem("AcousticIR/Gizmos/Show Probe Connections")]
        static void ToggleProbeConnections()
        {
            showProbeConnections = !showProbeConnections;
            Menu.SetChecked("AcousticIR/Gizmos/Show Probe Connections", showProbeConnections);
            SceneView.RepaintAll();
        }

        [MenuItem("AcousticIR/Gizmos/Show Probe Connections", true)]
        static bool ToggleProbeConnectionsValidate()
        {
            Menu.SetChecked("AcousticIR/Gizmos/Show Probe Connections", showProbeConnections);
            return true;
        }
    }
}
