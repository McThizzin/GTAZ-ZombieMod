using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Extensions;
using GTAZ.Utils;

namespace GTAZ.AI
{
    // Armed human NPC behavior. Fighters hold their ground and shoot zombies (TASK_COMBAT_PED),
    // cowards run from the nearest zombie (TASK_SMART_FLEE_PED). Both revert to wandering their
    // spawn area when no zombie is nearby. Tasks are only (re)issued on a mode or target change
    // - same lesson as ZombieAI: reissuing every tick restarts the task from scratch.
    public class SurvivorAI(Ped ped, Vector3 origin, bool isCoward, WeaponHash weapon)
    {
        public Ped Ped { get; } = ped;
        public Vector3 Origin { get; } = origin;
        public bool IsCoward { get; } = isCoward;
        public WeaponHash Weapon { get; } = weapon;

        private readonly float _perceptionRange = 45f;
        private readonly float _fearRange = 35f;
        private readonly int _updateInterval = 6;
        private int _tickCounter;
        private Blip _blip;

        private enum Mode { None, Wander, Combat, Flee }
        private Mode _mode = Mode.None;
        private Ped _taskTarget;

        // Survivors are COMPANION to the player (see SetupSurvivorRelationships), so the native
        // AI never puts them in combat with the player - not even when the player damages them
        // (Respect was tried first and still retaliated on player damage, the "hostile until they
        // join" bug). There is no scripted player-aggro path. Stray bullets don't flip them
        // hostile (combat attributes 25/68 + Companion stance) - they just take the hit and keep
        // fighting zombies.

        public void Update()
        {
            if (Ped?.Exists() is not true || Ped.IsDead)
                return;

            UpdateBlip();

            _tickCounter++;
            if (_tickCounter % _updateInterval != 0)
                return;

            Ped zombie = IsCoward ? FindNearestZombie(_fearRange) : FindNearestZombie(_perceptionRange);

            if (zombie != null)
            {
                if (IsCoward)
                {
                    if (_mode != Mode.Flee || _taskTarget != zombie)
                    {
                        _mode = Mode.Flee;
                        _taskTarget = zombie;
                        // (ped, fleeTarget, safeDistance, fleeTime, preferPavements,
                        //  updateIfVehicleIsUpsideDown, updateIfInWater) - 7 args.
                        Function.Call(Hash.TASK_SMART_FLEE_PED, Ped, zombie, 60f, -1, 0, 0, 0);
                    }
                }
                else if (_mode != Mode.Combat || _taskTarget != zombie || !Ped.IsInCombat)
                {
                    _mode = Mode.Combat;
                    _taskTarget = zombie;
                    Function.Call(Hash.TASK_COMBAT_PED, Ped, zombie, 0, 16);
                }
            }
            else if (_mode != Mode.Wander)
            {
                _mode = Mode.Wander;
                _taskTarget = null;
                Ped.Task.ClearAll();
                Function.Call(Hash.TASK_WANDER_IN_AREA, Ped, Origin.X, Origin.Y, Origin.Z, 12f, 0f, 0f);
            }
        }

        private Ped FindNearestZombie(float range)
        {
            var player = Game.Player.Character;
            foreach (var ped in World.GetNearbyPeds(Ped.Position, range))
            {
                if (ped == player || !ped.Exists() || ped.IsDead || !ped.IsZombie())
                    continue;

                if (Ped.HasClearLineOfSightTo(ped))
                    return ped;
            }
            return null;
        }

        // Green short-range blip so survivors are findable (they're otherwise easy to miss while
        // the horde gunfire is happening elsewhere). Re-checked every tick so the ShowSurvivorBlips
        // toggle takes effect immediately.
        private void UpdateBlip()
        {
            BlipHelper.Ensure(ref _blip, Ped, BlipSprite.Friend, BlipColor.Green, 0.8f,
                ModConfig.Current.ShowSurvivorBlips);
        }

        private void DeleteBlip()
        {
            BlipHelper.Delete(ref _blip);
        }

        // Stops the SurvivorAI owning this ped: deletes the survivor blip and marks it as not
        // tracked. Used by SurvivorSystem.DetachAmbientSurvivor when a survivor is recruited -
        // the ped must not keep getting wander/combat/flee tasks or distance-based despawns now
        // that FactionSystem's recruit AI owns it.
        public void Detach()
        {
            DeleteBlip();
        }

        public void OnDeath()
        {
            DeleteBlip();
            if (Ped?.Exists() is true)
                Ped.MarkAsNoLongerNeeded();
        }

        public void Cleanup()
        {
            DeleteBlip();
            if (Ped?.Exists() is true)
                Ped.Delete();
        }
    }
}
