using System;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Events;
using GTAZ.Extensions;
using GTAZ.Utils;


namespace GTAZ.AI
{
    public enum ZombieType
    {
        Walker,
        Runner,
        Horder
    }

    public enum ZombieState
    {
        Idle,
        Wandering,
        Chasing,
        Attacking,
        Dead
    }

    public abstract class ZombieAI
    {
        public Ped Ped { get; }
        public ZombieType Type { get; protected set; }
        public ZombieState State { get; set; } = ZombieState.Wandering;
        public Ped Target { get; set; }

        public float VisionRange { get; protected set; } = 50f;
        public float HearingRange { get; protected set; } = 30f;
        public float AttackRange { get; protected set; } = 2f;
        public float WanderRadius { get; protected set; } = 30f;
        public int Damage { get; protected set; } = 15;
        public int MaxHealth { get; protected set; } = 100;
        public float MoveSpeed { get; protected set; } = 1f;

        // Horde march mode: while HordeMoveTarget is set, the Wandering state marches the ped
        // toward that point instead of wandering its spawn area (set by HordeSystem). When the
        // zombie perceives the player it breaks into the normal chase/attack AI, and returns to
        // marching once the target is lost. Set to null to restore normal wandering.
        public Vector3? HordeMoveTarget { get; set; }
        public float HordeArriveRadius { get; set; } = 5f;
        // Set by HordeSystem while this member is being reeled back to the horde center
        // (ApplyGroupingPullBack). Hysteresis: only restored to the shared destination once
        // back well inside the trigger radius, so a member hovering on the boundary doesn't
        // flip HordeMoveTarget every tick and churn its march task (the straggler stutter).
        public bool HordePullingBack { get; set; }

        // Confirmed present in anim@ingame@melee@unarmed@streamed_variations_zombie.
        // Picked at random per swing so consecutive hits don't all play the same strike.
        private static readonly Random _attackAnimRng = new();
        private static readonly Random _facialRng = new();
        private static readonly string[] AttackAnimVariations =
        [
            "heavy_punch_b_var_2",
            "heavy_punch_c_var_1",
            "heavy_punch_b_var_1",
            "heavy_punch_a_var_1"
        ];

        // Tracks which variation is currently mid-swing so OnAttack() can skip re-issuing
        // the anim while it's still playing (see PlayAttackAnim).
        private string _currentAttackAnim;
        // Per-zombie facial expression (random mood from PedExtensions.FacialMoodAnims,
        // picked in the ctor). The engine clears SET_FACIAL_IDLE_ANIM_OVERRIDE on every task
        // start, so Update re-applies it on a cadence and fires PLAY_FACIAL_ANIM one-shots.
        private readonly string _facialAnim;
        private int _facialOverrideTimer;

        // Zombie audio. MakeZombieLike sets STOP_PED_SPEAKING + DISABLE_PED_PAIN_AUDIO ONCE at
        // spawn (both persist), so Update never calls them per-frame. Pain audio being disabled
        // stops the engine's automatic pain/death groans (the vanilla human scream on gunshot
        // kills and the death moan); scripted gurgles/moans play here by re-enabling pain audio
        // for a single PLAY_PAIN call. Reasons are the eAudDamageReason gurgle/moan family - NO
        // scream_panic/terror/fire, those are human screams the player asked to drop.
        private const float ZombieSoundRange = 45f;
        private const int ZombieSoundInterval = 600; // ~10s at 60fps
        private const int ZombieSoundChance = 40;    // percent per interval
        private static readonly Random _soundRng = new();
        private static readonly int[] ZombieGurgleReasons =
        [
            18, // AUD_DAMAGE_REASON_WHEEZE
            19, // AUD_DAMAGE_REASON_COUGH
            26, // AUD_DAMAGE_REASON_WHIMPER
            31, // AUD_DAMAGE_REASON_MELEE_SMALL_GRUNT
            32, // AUD_DAMAGE_REASON_MELEE_LARGE_GRUNT
            0,  // AUD_DAMAGE_REASON_DEFAULT
        ];
        private int _gurgleTimer;
        private bool _deathMoanPlayed;

        protected int _attackCooldown;
        protected bool _ragdollDisabled;
        protected Vector3 _spawnOrigin;
        protected int _updateInterval = 4;
        protected int _tickCounter;
        protected ZombieState _lastState = ZombieState.Wandering;
        protected Ped _followTaskTarget;
        protected Vector3? _lastHordeTarget;
        private int _waterRecoveryCooldown;
        private Blip _blip;
        private int _wanderReissueTimer;
        // Consecutive behavior ticks the zombie spent standing still while Chasing (follow task
        // cleared by the engine). See the Chasing branch - the stuck-speed fallback (lesson 38).
        private int _chaseStuckTicks;
        // Same counter for the horde march task (TASK_GO_TO_COORD_ANY_MEANS can be engine-cleared
        // too - blocked events, task-slot reuse, being bumped mid-march). See the Wandering branch.
        private int _marchStuckTicks;
        // Burning zombies (molotovs, exploded cars): MakeZombieLike sets config flag 430
        // (CPED_CONFIG_FLAG_IgnoreBeingOnFire) so they keep attacking instead of fire-panicking,
        // but that flag ALSO suppresses the engine fire's damage over time - without this, a
        // molotov'd zombie would burn forever. Re-applies scripted burn damage on a cadence
        // while ignited or standing in a fire pool (see the burn check in Update).
        private int _burnCheckTimer;

        protected ZombieAI(Ped ped, Vector3 spawnOrigin)
        {
            Ped = ped;
            _spawnOrigin = spawnOrigin;
            ped.KeepTaskWhenMarkedAsNoLongerNeeded = true;
            ped.BlockPermanentEvents = true;
            ped.MakeZombieLike();
            Type = ZombieType.Walker;
            _facialAnim = PedExtensions.FacialMoodAnims[_facialRng.Next(PedExtensions.FacialMoodAnims.Length)];
            Ped.ApplyZombieFace(_facialAnim, false);
            Ped.ApplyZombieFaceGore();
        }

        public virtual void Update()
        {
            if (Ped?.Exists() is not true)
            {
                State = ZombieState.Dead;
                return;
            }

            UpdateBlip();

            if (_attackCooldown > 0)
                _attackCooldown--;

            // Scripted zombie speech: STOP_PED_SPEAKING + DISABLE_PED_PAIN_AUDIO were set once
            // at spawn (they persist), so no per-frame call is needed. Play gurgle/moan reasons
            // on a cadence, gated by player range so a full horde isn't a noise wall. Pain
            // audio must be re-enabled for the single PLAY_PAIN call (muted while disabled).
            if (_gurgleTimer <= 0)
            {
                if (Ped.Position.DistanceTo2D(Game.Player.Character.Position) < ZombieSoundRange
                    && _soundRng.Next(100) < ZombieSoundChance)
                {
                    Function.Call(Hash.DISABLE_PED_PAIN_AUDIO, Ped, false);
                    Function.Call(Hash.PLAY_PAIN, Ped, ZombieGurgleReasons[_soundRng.Next(ZombieGurgleReasons.Length)], 0f);
                    Function.Call(Hash.DISABLE_PED_PAIN_AUDIO, Ped, true);
                }
                _gurgleTimer = ZombieSoundInterval;
            }
            else
            {
                _gurgleTimer--;
            }

            // The game clears SET_FACIAL_IDLE_ANIM_OVERRIDE whenever the ped starts a new
            // task (wander/chase/attack anims), so a one-time apply at spawn only shows on
            // true idles - and zombies are almost never idle. Re-apply the expression on a
            // short cadence, and fire a PLAY_FACIAL_ANIM one-shot on a slower cadence: those
            // play even mid-task, so the face is actually visible. Both no-op until the
            // facial dicts are loaded.
            if (++_facialOverrideTimer % 30 == 0)
            {
                Ped.ApplyZombieFace(_facialAnim, true);
                if (_facialOverrideTimer % 300 == 0)
                    Ped.PlayZombieFaceAnim(_facialAnim);
            }

            if (Ped.Health <= 30 && Ped.Health > 0 && !Ped.IsDead)
            {
                if (!_ragdollDisabled)
                {
                    _ragdollDisabled = true;
                    Ped.DisableRagdoll();
                    Ped.ResetClipsets();
                }
            }

            if (Ped.IsDead)
            {
                State = ZombieState.Dead;
                // The engine's death groan is suppressed by the spawn-time DISABLE_PED_PAIN_AUDIO,
                // so the vanilla human moan never fires. Play the scripted zombie death moan here
                // on the exact frame the ped dies - the same moment the engine would voice it, so
                // the ped is still audio-valid. Re-enable pain audio for the single PLAY_PAIN call.
                if (!_deathMoanPlayed)
                {
                    _deathMoanPlayed = true;
                    Function.Call(Hash.DISABLE_PED_PAIN_AUDIO, Ped, false);
                    Function.Call(Hash.PLAY_PAIN, Ped, 27, 0f); // AUD_DAMAGE_REASON_DYING_MOAN
                }
                return;
            }

            // Zombies don't swim. The pathing hints (SET_PED_PATH_MAY_ENTER_WATER) don't hold
            // for every task type, so if one still gets into water, cancel the swim task and
            // path it to the nearest dry navmesh polygon. No teleporting - that reads as the
            // zombie popping around - TASK_GO_TO_COORD_ANY_MEANS lets it swim/walk itself out.
            // GET_SAFE_COORD_FOR_PED with the NOT_WATER search flag (8) guarantees the target
            // isn't a water polygon; in deep water with no polygon nearby, just aim at the
            // player (presumably on land) so it naturally reaches shore. Cooldown: a zombie
            // mid-swim stays IsInWater until it's fully out, so re-targeting every frame would
            // restart the task before it ever advances.
            if (Ped.IsInWater || Ped.IsSwimming)
            {
                if (_waterRecoveryCooldown <= 0)
                {
                    Ped.Task.ClearAllImmediately();
                    var safe = new OutputArgument();
                    Vector3 dest;
                    if (Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED, Ped.Position.X, Ped.Position.Y, Ped.Position.Z, false, safe, 2 | 4 | 8))
                        dest = safe.GetResult<Vector3>();
                    else if (Game.Player.Character?.Exists() is true)
                        dest = Game.Player.Character.Position;
                    else
                        dest = _spawnOrigin;
                    Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, Ped, dest.X, dest.Y, dest.Z, 1f, 0, false, 0, 0f);
                    _waterRecoveryCooldown = 120;
                }
                return;
            }
            if (_waterRecoveryCooldown > 0)
                _waterRecoveryCooldown--;

            // Re-apply fire damage. MakeZombieLike sets config flag 430 (IgnoreBeingOnFire) so
            // burning zombies keep pressing Wandering/Chasing/attack instead of the engine's
            // fire-panic scattering, but the flag also kills the engine fire's damage-over-time -
            // a molotov'd zombie would stay on fire forever. Every ~0.5s, while the ped is
            // ignited (or standing in a fire pool, which also covers molotov ground fire), apply
            // scripted burn damage so it burns to death. Fireproof peds (an immune Horder) are
            // skipped so the horde immunity isn't script-damaged around.
            if (++_burnCheckTimer >= 30)
            {
                _burnCheckTimer = 0;
                if (!Ped.IsDead && !Ped.IsFireProof &&
                    (Ped.IsOnFire || Function.Call<int>(Hash.GET_NUMBER_OF_FIRES_IN_RANGE,
                        Ped.Position.X, Ped.Position.Y, Ped.Position.Z, 3f) > 0))
                {
                    Function.Call(Hash.APPLY_DAMAGE_TO_PED, Ped, 12, false);
                }
            }

            _tickCounter++;
            if (_tickCounter % _updateInterval != 0)
                return;

            UpdateTarget();
            ExecuteBehavior();
        }

        protected virtual void UpdateTarget()
        {
            if (Target?.Exists() is not true || Target.IsDead)
            {
                var newTarget = FindBestTarget();
                if (newTarget != null)
                {
                    Target = newTarget;
                    if (State != ZombieState.Attacking)
                        State = ZombieState.Chasing;
                }
                else if (Target?.Exists() is not true || Target.IsDead)
                {
                    // The old target died (often the zombie's own kill) and nothing else is
                    // around: drop it and fall back to Wandering instead of sitting in
                    // Attacking/Chasing against a corpse with no re-issued task - the
                    // "ambient zombies stand idle after killing an NPC" report.
                    Target = null;
                    if (State == ZombieState.Attacking || State == ZombieState.Chasing)
                        State = ZombieState.Wandering;
                }
            }
        }

        protected virtual void ExecuteBehavior()
        {
            // GET_SCRIPT_TASK_STATUS(ped, hash) checks the status of a SPECIFIC task, matched
            // by hash - the "0" being passed here never matches a real task hash, so it always
            // reports "not performing," which made the Wandering and Chasing branches below
            // re-issue their movement task from scratch on EVERY behavior tick. Re-issuing a
            // task restarts its animation from frame zero, which is exactly the "plays the
            // start of the anim and keeps restarting it" symptom - and it only showed up while
            // walking/chasing because Attacking never went through this path.
            //
            // Rather than hunt down the correct task-hash constant (native task hashes are
            // undocumented and change between game versions), the actual fix is that we don't
            // need a status check at all here: TASK_WANDER_IN_AREA and (with persistFollowing
            // = true) TASK_FOLLOW_TO_OFFSET_OF_ENTITY are both self-sustaining tasks that keep
            // running/re-targeting on their own. We only need to issue them once per state
            // entry, tracked here, not every tick.
            bool justEnteredState = State != _lastState;
            _lastState = State;

            switch (State)
            {
                case ZombieState.Idle:
                    Ped.Task.StandStill(1000);
                    break;

                case ZombieState.Wandering:
                    if (HordeMoveTarget is Vector3 hordeTarget)
                    {
                        // Horde member: march to the current horde destination. TASK_GO_TO_COORD
                        // is NOT in this SHVDN3's Hash enum, so use TASK_GO_TO_COORD_ANY_MEANS
                        // (a ped just walks it). Only (re)issue when entering the state or when
                        // the horde advanced the destination - reissuing every tick restarts the
                        // nav task from scratch (DOCS/08-Lessons-Learned.md lesson 5).
                        if (_lastHordeTarget != hordeTarget || justEnteredState)
                        {
                            _lastHordeTarget = hordeTarget;
                            if ((Ped.Position - hordeTarget).Length() > HordeArriveRadius)
                            {
                                Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, Ped,
                                    hordeTarget.X, hordeTarget.Y, hordeTarget.Z, MoveSpeed, 0, false, 0, 0f);
                                _marchStuckTicks = 0;
                            }
                            else
                            {
                                Ped.Task.StandStill(500);
                            }
                        }
                        else if ((Ped.Position - hordeTarget).Length() > HordeArriveRadius)
                        {
                            // The march task self-sustains while running, but the engine can
                            // clear it at any time (blocked events, task-slot reuse, being
                            // bumped during a stand-off), leaving a member standing frozen
                            // mid-march with State stuck in Wandering - the same class of bug
                            // as the stuck chase (lesson 50). Speed < 0.5f for ~30 behavior
                            // ticks -> re-issue the march task.
                            if (Ped.Speed < 0.5f)
                            {
                                if (++_marchStuckTicks > 30)
                                {
                                    _marchStuckTicks = 0;
                                    Function.Call(Hash.TASK_GO_TO_COORD_ANY_MEANS, Ped,
                                        hordeTarget.X, hordeTarget.Y, hordeTarget.Z, MoveSpeed, 0, false, 0, 0f);
                                }
                            }
                            else
                            {
                                _marchStuckTicks = 0;
                            }
                            Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, Ped, MoveSpeed);
                        }
                    }
                    else if (justEnteredState || --_wanderReissueTimer <= 0)
                    {
                        // TASK_WANDER_IN_AREA self-sustains while it runs, but the engine can
                        // clear a zombie's wander task (physics pushes, blocked events, task-slot
                        // reuse), leaving the ped standing with State stuck in Wandering - the
                        // "lots of ambient zombies just stand idle" report. Re-issue on a ~2s
                        // cadence; a wander re-issue just picks a new random point and does not
                        // cause the restart-stutter of re-issuing every tick (lesson 5).
                        // _wanderReissueTimer counts behavior ticks (one per _updateInterval
                        // frames), so 120/_updateInterval ≈ 2s at 60fps for every zombie type.
                        _wanderReissueTimer = 120 / _updateInterval;
                        Function.Call(Hash.TASK_WANDER_IN_AREA, Ped, _spawnOrigin.X, _spawnOrigin.Y, _spawnOrigin.Z, WanderRadius, 0f, 0f);
                    }
                    break;

                case ZombieState.Chasing:
                    if (Target?.Exists() is not true)
                    {
                        State = ZombieState.Wandering;
                        break;
                    }
                    if (Target.IsDead)
                    {
                        Target = null;
                        State = ZombieState.Wandering;
                        break;
                    }

                    var chaseDist = (Ped.Position - Target.Position).Length();
                    if (chaseDist <= AttackRange)
                    {
                        Ped.Task.ClearAll();
                        State = ZombieState.Attacking;
                        // Don't call OnAttack() here too - the Attacking branch below fires
                        // the first hit itself (cooldown starts at 0, so it goes on the very
                        // next behavior tick anyway). Calling it in both places meant every
                        // entry into melee range played the attack anim twice back-to-back,
                        // which read as a stutter right at the moment of engagement.
                    }
                    else if (chaseDist > VisionRange * 3f)
                    {
                        Target = null;
                        State = ZombieState.Wandering;
                    }
                    else
                    {
                        if (justEnteredState || _followTaskTarget != Target)
                        {
                            // Real signature: (ped, entity, offsetX, offsetY, offsetZ,
                            // movementSpeed:float, timeout:int, stoppingRange:float,
                            // persistFollowing:BOOL) - 9 args. Previous call passed only 8 with
                            // three wrong types (an int where movementSpeed/stoppingRange float
                            // slots were expected, and a float where the int timeout slot was
                            // expected - GTA natives bit-reinterpret mismatched types rather
                            // than convert them, so this was feeding garbage values into the
                            // task) and omitted persistFollowing entirely.
                            Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, Ped, Target, 0f, 0f, 0f, 1.0f, -1, AttackRange, true);
                            _followTaskTarget = Target;
                            _chaseStuckTicks = 0;
                        }
                        // The follow task self-sustains while running, but the engine can clear
                        // it at any time (blocked events, task-slot reuse, being bumped by the
                        // cars during a crew stand-off dismount) - and unlike Wandering, the
                        // Chasing branch only re-issued on state-entry/target-change, so a cleared
                        // follow task left the zombie standing frozen in Chasing while the
                        // stationary player never changed as a target: the "ambient zombies go
                        // idle during the stand-off" report (lesson 38 stuck-speed fallback).
                        if (Ped.Speed < 0.5f)
                        {
                            if (++_chaseStuckTicks > 30)
                            {
                                _chaseStuckTicks = 0;
                                Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, Ped, Target, 0f, 0f, 0f, 1.0f, -1, AttackRange, true);
                            }
                        }
                        else
                        {
                            _chaseStuckTicks = 0;
                        }
                        Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, Ped, MoveSpeed);
                    }
                    break;

                case ZombieState.Attacking:
                    if (Target?.Exists() is not true)
                    {
                        Target = null;
                        State = ZombieState.Wandering;
                        break;
                    }

                    var attackDist = (Ped.Position - Target.Position).Length();

                    // Hysteresis: the entry check above (chaseDist <= AttackRange) and this
                    // exit check used to share the exact same threshold - which is ALSO the
                    // stoppingRange passed to TASK_FOLLOW_TO_OFFSET_OF_ENTITY. Three separate
                    // things keyed off one exact number meant the ped's real position
                    // naturally hovered right on that boundary (normal nav-mesh/physics
                    // jitter), so it crossed back and forth over it constantly. Every
                    // crossing did Task.ClearAll() and re-issued a task (attack anim one way,
                    // follow task the other) - that's the stutter, and it's most visible right
                    // at melee range ("further" than fully closed-in contact), not once the
                    // ped is solidly inside AttackRange with margin to spare.
                    //
                    // Fix: only fall back to Chasing once the target is clearly outside
                    // AttackRange (+1m buffer). Inside that buffer but not yet in actual
                    // AttackRange, stay in Attacking and don't re-issue anything - the ped is
                    // already stopped there from the follow task and just needs the target to
                    // drift the last bit closer or further before we act again.
                    if (attackDist > AttackRange + 1f)
                    {
                        Ped.Task.ClearAll();
                        State = ZombieState.Chasing;
                        break;
                    }

                    if (attackDist <= AttackRange && _attackCooldown <= 0)
                    {
                        OnAttack();
                        _attackCooldown = 30;
                    }
                    break;
            }
        }

        protected virtual Ped FindBestTarget()
        {
            var player = Game.Player.Character;
            if (player?.Exists() is not true || player.IsDead)
                return null;

            if (Perception.CanDetectTarget(Ped, player, VisionRange))
                return player;

            if (Perception.CanHearTarget(Ped, player, HearingRange * 2f))
                return player;

            // Horde members march the sweep with the PLAYER as their one focus - a random
            // civilian dragging a member off the march into a detour (and into gunfire) is the
            // "horde gets distracted by random peds before crossing the player" report, and a
            // chasing horde loses most of its strength to the peds it chases. Skip the
            // nearest-ped scan entirely so only the player ever pulls a horde member off path.
            // Ambient zombies (HordeMoveTarget == null) keep the full perception scan.
            if (HordeMoveTarget is not null)
                return null;

            var nearby = World.GetNearbyPeds(Ped.Position, VisionRange);
            Ped best = null;
            float bestDist = float.MaxValue;

            foreach (var ped in nearby)
            {
                if (ped == player || ped.IsDead || ped.RelationshipGroup == PedExtensions.InfectedRelationshipGroup)
                    continue;

                if (Perception.CanDetectTarget(Ped, ped, VisionRange))
                {
                    float d = (Ped.Position - ped.Position).Length();
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = ped;
                    }
                }
                else if (Perception.CanHearTarget(Ped, ped, HearingRange))
                {
                    float d = (Ped.Position - ped.Position).Length();
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = ped;
                    }
                }
            }

            return best;
        }

        public virtual void OnTargetAcquired(Ped target)
        {
            Target = target;
            State = ZombieState.Chasing;
        }

        public virtual void OnAttack()
        {
            if (Target?.Exists() is not true)
                return;

            var dist = (Ped.Position - Target.Position).Length();
            // OnAttack() is now only ever called from the state machine's Attacking branch,
            // which already gates on dist <= AttackRange. The old "+ 1f" here let damage land
            // up to a meter past the actual reach the anim/state machine agreed on -
            // effectively invisible damage before the swing visually connects. Match the same
            // threshold everywhere instead of padding it further out.
            if (dist <= AttackRange)
            {
                // Point-blank fix: the old code re-issued TASK_PLAY_ANIM every attack
                // cooldown (30 ticks). The cooldown is shorter than the heavy-punch swing,
                // so the anim got restarted from frame 0 while still mid-play - at close
                // range that read as a stuttering loop of the punch's opening frames (it
                // only looked fine once the player walked out of range, because the zombie
                // then spent long enough in the Chasing walk that the anim finally played
                // out between hits). Wait for the current swing to finish before starting
                // another, and skip the damage on those hold ticks so damage stays synced
                // to the visible swing.
                if (PedExtensions.IsAttackAnimReady && _currentAttackAnim != null
                    && Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, Ped, PedExtensions.AttackAnimDict, _currentAttackAnim, 3))
                {
                    return;
                }

                if (PedExtensions.IsAttackAnimReady)
                {
                    _currentAttackAnim = AttackAnimVariations[_attackAnimRng.Next(AttackAnimVariations.Length)];
                    Function.Call(Hash.TASK_PLAY_ANIM, Ped, PedExtensions.AttackAnimDict, _currentAttackAnim, 8f, -8f, -1, 24, 0f, false, false, false);
                }

                var vehicle = Target.IsInVehicle() ? Target.CurrentVehicle : null;
                if (vehicle?.Exists() is true)
                {
                    // Occupant is behind the vehicle's body - beat on the vehicle,
                    // never the player inside. The attack range already gates this on
                    // distance to the occupant's ped, which is why the car is being hit
                    // at all. Also skip the infection roll: the hit never reaches the ped.
                    // BodyHealth drives the fire/explosion path at 0; EngineHealth stalls
                    // the car out. APPLY_DAMAGE_TO_VEHICLE is not in this SHVDN3 Hash enum.
                    // BodyHealth/EngineHealth are direct property writes that bypass
                    // SET_ENTITY_INVINCIBLE, so honor the god-mode flag explicitly here -
                    // otherwise Vehicle God Mode in the trainer can't stop zombie damage.
                    if (!vehicle.IsInvincible)
                    {
                        vehicle.BodyHealth -= Damage;
                        vehicle.EngineHealth -= Damage;
                    }
                }
                else
                {
                    // APPLY_DAMAGE_TO_PED is a scripted damage path that can still bite
                    // through SET_ENTITY_INVINCIBLE. Skip the hit when the target is
                    // god-mode so Player God Mode in the trainer holds against zombies too.
                    if (!Target.IsInvincible)
                    {
                        Function.Call(Hash.APPLY_DAMAGE_TO_PED, Target, Damage, true);
                    }
                }
            }
        }

        // Short-range red blip on the minimap so the player can see zombie positions. Re-checked
        // every tick so toggling ShowZombieBlips in the settings menu takes effect immediately.
        private void UpdateBlip()
        {
            BlipHelper.Ensure(ref _blip, Ped, BlipSprite.Enemy, BlipColor.Red, 0.7f,
                ModConfig.Current.ShowZombieBlips);
        }

        private void DeleteBlip()
        {
            BlipHelper.Delete(ref _blip);
        }

        public virtual void OnDeath()
        {
            State = ZombieState.Dead;
            DeleteBlip();
            Ped.ResetClipsets();
            Function.Call(Hash.STOP_PED_SPEAKING, Ped, true);
            Ped.MarkAsNoLongerNeeded();
            ZombieEvents.RaiseZombieDied(Ped);
        }

        public void Cleanup()
        {
            DeleteBlip();
            if (Ped?.Exists() is true)
            {
                Ped.MarkAsNoLongerNeeded();
                Ped.Delete();
            }
        }

        // Soft fade for horde members (distance despawn / horde teardown): removes the minimap
        // blip and lets the engine recycle the ped - unlike Cleanup() it does NOT hard-Delete,
        // which would pop the ped out of view right in front of the player.
        public void FadeOut()
        {
            DeleteBlip();
            if (Ped?.Exists() is true)
                Ped.MarkAsNoLongerNeeded();
        }
    }
}
