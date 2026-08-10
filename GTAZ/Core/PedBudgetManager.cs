using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;

namespace GTAZ.Core
{
    public enum PedCategory
    {
        Zombie,
        FactionNpc,
        Survivor,
        Ambient
    }

    public enum PedPriority
    {
        Distant = 0,
        Background = 1,
        Active = 2,
        Critical = 3
    }

    public class PedSlot
    {
        public Ped Ped { get; internal set; }
        public PedCategory Category { get; internal set; }
        public PedPriority Priority { get; set; }
        public Vector3 LastKnownPosition { get; set; }
        public DateTime LastPriorityUpdate { get; set; }
        public bool IsEssential { get; set; }
    }

    public class PedBudgetManager
    {
        private static PedBudgetManager _instance;
        public static PedBudgetManager Instance => _instance ??= new PedBudgetManager();

        public int GlobalBudget { get; set; } = 120;

        private readonly Dictionary<PedCategory, int> _defaultCategoryMinimums = new()
        {
            { PedCategory.Survivor, 8 },
            { PedCategory.FactionNpc, 20 },
            { PedCategory.Zombie, 70 },
            { PedCategory.Ambient, 0 }
        };

        private readonly Dictionary<PedCategory, int> _categoryMinimums = new()
        {
            { PedCategory.Survivor, 8 },
            { PedCategory.FactionNpc, 20 },
            { PedCategory.Zombie, 70 },
            { PedCategory.Ambient, 0 }
        };

        private int _hordeOverrideRefs;

        // True while the player is inside/near a territory (or a territory fight is active),
        // set by FactionSystem each tick. The FactionNpc reserve only exists here: defenders and
        // waves are only ever requested by the player being in a territory, and hordes never
        // march through territory ground, so outside territory context a 20-slot faction reserve
        // would be pure dead weight on the zombie budget.
        private bool _territoryContext;

        public bool HordeOverrideActive => _hordeOverrideRefs > 0;

        private readonly List<PedSlot> _slots = [];
        private Vector3 _lastKnownPlayerPosition;
        private bool _havePlayerPosition;

        public float CriticalRadius { get; set; } = 15f;
        public float ActiveRadius { get; set; } = 60f;
        public float BackgroundRadius { get; set; } = 150f;
        public float RadiusHysteresis { get; set; } = 10f;

        public int CurrentCount => _slots.Count;
        public int Available => Math.Max(0, GlobalBudget - CurrentCount);

        public bool TryRequestSlot(PedCategory category, PedPriority priority, Vector3 position, out PedSlot slot)
        {
            slot = null;

            if (Available > 0)
            {
                slot = new PedSlot
                {
                    Category = category,
                    Priority = priority,
                    LastKnownPosition = position,
                    LastPriorityUpdate = DateTime.UtcNow
                };
                _slots.Add(slot);
                return true;
            }

            var evictionCandidate = FindEvictionCandidate(priority);
            if (evictionCandidate is null)
                return false;

            Evict(evictionCandidate);

            slot = new PedSlot
            {
                Category = category,
                Priority = priority,
                LastKnownPosition = position,
                LastPriorityUpdate = DateTime.UtcNow
            };
            _slots.Add(slot);
            return true;
        }

        public enum SpawnOutcome
        {
            Spawned,
            NoSlotAvailable,
            FactorySpawnFailed
        }

        public SpawnOutcome TryRequestAndSpawn(PedCategory category, PedPriority priority, Vector3 position,
            Func<Ped> spawnFactory, out PedSlot slot)
        {
            if (!TryRequestSlot(category, priority, position, out slot))
                return SpawnOutcome.NoSlotAvailable;

            Ped ped;
            try
            {
                ped = spawnFactory?.Invoke();
            }
            catch
            {
                ReleaseSlot(slot);
                slot = null;
                throw;
            }

            if (ped?.Exists() is not true)
            {
                ReleaseSlot(slot);
                slot = null;
                return SpawnOutcome.FactorySpawnFailed;
            }

            AttachPed(slot, ped);
            return SpawnOutcome.Spawned;
        }

        public void AttachPed(PedSlot slot, Ped ped)
        {
            slot.Ped = ped;
        }

        public void ReleaseSlot(PedSlot slot)
        {
            _slots.Remove(slot);
        }

        // Dynamic rebalancing for horde events and territory fights (see DOCS/06-Roadmap.md §3.5,
        // §4.13): while either is active, the reserved FactionNpc/Survivor minimums are borrowed
        // so those slots become eviction-eligible. The override is REF-COUNTED because hordes
        // and territory fights can overlap (a fight can start while a horde is live) - the
        // minimums only restore when the LAST holder releases. Every RebalanceForHorde(true)
        // must be paired with exactly one false, or the minimums stay borrowed forever.
        public void RebalanceForHorde(bool active)
        {
            if (active)
                _hordeOverrideRefs++;
            else
                _hordeOverrideRefs = Math.Max(0, _hordeOverrideRefs - 1);

            RefreshMinimums();
        }

        // Territory context for the budget (see _territoryContext). Call every tick so the
        // FactionNpc reserve appears/disappears with the player's proximity to a zone.
        public void SetTerritoryContext(bool active)
        {
            _territoryContext = active;
            RefreshMinimums();
        }

        private void RefreshMinimums()
        {
            bool borrowed = _hordeOverrideRefs > 0;

            // Horde/fight override borrows both minimums (faction peds become evictable so the
            // event can push peds). Otherwise the FactionNpc reserve only exists in territory
            // context - outside a territory there is nothing to reserve for (see _territoryContext).
            _categoryMinimums[PedCategory.FactionNpc] =
                borrowed ? 0 : (_territoryContext ? _defaultCategoryMinimums[PedCategory.FactionNpc] : 0);

            _categoryMinimums[PedCategory.Survivor] =
                borrowed ? 0 : _defaultCategoryMinimums[PedCategory.Survivor];
        }

        public void PeriodicRescore(Vector3 playerPosition)
        {
            _lastKnownPlayerPosition = playerPosition;
            _havePlayerPosition = true;

            for (int i = _slots.Count - 1; i >= 0; i--)
            {
                var slot = _slots[i];

                if (slot.Ped?.Exists() is not true || slot.Ped.IsDead)
                {
                    _slots.RemoveAt(i);
                    continue;
                }

                slot.LastKnownPosition = slot.Ped.Position;
                float dist = slot.LastKnownPosition.DistanceTo(playerPosition);
                slot.Priority = ScorePriority(dist, slot.Priority);
                slot.LastPriorityUpdate = DateTime.UtcNow;
            }
        }

        private PedPriority ScorePriority(float distance, PedPriority current)
        {
            float buffer = RadiusHysteresis;

            if (distance <= CriticalRadius) return PedPriority.Critical;
            if (distance <= ActiveRadius + (current == PedPriority.Active ? buffer : 0)) return PedPriority.Active;
            if (distance <= BackgroundRadius + (current == PedPriority.Background ? buffer : 0)) return PedPriority.Background;
            return PedPriority.Distant;
        }

        private PedSlot FindEvictionCandidate(PedPriority requestingPriority)
        {
            var query = _slots
                .Where(s => !s.IsEssential && (s.Priority < requestingPriority
                            || (s.Priority == requestingPriority && IsOverCategoryMinimum(s.Category))))
                .OrderBy(s => s.Priority)
                .ThenBy(s => IsOverCategoryMinimum(s.Category) ? 0 : 1);

            var candidates = TryGetPlayerPosition(out var playerPos)
                ? query.ThenByDescending(s => s.LastKnownPosition.DistanceTo(playerPos))
                : query.ThenBy(s => s.LastPriorityUpdate);

            return candidates.FirstOrDefault();
        }

        private bool TryGetPlayerPosition(out Vector3 position)
        {
            if (_havePlayerPosition)
            {
                position = _lastKnownPlayerPosition;
                return true;
            }

            try
            {
                var player = Game.Player?.Character;
                if (player?.Exists() is true)
                {
                    position = player.Position;
                    return true;
                }
            }
            catch
            {
                // Player may not be available during loading
            }

            position = Vector3.Zero;
            return false;
        }

        private bool IsOverCategoryMinimum(PedCategory category)
        {
            int currentInCategory = _slots.Count(s => s.Category == category);
            _categoryMinimums.TryGetValue(category, out var min);
            return currentInCategory > min;
        }

        private void Evict(PedSlot slot)
        {
            if (slot.Ped?.Exists() is true)
            {
                slot.Ped.MarkAsNoLongerNeeded();
            }
            _slots.Remove(slot);
        }

        public string GetBudgetSummary()
        {
            var byCategory = _slots.GroupBy(s => s.Category)
                                    .Select(g => $"{g.Key}: {g.Count()}");
            return $"Peds {CurrentCount}/{GlobalBudget} | " + string.Join(", ", byCategory);
        }
    }
}
