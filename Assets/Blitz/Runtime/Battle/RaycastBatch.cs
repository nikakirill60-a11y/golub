using System;
using BlitzLegacy.Runtime.Telemetry;
using Unity.Collections;
using UnityEngine;

namespace BlitzLegacy.Runtime.Battle
{
    /// <summary>
    /// A set of independent rays answered together by <see cref="RaycastCommand"/>,
    /// so the physics queries run on the job workers in parallel instead of
    /// one after another on the main thread. Each ray reports up to
    /// <c>maxHits</c> colliders, one hit per collider and in no particular
    /// order, exactly what <c>Physics.RaycastNonAlloc</c> reports for the
    /// same ray, mask and trigger rule. Main thread only; the native buffers
    /// live for one <see cref="Run"/>.
    /// </summary>
    public sealed class RaycastBatch
    {
        private const int RaysPerJob = 4;

        private readonly int _maxHits;
        private RaycastCommand[] _commands = new RaycastCommand[16];
        private RaycastHit[] _results = new RaycastHit[0];
        private int _count;

        public RaycastBatch(int maxHits)
        {
            _maxHits = Math.Max(1, maxHits);
        }

        public int Count { get { return _count; } }

        public int MaxHits { get { return _maxHits; } }

        public void Clear()
        {
            _count = 0;
        }

        /// <summary>Queues one ray and returns its index.</summary>
        public int Add(Vector3 origin, Vector3 direction, float distance, int layerMask,
            QueryTriggerInteraction triggers)
        {
            if (_count == _commands.Length)
            {
                Array.Resize(ref _commands, _commands.Length * 2);
            }

            var query = new QueryParameters(layerMask, false, triggers, Physics.queriesHitBackfaces);
            _commands[_count] = new RaycastCommand(origin, direction, query, distance);
            return _count++;
        }

        /// <summary>Answers every queued ray.</summary>
        public void Run()
        {
            if (_count == 0)
            {
                return;
            }

            var total = _count * _maxHits;
            if (_results.Length < total)
            {
                _results = new RaycastHit[Math.Max(total, _results.Length * 2)];
            }

            var commands = new NativeArray<RaycastCommand>(_count, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            var results = new NativeArray<RaycastHit>(total, Allocator.TempJob);
            try
            {
                NativeArray<RaycastCommand>.Copy(_commands, commands, _count);
                using var slowScope = BlitzPerfMonitor.Slow("raycast.batch");
                RaycastCommand.ScheduleBatch(commands, results, RaysPerJob, _maxHits).Complete();
                NativeArray<RaycastHit>.Copy(results, _results, total);
            }
            finally
            {
                commands.Dispose();
                results.Dispose();
            }
        }

        /// <summary>
        /// Copies the hits of ray <paramref name="index"/> into
        /// <paramref name="buffer"/> and returns how many there are.
        /// </summary>
        public int Hits(int index, RaycastHit[] buffer)
        {
            var first = index * _maxHits;
            var count = 0;
            for (var i = 0; i < _maxHits && count < buffer.Length; i++)
            {
                var hit = _results[first + i];
                if (hit.colliderEntityId == default)
                {
                    break;
                }

                buffer[count++] = hit;
            }

            return count;
        }

        /// <summary>The nearest hit of ray <paramref name="index"/>, if any.</summary>
        public bool First(int index, out RaycastHit hit)
        {
            hit = _results[index * _maxHits];
            return hit.colliderEntityId != default;
        }
    }
}
