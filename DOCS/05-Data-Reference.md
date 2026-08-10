# GTAZ — Data Reference: models, territories, wave spawns

Static data the mod ships with. Sources of truth: `GTAZ/Systems/ZombieSystem.cs`,
`GTAZ/Systems/SurvivorSystem.cs`, `GTAZ/Systems/FactionSystem.cs`, and `Territories.txt` (repo
root — the human-edited master list whose content is ported into `FactionSystem.GetWaveSpawns`).

---

## NPC models per group

All ped models the mod spawns. Model names are `PedHash` enum members (use
`unchecked((int)PedHash.X)` for any that overflow `int.MaxValue`) or hardcoded joaat hashes for DLC
models absent from the enum. The pools are intentionally **overlap-free**: no survivor model appears
in the zombie or faction lists, and no faction shares a model with another faction.

### Zombies
Horde spawns (`HordeSystem`) reuse these exact pools via `ZombieSystem.WalkerModels` /
`ZombieSystem.RunnerModels`.

**Walkers**
- `Tramp01AMM`, `Tramp01AFM`, `Skidrow01AMM`, `Skidrow01AFM`, `Methhead01AMY`, `Hillbilly01AMM`,
  `Hillbilly02AMM`, `TrampBeac01AMM`, `StrPunk01GMY`, `Business01AMY`, `Hippy01AMY`,
  `Tranvest02AMM` (`a_m_m_tranvest_02`), `DeadHooker` (`mp_f_deadhooker`),
  `g_m_m_zombie_01` (`0xC61B7851`, not in enum)

**Runners (night only unless toggled)**
- `Zombie01` (`u_m_y_zombie_01`), `Juggalo01AMY`, `g_m_m_zombie_02` (`0x98549CC4`, not in enum),
  `ig_zombie_dj_01` (`0xBE5F26D7`, not in enum), `g_m_m_zombie_04` (`0x296EBEF2`, not in enum),
  `g_m_m_zombie_05` (`0xFAB86186`, not in enum)

### Survivors (ambient + outpost guards + recruits)
Recruits keep whatever survivor/engine model they had when adopted, so the pool below is the
spawned set; `ConvertNearbyEngineNpcs` also re-arms random engine civilians in place (any civilian
model, kept as-is).
- `Eastsa01AMY`, `Eastsa01AFY`, `Business02AMY`, `Business03AMY`, `Business04AFY`, `Hiker01AMY`,
  `Hiker01AFY`, `Malibu01AMM`, `MexLabor01AMM`, `Tourist01AMM`, `Tourist01AFY`, `Trucker01SMM`,
  `JanitorSMM`, `Farmer01AMM`, `Beach01AMM`

### The Families (formerly Bandits; internal Id `"Bandits"`)
- `BallaEast01GMY`, `BallaOrig01GMY`, `BallaSout01GMY`, `Ballas01GFY` (`g_f_y_ballas_01`),
  `Families01GFY` (`g_f_y_families_01`), `Famca01GMY` (`g_m_y_famca_01`),
  `Famdnf01GMY` (`g_m_y_famdnf_01`)

### Military
- `ExArmy01`, `Swat01SMY`, `Marine01SMM` (`s_m_m_marine_01`), `Blackops02SMY` (`s_m_y_blackops_02`),
  `Blackops03SMY` (`s_m_y_blackops_03`), `Marine03SMY` (`s_m_y_marine_03`)

### Altruists (formerly Cultists; internal Id `"Cultists"`)
- `Acult02AMO`, `FatCult01AFM` (`a_f_m_fatcult_01`), `Acult01AMO` (`a_m_o_acult_01`),
  `Acult01AMY` (`a_m_y_acult_01`), `Fooliganz01GFM` (`g_f_m_fooliganz_01`),
  `Fooliganz2` (`ig_fooliganz_02`)

### The Lost
- `Lost01GMY`, `Lost02GMY`, `Lost03GMY`, `Lost01GFY` (`g_f_y_lost_01`)

### The Lost bikes (ambient riders only)
~35% of ambient/patrol Lost spawn mounted (`FactionSystem.TryMountBike`, `LostRiderChance`):
- `Hexer` (50%), `Daemon` (50%)

---

## Territories & wave spawns

12 territories, built in `FactionSystem.BuildZones` from `GET_ZONE_AT_COORDS` at each center.
`HomeFactionId` per zone maps to a live faction. Each zone has **4 player-verified `Safe wave
spawn` points** (from `Territories.txt`) used by defender/defense/raid waves via `GetWaveSpawn`
(the point closest to the player is always excluded). Initial defenders spawn near the **zone
center** (`FindSafePosition(center, 5f, 40f)`) with a mapped point as fallback.

| Territory | Home faction | Game zone | Center (X, Y, Z) |
|---|---|---|---|
| Fort Zancudo | Military | `ARMYB` | -2128.68, 3122.73, 32.81 |
| Bolingbroke Penitentiary | Military | `JAIL` | 1724.20, 2535.20, 45.61 |
| Paleto Bay | The Lost | `PALETO` | -303.63, 6229.78, 31.45 |
| Grapeseed | The Lost | `GRAPES` | 1922.43, 4796.51, 43.89 |
| Altruist Camp | Altruists | `CMSW` | -1116.17, 4928.03, 218.29 |
| Grove Street | The Families | `DAVIS` | 152.98, -1780.49, 29.05 |
| Hawick | The Lost | `HAWICK` | 264.41, -159.50, 61.94 |
| Little Seoul | The Families | `KOREAT` | -745.16, -948.94, 17.93 |
| The Docks | The Lost | `TERMINA` | 979.09, -3115.16, 5.90 |
| KORTZ Center | The Families | `PBLUFF` | -2302.58, 395.16, 176.10 |
| Scrapyard | Altruists | `EBURO` | 1524.33, -2108.27, 76.72 |
| Galileo Observatory | The Families | `OBSERV` | -421.03, 1141.28, 325.91 |

Wave spawn points: see `Territories.txt` (the master list) or `FactionSystem.GetWaveSpawns` (the
port). Adding a zone = add a center to `BuildZones` **and** 4 mapped points + `HomeFactionId`;
without points the zone falls back to a random safe-position search (lesson 34).
