using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;

namespace GTAZ.Core
{
    public class SpawnRequest
    {
        public PedCategory Category { get; init; }
        public PedPriority Priority { get; set; }
        public Vector3 Position { get; init; }

        public Func<Ped> SpawnFactory { get; init; }

        public string Origin { get; init; } = "Unspecified";

        public Action<PedSlot> OnGranted { get; init; }
        public Action OnExpired { get; init; }

        public DateTime EnqueuedAt { get; } = DateTime.UtcNow;
        public TimeSpan TimeToLive { get; init; } = TimeSpan.FromSeconds(8);

        public PedPriority OriginalPriority { get; init; }
    }

    public class SpawnRequestQueue
    {
        private static SpawnRequestQueue _instance;
        public static SpawnRequestQueue Instance => _instance ??= new SpawnRequestQueue();

        private readonly List<SpawnRequest> _pending = [];

        public int MaxGrantsPerPass { get; set; } = 3;
        public TimeSpan AgingInterval { get; set; } = TimeSpan.FromSeconds(3);

        public int PendingCount => _pending.Count;

        public void Enqueue(SpawnRequest request)
        {
            _pending.Add(request);
        }

        public void ProcessQueue()
        {
            if (_pending.Count == 0) return;

            var now = DateTime.UtcNow;
            int grantsThisPass = 0;

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var req = _pending[i];
                if (now - req.EnqueuedAt >= req.TimeToLive)
                {
                    _pending.RemoveAt(i);
                    req.OnExpired?.Invoke();
                }
            }

            foreach (var req in _pending)
            {
                var waited = now - req.EnqueuedAt;
                int ageBumps = (int)(waited.TotalSeconds / AgingInterval.TotalSeconds);
                int agedValue = Math.Min((int)PedPriority.Critical, (int)req.OriginalPriority + ageBumps);
                req.Priority = (PedPriority)agedValue;
            }

            var ordered = _pending
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.EnqueuedAt)
                .ToList();

            foreach (var req in ordered)
            {
                if (grantsThisPass >= MaxGrantsPerPass)
                    break;

                PedBudgetManager.SpawnOutcome outcome;
                PedSlot slot;
                try
                {
                    outcome = PedBudgetManager.Instance.TryRequestAndSpawn(
                        req.Category, req.Priority, req.Position, req.SpawnFactory, out slot);
                }
                catch
                {
                    // Spawn factory threw - remove request and notify
                    _pending.Remove(req);
                    req.OnExpired?.Invoke();
                    continue;
                }

                switch (outcome)
                {
                    case PedBudgetManager.SpawnOutcome.NoSlotAvailable:
                        continue;

                    case PedBudgetManager.SpawnOutcome.FactorySpawnFailed:
                        _pending.Remove(req);
                        req.OnExpired?.Invoke();
                        continue;

                    case PedBudgetManager.SpawnOutcome.Spawned:
                        _pending.Remove(req);
                        req.OnGranted?.Invoke(slot);
                        grantsThisPass++;
                        break;
                }
            }
        }

        public void Cancel(SpawnRequest request)
        {
            _pending.Remove(request);
        }

        public string GetQueueSummary()
        {
            var byCategory = _pending.GroupBy(r => r.Category)
                                      .Select(g => $"{g.Key}: {g.Count()}");
            return $"Queued {_pending.Count} | " + string.Join(", ", byCategory);
        }

        public IEnumerable<string> GetDetailedPending()
        {
            var now = DateTime.UtcNow;
            return _pending
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.EnqueuedAt)
                .Select(r => $"[{r.Origin}] {r.Category}/{r.Priority} waited {(now - r.EnqueuedAt).TotalSeconds:F1}s");
        }
    }
}
