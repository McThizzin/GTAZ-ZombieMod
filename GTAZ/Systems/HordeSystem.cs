using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.AI;
using GTAZ.AI.ZombieTypes;
using GTAZ.Core;
using GTAZ.Events;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    public enum HordeKind
    {
        Wandering,
        Night
    }

    public class ActiveHorde(HordeKind kind, Vector3 spawnCenter, Vector3 destination,
        int targetSize, float heading, int lifespanSeconds)
    {
        public HordeKind Kind { get; } = kind;
        public Vector3 SpawnCenter { get; } = spawnCenter;
        public Vector3 Destination { get; set; } = destination;
        public int TargetSize { get; } = targetSize;
        public float Heading { get; } = heading;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public int LifespanSeconds { get; } = lifespanSeconds;
        public List<ZombieAI> Members { get; } = [];
        public bool IsEngaged { get; set; }

        // Destination pushed to members last tick - HordeSystem only re-pushes (and members
        // only reissue their march task) when this changes, so a static target never churns.
        public Vector3? LastPushedDestination { get; set; }

        public bool IsDepleted => Members.Count == 0;
    }

    public class HordeSystem(SurvivorSystem survivors, ZombieSystem zombies)
    {
        private readonly SurvivorSystem _survivors = survivors;
        private readonly ZombieSystem _zombies = zombies;
        private readonly PedBudgetManager _budget = PedBudgetManager.Instance;
        private readonly Random _rng = new();

        private readonly List<ActiveHorde> _activeHordes = [];

        private DateTime? _nextWanderingAt;
        private DateTime? _nextNightAt;
        private bool _wasNight;
        private bool _nightHordeFired;
        private int _retargetTimer;

        public int ActiveCount => _activeHordes.Count;
        public int TotalAlive => _activeHordes.Sum(h => h.Members.Count);
        public int TotalTarget => _activeHordes.Sum(h => h.TargetSize);
        private bool IsHordeActive => _activeHordes.Count > 0;

        public void OnTick()
        {
            // Same guard as ZombieSystem.OnTick - don't touch peds while the player is dead or
            // the game doesn't have them under control (mid-respawn, wasted screen, cutscenes).
            if (Game.Player.Character.IsDead || !Game.Player.CanControlCharacter)
                return;

            UpdateScheduler();
            UpdateActiveHordes();
        }

        private void UpdateScheduler()
        {
            var config = ModConfig.Current;
            if (!config.EnableHordeEvents)
                return;

            // Wandering horde on a real-time interval with jitter. Gated on no horde being
            // active (wandering and night hordes never stack) and on the budget not being
            // borrowed by a territory fight (FactionSystem sets HordeOverrideActive).
            _nextWanderingAt ??= DateTime.UtcNow.AddMinutes(config.HordeEventIntervalMinutes);

            if (!IsHordeActive && !_budget.HordeOverrideActive && DateTime.UtcNow >= _nextWanderingAt.Value)
            {
                StartHorde(HordeKind.Wandering);
                _nextWanderingAt = DateTime.UtcNow.AddMinutes(config.HordeEventIntervalMinutes * (0.7 + (_rng.NextDouble() * 0.6)));
            }

            // One night horde per night, rolled on entering the night window (20:00-05:00).
            int hour = GTA.Chrono.GameClock.Hour;
            bool isNight = hour >= 20 || hour <= 5;

            if (isNight && !_wasNight)
            {
                _wasNight = true;
                _nightHordeFired = false;
                if (_rng.NextDouble() < config.HordeNightChance)
                    _nextNightAt = DateTime.UtcNow.AddMinutes(3 + (_rng.NextDouble() * 12));
            }
            else if (!isNight && _wasNight)
            {
                _wasNight = false;
                _nextNightAt = null;
            }

            if (!_nightHordeFired && !IsHordeActive && !_budget.HordeOverrideActive
                && _nextNightAt is DateTime scheduled && DateTime.UtcNow >= scheduled)
            {
                StartHorde(HordeKind.Night);
                _nightHordeFired = true;
                _nextNightAt = null;
            }
        }

        private void UpdateActiveHordes()
        {
            if (_activeHordes.Count == 0)
                return;

            _retargetTimer--;

            for (int i = _activeHordes.Count - 1; i >= 0; i--)
            {
                var horde = _activeHordes[i];
                UpdateHorde(horde);

                if (horde.IsDepleted || IsExpired(horde))
                {
                    DespawnHorde(horde);
                    _activeHordes.RemoveAt(i);
                }
            }

            if (_activeHordes.Count == 0)
            {
                _budget.RebalanceForHorde(false);
            }
        }

        private void UpdateHorde(ActiveHorde horde)
        {
            // Tick members exactly like ZombieSystem does; remove the dead.
            for (int i = horde.Members.Count - 1; i >= 0; i--)
            {
                var member = horde.Members[i];
                member.Update();

                if (member.State == ZombieState.Dead)
                {
                    // Only run death cleanup if the ped still exists (it may have been
                    // evicted/despawned by budget pressure, in which case there's nothing
                    // to reset). A vanished ped with a stale slot is freed by PeriodicRescore.
                    if (member.Ped?.Exists() is true)
                        member.OnDeath();
                    horde.Members.RemoveAt(i);
                }
            }

            horde.IsEngaged = horde.Members.Any(m => m.State == ZombieState.Chasing || m.State == ZombieState.Attacking);

            // Members that have marched beyond the fade distance quietly despawn - the player
            // left them behind. Engaged members are chasing near the player, so they're never
            // past this threshold. Hordes also never trample territory: members that drift inside
            // any owned or faction zone fade out (territories are event-driven ground). Fading
            // (MarkAsNoLongerNeeded) rather than Delete avoids pop-in.
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;
            for (int i = horde.Members.Count - 1; i >= 0; i--)
            {
                var member = horde.Members[i];
                if (member.Ped?.Exists() is true && !member.Ped.IsDead
                    && ((member.Ped.Position - playerPos).Length() > config.HordeDespawnDistance
                        || FactionSystem.Instance.IsInAnyTerritory(member.Ped.Position)))
                {
                    member.FadeOut();
                    horde.Members.RemoveAt(i);
                }
            }

            // Advance the destination: night hordes hunt the player, wandering hordes migrate.
            if (horde.Kind == HordeKind.Night)
            {
                if (_retargetTimer <= 0)
                {
                    horde.Destination = Game.Player.Character.Position;
                    _retargetTimer = 120;
                }
            }
            else if (CentroidReached(horde))
            {
                AdvanceWanderingDestination(horde);
            }

            PushDestination(horde);
        }

        private bool CentroidReached(ActiveHorde horde)
        {
            var alive = horde.Members.Where(m => m.Ped?.Exists() is true && !m.Ped.IsDead).ToList();
            if (alive.Count == 0)
                return false;

            var centroid = Vector3.Zero;
            foreach (var m in alive)
                centroid += m.Ped.Position;
            centroid /= alive.Count;

            return (centroid - horde.Destination).Length() < 25f;
        }

        private void AdvanceWanderingDestination(ActiveHorde horde)
        {
            // Migrate onward: prefer a fresh safe navmesh point a few hundred meters further
            // (never inside ANY territory - hordes stay off owned and faction ground), fall back
            // to marching straight along the horde's heading.
            var safe = SpawnUtils.FindSafePosition(horde.Destination, 250f, 350f);
            for (int i = 0; i < 4 && safe != Vector3.Zero && FactionSystem.Instance.IsInAnyTerritory(safe); i++)
                safe = SpawnUtils.FindSafePosition(horde.Destination, 250f, 350f);

            if (safe != Vector3.Zero && !FactionSystem.Instance.IsInAnyTerritory(safe))
            {
                horde.Destination = safe;
                return;
            }

            var fallback = horde.Destination + (new Vector3((float)Math.Cos(horde.Heading), (float)Math.Sin(horde.Heading), 0f) * 300f);
            horde.Destination = FactionSystem.Instance.IsInAnyTerritory(fallback)
                ? horde.SpawnCenter
                : fallback;
        }

        private void PushDestination(ActiveHorde horde)
        {
            if (horde.Destination == horde.LastPushedDestination)
                return;

            horde.LastPushedDestination = horde.Destination;
            foreach (var member in horde.Members)
            {
                if (member.Ped?.Exists() is true)
                    member.HordeMoveTarget = horde.Destination;
            }
        }

        private bool IsExpired(ActiveHorde horde)
        {
            if (horde.Kind == HordeKind.Night)
            {
                int hour = GTA.Chrono.GameClock.Hour;
                if (hour >= 6 && hour < 20)
                    return true; // daybreak ends the siege
            }

            return (DateTime.UtcNow - horde.StartedAt).TotalSeconds > horde.LifespanSeconds;
        }

        private void DespawnHorde(ActiveHorde horde)
        {
            foreach (var member in horde.Members)
                member.FadeOut();
            horde.Members.Clear();
        }

        public bool StartHorde(HordeKind kind)
        {
            var config = ModConfig.Current;
            if (!config.EnableHordeEvents)
                return false;

            // Never start a horde while a territory fight is borrowing the budget - the fight
            // and the horde would both push peds under the same HordeOverride, stacking
            // pressure on the global cap.
            if (_budget.HordeOverrideActive && !IsHordeActive)
                return false;

            var player = Game.Player.Character;
            if (player?.Exists() is not true || player.IsDead || !Game.Player.CanControlCharacter)
                return false;

            int size = MathUtils.Clamp(kind == HordeKind.Night ? config.HordeNightSize : config.WanderingHordeSize,
                1, config.MaxHordeSize);

            var playerPos = player.Position;
            var spawnCenter = SpawnUtils.FindSafePosition(playerPos, config.HordeSpawnDistanceMin, config.HordeSpawnDistanceMax);
            if (spawnCenter == Vector3.Zero)
                return false;

            // Hordes never stage inside ANY territory (owned or faction) - territories are
            // event-driven ground, so a horde has to form up somewhere neutral.
            for (int i = 0; i < 6 && FactionSystem.Instance.IsInAnyTerritory(spawnCenter); i++)
                spawnCenter = SpawnUtils.FindSafePosition(playerPos, config.HordeSpawnDistanceMin, config.HordeSpawnDistanceMax);
            if (FactionSystem.Instance.IsInAnyTerritory(spawnCenter))
                return false;

            float heading = (float)(_rng.NextDouble() * Math.PI * 2);
            var destination = spawnCenter + (new Vector3((float)Math.Cos(heading), (float)Math.Sin(heading), 0f) * 300f);

            var horde = new ActiveHorde(kind, spawnCenter, destination, size, heading,
                lifespanSeconds: kind == HordeKind.Night ? 1200 : 600);

            Announce(kind, spawnCenter);

            _budget.RebalanceForHorde(true);

            _activeHordes.Add(horde);

            // Materialize via SpawnRequestQueue (3 grants/pass) so a 50-strong night horde
            // streams in over ~17 passes instead of spiking a frame.
            for (int i = 0; i < size; i++)
            {
                var memberPos = SpawnUtils.FindSafePosition(spawnCenter, 5f, 25f);
                if (memberPos == Vector3.Zero)
                    continue;

                bool isRunner = _rng.NextDouble() < config.HordeRunnerChance;
                var capturedPos = memberPos;
                bool capturedRunner = isRunner;
                string origin = kind == HordeKind.Night ? "HordeNight" : "HordeWander";

                // Materialize via ZombieSystem.EnqueueZombieSpawn so horde members use the same
                // model-pick + deferred SpawnRequestQueue path as ambient spawns (3 grants/pass so
                // a 50-strong night horde streams in over ~17 passes instead of spiking a frame).
                // Members are filed in horde.Members (not ZombieSystem._activeZombies) so
                // CleanupDistantZombies never despawns them.
                _zombies.EnqueueZombieSpawn(capturedPos, capturedRunner, origin,
                    zombie =>
                    {
                        zombie.HordeMoveTarget = destination;
                        horde.LastPushedDestination = destination;
                        horde.Members.Add(zombie);
                    });
            }

            Logger.Info($"Horde started: {kind} (target {size}) at {spawnCenter}");
            return true;
        }

        private void Announce(HordeKind kind, Vector3 position)
        {
            string message = kind == HordeKind.Night
                ? "~r~The dead are converging on your position..."
                : "~o~A horde is moving through the area...";
            GTA.UI.Notification.PostTicker(message, false);

            // Distant atmospheric cue: distant gunfire read as chaos from the horde's
            // direction plus a gameplay-cam shake. If the sound name/set doesn't exist in
            // vanilla this silently no-ops - the ticker above still announces.
            Function.Call(Hash.PLAY_SOUND_FROM_COORD, 0, "Distant_Gun_Fire",
                position.X, position.Y, position.Z, "Distant_Gun_Fire_Events", false, 0, false);
            Function.Call(Hash.SHAKE_GAMEPLAY_CAM, "SMALL_EXPLOSION_SHAKE", 0.6f);

            // The gunfire comes from somewhere real: drop armed survivors at the source so the
            // horde materializing there has people to attack (and who shoot back).
            _survivors.SpawnAt(position, 2);
        }

        public void ClearAllHordes()
        {
            foreach (var horde in _activeHordes)
                DespawnHorde(horde);
            _activeHordes.Clear();

            _budget.RebalanceForHorde(false);
        }

        public string GetSummary()
        {
            if (_activeHordes.Count == 0)
                return "Hordes: none active";
            return "Hordes: " + string.Join(", ",
                _activeHordes.Select(h => $"{h.Kind} {h.Members.Count}/{h.TargetSize}"));
        }

        public void OnAborted()
        {
            ClearAllHordes();
        }
    }
}
