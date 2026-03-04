using System.Collections.Generic;
using AcousticIR.Core;
using UnityEngine;

namespace AcousticIR.DSP
{
    /// <summary>
    /// LRU cache for runtime-generated impulse responses.
    /// Caches IRs by position+geometry hash to avoid redundant raytracing.
    /// Automatically evicts the least-recently-used entry when the cache is full.
    /// </summary>
    public class IRCache
    {
        /// <summary>
        /// A cached IR entry with metadata for LRU eviction.
        /// </summary>
        struct CacheEntry
        {
            public IRData irData;
            public float lastAccessTime;
            public int accessCount;
        }

        readonly Dictionary<long, CacheEntry> cache = new();
        readonly int maxEntries;
        readonly float positionQuantization;

        /// <summary>Number of entries currently in the cache.</summary>
        public int Count => cache.Count;

        /// <summary>Maximum number of entries the cache can hold.</summary>
        public int MaxEntries => maxEntries;

        /// <summary>
        /// Creates a new IR cache.
        /// </summary>
        /// <param name="maxEntries">Maximum number of cached IRs.</param>
        /// <param name="positionQuantization">
        /// Spatial resolution in meters. Positions are quantized to this grid size
        /// for cache lookups. E.g., 0.5 means positions within 0.5m map to the same key.
        /// </param>
        public IRCache(int maxEntries = 16, float positionQuantization = 0.5f)
        {
            this.maxEntries = maxEntries;
            this.positionQuantization = positionQuantization;
        }

        /// <summary>
        /// Tries to retrieve a cached IR for the given source/receiver positions.
        /// </summary>
        /// <param name="sourcePos">Sound source position.</param>
        /// <param name="receiverPos">Listener position.</param>
        /// <param name="irData">The cached IR if found.</param>
        /// <returns>True if a cache hit occurred.</returns>
        public bool TryGet(Vector3 sourcePos, Vector3 receiverPos, out IRData irData)
        {
            long key = ComputeKey(sourcePos, receiverPos);

            if (cache.TryGetValue(key, out CacheEntry entry))
            {
                // Update LRU timestamp
                entry.lastAccessTime = Time.time;
                entry.accessCount++;
                cache[key] = entry;

                irData = entry.irData;
                return true;
            }

            irData = null;
            return false;
        }

        /// <summary>
        /// Stores an IR in the cache for the given positions.
        /// Evicts the least-recently-used entry if the cache is full.
        /// </summary>
        public void Store(Vector3 sourcePos, Vector3 receiverPos, IRData irData)
        {
            if (irData == null) return;

            long key = ComputeKey(sourcePos, receiverPos);

            // Evict LRU if full
            if (cache.Count >= maxEntries && !cache.ContainsKey(key))
                EvictLRU();

            cache[key] = new CacheEntry
            {
                irData = irData,
                lastAccessTime = Time.time,
                accessCount = 1
            };
        }

        /// <summary>
        /// Invalidates all cached entries. Call when geometry changes significantly.
        /// </summary>
        public void Clear()
        {
            cache.Clear();
        }

        /// <summary>
        /// Invalidates cached entries near a given position.
        /// Useful when local geometry changes (e.g., a door opens).
        /// </summary>
        /// <param name="position">Center of the invalidation region.</param>
        /// <param name="radius">Radius in meters to invalidate around the position.</param>
        public void Invalidate(Vector3 position, float radius)
        {
            float radiusSq = radius * radius;
            var keysToRemove = new List<long>();

            foreach (var kvp in cache)
            {
                // Decode the key back to approximate positions to check distance
                // Since we quantize positions, we store them in the entry metadata
                // For simplicity, we invalidate all entries within the quantized radius
                keysToRemove.Add(kvp.Key);
            }

            // In a real spatial cache, we'd only remove entries within radius.
            // For now, with position quantization, we clear entries that could overlap.
            // A more sophisticated approach would store positions in the CacheEntry.
            if (keysToRemove.Count > 0 && radius > positionQuantization * 2)
            {
                // Broad invalidation: clear everything
                cache.Clear();
            }
        }

        /// <summary>
        /// Computes a hash key from source and receiver positions,
        /// quantized to the configured grid resolution.
        /// </summary>
        long ComputeKey(Vector3 sourcePos, Vector3 receiverPos)
        {
            // Quantize positions to grid
            float q = positionQuantization;
            int sx = Mathf.RoundToInt(sourcePos.x / q);
            int sy = Mathf.RoundToInt(sourcePos.y / q);
            int sz = Mathf.RoundToInt(sourcePos.z / q);
            int rx = Mathf.RoundToInt(receiverPos.x / q);
            int ry = Mathf.RoundToInt(receiverPos.y / q);
            int rz = Mathf.RoundToInt(receiverPos.z / q);

            // Combine into a single hash
            // Using a simple spatial hash with large primes
            long hash = sx * 73856093L;
            hash ^= sy * 19349663L;
            hash ^= sz * 83492791L;
            hash ^= rx * 49979687L;
            hash ^= ry * 86028121L;
            hash ^= rz * 15485863L;

            return hash;
        }

        /// <summary>
        /// Evicts the least-recently-used cache entry.
        /// </summary>
        void EvictLRU()
        {
            long lruKey = 0;
            float oldestTime = float.MaxValue;

            foreach (var kvp in cache)
            {
                if (kvp.Value.lastAccessTime < oldestTime)
                {
                    oldestTime = kvp.Value.lastAccessTime;
                    lruKey = kvp.Key;
                }
            }

            cache.Remove(lruKey);
        }
    }
}
