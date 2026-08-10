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
        // Optional bike for The Lost ambient riders (set after creation when the spawn mounted
        // one). When the rider dies the bike is handed back to the game (ReleaseVehicle) so it
        // survives as a rideable hexer/daemon; Cleanup (script despawn) still deletes it.
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

        // A rider's bike outlives them: drop persistence and tracking so the game's cleaner can
        // reclaim it later, but the bike stays on the street for the player to grab instead of
        // vanishing the instant its rider dies. Cleanup (script-driven despawn/eviction) still
        // deletes the vehicle so evicted peds don't leave riderless bike piles behind.
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
            Vehicle.Delete();
            Vehicle = null;
        }
    }
}
