using GTA;
using GTA.Math;
using GTA.Native;

namespace GTAZ.UI.Trainer.Features
{
    public static class PlayerTrainer
    {
        private static Ped Player => Game.Player.Character;

        public static bool GodModeOn;
        public static bool InfiniteStaminaOn;
        public static bool SuperJumpOn;
        public static bool FastRunOn;

        public static void ShowStatus(string feature, bool enabled)
        {
            GTA.UI.Notification.PostTicker($"~b~{feature} {(enabled ? "~g~Enabled" : "~r~Disabled")}", false);
        }

        public static void ToggleGodMode(bool enabled)
        {
            GodModeOn = enabled;
            ApplyGodMode();
            ShowStatus("God Mode: ", enabled);
        }

        // The game clears the player's invincibility/proof flags on vehicle changes, teleports
        // and area streaming (that's why god mode "stopped working" mid-exploration while the
        // checkbox stayed checked), so the flags must be re-asserted every frame while enabled,
        // not set once on toggle. SET_PLAYER_INVINCIBLE covers kill-conditions the per-entity
        // proofs miss (fall damage, mission-side death checks).
        public static void ApplyGodMode()
        {
            var player = Game.Player.Character;
            if (player?.Exists() is not true)
                return;

            Function.Call(Hash.SET_PLAYER_INVINCIBLE, Game.Player.Handle, GodModeOn);
            player.IsInvincible = GodModeOn;
            player.IsBulletProof = GodModeOn;
            player.IsCollisionProof = GodModeOn;
            player.IsExplosionProof = GodModeOn;
            player.IsFireProof = GodModeOn;
            player.IsMeleeProof = GodModeOn;
            player.IsSmokeProof = GodModeOn;
            player.IsSteamProof = GodModeOn;
        }

        public static void Heal()
        {
            Player.Health = Player.MaxHealth;
            Player.Armor = Game.Player.MaxArmor;
            GTA.UI.Notification.PostTicker("Player has been healed.", false);
        }

        public static void InfiniteStamina(bool enabled)
        {
            InfiniteStaminaOn = enabled;
            ShowStatus("Infinite Stamina: ", enabled);
        }

        public static void Clean()
        {
            Player.ClearBloodDamage();
            Player.ClearVisibleDamage();
            GTA.UI.Notification.PostTicker("~b~Player has been cleaned.", false);
        }

        public static void SetVisibility(bool invisible)
        {
            Player.IsVisible = !invisible;
            ShowStatus("Player Visibility: ", invisible);
        }

        public static void SetCash(int amount)
        {
            string statName;
            int modelHash = Player.Model.Hash;
            if (modelHash == unchecked((int)GTA.StringHash.AtStringHash("player_zero", 0)))
            {
                statName = "SP0_TOTAL_CASH"; // Michael
            }
            else if (modelHash == unchecked((int)GTA.StringHash.AtStringHash("player_one", 0)))
            {
                statName = "SP1_TOTAL_CASH"; // Franklin
            }
            else if (modelHash == unchecked((int)GTA.StringHash.AtStringHash("player_two", 0)))
            {
                statName = "SP2_TOTAL_CASH"; // Trevor
            }
            else
            {
                GTA.UI.Notification.PostTicker("~r~Unknown character model. Cannot set cash.", false);
                return;
            }

            Function.Call(Hash.STAT_SET_INT, GTA.StringHash.AtStringHash(statName, 0), amount, true);
            GTA.UI.Notification.PostTicker($"~g~Set money to: ~s~${amount}", false);
        }

        public static void SetWantedLevel(int level)
        {
            Game.Player.Wanted.SetWantedLevel(level, false);
            Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            GTA.UI.Notification.PostTicker($"~b~Wanted Level Set: {level}", false);
        }

        public static void NeverWanted(bool enabled)
        {
            Game.MaxWantedLevel = enabled ? 0 : 5;
            if (enabled)
            {
                Game.Player.Wanted.SetWantedLevel(0, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
            ShowStatus("Never Wanted: ", enabled);
        }

        public static void SuperJump(bool enabled)
        {
            SuperJumpOn = enabled;
            ShowStatus("Super Jump: ", enabled);
        }

        public static void FastRun(bool enabled)
        {
            FastRunOn = enabled;
            Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, Game.Player.Handle, enabled ? 1.49f : 1.0f);
            ShowStatus("Fast Run: ", enabled);
        }

        // Teleporting to a waypoint is two-phase because a far/unstreamed destination has no
        // ground data until the player is near it, and GET_GROUND_Z_FOR_3D_COORD only resolves
        // within render distance (it also can't cast from a very high start - the raycast caps
        // out). This ports the sample trainer's proven approach (ModSamples\GTAV-Trainer,
        // PlayerFunctions.TeleportToWayPoint): teleport the player to a LOW test height over the
        // waypoint, let the area stream a few ticks, probe the ground from that same height, and
        // step the height up until one succeeds. Starting low (100m) stays out of the T-pose
        // altitude range and resolves on the first step for normal terrain.
        //
        // The old implementation parked the player at 1300m and cast from 2000m - both out of
        // bounds: the player T-posed at that altitude and the probe never returned, so it fell
        // through to the timeout and bounced the player back to their original spot.
        private static readonly float[] TeleportTestHeights = [100f, 150f, 200f, 250f, 300f, 400f, 500f, 600f, 800f, 1000f];

        private static Vector3? _pendingTeleport;
        private static Vector3? _teleportReturnPosition;
        private static int _teleportHeightIndex;
        private static int _teleportWaitTicks;

        public static void Tick()
        {
            if (GodModeOn)
                ApplyGodMode();

            if (_pendingTeleport is not Vector3 pending)
                return;

            if (Game.Player.Character?.Exists() is not true)
            {
                _pendingTeleport = null;
                return;
            }

            Entity entity = Player.IsInVehicle() ? (Entity)Player.CurrentVehicle : Player;

            // Give the destination a few ticks to stream (stands in for the trainer's
            // Script.Wait(100)) before probing from the current test height.
            if (_teleportWaitTicks++ < 6)
                return;
            _teleportWaitTicks = 0;

            float height = TeleportTestHeights[_teleportHeightIndex];
            var ground = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, pending.X, pending.Y, height, ground, true, false))
            {
                entity.Position = new Vector3(pending.X, pending.Y, ground.GetResult<float>() + 1f);
                _pendingTeleport = null;
                _teleportReturnPosition = null;
                GTA.UI.Notification.PostTicker("~b~Teleported to waypoint.", false);
                return;
            }

            // This height didn't clear the terrain (e.g. mountain) - step up and try again.
            if (++_teleportHeightIndex >= TeleportTestHeights.Length)
            {
                if (_teleportReturnPosition is Vector3 back)
                    entity.Position = back;
                _pendingTeleport = null;
                _teleportReturnPosition = null;
                GTA.UI.Notification.PostTicker("~r~Couldn't find ground at the waypoint.", false);
                return;
            }

            entity.Position = new Vector3(pending.X, pending.Y, TeleportTestHeights[_teleportHeightIndex]);
        }

        public static void TeleportToWaypoint()
        {
            if (!Game.IsWaypointActive)
            {
                GTA.UI.Notification.PostTicker("~r~Error:~s~ No waypoint set.", false);
                return;
            }

            var waypoint = World.WaypointPosition;
            Entity entity = Player.IsInVehicle() ? (Entity)Player.CurrentVehicle : Player;

            // Fast path: for a streamed (nearby) waypoint World.WaypointPosition.Z is a real
            // ground estimate, so GET_SAFE_COORD_FOR_PED resolves an instant, exact spot.
            var safe = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_SAFE_COORD_FOR_PED, waypoint.X, waypoint.Y, waypoint.Z, false, safe, 2 | 4 | 8))
            {
                entity.Position = safe.GetResult<Vector3>() + new Vector3(0f, 0f, 1f);
                GTA.UI.Notification.PostTicker("~b~Teleported to waypoint.", false);
                return;
            }

            _teleportReturnPosition = entity.Position;
            _teleportHeightIndex = 0;
            _teleportWaitTicks = 0;
            _pendingTeleport = new Vector3(waypoint.X, waypoint.Y, waypoint.Z);
            entity.Position = new Vector3(waypoint.X, waypoint.Y, TeleportTestHeights[0]);
            GTA.UI.Notification.PostTicker("~b~Teleporting to waypoint...", false);
        }
    }
}
