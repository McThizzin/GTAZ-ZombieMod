using System;
using GTA;

namespace GTAZ.UI.Trainer.Features
{
    public static class WeaponTrainer
    {
        private static Ped Player => Game.Player.Character;

        public static bool InfiniteAmmoEnabled;

        public static void GiveAllWeapons()
        {
            foreach (WeaponHash weapon in Enum.GetValues(typeof(WeaponHash)))
            {
                if (weapon == WeaponHash.Unarmed)
                    continue;
                try
                {
                    Player.Weapons.Give(weapon, 9999, false, false);
                }
                catch
                {
                }
            }
            GTA.UI.Notification.PostTicker("~b~All weapons given.", false);
        }

        public static void RemoveAllWeapons()
        {
            Player.Weapons.RemoveAll();
            GTA.UI.Notification.PostTicker("~b~All weapons removed.", false);
        }

        public static void SelectWeapon(WeaponHash weapon)
        {
            try
            {
                Player.Weapons.Give(weapon, 999, true, true);
                GTA.UI.Notification.PostTicker($"~b~Gave weapon: ~g~{weapon}", false);
            }
            catch
            {
                GTA.UI.Notification.PostTicker("~r~Failed to give weapon.", false);
            }
        }

        public static void SetInfiniteAmmo(bool enabled)
        {
            InfiniteAmmoEnabled = enabled;
            var weapon = Player.Weapons.Current;
            if (weapon != null && weapon.Hash != WeaponHash.Unarmed)
                weapon.InfiniteAmmo = enabled;
        }

        public static void SetAmmo(int amount)
        {
            var weapon = Player.Weapons.Current;
            if (weapon != null && weapon.Hash != WeaponHash.Unarmed)
            {
                weapon.Ammo = amount;
                GTA.UI.Notification.PostTicker("~b~Ammo set.", false);
            }
            else
            {
                GTA.UI.Notification.PostTicker("~r~No weapon to modify.", false);
            }
        }

        public static void RemoveCurrentWeapon()
        {
            var weapon = Player.Weapons.Current;
            if (weapon != null && weapon.Hash != WeaponHash.Unarmed)
            {
                Player.Weapons.Remove(weapon.Hash);
                GTA.UI.Notification.PostTicker($"~b~Removed weapon: ~g~{weapon.Hash}", false);
            }
            else
            {
                GTA.UI.Notification.PostTicker("~r~No weapon to remove.", false);
            }
        }
    }
}
