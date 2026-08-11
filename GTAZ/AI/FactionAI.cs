using System;
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
    // OnCrewStandOff fires when this crew member leaves its car for good (the stand-off dismount
    // or the driver-dead exit). FactionNPC.AttachCrewCar wires it to ReleaseVehicle so the gang
    // car is handed back to the game (parked + grab-able) instead of vanishing when the driver
    // bails (lesson 43).
    public class FactionAI(Ped ped, FactionDef faction, Vector3 origin, FactionRole role, bool territoryBound = false)
    {
        public Action OnCrewStandOff { get; set; }
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
        // bails out to fight on foot when the player stands their ground near the cars (stand-off
        // gate, see the constants below) or the car stalls (driver dead). Outrunning them past
        // CrewGiveUpDistance for a few seconds ends the chase: the cars drive off and the normal
        // FactionDespawnDistance cleanup fades the crew out.
        private const float CrewSpotRange = 120f;
        private const float CrewGiveUpDistance = 300f;
        private const int CrewGiveUpTicks = 240;
        private const float CrewDismountRange = 15f;
        private const float CrewDriveByRange = 40f;
        // Stand-off gate: the crew only bails out of the cars for a genuine on-foot fight. The
        // player must be on foot, moving slower than a jog (not actively fleeing), and standing
        // their ground within CrewDismountRange for ~2s (CrewStandOffTicks crew updates at the
        // 6-frame cadence). Until then the cars keep harrying + drive-bying an on-foot player. The
        // old "on foot within 15m" gate tripped the instant a car reached an on-foot player, so the
        // crew piled out before the chase/drive-by phase happened at all (lesson 44).
        private const float CrewStandOffMaxPlayerSpeed = 4f;
        private const int CrewStandOffTicks = 20;
        // On-foot combat re-point: TASK_COMBAT_PED lets the native AI retarget onto whoever engages
        // the ped, and zombies share mutual Hate with the crew, so a dismounted crew member can
        // drift onto the nearest zombie mid-fight. Re-issue the task against the player on a ~5s
        // cadence (CrewCombatReissueUpdates crew updates) so focus always comes back to the player.
        // Deliberately shorter than the 300-tick chase-task reissue - a pulled-off fighter needs a
        // snappier re-lock than a driving task (lesson 45).
        private const int CrewCombatReissueUpdates = 50;
        // Warp-recovery net cadence + attempt cap (crew updates, 6 frames each): a member whose
        // initial TASK_WARP_PED_INTO_VEHICLE failed gets re-warped into the car during cruise
        // (lesson 42).
        private const int CrewWarpRetryUpdates = 12;
        private const int CrewWarpAttempts = 10;

        private bool _crewEngaged;
        private int _crewLostTicks;
        private int _crewTaskReissueTick;
        // The crew's gang car + this member's seat, set by the spawner for FactionRole.Crew
        // (FactionNPC.AttachCrewCar) so the warp-recovery net knows where to put a failed-warp
        // member back. Only the driver additionally owns the car (FactionNPC.Vehicle) - that's the
        // lifecycle handle, released on dismount/death (lessons 42/43).
        public Vehicle CrewVehicle { get; set; }
        public int CrewSeat { get; set; } = -2;
        private int _crewWarpRetryTicks;
        private int _crewWarpAttempts = CrewWarpAttempts;
        // True once this crew member has left its car for good (stand-off dismount or driver-dead
        // exit). Blocks the warp-recovery net from re-mounting a dismounted fighter - there is no
        // re-mount choreography, same rule as The Lost riders.
        private bool _crewDismounted;
        // Set when the stand-off leave-vehicle task is issued and cleared the moment the ped is
        // actually out of the car. Two jobs: (1) stop UpdateCrew from re-issuing TASK_LEAVE_VEHICLE
        // every 6-frame pass (which restarts the exit animation), and (2) force an immediate
        // TASK_COMBAT_PED on the first on-foot pass instead of waiting for the ~5s re-point
        // cadence (lesson 47 - the "they stand idle for a couple seconds after getting out" report).
        private bool _crewExitPending;
        // Consecutive crew updates where the player stood their ground near the car (stand-off gate).
        private int _crewStandOffTicks;
        // On-foot combat re-point cadence counter.
        private int _crewCombatReissueTicks;

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
        // player stands their ground near the cars for a couple of seconds, or the car stalls
        // (driver dead). The pursuit only ends when the player outruns the crew
        // (CrewGiveUpDistance for a sustained stretch) - then the cars drive off and the
        // distance-despawn fades them out. Crew peds run with BlockPermanentEvents (set in
        // SpawnCrewForCar) so the engine's ambient AI can't hijack a passenger out of the car.
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
                    // Crew spawn safety net: the initial TASK_WARP_PED_INTO_VEHICLE (issued by the
                    // spawner) can fail while the car/ped stream in, stranding a member next to the
                    // car - the "one group wasn't in a car" report. Re-warp on a slow cadence while
                    // cruising (issued AFTER the cruise task so the warp task is the last one set);
                    // never after the crew has engaged/dismounted (lesson 42).
                    TryReWarpCrew();
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
                bool driverDead = Ped.CurrentVehicle.Driver?.IsDead is not false;

                // Stand-off gate: pile out only for a genuine on-foot fight. The crew keeps
                // harrying/drive-bying a moving or distant player, and bails once the player has
                // stood their ground near the car for a couple of seconds (lesson 44).
                bool standOff = !playerInVehicle
                    && player.Speed < CrewStandOffMaxPlayerSpeed
                    && dist <= CrewDismountRange;
                if (standOff)
                {
                    if (_crewStandOffTicks < CrewStandOffTicks)
                        _crewStandOffTicks++;
                }
                else
                {
                    _crewStandOffTicks = 0;
                }

                // The car is stalled (driver dead) or the player has stood their ground near the
                // cars: pile out and fight on foot (the "stand off" resolution).
                if (driverDead || _crewStandOffTicks >= CrewStandOffTicks)
                {
                    DismountAndFight(player);
                    return;
                }

                if (IsCrewDriver())
                {
                    // Driver: keep the car on the player across the whole pursuit.
                    // If a leave task was pending (player ran away mid-exit), it's abandoned -
                    // the crew is driving again, so a future stand-off re-issues the exit.
                    _crewExitPending = false;
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
                    _crewExitPending = false;
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
                // The crew is bailing out of the car for good (stand-off / driver dead): hand the
                // car back to the game (release, not delete) so it stays parked for the player to
                // grab and the engine reclaims it later - The Lost bike pattern, so the car never
                // vanishes mid-scene. Only the driver holds FactionNPC.Vehicle, so only the
                // driver's release callback does anything (lesson 43).
                _crewDismounted = true;
                OnCrewStandOff?.Invoke();
                // Issue the exit task once. Re-issuing TASK_LEAVE_VEHICLE on every 6-frame pass
                // restarts the exit animation, so the crew member keeps climbing out and never
                // completes it (lesson 47).
                if (!_crewExitPending)
                {
                    _crewExitPending = true;
                    Function.Call(Hash.TASK_LEAVE_VEHICLE, Ped, Ped.CurrentVehicle, 0);
                }
                _mode = Mode.Combat;
                _taskTarget = player;
                return;
            }

            // Just got out of the car (or a member that never mounted): start fighting NOW. The
            // ~5s cadence below is for re-pointing mid-fight, not for the initial engagement -
            // waiting for it is exactly the "stand idle for a couple seconds after getting out"
            // report (lesson 47). On the first on-foot pass the exit is complete, so the combat
            // task is safe to issue (it can't cancel the exit anymore).
            if (_crewExitPending)
            {
                _crewExitPending = false;
                _crewCombatReissueTicks = 0;
                _mode = Mode.Combat;
                _taskTarget = player;
                Function.Call(Hash.TASK_COMBAT_PED, Ped, player, 0, 16);
                return;
            }

            // On-foot combat vs the player. TASK_COMBAT_PED lets the native AI retarget onto
            // whoever engages the ped, and zombies share mutual Hate with the crew, so a crew
            // member can drift onto the nearest zombie mid-fight (the "most of them attacked
            // zombies instead of the player" report). Re-point the task at the player on a slow
            // cadence so focus always comes back - never every tick, that stutters the ped
            // (lessons 37/38/45).
            if (_mode != Mode.Combat || _taskTarget != player)
            {
                _mode = Mode.Combat;
                _taskTarget = player;
                Function.Call(Hash.TASK_COMBAT_PED, Ped, player, 0, 16);
            }
            else if (_crewCombatReissueTicks >= CrewCombatReissueUpdates)
            {
                _crewCombatReissueTicks = 0;
                Function.Call(Hash.TASK_COMBAT_PED, Ped, player, 0, 16);
            }
            else
            {
                _crewCombatReissueTicks++;
            }
        }

        // Re-warps a crew member who never made it into the car at spawn (the initial
        // TASK_WARP_PED_INTO_VEHICLE can fail while the car/ped stream in). Runs only while the
        // crew is still cruising (never after engagement or a stand-off) and stops once the ped is
        // seated or the attempt cap is exhausted - a stranded member that can't be fixed just
        // fights on foot (lesson 42).
        private void TryReWarpCrew()
        {
            if (_crewDismounted || _crewWarpAttempts <= 0)
                return;
            bool inVehicle = Ped.IsInVehicle() && Ped.CurrentVehicle?.Exists() is true;
            if (inVehicle || CrewVehicle?.Exists() is not true)
                return;
            if (_crewWarpRetryTicks++ < CrewWarpRetryUpdates)
                return;
            _crewWarpRetryTicks = 0;
            _crewWarpAttempts--;
            Function.Call(Hash.TASK_WARP_PED_INTO_VEHICLE, Ped, CrewVehicle, CrewSeat);
        }

        private bool IsCrewDriver()
        {
            if (Ped.CurrentVehicle?.Exists() is not true)
                return false;
            return Ped.CurrentVehicle.Driver == Ped;
        }

        // Passenger drive-by: TASK_VEHICLE_SHOOT_AT_PED makes the passenger lean out and keep
        // shooting at the player while they're in range, re-aiming on its own - the reliable
        // in-vehicle fire primitive. (TASK_DRIVE_BY was the original choice but is notoriously
        // dead: its own native doc says "doesn't seem to do anything", so passengers never fired
        // during the chase - lesson 48.) The 300-update reissue below is just a cleared-task
        // safety net.
        private void IssueDriveBy(Ped target)
        {
            _crewTaskReissueTick = 0;
            // p2 is the shoot range (the drive-by family only fires once the target is within it;
            // the old 0f meant never in range - lesson 48). CrewDriveByRange matches the pursuit
            // engage distance so the passenger opens up as soon as the chase is on.
            Function.Call(Hash.TASK_VEHICLE_SHOOT_AT_PED, Ped, target, CrewDriveByRange);
        }

        // The chase/drive-by tasks are self-sustaining but the engine can clear them (blocked
        // events, physics, task-slot reuse). Re-issue on a cadence so a cleared task recovers.
        // The pursuit reissue stays slow (300 updates ~30s) - re-issuing TASK_VEHICLE_CHASE too
        // often makes the driver brake-stutter. The drive-by is a fire task (like the on-foot
        // combat re-point) so it recovers much faster (~25 updates ~2.5s); a passenger with a
        // cleared shoot task is doing nothing meanwhile (lessons 37/38/45/48).
        private void ReissueCrewTask()
        {
            int cadence = _mode == Mode.CrewDriveBy ? 25 : 300;
            if (_crewTaskReissueTick < cadence)
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
            // Any transition back to cruise abandons a pending exit (give-up drive-away can fire
            // while a member is mid-climb) - a stale flag would otherwise skip the exit task on a
            // later re-engage and strand the member in the car (lesson 47).
            _crewExitPending = false;

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
