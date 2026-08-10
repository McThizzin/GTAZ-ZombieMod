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
        Runner
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

        // Confirmed present in anim@ingame@melee@unarmed@streamed_variations_zombie.
        // Picked at random per swing so consecutive hits don't all play the same strike.
        private static readonly Random _attackAnimRng = new();
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
        private int _facialOverrideTimer;

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

        protected ZombieAI(Ped ped, Vector3 spawnOrigin)
        {
            Ped = ped;
            _spawnOrigin = spawnOrigin;
            ped.KeepTaskWhenMarkedAsNoLongerNeeded = true;
            ped.BlockPermanentEvents = true;
            ped.MakeZombieLike();
            Type = ZombieType.Walker;
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

            Function.Call(Hash.STOP_PED_SPEAKING, Ped, true);

            // The game clears SET_FACIAL_IDLE_ANIM_OVERRIDE whenever the ped starts a new
            // task (wander/chase/attack anims), so a one-time apply at spawn only shows on
            // true idles - and zombies are almost never idle. Re-apply on a short cadence
            // to keep the burned face up. ApplyBurningFace no-ops until the dicts are loaded.
            if (++_facialOverrideTimer % 30 == 0)
                Ped.ApplyBurningFace();

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
                            }
                            else
                            {
                                Ped.Task.StandStill(500);
                            }
                        }
                    }
                    else if (justEnteredState || --_wanderReissueTimer <= 0)
                    {
                        // TASK_WANDER_IN_AREA self-sustains while it runs, but the engine can
                        // clear a zombie's wander task (physics pushes, blocked events, task-slot
                        // reuse), leaving the ped standing with State stuck in Wandering - the
                        // "lots of ambient zombies just stand idle" report. Re-issue on a 2s
                        // cadence; a wander re-issue just picks a new random point and does not
                        // cause the restart-stutter of re-issuing every tick (lesson 5).
                        _wanderReissueTimer = 120;
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
