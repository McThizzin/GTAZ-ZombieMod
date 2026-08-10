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

        // The Families car-crew chase (FactionRole.Crew). Crew cruises until it spots the player
        // (CrewSpotRange + LOS), then CHASES them in the cars - the player is their only focus and
        // ambient zombies never distract them. Passengers drive-by from the car; the crew only
        // dismounts to fight when the player is on foot and the cars catch up (CrewDismountRange).
        // Outrunning them past CrewGiveUpDistance for a few seconds ends the chase: the cars drive
        // off and the normal FactionDespawnDistance cleanup fades the crew out.
        private const float CrewSpotRange = 120f;
        private const float CrewGiveUpDistance = 300f;
        private const int CrewGiveUpTicks = 240;
        private const float CrewDismountRange = 15f;
        private const float CrewDriveByRange = 40f;

        private bool _crewEngaged;
        private int _crewLostTicks;
        private int _crewTaskReissueTick;

        private enum Mode { None, Combat, Guard, Follow, Wander, VehicleChase, CrewCruise, CrewPursuit, CrewDriveBy }
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

            // Gang-car crew (The Families encounter) runs its own state machine - it never uses
            // the on-foot scan below: the player is the crew's only focus, so zombies/rivals must
            // not pull a crew member out of the car or off a cruise (the user's design).
            if (Role == FactionRole.Crew)
            {
                UpdateCrew();
                return;
            }

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

        // --- The Families car-crew chase (FactionRole.Crew) ---
        //
        // States: CrewCruise (unspotted, driving the area) -> CrewPursuit (driver chases) /
        // CrewDriveBy (passengers shoot from the car) -> dismount to on-foot Combat when the
        // player is on foot and the cars catch up, or the car stalls (driver dead). The pursuit
        // only ends when the player outruns the crew (CrewGiveUpDistance for a sustained stretch)
        // - then the cars drive off and the distance-despawn fades them out.
        private void UpdateCrew()
        {
            var player = Game.Player.Character;
            bool playerOk = player?.Exists() is true && !player.IsDead;

            bool inVehicle = Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true;

            if (!_crewEngaged)
            {
                // Cruising. Only engage when the player is actually spotted - the crew is hunting,
                // so it sees further than an on-foot patrol, but still needs LOS (no aggro through
                // walls while cruising).
                if (playerOk)
                {
                    float spotDist = SpawnUtils.HorizontalDistance(Ped.Position, player.Position);
                    if (spotDist <= CrewSpotRange && Ped.HasClearLineOfSightTo(player))
                    {
                        _crewEngaged = true;
                        _crewLostTicks = 0;
                    }
                }

                if (!_crewEngaged)
                {
                    IssueCrewCruise();
                    return;
                }
            }

            if (!playerOk)
            {
                DisengageCrew(driveAway: true);
                return;
            }

            float dist = SpawnUtils.HorizontalDistance(Ped.Position, player.Position);

            // Lost the player: outrun past CrewGiveUpDistance for a sustained stretch -> give up,
            // drive off, and let the distance-despawn fade the crew out.
            if (dist > CrewGiveUpDistance)
            {
                _crewLostTicks++;
                if (_crewLostTicks > CrewGiveUpTicks)
                {
                    DisengageCrew(driveAway: true);
                    return;
                }
            }
            else
            {
                _crewLostTicks = 0;
            }

            if (inVehicle)
            {
                bool playerInVehicle = player.IsInVehicle();
                bool driverDead = Ped.CurrentVehicle.Driver?.IsDead != false;
                bool nearOnFoot = !playerInVehicle && dist <= CrewDismountRange;

                // The car is stalled (driver dead) or the player is on foot and the cars have
                // caught up: pile out and fight on foot (the "stand off" resolution).
                if (driverDead || nearOnFoot)
                {
                    DismountAndFight(player);
                    return;
                }

                if (IsCrewDriver())
                {
                    // Driver: keep the car on the player across the whole pursuit.
                    if (_mode != Mode.CrewPursuit || _taskTarget != player)
                    {
                        _mode = Mode.CrewPursuit;
                        _taskTarget = player;
                        _crewTaskReissueTick = 0;
                        // Aggressive ramming + PIT attempts so the pursuit actually harries the
                        // player's car instead of convoying politely behind it.
                        Function.Call(Hash.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG, Ped, 1, true);
                        Function.Call(Hash.SET_TASK_VEHICLE_CHASE_BEHAVIOR_FLAG, Ped, 16, true);
                        Function.Call(Hash.TASK_VEHICLE_CHASE, Ped, player);
                    }
                    else
                    {
                        ReissueCrewTask();
                    }
                }
                else
                {
                    // Passenger: lean out and shoot the player from the car.
                    if (_mode != Mode.CrewDriveBy || _taskTarget != player)
                    {
                        _mode = Mode.CrewDriveBy;
                        _taskTarget = player;
                        IssueDriveBy(player);
                    }
                    else
                    {
                        ReissueCrewTask();
                    }
                }
                return;
            }

            // Dismounted (bailed, car wrecked, or mid-standoff): fight the player on foot. No
            // re-mount choreography - once the crew is out, the fight stays on foot (same rule
            // as The Lost riders).
            DismountAndFight(player);
        }

        // Leaves the car (if still in it) and fights the player on foot. The exit task must be
        // given alone - TASK_COMBAT_PED now would cancel it - so combat is set for the next
        // Update pass once the ped is out.
        private void DismountAndFight(Ped player)
        {
            if (Ped.IsInVehicle())
            {
                Function.Call(Hash.TASK_LEAVE_VEHICLE, Ped, Ped.CurrentVehicle, 0);
                _mode = Mode.Combat;
                _taskTarget = player;
                return;
            }

            if (_mode != Mode.Combat || _taskTarget != player)
            {
                _mode = Mode.Combat;
                _taskTarget = player;
                Function.Call(Hash.TASK_COMBAT_PED, Ped, player, 0, 16);
            }
        }

        private bool IsCrewDriver()
        {
            if (Ped.CurrentVehicle?.Exists() is not true)
                return false;
            return Ped.CurrentVehicle.Driver == Ped;
        }

        // Passenger drive-by: TASK_DRIVE_BY aims and fires at the player from the car window.
        // The firing pattern (0x9C00E9AF) is the mission drive-by burst. Accuracy 60 matches the
        // survivor shooters so the drive-by is threatening without being a sniper.
        private void IssueDriveBy(Ped target)
        {
            _crewTaskReissueTick = 0;
            var targetPos = target.Position;
            Function.Call(Hash.TASK_DRIVE_BY, Ped, target, target.CurrentVehicle,
                targetPos.X, targetPos.Y, targetPos.Z, CrewDriveByRange, 60, false, unchecked((int)0x9C00E9AF));
        }

        // The chase/drive-by tasks are self-sustaining but the engine can clear them (blocked
        // events, physics, task-slot reuse). Re-issue on a slow cadence so a cleared task recovers
        // without the brake-stutter of re-issuing every update (lessons 37/38).
        private void ReissueCrewTask()
        {
            if (_crewTaskReissueTick < 300)
            {
                _crewTaskReissueTick++;
                return;
            }
            _crewTaskReissueTick = 0;

            var player = Game.Player.Character;
            if (player?.Exists() is not true)
                return;

            if (_mode == Mode.CrewPursuit)
                Function.Call(Hash.TASK_VEHICLE_CHASE, Ped, player);
            else if (_mode == Mode.CrewDriveBy)
                IssueDriveBy(player);
        }

        // Idle cruise: drive-wander the crew's Origin while unspotted (12 m/s so the cars cruise
        // visibly instead of piling up on a corner). Re-issued on a cadence - the engine can clear
        // it (lesson 37). A passenger does nothing (the driver's task moves the car); an unmounted
        // crew member wanders on foot.
        private void IssueCrewCruise()
        {
            bool inVehicle = Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true;

            if (inVehicle && !IsCrewDriver())
            {
                _mode = Mode.CrewCruise;
                _crewTaskReissueTick = 0;
                return;
            }

            if (_mode == Mode.CrewCruise && _crewTaskReissueTick < 360)
            {
                _crewTaskReissueTick++;
                return;
            }
            _crewTaskReissueTick = 0;
            _mode = Mode.CrewCruise;

            if (inVehicle)
            {
                Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, Ped, Ped.CurrentVehicle, 12f, 786603);
            }
            else
            {
                Ped.Task.ClearAll();
                Function.Call(Hash.TASK_WANDER_IN_AREA, Ped, Origin.X, Origin.Y, Origin.Z, 20f, 0f, 0f);
            }
        }

        // End the pursuit: the crew gives up on the player. With driveAway (lost them / player
        // gone) the driver floors it away from the player so the normal FactionDespawnDistance
        // cleanup fades the whole crew out - a natural "they gave up and left" resolution instead
        // of a pop-out. Passengers ride along; a dismounted crew member just cruises on foot.
        private void DisengageCrew(bool driveAway = false)
        {
            _crewEngaged = false;
            _crewLostTicks = 0;
            _crewTaskReissueTick = 0;
            _taskTarget = null;

            if (driveAway && Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true && IsCrewDriver())
            {
                var player = Game.Player.Character;
                var from = player?.Exists() is true ? player.Position : Ped.Position;
                Vector3 away = Ped.Position - from;
                away.Z = 0f;
                if (away.LengthSquared() < 1f)
                    away = new Vector3(0f, 1f, 0f);
                away = away.Normalized;
                Vector3 target = Ped.Position + (away * 400f);
                _mode = Mode.CrewCruise;
                Function.Call(Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE, Ped, Ped.CurrentVehicle,
                    target.X, target.Y, target.Z, 16f, 786603, 5f);
                return;
            }

            IssueCrewCruise();
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
