using GTA;
using GTA.Native;

namespace GTAZ.UI.Trainer.Features
{
    public static class VehicleTrainer
    {
        private static Ped Player => Game.Player.Character;

        private static Vehicle CurrentVehicle
        {
            get
            {
                if (!Player.IsInVehicle())
                    return null;
                return Player.CurrentVehicle;
            }
        }

        public static bool VehicleGodModeOn;

        public static void VehicleGodMode(bool enabled)
        {
            VehicleGodModeOn = enabled;
            if (!Player.IsInVehicle())
            {
                if (enabled)
                    GTA.UI.Notification.PostTicker("~b~Vehicle God Mode: ~y~will apply when you enter a vehicle", false);
                return;
            }
            ApplyVehicleGodMode();
            PlayerTrainer.ShowStatus("Vehicle God Mode: ", enabled);
        }

        // Re-asserted every frame while enabled (and on every vehicle the player enters): the
        // game resets vehicle damage-proof flags on damage events and when switching vehicles, so
        // setting them once on toggle lets god mode "wear off" mid-drive (same failure as player
        // god mode). No notification here - this runs every frame.
        public static void ApplyVehicleGodMode()
        {
            if (!VehicleGodModeOn)
                return;
            var vehicle = CurrentVehicle;
            if (vehicle?.Exists() is not true)
                return;
            vehicle.IsInvincible = true;
            vehicle.CanBeVisiblyDamaged = false;
            vehicle.CanTiresBurst = false;
            vehicle.CanEngineDegrade = false;
            vehicle.CanWheelsBreak = false;
        }

        public static void Fix()
        {
            var vehicle = CurrentVehicle;
            if (vehicle is null)
            {
                GTA.UI.Notification.PostTicker("~b~Player is not in a ~r~Vehicle", false);
                return;
            }

            vehicle.Repair();
            vehicle.Wash();
            GTA.UI.Notification.PostTicker("~b~Vehicle has been repaired + cleaned.", false);
        }

        public static void Clean()
        {
            var vehicle = CurrentVehicle;
            if (vehicle is null)
            {
                GTA.UI.Notification.PostTicker("~b~Player is not in a ~r~Vehicle", false);
                return;
            }

            vehicle.Wash();
            GTA.UI.Notification.PostTicker("~b~Vehicle has been cleaned.", false);
        }

        public static void Delete()
        {
            var vehicle = CurrentVehicle;
            if (vehicle is null)
            {
                GTA.UI.Notification.PostTicker("~b~Player is not in a ~r~Vehicle", false);
                return;
            }

            vehicle.Delete();
            GTA.UI.Notification.PostTicker("~b~Vehicle has been deleted.", false);
        }

        public static void Seatbelt(bool enabled)
        {
            if (!Player.IsInVehicle())
            {
                GTA.UI.Notification.PostTicker("~b~Player is not in a ~r~Vehicle", false);
                return;
            }

            Function.Call(Hash.SET_PED_CAN_BE_KNOCKED_OFF_VEHICLE, Player, enabled ? 1 : 0);
            Function.Call(Hash.SET_PED_CAN_RAGDOLL, Player, !enabled);
            PlayerTrainer.ShowStatus("Seatbelt: ", enabled);
        }
    }
}
