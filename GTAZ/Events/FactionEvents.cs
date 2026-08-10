using System;
using GTA;
using GTAZ.Data;

namespace GTAZ.Events
{
    public static class FactionEvents
    {
        public static event Action<TerritoryZone> OnZoneCaptured;
        public static event Action<Ped, FactionDef> OnFactionNPCDeath;
        public static event Action<Ped> OnRecruitJoined;
        public static event Action<Ped> OnRecruitLeft;

        public static void RaiseZoneCaptured(TerritoryZone zone) => OnZoneCaptured?.Invoke(zone);
        public static void RaiseFactionNPCDeath(Ped ped, FactionDef faction) => OnFactionNPCDeath?.Invoke(ped, faction);
        public static void RaiseRecruitJoined(Ped ped) => OnRecruitJoined?.Invoke(ped);
        public static void RaiseRecruitLeft(Ped ped) => OnRecruitLeft?.Invoke(ped);
    }
}
