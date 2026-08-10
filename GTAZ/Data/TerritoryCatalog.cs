using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;

namespace GTAZ.Data
{
    // Static territory data: the 12 capturable game-zone centers, the home faction per zone
    // (matching Territories.txt), and the player-mapped safe wave spawn points. Pure data - the
    // FactionSystem ctor calls BuildZones() and stores the list. Zone membership is resolved from
    // each center's game-zone hash at runtime, so no hardcoded zone-name table can go stale.
    public static class TerritoryCatalog
    {
        public static List<TerritoryZone> BuildZones()
        {
            Vector3[] centers =
            [
                new(-2128.68f, 3122.73f, 32.81f),  // Fort Zancudo (ARMYB)
                new(1724.20f, 2535.20f, 45.61f),   // Bolingbroke Penitentiary (JAIL)
                new(-303.63f, 6229.78f, 31.45f),   // Paleto Bay (PALETO)
                new(1922.43f, 4796.51f, 43.89f),   // Grapeseed (GRAPES)
                new(-1116.17f, 4928.03f, 218.29f), // Altruist Camp (CMSW)
                new(152.98f, -1780.49f, 29.05f),   // Grove Street (DAVIS)
                new(264.41f, -159.50f, 61.94f),    // Hawick (HAWICK), moved from Vinewood Racetrack (HORS)
                new(-745.16f, -948.94f, 17.93f),   // Little Seoul (KOREAT), moved from Vespucci Canals (VCANA)
                new(979.09f, -3115.16f, 5.90f),    // The Docks (TERMINA)
                new(-2302.58f, 395.16f, 176.10f),  // KORTZ Center (PBLUFF)
                new(1524.33f, -2108.27f, 76.72f),  // Scrapyard (EBURO)
                new(-421.03f, 1141.28f, 325.91f),  // Galileo Observatory (OBSERV)
            ];

            // Home faction per territory, matching Territories.txt. Defenders/defense waves/raids
            // spawn as this faction and a surrendered zone reverts to it (the zone's Owner starts
            // as this faction on first tick instead of being uniformly bandits).
            string[] homeFactions =
            [
                FactionCatalog.MilitaryFactionId,   // Fort Zancudo
                FactionCatalog.MilitaryFactionId,   // Bolingbroke
                FactionCatalog.LostFactionId,       // Paleto Bay
                FactionCatalog.LostFactionId,       // Grapeseed
                FactionCatalog.CultistFactionId,    // Altruist Camp
                FactionCatalog.BanditFactionId,     // Grove Street
                FactionCatalog.LostFactionId,       // Hawick
                FactionCatalog.BanditFactionId,     // Little Seoul
                FactionCatalog.LostFactionId,       // The Docks
                FactionCatalog.BanditFactionId,     // KORTZ Center
                FactionCatalog.CultistFactionId,    // Scrapyard
                FactionCatalog.BanditFactionId,     // Galileo Observatory
            ];

            var zones = new List<TerritoryZone>(centers.Length);
            for (int i = 0; i < centers.Length; i++)
            {
                var center = centers[i];
                int hash = Function.Call<int>(Hash.GET_ZONE_AT_COORDS, center.X, center.Y, center.Z);
                // NOTE: GET_NAME_OF_ZONE takes COORDINATES in this game build, not the zone
                // hash (see NATIVES/ZONE/GetNameOfZone.md).
                string name = Function.Call<string>(Hash.GET_NAME_OF_ZONE, center.X, center.Y, center.Z);
                zones.Add(new TerritoryZone
                {
                    ZoneName = string.IsNullOrEmpty(name) ? "UNKNOWN" : name,
                    Center = center,
                    ZoneHash = hash,
                    Owner = null,
                    IsPlayerOwned = false,
                    HomeFactionId = homeFactions[i],
                    WaveSpawnPoints = GetWaveSpawns(name)
                });
            }
            return zones;
        }

        // Player-picked safe wave spawn points per territory from the mapping pass (see
        // Territories.txt; faction labels there are ignored for now - owners are still assigned
        // uniformly in OnTick). Keyed by the resolved game-zone name (GET_NAME_OF_ZONE). All 12
        // territories are mapped; unknown zones get an empty array and fall back to a random safe
        // position around the center.
        private static Vector3[] GetWaveSpawns(string zoneName)
        {
            return zoneName switch
            {
                // Fort Zancudo
                "ARMYB" => [new(-2166.43f, 3255.47f, 32.81f), new(-2187.29f, 3040.79f, 32.96f), new(-2305.52f, 3185.29f, 32.81f), new(-1898.92f, 3135.22f, 32.81f)],
                // Bolingbroke Penitentiary
                "JAIL" => [new(1639.35f, 2458.52f, 45.56f), new(1780.08f, 2515.45f, 45.57f), new(1752.91f, 2599.80f, 45.56f), new(1660.80f, 2685.38f, 55.19f)],
                // Paleto Bay
                "PALETO" => [new(-284.53f, 6158.73f, 31.50f), new(-379.91f, 6087.21f, 31.60f), new(-371.50f, 6337.19f, 29.84f), new(-163.77f, 6304.08f, 31.21f)],
                // Grove Street
                "DAVIS" => [new(73.93f, -1972.18f, 20.82f), new(-39.54f, -1839.04f, 26.09f), new(143.37f, -1695.83f, 29.29f), new(281.75f, -1927.15f, 25.83f)],
                // Altruist Camp
                "CMSW" => [new(-1001.54f, 4851.53f, 274.61f), new(-1069.66f, 5006.60f, 185.03f), new(-1149.44f, 4955.99f, 220.57f), new(-1237.90f, 4877.17f, 189.76f)],
                // The Docks
                "TERMINA" => [new(810.79f, -2976.81f, 6.02f), new(1020.42f, -2981.72f, 5.90f), new(1117.47f, -3033.04f, 5.90f), new(920.59f, -3258.89f, 5.90f)],
                // Galileo Observatory
                "OBSERV" => [new(-277.90f, 1208.96f, 318.40f), new(-428.05f, 1222.13f, 325.76f), new(-485.73f, 1100.51f, 320.10f), new(-431.37f, 1057.24f, 320.98f)],
                // Grapeseed
                "GRAPES" => [new(2014.57f, 4980.34f, 41.25f), new(2132.32f, 4781.77f, 40.97f), new(1788.87f, 4614.68f, 37.18f), new(1729.30f, 4783.26f, 41.98f)],
                // KORTZ Center
                "PBLUFF" => [new(-2232.70f, 557.82f, 167.01f), new(-2189.86f, 285.07f, 169.60f), new(-2302.11f, 210.21f, 167.60f), new(-2352.04f, 265.10f, 165.05f)],
                // Scrapyard
                "EBURO" => [new(1500.54f, -1887.57f, 71.90f), new(1618.89f, -2235.76f, 107.09f), new(1432.03f, -2236.57f, 61.04f), new(1477.41f, -1936.14f, 71.04f)],
                // Hawick (HAWICK) and Little Seoul (KOREAT) were relocated from Vinewood
                // Racetrack (HORS) and Vespucci Canals (VCANA); their old spawn points belong to
                // the old locations. Player-mapped safe points now in place.
                "HAWICK" => [new(326.69f, -210.84f, 54.09f), new(106.87f, -132.63f, 54.75f), new(464.23f, -124.02f, 62.21f), new(113.70f, -97.70f, 60.85f)],
                "KOREAT" => [new(-552.01f, -904.05f, 23.79f), new(-761.19f, -1072.76f, 11.91f), new(-889.26f, -853.38f, 20.57f), new(-785.40f, -806.94f, 20.67f)],
                _ => []
            };
        }
    }
}
