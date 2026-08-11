using GTA;
using GTA.Math;
using GTAZ.AI;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    public enum FactionRole
    {
        Patrol,  // ambient wanderer
        Guard,   // outpost defender/guard - holds a zone center
        Raider,  // bandit patrol member
        Recruit, // player's group member
        Crew     // gang-car crew member (The Families car encounter)
    }

    // Wraps a single Phase 4 faction ped plus its budget slot. The slot handle is kept so
    // recruits can be marked IsEssential (the budget never evicts them) and so the slot can be
    // released on cleanup. Blip rendering mirrors SurvivorAI.UpdateBlip - re-checked every tick
    // so the ShowFactionBlips toggle applies immediately.
    public class FactionNPC(Ped ped, FactionDef faction, Vector3 origin, FactionRole role,
        TerritoryZone homeZone, PedSlot slot, WeaponHash weapon)
    {
        public Ped Ped { get; } = ped;
        public FactionDef Faction { get; } = faction;
        public Vector3 Origin { get; } = origin;
        public FactionRole Role { get; private set; } = role;
        public TerritoryZone HomeZone { get; } = homeZone;
        public PedSlot Slot { get; } = slot;
        public WeaponHash Weapon { get; } = weapon;
        // Optional vehicle owned by this ped: The Lost riders' bike and the Families crew DRIVER's
        // gang car (set after creation). The owner holds it so the vehicle survives their death
        // (handed back to the game in ReleaseVehicle so it stays parked and grab-able) and is
        // deleted only when the script deliberately despawns the ped while the vehicle is still in
        // active use (Cleanup). When the crew dismounts into an on-foot stand-off the driver's AI
        // fires the stand-off callback, which releases the car the same way (lesson 43). Passengers
        // hold no Vehicle reference - they ride the driver's car (their CrewVehicle is on the AI).
        public Vehicle Vehicle { get; internal set; }
        // Last known live ped position (updated each tick) so diagnostics can report where a
        // vanished recruit was last seen - Ped can't be queried once the engine has culled it.
        public Vector3 LastPosition { get; private set; } = origin;

        private readonly FactionAI _ai = new(ped, faction, origin, role, homeZone != null);
        private Blip _blip;

        public bool IsRecruit => Role == FactionRole.Recruit;

        public void Update()
        {
            if (Ped?.Exists() is true)
                LastPosition = Ped.Position;
            _ai.Update();
            UpdateBlip();
        }

        public void SetRole(FactionRole role)
        {
            Role = role;
            _ai.SetRole(role);
        }

        // Crew cars (FactionRole.Crew): the spawner tells the AI which gang car + seat this member
        // belongs to so the warp-recovery net (TryReWarpCrew) can re-warp a member whose initial
        // TASK_WARP_PED_INTO_VEHICLE failed. Also wires the stand-off release: when this member
        // leaves its car for good the AI fires ReleaseVehicle, so the DRIVER's car is handed back
        // to the game (parked + grab-able) instead of vanishing mid-scene - passengers' releases
        // are no-ops (they hold no Vehicle). Only the DRIVER additionally gets Vehicle set
        // (ownership/lifecycle); passengers just ride along.
        public void AttachCrewCar(Vehicle car, int seat)
        {
            _ai.CrewVehicle = car;
            _ai.CrewSeat = seat;
            _ai.OnCrewStandOff = ReleaseVehicle;
        }

        // Recruits are never budget-evicted (their slot is essential and the ped is persistent).
        public void MarkEssential()
        {
            if (Ped?.Exists() is true)
                Ped.IsPersistent = true;
            if (Slot is null)
                return;
            Slot.IsEssential = true;
        }

        public void ClearEssential()
        {
            if (Ped?.Exists() is true)
                Ped.IsPersistent = false;
            if (Slot is null)
                return;
            Slot.IsEssential = false;
        }

        private void UpdateBlip()
        {
            bool isFriendly = IsRecruit || Faction.RelationToPlayer != PlayerRelation.Hostile;
            bool isGreen = IsRecruit;
            BlipHelper.Ensure(ref _blip, Ped,
                isFriendly ? BlipSprite.Friend : BlipSprite.Enemy,
                isGreen ? BlipColor.Green : Faction.Color,
                0.8f, ModConfig.Current.ShowFactionBlips, Faction.DisplayName);
        }

        private void DeleteBlip()
        {
            BlipHelper.Delete(ref _blip);
        }

        public void OnDeath()
        {
            DeleteBlip();
            ReleaseVehicle();
            if (Ped?.Exists() is true)
                Ped.MarkAsNoLongerNeeded();
        }

        public void Cleanup()
        {
            DeleteBlip();
            DeleteVehicle();
            if (Ped?.Exists() is true)
                Ped.Delete();
        }

        // A rider's bike (or a crew car) outlives its owner: drop persistence and tracking so the
        // game's cleaner can reclaim it later, but the vehicle stays on the street for the player
        // to grab instead of vanishing the instant its owner dies. Cleanup (script-driven
        // despawn/eviction) still deletes the vehicle so evicted peds don't leave riderless
        // vehicle piles behind.
        private void ReleaseVehicle()
        {
            if (Vehicle?.Exists() is not true)
                return;
            Vehicle.IsPersistent = false;
            Vehicle.MarkAsNoLongerNeeded();
            Vehicle = null;
        }

        private void DeleteVehicle()
        {
            if (Vehicle?.Exists() is not true)
                return;

            // Script-driven despawn (distance fade / eviction) of the owning driver while the
            // vehicle is still in active use. If other living crew are still aboard, hand the
            // vehicle to the game (release) instead of deleting it out from under them - that was
            // the "car vanished mid-scene" report. An empty crew car (give-up drive-off with
            // nobody left inside, or a riderless bike) is deleted outright so no vehicle piles
            // build up. Occupant-aware also protects a car the PLAYER has jacked mid-chase
            // (lesson 43).
            foreach (var occupant in Vehicle.Occupants)
            {
                if (occupant != null && occupant != Ped && occupant.Exists() && !occupant.IsDead)
                {
                    Vehicle.IsPersistent = false;
                    Vehicle.MarkAsNoLongerNeeded();
                    Vehicle = null;
                    return;
                }
            }

            Vehicle.Delete();
            Vehicle = null;
        }
    }
}
