using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Extensions;
using GTAZ.Systems;
using GTAZ.Utils;

namespace GTAZ.AI
{
    // Phase 4 faction ped behavior. Combat-first AI: any ped whose relationship group hates
    // ours (zombies, rival factions, and the player for hostile factions) becomes a
    // TASK_COMBAT_PED target. When nothing hostile is in range the ped falls back to a role
    // task: guards hold their post (TASK_GUARD_CURRENT_POSITION), recruits let the game's own
    // group AI follow the player, everyone else wanders their Origin.
    // Tasks are only (re)issued on a mode or target change - same lesson as ZombieAI/SurvivorAI:
    // reissuing every tick restarts the task from scratch and peds stutter.
    public class FactionAI(Ped ped, FactionDef faction, Vector3 origin, FactionRole role, bool territoryBound = false)
    {
        public Ped Ped { get; } = ped;
        public FactionDef Faction { get; } = faction;
        public Vector3 Origin { get; } = origin;
        public FactionRole Role { get; private set; } = role;

        private readonly int _updateInterval = 6;
        private int _tickCounter;
        private Ped _taskTarget;

        // Counts consecutive update ticks a patrol/guard has stood still while not in combat
        // (stuck-scenario fallback, see Update()).
        private int _stuckTicks;

        // Territory-bound peds (defenders, reinforcement guards, raiders - anything spawned with
        // a HomeZone) pursue the player across the whole fight. Ambient patrols get a short
        // pursuit range so the entire map doesn't aggro at once.
        private readonly bool _territoryBound = territoryBound;

        // Mounted riders (The Lost bikers) ride the player down before dismounting to fight.
        // TASK_COMBAT_PED drops a driver out of the vehicle, so ordering combat at pursuit range
        // made bikers abandon their bike half a block away and fight on foot at long range. They
        // chase on the bike until within VehicleDismountRange, then dismount to combat.
        private const float VehicleDismountRange = 20f;
        private const float VehicleChaseSpeed = 18f;
        private Vector3 _driveTarget;
        private int _lastDriveIssueTick;

        private enum Mode { None, Combat, Guard, Follow, Wander, VehicleChase }
        private Mode _mode = Mode.None;

        // No scripted "player aggro" here. Friendly faction peds (survivor guards, recruits) are
        // COMPANION to the player (see SetupFactionRelationships - Respect was tried first but a
        // Respect ped still retaliates when the player damages it, which is the "hostile until
        // they join" bug), so the native AI never makes them fight the player and there is no
        // scripted damage-based flip either - a HAS_ENTITY_BEEN_DAMAGED_BY window can't tell a
        // direct shot from a stray bullet, and it was turning guards AND recruits hostile on
        // accidental hits all the time (the "hostile survivors after taking a zone" and "recruits
        // turn on the player" bugs). Combat is scripted only against relationship-Hate targets
        // (zombies, bandits, The Lost).

        // Recruiting/abandoning changes the ped's job at runtime (patrol -> recruit -> patrol).
        public void SetRole(FactionRole role)
        {
            if (Role == role)
                return;
            Role = role;
            _mode = Mode.None;
            _taskTarget = null;
        }

        public void Update()
        {
            if (Ped?.Exists() is not true || Ped.IsDead)
                return;

            _tickCounter++;
            if (_tickCounter % _updateInterval != 0)
                return;

            float range = Role == FactionRole.Recruit
                ? ModConfig.Current.RecruitCombatRange
                : 45f;

            bool mounted = Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true;

            // Hostile-to-player factions (bandits) home in on the player instead of waiting for
            // them to walk into the 45m scan radius - that's why territory waves spawned and then
            // just stood at their spawn point (defenders/reinforcements spawn 40-140m out, often
            // behind cover). No LOS gate: TASK_COMBAT_PED paths around buildings, so a wave that
            // spawns out of sight still advances on the player. Territory-bound peds chase across
            // the whole fight; ambient patrols only react at close range.
            if (Faction.RelationToPlayer == PlayerRelation.Hostile)
            {
                var player = Game.Player.Character;
                if (player?.Exists() is true && !player.IsDead)
                {
                    // Mounted riders (The Lost bikers) ride the player down across the full
                    // pursuit range: an ambient biker spawning 100-200m out never got within the
                    // 60m on-foot range, so bikers were "spawning but never making it to the
                    // player". On-foot ambient patrols keep the short range so the whole map
                    // doesn't aggro at once.
                    float pursueRange = _territoryBound || mounted ? 300f : 60f;
                    float dist = SpawnUtils.HorizontalDistance(Ped.Position, player.Position);
                    if (dist <= pursueRange)
                    {
                        // Mounted riders chase the player down on the bike first (drive to their
                        // position), and only dismount once they're close enough to actually fight.
                        if (mounted && dist > VehicleDismountRange)
                        {
                            if (_mode != Mode.VehicleChase)
                            {
                                _mode = Mode.VehicleChase;
                                _taskTarget = player;
                                _lastDriveIssueTick = 0;
                            }
                            IssueDriveToPlayer();
                            return;
                        }

                        if (_mode != Mode.Combat || _taskTarget != player)
                        {
                            _mode = Mode.Combat;
                            _taskTarget = player;
                            Function.Call(Hash.TASK_COMBAT_PED, Ped, player, 0, 16);
                        }
                        return;
                    }
                }
            }

            // Mounted riders only engage non-player enemies at close range: ordering combat at
            // the full 45m scan makes a rider abandon its bike to chase a zombie half a block
            // away, so the map's bikers vanish into foot patrols (and bikes get left behind).
            // A zombie has to actually catch the bike (within dismount range) to force the fight.
            Ped enemy = FindNearestEnemy(mounted ? VehicleDismountRange : range);
            if (enemy != null)
            {
                if (_mode != Mode.Combat || _taskTarget != enemy)
                {
                    _mode = Mode.Combat;
                    _taskTarget = enemy;
                    Function.Call(Hash.TASK_COMBAT_PED, Ped, enemy, 0, 16);
                }
            }
            else if (_mode is Mode.Combat or Mode.VehicleChase)
            {
                _mode = Mode.None;
                _taskTarget = null;
                ApplyIdleTask();
            }
            else if (_mode == Mode.None)
            {
                // Spawn or role-change with no enemy in range: issue the role task right away.
                // Without this a freshly-spawned guard (or a re-rolled recruit) had no task at
                // all, so the engine drove it as an ambient ped - some wandered, most stood.
                ApplyIdleTask();
            }
            else if (!Ped.IsInVehicle() && _mode is Mode.Guard or Mode.Wander && Ped.Speed < 0.5f)
            {
                // Stuck-task fallback: survivor peds run with BlockPermanentEvents=false (so
                // they can take damage and die for loot), which lets the engine interrupt the
                // patrol task with an ambient scenario - the guard then stands playing it
                // forever, because the only other reissue path is combat exit. If a
                // patrol/guard isn't fighting, isn't riding, and has stood still for ~1.5s,
                // clear the stuck scenario and re-issue the patrol.
                _stuckTicks++;
                if (_stuckTicks > 90)
                {
                    _stuckTicks = 0;
                    Ped.Task.ClearAll();
                    ApplyIdleTask();
                }
            }
            else
            {
                _stuckTicks = 0;
            }
        }

        // Drive the rider's current vehicle toward the player. The drive task is only re-issued
        // when the player actually moves (or the task has gone stale) - restarting a drive task
        // every update makes the driver brake and stutter.
        private void IssueDriveToPlayer()
        {
            var player = Game.Player.Character;
            var vehicle = Ped.CurrentVehicle;
            if (player?.Exists() is not true || vehicle?.Exists() is not true)
                return;

            bool targetMoved = SpawnUtils.HorizontalDistance(_driveTarget, player.Position) >= 4f;
            bool taskStale = _tickCounter - _lastDriveIssueTick > 60;
            if (!targetMoved && !taskStale)
                return;

            _driveTarget = player.Position;
            _lastDriveIssueTick = _tickCounter;
            Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, Ped, vehicle,
                _driveTarget.X, _driveTarget.Y, _driveTarget.Z, VehicleChaseSpeed, 786603, 3f);
        }

        private void ApplyIdleTask()
        {
            switch (Role)
            {
                // Patrol the outpost instead of standing at one spot: TASK_WANDER_IN_AREA walks
                // the guard around the zone center (Origin is the post, spawned near the center)
                // so a held territory reads as manned rather than a static cluster of survivors.
                // Guards still break to combat whenever an enemy is in range (the Update() scan
                // runs before this idle task is applied) and retake the patrol after combat via
                // the _mode reset. Only issued once per idle entry - never every tick.
                case FactionRole.Guard:
                    _mode = Mode.Guard;
                    Function.Call(Hash.TASK_WANDER_IN_AREA, Ped, Origin.X, Origin.Y, Origin.Z, 100f, 0f, 0f);
                    break;

                // Recruits follow the player with an explicit follow task (TASK_FOLLOW_TO_OFFSET_OF_ENTITY).
                // Group membership alone was not reliably driving formation following (round-5
                // "joined survivors don't follow"), so give the game a concrete long-running task;
                // the ped stays in the player's group for the Companion stance. Only re-issued on
                // idle entry (post-combat / post-role-change), never every tick, so no stutter.
                case FactionRole.Recruit:
                {
                    _mode = Mode.Follow;
                    var player = Game.Player.Character;
                    if (player?.Exists() is true)
                    {
                        Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, Ped, player,
                            0f, 0f, 0f, 1.2f, -1, 2f, true);
                    }
                    break;
                }

                default:
                    _mode = Mode.Wander;

                    // The Lost ambient riders cruise instead of walking while they're still
                    // mounted: TASK_VEHICLE_DRIVE_WANDER keeps them riding around their Origin
                    // so the map sees bikers, not stationary peds. They only ever leave the
                    // bike to fight (TASK_COMBAT_PED drops them off), and once dismounted they
                    // stay on foot for this spawn - no re-mount choreography.
                    if (Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true)
                    {
                        Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, Ped, Ped.CurrentVehicle, 14f, 786603);
                    }
                    else
                    {
                        Ped.Task.ClearAll();
                        Function.Call(Hash.TASK_WANDER_IN_AREA, Ped, Origin.X, Origin.Y, Origin.Z, 20f, 0f, 0f);
                    }
                    break;
            }
        }

        private Ped FindNearestEnemy(float range)
        {
            foreach (var ped in World.GetNearbyPeds(Ped.Position, range))
            {
                if (ped == Ped || !ped.Exists() || ped.IsDead || !ped.IsHumanoid())
                    continue;

                if (Ped.RelationshipGroup.GetRelationshipBetweenGroups(ped.RelationshipGroup) != Relationship.Hate)
                    continue;

                // Only engage what the ped can actually see (matches SurvivorAI).
                if (Ped.HasClearLineOfSightTo(ped))
                    return ped;
            }
            return null;
        }
    }
}
