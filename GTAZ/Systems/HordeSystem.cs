using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.AI;
using GTAZ.AI.ZombieTypes;
using GTAZ.Core;
using GTAZ.Data;
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
        // Direction of travel. Wandering hordes sweep along it (sweep-path migration §3.1) and
        // the flowing march keeps advancing the same way; re-rolled when a territory blocks it.
        public float Heading { get; set; } = heading;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public int LifespanSeconds { get; } = lifespanSeconds;
        public List<ZombieAI> Members { get; } = [];
        public bool IsEngaged { get; set; }

        // Spawn requests enqueued but not yet materialized (grants stream in over several
        // queue passes, 3/pass). A horde is only DEPLETED once every request has resolved AND
        // no member remains - counting Members alone would instantly "deplete" a fresh horde
        // whose members are still in the queue (ZombieSystem.ProcessQueue runs before
        // HordeSystem.OnTick, so on the StartHorde frame the grants haven't landed yet), and
        // later grants would spawn as orphan zombies with no horde to control them.
        public int PendingSpawns { get; set; }

        // The Horder (horde center) member, if it has materialized yet. When alive it is the
        // march/blip reference point (§3.6); falls back to the computed centroid when null/dead.
        public HorderAI Horder { get; set; }

        // Red skull map blip (BlipSprite.TeamDeathmatch, ID 378) anchored on the Horder/centroid.
        public Blip Blip { get; set; }

        // Destination pushed to members last tick - HordeSystem only re-pushes (and members
        // only reissue their march task) when this changes, so a static target never churns.
        public Vector3? LastPushedDestination { get; set; }

        public bool IsDepleted => Members.Count == 0 && PendingSpawns == 0;
    }

    public class HordeSystem(ZombieSystem zombies)
    {
        private readonly ZombieSystem _zombies = zombies;
        private readonly PedBudgetManager _budget = PedBudgetManager.Instance;
        private readonly Random _rng = new();

        private readonly List<ActiveHorde> _activeHordes = [];

        // Flowing march cadence (§3.3): the destination is re-pushed to a point ahead of the
        // horde center every ~5s so the mass flows forward as one body instead of marching to a
        // fixed point, arriving, and waiting. Counts frames.
        private const int FlowPushInterval = 300;
        private const float FlowPushDistance = 140f;

        private DateTime? _nextWanderingAt;
        private DateTime? _nextNightAt;
        private bool _wasNight;
        private bool _nightHordeFired;
        private int _flowTimer;

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
                    if (member == horde.Horder)
                        horde.Horder = null;
                    horde.Members.RemoveAt(i);
                }
            }

            horde.IsEngaged = horde.Members.Any(m => m.State == ZombieState.Chasing || m.State == ZombieState.Attacking);

            // Members that have marched beyond the fade distance quietly despawn - the player
            // left them behind. Members MID-FIGHT are never faded: an engaged member is near the
            // player fighting, and removing it would both pop it mid-combat and drop the Horder
            // immunity's "other member alive" count while its horde is visibly still fighting
            // (the "ped killed the Horder before all of the horde" report). Hordes also never
            // trample territory WHILE MARCHING: members that drift inside any owned or faction
            // zone fade out, but an active fight inside a ring is left alone (territories are
            // event-driven ground). Fading (MarkAsNoLongerNeeded) rather than Delete avoids pop-in.
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;
            for (int i = horde.Members.Count - 1; i >= 0; i--)
            {
                var member = horde.Members[i];
                if (member.Ped?.Exists() is true && !member.Ped.IsDead
                    && member.State != ZombieState.Chasing && member.State != ZombieState.Attacking
                    && ((member.Ped.Position - playerPos).Length() > config.HordeDespawnDistance
                        || FactionSystem.Instance.IsInAnyTerritory(member.Ped.Position)))
                {
                    member.FadeOut();
                    if (member == horde.Horder)
                        horde.Horder = null;
                    horde.Members.RemoveAt(i);
                }
            }

            UpdateHordeBlip(horde);

            // Cadence-based destination push (§3.3): every ~5s re-point the march target, so
            // no member ever "arrives" and waits - the mass flows forward as one body. The push
            // only fires on this cadence so engaged surge / night retarget don't spam
            // LastPushedDestination churn.
            _flowTimer--;
            if (_flowTimer <= 0)
            {
                _flowTimer = FlowPushInterval;

                if (horde.IsEngaged)
                {
                    // Engagement cascade (§3.4): re-point the march at the player so the whole
                    // horde converges on the fight, not just the members that perceive it.
                    // Distance-gated so a horde across the map doesn't all start marching at
                    // the player from off-map, and stopped at the territory edge when the
                    // player stands inside a zone (hordes never trample territory).
                    if (SpawnUtils.HorizontalDistance(playerPos, GetHordeReference(horde)) <= config.HordeEngageDistance)
                        horde.Destination = ResolveSurgeTarget(playerPos);
                }
                else if (horde.Kind == HordeKind.Night)
                {
                    // Night sieges hunt the player.
                    horde.Destination = playerPos;
                }
                else
                {
                    AdvanceFlowingMarch(horde);
                }
            }

            PushDestination(horde);
            ApplyGroupingPullBack(horde);
            UpdateHorderImmunity(horde);
        }

        // The horde's center point: the Horder when alive (it leads, the horde follows it),
        // else the centroid of the alive members, else the original spawn center (§3.6).
        private Vector3 GetHordeReference(ActiveHorde horde)
        {
            if (horde.Horder?.Ped?.Exists() is true && !horde.Horder.Ped.IsDead)
                return horde.Horder.Ped.Position;

            var alive = horde.Members.Where(m => m.Ped?.Exists() is true && !m.Ped.IsDead).ToList();
            if (alive.Count == 0)
                return horde.SpawnCenter;

            var centroid = Vector3.Zero;
            foreach (var m in alive)
                centroid += m.Ped.Position;
            return centroid / alive.Count;
        }

        // Flowing march (§3.3): advance the destination to a point ~140m ahead of the horde
        // center along its heading, so the group keeps sweeping one direction across the map
        // instead of marching to a point, arriving, and waiting. Territory avoidance: if the
        // heading would cross a territory, re-roll it; if every re-roll is blocked, hold
        // position and retry on the next cadence.
        private void AdvanceFlowingMarch(ActiveHorde horde)
        {
            var origin = GetHordeReference(horde);
            var dir = new Vector3((float)Math.Cos(horde.Heading), (float)Math.Sin(horde.Heading), 0f);
            var next = origin + (dir * FlowPushDistance);

            for (int attempt = 0; attempt < 6 && FactionSystem.Instance.IsInAnyTerritory(next); attempt++)
            {
                horde.Heading = (float)(_rng.NextDouble() * Math.PI * 2);
                dir = new Vector3((float)Math.Cos(horde.Heading), (float)Math.Sin(horde.Heading), 0f);
                next = origin + (dir * FlowPushDistance);
            }

            if (FactionSystem.Instance.IsInAnyTerritory(next))
                return;

            horde.Destination = next;
        }

        // Surge target (§3.4): the player's position when they're outside any territory,
        // otherwise the nearest point just outside the territory ring - the horde stops at the
        // edge and the player has to lure it back out (hordes never trample territory).
        private Vector3 ResolveSurgeTarget(Vector3 playerPos)
        {
            if (!FactionSystem.Instance.IsInAnyTerritory(playerPos))
                return playerPos;

            TerritoryZone nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var zone in FactionSystem.Instance.Zones)
            {
                float d = SpawnUtils.HorizontalDistance(zone.Center, playerPos);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = zone;
                }
            }

            if (nearest == null)
                return playerPos;

            var dir = playerPos - nearest.Center;
            dir.Z = 0f;
            if (dir.Length() < 1f)
                dir = new Vector3(1f, 0f, 0f);
            else
                dir.Normalize();

            return nearest.Center + (dir * (ModConfig.Current.TerritoryRadius + 8f));
        }

        // Red skull map blip (BlipSprite.TeamDeathmatch, ID 378) per active horde, anchored on
        // the Horder/centroid and re-checked every tick so toggling ShowHordeBlips applies
        // immediately. Visible from the moment the horde is announced (§3.2).
        private void UpdateHordeBlip(ActiveHorde horde)
        {
            if (!ModConfig.Current.ShowHordeBlips || horde.IsDepleted)
            {
                if (horde.Blip?.Exists() is true)
                {
                    horde.Blip.Delete();
                    horde.Blip = null;
                }
                return;
            }

            if (horde.Blip?.Exists() is not true)
            {
                horde.Blip = World.CreateBlip(GetHordeReference(horde));
                horde.Blip.Sprite = BlipSprite.TeamDeathmatch;
                horde.Blip.Color = BlipColor.Red;
                horde.Blip.Scale = 1f;
                horde.Blip.IsShortRange = true;
                horde.Blip.Name = "Horde";
            }
            else
            {
                horde.Blip.Position = GetHordeReference(horde);
            }
        }

        // Grouping pull-back (§3.3): a member that drifted far from the horde center (> 25m) is
        // reeled back toward it, so the group never stretches into a line. Once back within
        // range it resumes marching to the shared flowing destination. Skipped while engaged -
        // the surge already points everyone at the fight.
        //
        // Hysteresis: the release threshold (15m) sits well inside the trigger (25m) and a
        // pulled member is tracked by HordePullingBack. Without both, a member hovering on the
        // 25m boundary flipped HordeMoveTarget between center and destination every tick, which
        // re-issued the march task every behavior tick - the "stragglers step one way, stop,
        // step the other way, repeat" stutter. While pulling, the reel target is only re-pointed
        // when the flowing center has moved far enough that the old point would mislead.
        private const float GroupingPullBackDistance = 25f;
        private const float GroupingPullBackRelease = 15f;
        private const float GroupingPullBackRepointDistance = 5f;

        private void ApplyGroupingPullBack(ActiveHorde horde)
        {
            if (horde.IsEngaged || horde.Members.Count < 2)
                return;

            var center = GetHordeReference(horde);
            foreach (var member in horde.Members)
            {
                if (member.Ped?.Exists() is not true || member.Ped.IsDead)
                    continue;
                if (member.State == ZombieState.Chasing || member.State == ZombieState.Attacking)
                    continue;

                float dist = SpawnUtils.HorizontalDistance(center, member.Ped.Position);
                if (member.HordePullingBack)
                {
                    if (dist <= GroupingPullBackRelease)
                    {
                        // Reeled back in - release to the shared march destination.
                        member.HordePullingBack = false;
                        member.HordeMoveTarget = horde.Destination;
                    }
                    else if (member.HordeMoveTarget is Vector3 pullTarget
                        && (pullTarget - center).Length() > GroupingPullBackRepointDistance)
                    {
                        // The horde center flows forward while the straggler reels - chase a
                        // live point. Gated on >5m so a slowly-crawling center doesn't churn
                        // the march task (it moves only ~0.1m per behavior tick).
                        member.HordeMoveTarget = center;
                    }
                }
                else if (dist > GroupingPullBackDistance)
                {
                    member.HordePullingBack = true;
                    member.HordeMoveTarget = center;
                }
                else if (member.HordeMoveTarget != horde.Destination)
                {
                    member.HordeMoveTarget = horde.Destination;
                }
            }
        }

        // Horder immunity (§3.6): the Horder takes no damage while any OTHER member of its
        // horde is alive (itself excluded - once the horde is cleared it must be killable).
        // Also immune while the horde is still materializing (members stream in over ~17 queue
        // passes; without this the Horder could be sniped in the brief window before the first
        // member lands). The 30s bound keeps a failed member position from locking the Horder
        // immune forever. Set here after the member loops; HorderAI.Update re-asserts the
        // proofs from this flag every tick (lesson 35).
        private void UpdateHorderImmunity(ActiveHorde horde)
        {
            if (horde.Horder?.Ped?.Exists() is not true || horde.Horder.Ped.IsDead)
            {
                horde.Horder = null;
                return;
            }

            bool otherAlive = horde.Members.Any(m => m != horde.Horder
                && m.Ped?.Exists() is true && !m.Ped.IsDead);
            bool stillForming = horde.Members.Count < horde.TargetSize
                && (DateTime.UtcNow - horde.StartedAt).TotalSeconds < 30;

            horde.Horder.HordeAlive = otherAlive || stillForming;
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
            if (horde.Blip?.Exists() is true)
            {
                horde.Blip.Delete();
                horde.Blip = null;
            }
            if (horde.Horder is { } horder)
                horder.HordeAlive = false;
            foreach (var member in horde.Members)
                member.FadeOut();
            horde.Members.Clear();
            horde.Horder = null;
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

            // Sweep-path migration (§3.1): the wandering horde's destination goes on the FAR
            // side of the player (opposite the spawn center, HordeSpawnDistanceMax + 200m out)
            // so the march path sweeps ACROSS the player's area instead of a random heading
            // that can walk away (the never-encounter problem). Heading = direction of travel,
            // stored so the flowing march keeps advancing the same way. Night sieges just hunt
            // the player.
            float heading;
            Vector3 destination;
            if (kind == HordeKind.Night)
            {
                heading = (float)(_rng.NextDouble() * Math.PI * 2);
                destination = playerPos;
            }
            else
            {
                var travel = playerPos - spawnCenter;
                travel.Z = 0f;
                if (travel.Length() < 5f)
                    travel = new Vector3(1f, 0f, 0f);
                travel.Normalize();

                // Jittered so the sweep doesn't dead-center the player every time.
                float reach = config.HordeSpawnDistanceMax + 200f;
                float baseAngle = (float)Math.Atan2(travel.Y, travel.X);
                heading = baseAngle + ((float)(_rng.NextDouble() - 0.5f) * 0.9f);
                destination = playerPos + (new Vector3((float)Math.Cos(heading), (float)Math.Sin(heading), 0f) * reach);

                for (int i = 0; i < 6 && FactionSystem.Instance.IsInAnyTerritory(destination); i++)
                {
                    heading = baseAngle + (float)((_rng.NextDouble() - 0.5f) * 0.9f);
                    destination = playerPos + (new Vector3((float)Math.Cos(heading), (float)Math.Sin(heading), 0f) * reach);
                }
            }

            var horde = new ActiveHorde(kind, spawnCenter, destination, size, heading,
                lifespanSeconds: kind == HordeKind.Night ? 1200 : 600);

            Announce(kind, spawnCenter);

            _budget.RebalanceForHorde(true);

            _activeHordes.Add(horde);

            // Materialize via SpawnRequestQueue (3 grants/pass) so a 50-strong night horde
            // streams in over ~17 passes instead of spiking a frame.
            string origin = kind == HordeKind.Night ? "HordeNight" : "HordeWander";

            // One Horder per horde (g_m_m_zombie_02, §3.6): the visual/behavioral center the
            // horde forms around. Spawned at/near the spawn center so the rest materialize
            // around it; it counts as one of the `size` members (no extra budget). While alive
            // it is the march/blip reference; members that drift >25m are reeled toward it.
            var horderPos = SpawnUtils.FindSafePosition(spawnCenter, 3f, 10f);
            bool horderQueued = horderPos != Vector3.Zero;
            if (horderQueued)
            {
                var capturedHorderPos = horderPos;
                horde.PendingSpawns++;
                _zombies.EnqueueZombieSpawn(capturedHorderPos, false, origin,
                    zombie =>
                    {
                        horde.Horder = zombie as HorderAI;
                        zombie.HordeMoveTarget = destination;
                        horde.LastPushedDestination = destination;
                        horde.Members.Add(zombie);
                        horde.PendingSpawns--;
                    }, horder: true, onExpired: () => horde.PendingSpawns--);
            }

            int memberCount = size - (horderQueued ? 1 : 0);
            for (int i = 0; i < memberCount; i++)
            {
                // Tight cluster (4-12m): members materialize as a pack around the spawn center,
                // not strung out across a 25m radius - a wide radius in a built-up block put
                // members on separate rooftops/behind buildings, hard to find all of the horde.
                var memberPos = SpawnUtils.FindSafePosition(spawnCenter, 4f, 12f);
                if (memberPos == Vector3.Zero)
                    continue;

                bool isRunner = _rng.NextDouble() < config.HordeRunnerChance;
                var capturedPos = memberPos;
                bool capturedRunner = isRunner;

                // Materialize via ZombieSystem.EnqueueZombieSpawn so horde members use the same
                // model-pick + deferred SpawnRequestQueue path as ambient spawns (3 grants/pass so
                // a 50-strong night horde streams in over ~17 passes instead of spiking a frame).
                // Members are filed in horde.Members (not ZombieSystem._activeZombies) so
                // CleanupDistantZombies never despawns them. PendingSpawns tracks requests that
                // haven't granted yet - without it a horde whose members are still queued reads as
                // depleted on its first tick and its later-granted members orphan (the "20 zombies
                // over ambient, no horde" report).
                horde.PendingSpawns++;
                _zombies.EnqueueZombieSpawn(capturedPos, capturedRunner, origin,
                    zombie =>
                    {
                        zombie.HordeMoveTarget = destination;
                        horde.LastPushedDestination = destination;
                        horde.Members.Add(zombie);
                        horde.PendingSpawns--;
                    }, onExpired: () => horde.PendingSpawns--);
            }

            Logger.Info($"Horde started: {kind} (target {size}) at {spawnCenter} heading {heading}");
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
