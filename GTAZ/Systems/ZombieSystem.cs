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
using GTAZ.Extensions;
using GTAZ.Utils;


namespace GTAZ.Systems
{
    public class ZombieSystem
    {
        private readonly List<ZombieAI> _activeZombies = [];
        private readonly PedBudgetManager _budget = PedBudgetManager.Instance;
        private readonly SpawnRequestQueue _queue = SpawnRequestQueue.Instance;

        public int TargetZombieCount { get; set; } = 80;
        public float SpawnRadius { get; set; } = 200f;
        public float DespawnRadius { get; set; } = 300f;
        public float MinSpawnDistance { get; set; } = 80f;
        public bool NightRunnersEnabled { get; set; } = true;
        public float NightRunnerChance { get; set; } = 0.4f;

        // Shared with HordeSystem so horde spawns use the exact same model pool as ambient.
        public static readonly int[] WalkerModels =
        [
            (int)PedHash.Tramp01AMM,
            (int)PedHash.Tramp01AFM,
            (int)PedHash.Skidrow01AMM,
            unchecked((int)PedHash.Skidrow01AFM),
            (int)PedHash.Methhead01AMY,
            (int)PedHash.Hillbilly01AMM,
            (int)PedHash.Hillbilly02AMM,
            (int)PedHash.TrampBeac01AMM,
            unchecked((int)PedHash.StrPunk01GMY),
            unchecked((int)PedHash.Business01AMY),
            (int)PedHash.Hippy01AMY,
            unchecked((int)PedHash.Tranvest02AMM),
            (int)PedHash.DeadHooker,
            unchecked((int)0xC61B7851), // g_m_m_zombie_01
        ];

        public static readonly int[] RunnerModels =
        [
            unchecked((int)PedHash.Zombie01),
            unchecked((int)PedHash.Juggalo01AMY),
            unchecked((int)0xBE5F26D7), // ig_zombie_dj_01
            unchecked((int)0x296EBEF2), // g_m_m_zombie_04
            unchecked((int)0xFAB86186), // g_m_m_zombie_05
        ];

        // The Horder (horde center) model - g_m_m_zombie_02 is NOT in the SHVDN3 PedHash enum.
        // Deliberately removed from RunnerModels above so it never appears as an ambient runner:
        // HordeSystem is the only spawner, exactly one per horde.
        public const int HorderModel = unchecked((int)0x98549CC4);

#pragma warning disable IDE0044, RCS1169 // _spawnTimer is mutated in TryAmbientSpawn
        private int _spawnTimer;
#pragma warning restore IDE0044, RCS1169
        private readonly int _spawnInterval = 40;
        private readonly Random _rng = new();

        public int ActiveCount => _activeZombies.Count;

        public ZombieSystem()
        {
        }

        public void OnTick()
        {
            // Don't touch peds while the player is dead or the game doesn't have them under
            // control (mid-respawn, wasted screen, cutscenes, etc). CREATE_PED, CLONE_PED, and
            // MarkAsNoLongerNeeded calls firing during that transition are a well-known source
            // of instability - the engine is doing its own internal fixup during that window,
            // and every system here (ambient spawning, corpse reanimation, distant cleanup,
            // budget rescoring) was still running through it every single frame.
            if (Game.Player.Character.IsDead || !Game.Player.CanControlCharacter)
                return;

            PedBudgetManager.Instance.PeriodicRescore(Game.Player.Character.Position);
            UpdateActiveZombies();
            ProcessQueue();
            TryAmbientSpawn();
            CleanupDistantZombies();
        }

        private void UpdateActiveZombies()
        {
            for (int i = _activeZombies.Count - 1; i >= 0; i--)
            {
                var z = _activeZombies[i];
                z.Update();

                if (z.State == ZombieState.Dead)
                {
                    z.OnDeath();
                    _activeZombies.RemoveAt(i);
                }
            }
        }

        private void ProcessQueue()
        {
            // No ambient-count guard here: this processes the SHARED queue (ambient + horde
            // SpawnRequests). Ambient enqueue is already throttled against TargetZombieCount in
            // TryAmbientSpawn; gating the whole queue on the ambient count would stall horde
            // requests whenever ambient is full (the horde-never-materializes bug).
            _queue.ProcessQueue();
        }

        private void TryAmbientSpawn()
        {
            _spawnTimer++;
            if (_spawnTimer < _spawnInterval)
                return;

            _spawnTimer = 0;

            // Faction-held (hostile) territory caps ambient zombie density at an in-territory
            // limit instead of the global TargetZombieCount: a probability gate (round-6)
            // still filled the ring to 80 eventually, just slower. While the player stands in
            // a hostile ring, ambient requests stop once that many zombies are already inside
            // the ring and resume when the count drops back under - the garrison fights a
            // bounded stream instead of being swarmed (a 10-defender wave died to zombies in
            // ~50s, so capture took 1 kill). Owned zones stay fully suppressed (safe territory)
            // via the IsPlayerOwnedZone check inside the loop.
            var playerPos = Game.Player.Character.Position;
            var hostileZone = ModConfig.Current.EnableFactions
                ? FactionSystem.Instance.HostileTerritoryAt(playerPos)
                : null;

            if (hostileZone != null
                && CountZombiesInRing(hostileZone) >= ModConfig.Current.HostileTerritoryZombieCap)
            {
                return;
            }

            int batch = _rng.Next(2, 5);
            for (int i = 0; i < batch; i++)
            {
                if (_activeZombies.Count >= TargetZombieCount)
                    return;

                if (_budget.Available <= 0)
                    return;

                var spawnPos = FindSpawnPosition(playerPos);
                if (spawnPos == Vector3.Zero)
                    continue;

                // Player-owned zones (Phase 4) are safe territory: suppress ambient zombie
                // spawns inside them. Hordes are unaffected - they enqueue their own
                // SpawnRequests and never route through this ambient path.
                if (FactionSystem.Instance.IsPlayerOwnedZone(spawnPos))
                    continue;

                int hour = GTA.Chrono.GameClock.Hour;
                bool isNight = hour >= 20 || hour <= 5;
                bool spawnRunner = isNight && NightRunnersEnabled && _rng.NextDouble() < NightRunnerChance;

                EnqueueZombieSpawn(spawnPos, spawnRunner, "Ambient",
                    zombie => _activeZombies.Add(zombie));
            }
        }

        // Zombies from this system's active list (ambient + debug spawns - hordes live in
        // HordeSystem's own list and never route through ambient anyway) currently inside the
        // territory ring, used to gate ambient spawns against the in-territory cap.
        private int CountZombiesInRing(TerritoryZone zone)
        {
            float radius = ModConfig.Current.TerritoryRadius;
            int count = 0;
            foreach (var zombie in _activeZombies)
            {
                if (zombie?.Ped?.Exists() is not true)
                    continue;
                if (SpawnUtils.HorizontalDistance(zone.Center, zombie.Ped.Position) <= radius)
                    count++;
            }
            return count;
        }

        public void SpawnZombieAt(Vector3 position, bool runner = false)
        {
            if (Game.Player.Character.IsDead || !Game.Player.CanControlCharacter)
                return;

            if (_budget.Available <= 0)
                return;

            EnqueueZombieSpawn(position, runner, "Debug",
                zombie => _activeZombies.Add(zombie));
        }

        // Shared zombie-materialization path for every spawner (ambient, debug, hordes). Picks a
        // model from the runner/walker pool (or the Horder model for a horde center), defers the
        // actual ped creation through the SpawnRequestQueue (3 grants/pass so bursts stream in over
        // several frames), and hands the finished ZombieAI to onSpawned so each caller files it in
        // its own list (ambient's _activeZombies vs HordeSystem's horde.Members).
        // RaiseZombieSpawned fires after the callback so listeners always find the new zombie
        // already registered.
        public void EnqueueZombieSpawn(Vector3 position, bool runner, string origin,
            Action<ZombieAI> onSpawned, bool horder = false, Action onExpired = null)
        {
            var request = new SpawnRequest
            {
                Category = PedCategory.Zombie,
                Priority = PedPriority.Active,
                Position = position,
                Origin = origin,
                SpawnFactory = () =>
                {
                    var models = runner ? RunnerModels : WalkerModels;
                    int modelHash = horder ? HorderModel : models[_rng.Next(models.Length)];
                    var model = new Model(modelHash);
                    model.Request();
                    return World.CreatePed(model, position, _rng.Next(0, 360));
                },
                OnGranted = slot =>
                {
                    var ped = slot.Ped;
                    if (ped?.Exists() is not true)
                        return;

                    ZombieAI zombie = horder
                        ? new HorderAI(ped, position)
                        : runner ? new RunnerAI(ped, position) : new WalkerAI(ped, position);
                    onSpawned?.Invoke(zombie);
                    ZombieEvents.RaiseZombieSpawned(ped);
                },
                OnExpired = onExpired
            };

            _queue.Enqueue(request);
        }

        public void KillAllZombies()
        {
            foreach (var z in _activeZombies)
            {
                if (z.Ped?.Exists() is true && !z.Ped.IsDead)
                    Function.Call(Hash.APPLY_DAMAGE_TO_PED, z.Ped, 1000f, true);
            }
        }

        public void ClearAllZombies()
        {
            foreach (var z in _activeZombies)
                z.Cleanup();
            _activeZombies.Clear();
        }

        private Vector3 FindSpawnPosition(Vector3 around)
        {
            // GET_SAFE_COORD_FOR_PED is the same native the game's own population system
            // uses to place peds: it walks the navmesh to find a real, walkable spot and
            // rejects interiors/water/isolated polygons. World.GetGroundHeight only samples
            // collision directly below the point - at 50-200m from the player that collision
            // is often not streamed in yet, so it was silently falling back to `around.Z`
            // (the player's own height) which routinely dropped zombies mid-air over a valley
            // or embedded them in a roof/hillside. That mis-placement is what was causing the
            // T-pose (ped created, then immediately destroyed/corrupted by collision push-out)
            // and "flying into the air" (physics violently resolving the spawn-in-geometry).
            return SpawnUtils.FindSafePosition(around, MinSpawnDistance, SpawnRadius);
        }

        private void CleanupDistantZombies()
        {
            var playerPos = Game.Player.Character.Position;

            for (int i = _activeZombies.Count - 1; i >= 0; i--)
            {
                var z = _activeZombies[i];
                var dist = (z.Ped.Position - playerPos).Length();

                if (dist > DespawnRadius)
                {
                    z.Cleanup();
                    _activeZombies.RemoveAt(i);
                }
            }
        }

        public bool IsZombie(Ped ped)
        {
            return _activeZombies.Any(z => z.Ped == ped);
        }

        public void OnAborted()
        {
            foreach (var z in _activeZombies)
                z.Cleanup();

            _activeZombies.Clear();
        }
    }
}
