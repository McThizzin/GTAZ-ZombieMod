using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Extensions;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    // Phase 4 faction spawning (DOCS/11-Refactoring-Plan.md §3.4), split out of FactionSystem.
    // Owns the ambient/patrol spawn timers and every SpawnRequest construction for faction peds:
    // ambient faction members, The Families' timed patrol, and the territory-bound
    // defenders/reinforcements/raiders/guards (SpawnFactionPed). Spawns go through the shared
    // SpawnRequestQueue like every other ped source (PedCategory.FactionNpc), so a spawn can be
    // budget-delayed or expire; territory-bound spawns are Critical priority so they grant ahead
    // of hordes/ambient zombies.
    //
    // Callback couplings into FactionSystem (per the §3.4 plan): addActivePed records a spawned
    // ped in the active list, countAlive reads ambient-population (HomeZone == null) per faction,
    // and the territory queries (isPlayerOwnedZone, territoryOwnerAt, isInAnyTerritory) keep
    // ambient/patrol spawns out of owned zones and inside hostile-held turf. rng is shared with
    // FactionSystem so territory wave picks and spawn model picks use one random stream.
    public class FactionSpawner(PedBudgetManager budget, SpawnRequestQueue queue, Random rng,
        List<FactionDef> definitions, FactionDef families, Action<FactionNPC> addActivePed,
        Func<FactionDef, int> countAlive, Func<Vector3, bool> isPlayerOwnedZone,
        Func<Vector3, FactionDef> territoryOwnerAt, Func<Vector3, bool> isInAnyTerritory)
    {
        private const float LostRiderChance = 0.6f;

        private int _ambientSpawnTimer;
        private int _banditPatrolTimer;

        // Weighted ambient spawn near the player (only factions with AmbientSpawnWeight > 0).
        public void TryAmbientSpawn()
        {
            var config = ModConfig.Current;

            if (++_ambientSpawnTimer < 360)
                return;
            _ambientSpawnTimer = 0;

            if (rng.NextDouble() >= config.FactionSpawnChance)
                return;
            if (budget.Available <= 0)
                return;

            var candidates = definitions
                .Where(d => d.AmbientSpawnWeight > 0 && countAlive(d) < config.FactionSpawnTarget)
                .ToList();
            if (candidates.Count == 0)
                return;

            var faction = WeightedPick(candidates);
            var playerPos = Game.Player.Character.Position;
            // Spawn well outside hostile pursuit range (60m ambient): a faction ped landing at
            // 40m is instantly in aggro range and opens fire the moment it materializes. 100-200m
            // keeps the map populated but lets the player close the gap on their own terms.
            var pos = SpawnUtils.FindSafePosition(playerPos, 100f, 200f);
            if (pos == Vector3.Zero)
                return;

            // Owned zones are safe territory: no ambient faction members inside them (guards +
            // timer raids are the event-driven action there). Hostile territories SHOULD feel
            // occupied - force the holding faction's ambient members to spawn inside them, so
            // walking into a territory meets a live garrison instead of empty ground. Their
            // deaths still count toward that zone's defense quota (they die within
            // TerritoryRadius), which is what starts the reinforcement waves.
            if (isPlayerOwnedZone(pos))
                return;

            var occupant = territoryOwnerAt(pos);
            if (occupant != null)
                faction = occupant;

            SpawnFactionPed(faction, pos, FactionRole.Patrol, null);
        }

        // Periodic bandit raiding party near the player, OUTSIDE any territory. Territories are
        // event-driven: owned zones only see bandits via timer raids, hostile zones via their
        // defenders + defense waves. This used to anchor the patrol to the held zone center every
        // BanditPatrolIntervalMinutes, which is exactly the "bandits keep attacking my territory over
        // and over" complaint.
        public void TryBanditPatrol()
        {
            var config = ModConfig.Current;

            _banditPatrolTimer++;
            int interval = Math.Max(60, config.BanditPatrolIntervalMinutes * 60 * 60);
            if (_banditPatrolTimer < interval)
                return;
            _banditPatrolTimer = 0;

            if (countAlive(families) >= config.FactionSpawnTarget + 4)
                return;
            if (budget.Available <= 0)
                return;

            var playerPos = Game.Player.Character.Position;
            if (isInAnyTerritory(playerPos))
                return;

            int count = rng.Next(3, 5);
            for (int i = 0; i < count; i++)
            {
                var pos = SpawnUtils.FindSafePosition(playerPos, 100f, 200f);
                if (pos == Vector3.Zero || isInAnyTerritory(pos))
                    continue;
                SpawnFactionPed(families, pos, FactionRole.Raider, null);
            }
        }

        public void SpawnFactionPed(FactionDef faction, Vector3 pos, FactionRole role,
            TerritoryZone homeZone, Action<FactionNPC> onSpawned = null, Action onExpired = null,
            Vector3? originOverride = null)
        {
            var request = new SpawnRequest
            {
                Category = PedCategory.FactionNpc,
                // Territory-bound spawns (defenders/waves/raiders/guards with a HomeZone) are
                // Critical so they grant BEFORE horde/ambient spawns (both Active on the shared
                // queue) - faction waves take priority over hordes, and hordes never march
                // through territory ground so they're far enough away to not matter mid-fight.
                Priority = homeZone != null ? PedPriority.Critical : PedPriority.Active,
                Position = pos,
                Origin = $"Faction:{faction.Id}",
                SpawnFactory = () =>
                {
                    var modelHash = faction.PedModels[rng.Next(faction.PedModels.Length)];
                    var model = new Model(modelHash);
                    model.Request();
                    return World.CreatePed(model, pos, rng.Next(0, 360));
                },
                OnGranted = slot =>
                {
                    var ped = slot.Ped;
                    if (ped?.Exists() is not true)
                        return;

                    var weapon = ped.MakeFactionLike(faction);
                    var npc = new FactionNPC(ped, faction, originOverride ?? pos, role, homeZone, slot, weapon);

                    // Territory-bound peds (defenders, reinforcements, raiders - anything with a
                    // HomeZone) are the event-driven action, so they must not be budget-evicted or
                    // engine-culled mid-fight. During a hostile-zone defense UpdateHostileDefenseOverride
                    // borrows the horde budget (FactionNpc minimum -> 0), which made distant defenders
                    // eviction candidates - waves despawned without fighting and the "zone taken with
                    // no fight" capture happened on a couple of kills. Essential blocks the budget
                    // eviction path AND pins the ped persistent so the engine can't cull it either.
                    if (homeZone != null)
                        npc.MarkEssential();

                    // The Lost ride: a share of ambient/patrol Lost spawn mounted on a bike.
                    // They drive-wander toward their area and dismount when an enemy shows up
                    // (the combat task drops them off; FactionAI only resumes riding while the
                    // ped is still mounted, so a dismounted rider fights on foot for good).
                    if (faction.Id == FactionCatalog.LostFactionId && role == FactionRole.Patrol
                        && homeZone is null && rng.NextDouble() < LostRiderChance)
                    {
                        TryMountBike(npc);
                    }

                    // Spawned peds are always active-tracked; recruits are created directly
                    // (RecruitManager.TryToggleRecruit adopts an existing ped, never this path).
                    addActivePed(npc);

                    onSpawned?.Invoke(npc);
                },
                OnExpired = onExpired
            };

            queue.Enqueue(request);
        }

        // Warps a faction ped onto a freshly-created bike. The bike is marked persistent so the
        // game's vehicle cleaner doesn't delete it out from under the rider mid-cruise. Spawns
        // ON a street (bikers need drivable ground, and navmesh-safe spots aren't always on a
        // road) via World.GetNextPositionOnStreet, falling back to the origin if no street is
        // found. The FactionNPC owns it: the bike survives the rider's death (handed back to the
        // game in FactionNPC.OnDeath) and is deleted only when the script deliberately despawns
        // the ped, so no riderless bikes pile up on eviction.
        private void TryMountBike(FactionNPC npc)
        {
            var ped = npc.Ped;
            if (ped?.Exists() is not true)
                return;

            var roadPos = World.GetNextPositionOnStreet(npc.Origin);
            var spawnPos = roadPos != Vector3.Zero ? roadPos : npc.Origin;

            var model = new Model(rng.NextDouble() < 0.5 ? VehicleHash.Hexer : VehicleHash.Daemon);
            var bike = World.CreateVehicle(model, spawnPos, rng.Next(0, 360));
            if (bike?.Exists() is not true)
                return;

            bike.IsPersistent = true;
            Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, ped, bike, -1);
            npc.Vehicle = bike;
        }

        private FactionDef WeightedPick(List<FactionDef> candidates)
        {
            float total = candidates.Sum(d => d.AmbientSpawnWeight);
            float roll = (float)rng.NextDouble() * total;
            foreach (var candidate in candidates)
            {
                roll -= candidate.AmbientSpawnWeight;
                if (roll <= 0f)
                    return candidate;
            }
            return candidates[0];
        }
    }
}
