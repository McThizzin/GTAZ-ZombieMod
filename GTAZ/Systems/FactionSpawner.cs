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

        // The Families' gang cars (Phase 5 encounter rework, DOCS/11-Refactoring-Plan.md §7.3):
        // Cavalcade XL (cavalcade3, hash 3265236814) plus the player-requested Impaler SZ
        // (impaler5, hash 3816328113) and Super Diamond (superd, hash 1123216662). The original
        // faction3/voodoo2 were 2-door cars: _crewSeats is driver + 3 passengers ([-1,0,1,2]), so
        // warping the back-seat crew into a 2-seater FAILED for seats 1/2 every spawn - a log wall
        // of "failed initial vehicle warp" plus stranded crew standing next to the car (lesson 46).
        // The models below are all 4-doors; SpawnCrewForCar also skips seats past the car's
        // PassengerCapacity as a belt-and-suspenders guard.
        private static readonly VehicleHash[] _familiesCars =
        [
            VehicleHash.Impaler5,  // impaler5 (Impaler SZ)
            VehicleHash.Superd,    // superd (Super Diamond)
            VehicleHash.Cavalcade3 // cavalcade3 (Cavalcade XL)
        ];

        private static readonly int[] _crewSeats = [-1, 0, 1, 2];

        private int _ambientSpawnTimer;
        private int _familiesEncounterTimer;

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

        // The Families car encounter (the former "bandit patrol" - renamed in the Phase 5 rework,
        // DOCS/11-Refactoring-Plan.md §7.3): 1-2 gang cars full of Families crew (4-8 GSF members)
        // roll through the area OUTSIDE any territory. The crew cruises until it spots the player,
        // then CHASES them in the cars (drive-by shooting from the passengers) - the player is
        // their only focus, never ambient zombies. This is an open-world encounter, NOT a territory
        // attack: owned zones only see Families via the home-faction timer raids and hostile zones
        // via their defenders + defense waves. The old version anchored a raiding party to a held
        // zone center every BanditPatrolIntervalMinutes - exactly the "bandits keep attacking my
        // territory over and over" complaint.
        public void TryFamiliesEncounter()
        {
            var config = ModConfig.Current;

            _familiesEncounterTimer++;
            int interval = Math.Max(60, config.BanditPatrolIntervalMinutes * 60 * 60);
            if (_familiesEncounterTimer < interval)
                return;
            _familiesEncounterTimer = 0;

            if (countAlive(families) >= config.FactionSpawnTarget + 4)
                return;
            if (budget.Available < 4)
                return;

            var playerPos = Game.Player.Character.Position;
            if (isInAnyTerritory(playerPos))
                return;

            int carsSpawned = 0;
            int carCount = rng.Next(1, 3);
            for (int c = 0; c < carCount; c++)
            {
                if (budget.Available < 4)
                    break;

                // Each car gets its own street point (FindSafePosition probes terrain first; the
                // 40m offset per car keeps the two cars from stacking on one road).
                var anchor = SpawnUtils.FindSafePosition(playerPos, 100f + (c * 40f), 200f + (c * 40f));
                if (anchor == Vector3.Zero)
                    continue;
                var roadPos = World.GetNextPositionOnStreet(anchor);
                var spawnPos = roadPos != Vector3.Zero ? roadPos : anchor;
                if (isInAnyTerritory(spawnPos))
                    continue;

                var car = CreateCrewCar(spawnPos);
                if (car is null)
                    continue;

                if (!SpawnCrewForCar(car, spawnPos))
                {
                    car.Delete();
                    continue;
                }
                carsSpawned++;
            }

            if (carsSpawned > 0)
                GTA.UI.Notification.PostTicker("~o~A Families patrol is rolling through the area", false);
        }

        // Creates the crew's gang car. World.CreateVehicle(model, ...) requests the model
        // internally (same pattern as TryMountBike); the car is persistent so the engine's
        // vehicle cleaner doesn't delete it out from under the crew mid-chase. The driver owns
        // it (FactionNPC.Vehicle) so Cleanup deletes it and OnDeath hands it back to the game.
        private Vehicle CreateCrewCar(Vector3 pos)
        {
            var model = new Model(_familiesCars[rng.Next(_familiesCars.Length)]);
            var car = World.CreateVehicle(model, pos, rng.Next(0, 360));
            if (car?.Exists() is not true)
                return null;
            car.IsPersistent = true;
            return car;
        }

        // Spawns the crew for one car (driver + 3 passengers = 4 GSF members). The crew spawns
        // synchronously through the budget (TryRequestAndSpawn, Active priority) rather than the
        // SpawnRequestQueue so a car never half-populates over the queue's 3-grants-a-pass - a
        // half-full car reads as a bug. Returns false if the driver couldn't spawn (the caller
        // deletes the car); a failed passenger just leaves that seat empty. The driver carries
        // the car reference (npc.Vehicle) like the bikers' bikes; every member gets the car + seat
        // on the AI (AttachCrewCar) so the warp-recovery net can put a failed-warp member back in.
        private bool SpawnCrewForCar(Vehicle car, Vector3 origin)
        {
            foreach (int seat in _crewSeats)
            {
                // 2-door cars have no rear seats: never try to warp crew into a seat the car
                // doesn't have (the "failed initial vehicle warp" log wall + stranded back-seat
                // crew, lesson 46). PassengerCapacity excludes the driver (-1 always valid).
                if (seat >= 0 && seat >= car.PassengerCapacity)
                    continue;

                var outcome = budget.TryRequestAndSpawn(PedCategory.FactionNpc, PedPriority.Active,
                    origin, () =>
                    {
                        var modelHash = families.PedModels[rng.Next(families.PedModels.Length)];
                        var model = new Model(modelHash);
                        model.Request();
                        return World.CreatePed(model, origin, rng.Next(0, 360));
                    }, out var slot);

                if (outcome != PedBudgetManager.SpawnOutcome.Spawned)
                {
                    if (seat == -1)
                        return false;
                    continue;
                }

                var ped = slot.Ped;
                var weapon = ped.MakeFactionLike(families);
                // Drive-by capable weapons only (pistols/SMGs) - rifles/shotguns can't shoot
                // from a car. The faction pool can roll an AssaultRifle, and a rifle-armed crew
                // member sits in the car unable to fire even with the drive-by task running
                // (the "sometimes they shoot and sometimes they don't" report - pistol/SMG rolls
                // worked, rifle rolls didn't). Give a guaranteed drive-by weapon alongside the
                // random one and use IT as the vehicle weapon; the random weapon stays selected
                // on-foot for the stand-off fight.
                var driveByWeapon = IsDriveByCapable(weapon) ? weapon : WeaponHash.CombatPistol;
                if (driveByWeapon != weapon)
                    ped.Weapons.Give(driveByWeapon, 120, false, false);
                // A ped seated in a car only uses its "vehicle weapon" slot when aiming/shooting
                // from the window - the on-foot selection (Weapons.Select inside MakeFactionLike)
                // doesn't carry into the car. Without this the crew sits in the car unarmed and
                // does the unarmed-in-vehicle "flip the player off" reaction when the player aims
                // at them, and TASK_VEHICLE_SHOOT_AT_PED has nothing to fire (lesson 48).
                Function.Call(Hash.SET_CURRENT_PED_VEHICLE_WEAPON, ped, driveByWeapon);

                // Crew peds run with BlockPermanentEvents so the engine's ambient AI can't hijack
                // a passenger into leaving the car or wandering off while the crew cruises (the
                // "one group got out before the player even saw them" report). MakeFactionLike is
                // shared by every faction and some paths rely on engine interrupts, so this is set
                // only for the scripted car-crew right here (lesson 41).
                ped.BlockPermanentEvents = true;
                // CA_DISABLE_TARGET_CHANGES_DURING_VEHICLE_PURSUIT (59) is a DRIVER pursuit
                // attribute - it keeps the driver locked on the player instead of retargeting onto
                // a zombie mid-chase. Passengers must NOT get it: applied to them it can suppress
                // their drive-by focus on the player while the car is in pursuit (lesson 45/48).
                // CA_DISABLE_SECONDARY_TARGET (25, set in MakeFactionLike) already blocks drive-by
                // secondary targets for everyone, and BlockPermanentEvents means the crew never
                // returns fire at zombies on its own.
                if (seat == -1)
                    Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, ped, 59, true);

                var npc = new FactionNPC(ped, families, origin, FactionRole.Crew, null, slot, weapon);
                npc.AttachCrewCar(car, seat);
                if (seat == -1)
                    npc.Vehicle = car;

                // TASK_WARP_PED_INTO_VEHICLE fails while the car model is still streaming in (the
                // "wasn't in a car" report + the log wall of failed initial warps). Only attempt
                // the warp once the model is actually loaded - otherwise skip it and let the AI's
                // re-warp net (TryReWarpCrew, every ~1.2s up to 10 attempts during cruise) seat the
                // member as soon as streaming finishes (lessons 42/49). Verify after a warp attempt;
                // a genuine miss is still recovered by the net.
                if (car.Model.IsLoaded)
                {
                    Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, ped, car, seat);
                    if (!ped.IsInVehicle(car) && !ped.IsInVehicle())
                        Logger.Warn($"Crew member (seat {seat}) failed initial vehicle warp - re-warp net will retry");
                }

                addActivePed(npc);
            }
            return true;
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

        // Which weapons a ped can actually fire from a car window. Only pistols and SMGs drive-by;
        // rifles/shotguns/launchers can't shoot while seated in a vehicle, so a crew member whose
        // random roll landed on one sits in the car unable to fire no matter what task it has.
        private static bool IsDriveByCapable(WeaponHash weapon)
        {
            return weapon is WeaponHash.Pistol
                or WeaponHash.CombatPistol
                or WeaponHash.MicroSMG
                or WeaponHash.AssaultSMG
                or WeaponHash.SMG;
        }
    }
}
