using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Burst-compiled job that processes RaycastHit results for one bounce level.
    /// For each active ray, it checks the hit result, applies material absorption,
    /// calculates the new reflected direction, and checks receiver intersection.
    /// </summary>
    [BurstCompile]
    public struct ProcessBounceJob : IJobParallelFor
    {
        /// <summary>Current state of each active ray.</summary>
        [ReadOnly] public NativeArray<ActiveRay> activeRays;

        /// <summary>RaycastHit results from RaycastCommand.ScheduleBatch.</summary>
        [ReadOnly] public NativeArray<RaycastHit> hitResults;

        /// <summary>Material data indexed by collider instance ID lookup.</summary>
        [ReadOnly] public NativeArray<MaterialData> materials;

        /// <summary>Maps collider instance ID to material index. -1 = use default.</summary>
        [ReadOnly] public NativeHashMap<int, int> colliderToMaterial;

        /// <summary>Simulation parameters.</summary>
        [ReadOnly] public RaytraceParams parameters;

        /// <summary>Default material for untagged surfaces.</summary>
        [ReadOnly] public MaterialData defaultMaterial;

        /// <summary>Updated ray states after this bounce (same length as activeRays).</summary>
        [WriteOnly] public NativeArray<ActiveRay> updatedRays;

        /// <summary>
        /// Flags: 0 = ray died, 1 = ray alive.
        /// Used to compact the active ray list between bounces.
        /// </summary>
        [WriteOnly] public NativeArray<int> aliveFlags;

        /// <summary>
        /// Ray arrivals found during this bounce (concurrent write).
        /// One slot per ray - only written if the ray reached the receiver.
        /// </summary>
        public NativeArray<RayArrival> arrivals;

        /// <summary>
        /// Flags: 0 = no arrival, 1 = arrival written.
        /// </summary>
        [WriteOnly] public NativeArray<int> arrivalFlags;

        public void Execute(int index)
        {
            ActiveRay ray = activeRays[index];
            RaycastHit hit = hitResults[index];
            var rng = new Unity.Mathematics.Random(ray.randomSeed + (uint)(ray.bounceCount * 7919));

            // Check if the ray hit anything
            bool didHit = hit.colliderInstanceID != 0;

            if (!didHit)
            {
                // Ray escaped into open space - energy is lost
                // But first check if it passed through the receiver on its way out
                float maxCheck = parameters.maxDistance - ray.totalDistance;
                float receiverDist = AcousticMath.CheckReceiverIntersection(
                    ray.origin, ray.direction, maxCheck,
                    parameters.receiverPosition, parameters.receiverRadius);

                if (receiverDist >= 0f)
                {
                    float totalDist = ray.totalDistance + receiverDist;
                    // Only apply air absorption for the last segment.
                    // Geometric 1/r² is implicitly handled by receiver sphere geometry.
                    AbsorptionCoefficients attenuated = AcousticMath.ApplyAirAbsorption(
                        ray.bandEnergy, receiverDist);

                    arrivals[index] = new RayArrival
                    {
                        time = totalDist / parameters.speedOfSound,
                        bandEnergy = attenuated,
                        direction = ray.direction,
                        bounceCount = ray.bounceCount
                    };
                    arrivalFlags[index] = 1;
                }

                // Ray is dead (escaped)
                aliveFlags[index] = 0;
                updatedRays[index] = ray;
                return;
            }

            float hitDistance = hit.distance;
            float3 hitPoint = ray.origin + ray.direction * hitDistance;
            float3 hitNormal = hit.normal;
            float newTotalDistance = ray.totalDistance + hitDistance;

            // Check if the ray passed through the receiver before hitting the surface
            float recDist = AcousticMath.CheckReceiverIntersection(
                ray.origin, ray.direction, hitDistance,
                parameters.receiverPosition, parameters.receiverRadius);

            if (recDist >= 0f)
            {
                float arrivalTotalDist = ray.totalDistance + recDist;
                // Only apply air absorption for the last segment.
                // Geometric 1/r² is implicitly handled by receiver sphere geometry.
                AbsorptionCoefficients attenuated = AcousticMath.ApplyAirAbsorption(
                    ray.bandEnergy, recDist);

                arrivals[index] = new RayArrival
                {
                    time = arrivalTotalDist / parameters.speedOfSound,
                    bandEnergy = attenuated,
                    direction = ray.direction,
                    bounceCount = ray.bounceCount
                };
                arrivalFlags[index] = 1;
            }

            // Look up the material for the hit collider
            MaterialData mat = defaultMaterial;
            if (colliderToMaterial.TryGetValue(hit.colliderInstanceID, out int matIndex))
            {
                if (matIndex >= 0 && matIndex < materials.Length)
                    mat = materials[matIndex];
            }

            // Apply surface absorption (frequency-dependent)
            AbsorptionCoefficients reflected = mat.absorption.Reflect(ray.bandEnergy);

            // Apply air absorption for this segment (no 1/r² - handled by receiver geometry)
            reflected = AcousticMath.ApplyAirAbsorption(reflected, hitDistance);

            // Check if ray has enough energy to continue
            if (reflected.TotalEnergy < parameters.energyThreshold ||
                newTotalDistance >= parameters.maxDistance)
            {
                aliveFlags[index] = 0;
                updatedRays[index] = ray;
                return;
            }

            // Check for diffraction at grazing angles
            float3 newDirection;
            AbsorptionCoefficients finalEnergy = reflected;

            if (parameters.enableDiffraction &&
                AcousticMath.ShouldDiffract(ray.direction, hitNormal, ref rng))
            {
                // Diffraction: bend ray around edge with frequency-dependent filter
                newDirection = AcousticMath.DiffractedDirection(ray.direction, hitNormal, ref rng);
                finalEnergy = AcousticMath.ApplyDiffractionFilter(reflected);
            }
            else
            {
                // Normal reflection (hybrid specular/diffuse)
                newDirection = AcousticMath.HybridReflect(
                    ray.direction, hitNormal, mat.scattering, mat.diffusion, ref rng);
            }

            // Check if diffracted energy is still above threshold
            if (finalEnergy.TotalEnergy < parameters.energyThreshold)
            {
                aliveFlags[index] = 0;
                updatedRays[index] = ray;
                return;
            }

            // Update ray state
            updatedRays[index] = new ActiveRay
            {
                origin = hitPoint + hitNormal * 0.001f, // Small offset to avoid self-intersection
                direction = newDirection,
                bandEnergy = finalEnergy,
                totalDistance = newTotalDistance,
                bounceCount = ray.bounceCount + 1,
                randomSeed = rng.state
            };
            aliveFlags[index] = 1;
        }
    }

    // ========================================================================
    // NEXT EVENT ESTIMATION (NEE)
    // After each bounce, shoot a shadow ray from each bounce point directly
    // toward the receiver. If the ray has clear line of sight, record an
    // arrival weighted by the solid angle of the receiver sphere.
    // This dramatically increases the number of useful arrivals.
    // ========================================================================

    /// <summary>
    /// Builds RaycastCommands for NEE shadow rays.
    /// For each alive ray (that just bounced), creates a command from the
    /// bounce point toward the receiver center.
    /// </summary>
    [BurstCompile]
    public struct BuildNEECommandsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ActiveRay> updatedRays;
        [ReadOnly] public NativeArray<int> aliveFlags;
        public float3 receiverPosition;

        [WriteOnly] public NativeArray<RaycastCommand> neeCommands;

        public void Execute(int index)
        {
            if (aliveFlags[index] == 0)
            {
                // Dead ray - create a zero-length dummy command
                neeCommands[index] = new RaycastCommand(
                    new float3(0, -10000, 0), new float3(0, 1, 0),
                    QueryParameters.Default, 0.001f);
                return;
            }

            ActiveRay ray = updatedRays[index];
            float3 toReceiver = receiverPosition - ray.origin;
            float dist = math.length(toReceiver);

            if (dist < 0.01f)
            {
                // Receiver basically at bounce point - skip
                neeCommands[index] = new RaycastCommand(
                    new float3(0, -10000, 0), new float3(0, 1, 0),
                    QueryParameters.Default, 0.001f);
                return;
            }

            float3 dir = toReceiver / dist;

            neeCommands[index] = new RaycastCommand(
                ray.origin, dir, QueryParameters.Default, dist);
        }
    }

    /// <summary>
    /// Processes NEE shadow ray results.
    /// If a shadow ray reaches the receiver without obstruction, records an arrival
    /// weighted by the geometric solid angle fraction of the receiver sphere.
    /// </summary>
    [BurstCompile]
    public struct ProcessNEEJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ActiveRay> updatedRays;
        [ReadOnly] public NativeArray<int> aliveFlags;
        [ReadOnly] public NativeArray<RaycastHit> neeHitResults;
        [ReadOnly] public RaytraceParams parameters;

        [WriteOnly] public NativeArray<RayArrival> neeArrivals;
        [WriteOnly] public NativeArray<int> neeArrivalFlags;

        public void Execute(int index)
        {
            neeArrivalFlags[index] = 0;

            if (aliveFlags[index] == 0)
                return;

            ActiveRay ray = updatedRays[index];
            RaycastHit shadowHit = neeHitResults[index];

            float3 toReceiver = parameters.receiverPosition - ray.origin;
            float distToReceiver = math.length(toReceiver);

            // Skip if receiver is basically at the bounce point
            if (distToReceiver < 0.05f)
                return;

            // Skip if total path would exceed max distance
            float totalDist = ray.totalDistance + distToReceiver;
            if (totalDist > parameters.maxDistance)
                return;

            // Check if shadow ray hit something BEFORE reaching the receiver
            bool blocked = shadowHit.colliderInstanceID != 0
                && shadowHit.distance < (distToReceiver - 0.05f);

            if (blocked)
                return; // No line of sight

            // === Clear line of sight! Record NEE arrival. ===

            // Geometric weight: solid angle fraction of receiver sphere
            // Ω = π*r² / d² (solid angle of disc approximation for small angles)
            // Fraction of hemisphere: Ω / (2π) = r² / (2*d²)
            float rr = parameters.receiverRadius * parameters.receiverRadius;
            float dd = distToReceiver * distToReceiver;
            float solidAngleWeight = rr / (2f * dd);

            // Apply air absorption for the shadow ray segment
            AbsorptionCoefficients attenuated = AcousticMath.ApplyAirAbsorption(
                ray.bandEnergy, distToReceiver);

            // Scale by solid angle weight (how much energy actually reaches the receiver)
            AbsorptionCoefficients weighted = new AbsorptionCoefficients
            {
                band125Hz = attenuated.band125Hz * solidAngleWeight,
                band250Hz = attenuated.band250Hz * solidAngleWeight,
                band500Hz = attenuated.band500Hz * solidAngleWeight,
                band1kHz = attenuated.band1kHz * solidAngleWeight,
                band2kHz = attenuated.band2kHz * solidAngleWeight,
                band4kHz = attenuated.band4kHz * solidAngleWeight
            };

            float3 dir = toReceiver / distToReceiver;

            neeArrivals[index] = new RayArrival
            {
                time = totalDist / parameters.speedOfSound,
                bandEnergy = weighted,
                direction = dir,
                bounceCount = ray.bounceCount
            };
            neeArrivalFlags[index] = 1;
        }
    }

    /// <summary>
    /// Job to compact the active ray list by removing dead rays.
    /// Uses the aliveFlags from ProcessBounceJob to build a compacted list.
    /// </summary>
    [BurstCompile]
    public struct CompactRaysJob : IJob
    {
        [ReadOnly] public NativeArray<ActiveRay> sourceRays;
        [ReadOnly] public NativeArray<int> aliveFlags;
        public NativeList<ActiveRay> compactedRays;

        public void Execute()
        {
            compactedRays.Clear();
            for (int i = 0; i < sourceRays.Length; i++)
            {
                if (aliveFlags[i] == 1)
                    compactedRays.Add(sourceRays[i]);
            }
        }
    }

    /// <summary>
    /// Job to collect arrivals from the sparse arrivals array into a compact list.
    /// </summary>
    [BurstCompile]
    public struct CollectArrivalsJob : IJob
    {
        [ReadOnly] public NativeArray<RayArrival> sparseArrivals;
        [ReadOnly] public NativeArray<int> arrivalFlags;
        public NativeList<RayArrival> collectedArrivals;

        public void Execute()
        {
            for (int i = 0; i < sparseArrivals.Length; i++)
            {
                if (arrivalFlags[i] == 1)
                    collectedArrivals.Add(sparseArrivals[i]);
            }
        }
    }
}
