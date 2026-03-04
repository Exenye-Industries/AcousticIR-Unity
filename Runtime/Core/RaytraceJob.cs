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
                    // Only attenuate by the last segment (receiverDist), not the total path.
                    // ray.bandEnergy already includes attenuation from all previous bounces.
                    AbsorptionCoefficients attenuated = AcousticMath.AttenuateByDistance(
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
                // Only attenuate by the last segment (recDist), not the total path.
                // ray.bandEnergy already includes attenuation from all previous bounces.
                AbsorptionCoefficients attenuated = AcousticMath.AttenuateByDistance(
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

            // Apply distance attenuation for this segment
            reflected = AcousticMath.AttenuateByDistance(reflected, hitDistance);

            // Check if ray has enough energy to continue
            if (reflected.TotalEnergy < parameters.energyThreshold ||
                newTotalDistance >= parameters.maxDistance)
            {
                aliveFlags[index] = 0;
                updatedRays[index] = ray;
                return;
            }

            // Calculate reflected direction (hybrid specular/diffuse)
            float3 newDirection = AcousticMath.HybridReflect(
                ray.direction, hitNormal, mat.diffusion, ref rng);

            // Update ray state
            updatedRays[index] = new ActiveRay
            {
                origin = hitPoint + hitNormal * 0.001f, // Small offset to avoid self-intersection
                direction = newDirection,
                bandEnergy = reflected,
                totalDistance = newTotalDistance,
                bounceCount = ray.bounceCount + 1,
                randomSeed = rng.state
            };
            aliveFlags[index] = 1;
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
