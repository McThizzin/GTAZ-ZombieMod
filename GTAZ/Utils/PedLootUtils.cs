using GTA;
using GTAZ.Utils;

namespace GTAZ.Utils
{
    // Shared weapon-loot drop for killed human peds (survivors and faction members). The pickup
    // model is left null (0 hash) so the game picks the weapon's default mesh. The mapping is the
    // union of the old SurvivorSystem + FactionSystem copies; every weapon either of them ever
    // issued to a ped is covered, and anything unmapped falls back to a pistol pickup.
    public static class PedLootUtils
    {
        public static void DropLoot(Ped ped, WeaponHash weapon)
        {
            if (ped?.Exists() is not true)
                return;
            World.CreatePickup(WeaponToPickup(weapon), ped.Position);
        }

        public static PickupType WeaponToPickup(WeaponHash weapon)
        {
            return weapon switch
            {
                WeaponHash.CombatPistol => PickupType.WeaponCombatPistol,
                WeaponHash.MicroSMG => PickupType.WeaponMicroSMG,
                WeaponHash.AssaultRifle => PickupType.WeaponAssaultRifle,
                WeaponHash.PumpShotgun => PickupType.WeaponPumpShotgun,
                WeaponHash.SawnOffShotgun => PickupType.WeaponSawnoffShotgun,
                WeaponHash.CarbineRifle => PickupType.WeaponCarbineRifle,
                _ => PickupType.WeaponPistol
            };
        }
    }
}
