using System;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Extensions;

namespace GTAZ.Systems
{
    public class WorldSystem
    {
        private bool _configured;
        private bool _reset;
        private int _clearTimer;

        public bool IsApocalypseMode { get; set; } = true;
        public bool ClearTraffic { get; set; } = true;
        public bool DisableCops { get; set; } = true;

        public void OnTick()
        {
            // Game-time scale (the "TimeScale" world setting). Applied every frame so toggling
            // it in the settings menu takes effect immediately and the engine can't silently
            // clear it (same re-assert rule as the trainer god modes, lesson 35). 1.0 = normal
            // GTA time; values below slow the whole simulation (0.0 freezes it, so clamp).
            Function.Call(Hash.SET_TIME_SCALE, Math.Max(0.1f, ModConfig.Current.TimeScale));

            if (!IsApocalypseMode)
            {
                if (!_reset)
                {
                    ResetWorld();
                    _reset = true;
                }
                return;
            }

            _reset = false;

            if (!_configured)
            {
                ConfigureWorld();
                _configured = true;
            }

            if (ClearTraffic)
            {
                WorldExtensions.SetAllDensitiesZero();
                _clearTimer++;
                if (_clearTimer >= 60)
                {
                    _clearTimer = 0;
                    ClearAmbientPopulation(Game.Player.Character.Position);
                }
            }

            if (DisableCops)
            {
                Game.MaxWantedLevel = 0;
                Game.Player.Wanted.SetWantedLevel(0, false);
                Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
            }
        }

        private const float ClearRadius = 200f;

        private void ClearAmbientPopulation(Vector3 center)
        {
            foreach (var ped in World.GetNearbyPeds(center, ClearRadius))
            {
                if (!ped.Exists() || ped.IsPlayer || ped.IsZombie() || ped.IsSurvivor() || ped.IsFactionPed())
                    continue;
                ped.MarkAsNoLongerNeeded();
            }

            foreach (var vehicle in World.GetNearbyVehicles(center, ClearRadius))
            {
                if (!vehicle.Exists())
                    continue;
                var driver = vehicle.Driver;
                if (driver == Game.Player.Character || driver?.IsZombie() is true)
                    continue;
                vehicle.MarkAsNoLongerNeeded();
            }
        }

        private void ConfigureWorld()
        {
            Game.MaxWantedLevel = 0;
            Game.Player.Wanted.SetWantedLevel(0, false);
            Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
        }

        private void ResetWorld()
        {
            Function.Call(Hash.SET_TIME_SCALE, 1.0f);
            Game.MaxWantedLevel = 5;
            Game.Player.Wanted.SetWantedLevel(0, false);
            Game.Player.Wanted.ApplyWantedLevelChangeNow(false);
        }

        public void OnAborted()
        {
            ResetWorld();
        }
    }
}
