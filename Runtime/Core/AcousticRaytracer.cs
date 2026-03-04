using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AcousticIR.Core
{
    /// <summary>
    /// Main acoustic raytracing engine. Orchestrates iterative bounce simulation
    /// using RaycastCommand batches and Burst-compiled processing jobs.
    /// </summary>
    public class AcousticRaytracer : IDisposable
    {
        readonly RaytraceParams parameters;
        readonly MaterialData defaultMaterial;
        readonly NativeArray<MaterialData> materials;
        readonly NativeHashMap<int, int> colliderToMaterial;

        bool disposed;

        /// <summary>
        /// Creates a new raytracer instance with the given parameters and material setup.
        /// </summary>
        /// <param name="parameters">Simulation parameters.</param>
        /// <param name="materialList">List of acoustic materials in the scene.</param>
        /// <param name="colliderMapping">Maps collider instance IDs to material list indices.</param>
        /// <param name="defaultMat">Default material for surfaces without AcousticSurface.</param>
        public AcousticRaytracer(
            RaytraceParams parameters,
            List<MaterialData> materialList,
            Dictionary<int, int> colliderMapping,
            MaterialData defaultMat)
        {
            this.parameters = parameters;
            this.defaultMaterial = defaultMat;

            materials = new NativeArray<MaterialData>(
                materialList.Count > 0 ? materialList.Count : 1,
                Allocator.Persistent);
            for (int i = 0; i < materialList.Count; i++)
                materials[i] = materialList[i];

            colliderToMaterial = new NativeHashMap<int, int>(
                colliderMapping.Count, Allocator.Persistent);
            foreach (var kvp in colliderMapping)
                colliderToMaterial.Add(kvp.Key, kvp.Value);
        }

        /// <summary>
        /// Runs the full raytrace simulation synchronously.
        /// Returns all ray arrivals at the receiver.
        /// </summary>
        /// <param name="rayCount">Number of rays to emit from the source.</param>
        /// <returns>List of arrivals (caller must dispose the NativeList).</returns>
        public NativeList<RayArrival> Trace(int rayCount)
        {
            var allArrivals = new NativeList<RayArrival>(
                rayCount * 2, Allocator.Persistent);

            // Initialize rays with Fibonacci sphere directions
            var activeRays = new NativeList<ActiveRay>(rayCount, Allocator.TempJob);
            uint baseSeed = (uint)UnityEngine.Random.Range(1, int.MaxValue);

            for (int i = 0; i < rayCount; i++)
            {
                float3 dir = AcousticMath.FibonacciSpherePoint(i, rayCount);
                activeRays.Add(new ActiveRay
                {
                    origin = parameters.sourcePosition,
                    direction = dir,
                    bandEnergy = AbsorptionCoefficients.FullEnergy,
                    totalDistance = 0f,
                    bounceCount = 0,
                    randomSeed = baseSeed + (uint)i
                });
            }

            // Iterative bounce loop
            for (int bounce = 0; bounce < parameters.maxBounces; bounce++)
            {
                int count = activeRays.Length;
                if (count == 0)
                    break;

                // 1. Build RaycastCommand array
                var commands = new NativeArray<RaycastCommand>(count, Allocator.TempJob);
                var hitResults = new NativeArray<RaycastHit>(count, Allocator.TempJob);

                for (int i = 0; i < count; i++)
                {
                    var ray = activeRays[i];
                    float remainingDist = parameters.maxDistance - ray.totalDistance;
                    commands[i] = new RaycastCommand(
                        ray.origin, ray.direction, QueryParameters.Default, remainingDist);
                }

                // 2. Schedule raycast batch
                var raycastHandle = RaycastCommand.ScheduleBatch(
                    commands, hitResults, 32);
                raycastHandle.Complete();

                // 3. Process hits with Burst job
                var updatedRays = new NativeArray<ActiveRay>(count, Allocator.TempJob);
                var aliveFlags = new NativeArray<int>(count, Allocator.TempJob);
                var sparseArrivals = new NativeArray<RayArrival>(count, Allocator.TempJob);
                var arrivalFlags = new NativeArray<int>(count, Allocator.TempJob);

                var processJob = new ProcessBounceJob
                {
                    activeRays = activeRays.AsArray(),
                    hitResults = hitResults,
                    materials = materials,
                    colliderToMaterial = colliderToMaterial,
                    parameters = parameters,
                    defaultMaterial = defaultMaterial,
                    updatedRays = updatedRays,
                    aliveFlags = aliveFlags,
                    arrivals = sparseArrivals,
                    arrivalFlags = arrivalFlags
                };

                var processHandle = processJob.Schedule(count, 64);
                processHandle.Complete();

                // 4. Collect arrivals
                var collectJob = new CollectArrivalsJob
                {
                    sparseArrivals = sparseArrivals,
                    arrivalFlags = arrivalFlags,
                    collectedArrivals = allArrivals
                };
                collectJob.Run();

                // 5. Compact surviving rays
                var compactedRays = new NativeList<ActiveRay>(count, Allocator.TempJob);
                var compactJob = new CompactRaysJob
                {
                    sourceRays = updatedRays,
                    aliveFlags = aliveFlags,
                    compactedRays = compactedRays
                };
                compactJob.Run();

                // Swap active rays
                activeRays.Clear();
                for (int i = 0; i < compactedRays.Length; i++)
                    activeRays.Add(compactedRays[i]);

                // Dispose temporary arrays
                commands.Dispose();
                hitResults.Dispose();
                updatedRays.Dispose();
                aliveFlags.Dispose();
                sparseArrivals.Dispose();
                arrivalFlags.Dispose();
                compactedRays.Dispose();
            }

            activeRays.Dispose();
            return allArrivals;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (materials.IsCreated) materials.Dispose();
            if (colliderToMaterial.IsCreated) colliderToMaterial.Dispose();
        }
    }
}
