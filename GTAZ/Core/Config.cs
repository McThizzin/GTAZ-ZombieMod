using System;
using System.Windows.Forms;
using GTAZ.Data.Serialization;

namespace GTAZ.Core
{
    public class ModConfig
    {
        public static ModConfig Current { get; set; } = new();

        private const string ConfigFile = "ModConfig";

        // Persists to scripts/GTAZ/ModConfig.ini (same folder as the player saves). Loaded once
        // at startup (ModCore constructor, before the hotkeys are read) and saved whenever a
        // settings menu closes or the script aborts.
        public static void Load()
        {
            try
            {
                Current = IniSerializer.Deserialize<ModConfig>(ConfigFile);
                Logger.Info("Config loaded");
            }
            catch (Exception ex)
            {
                Logger.Error($"Config load failed: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                IniSerializer.Serialize(ConfigFile, this);
                Logger.Info("Config saved");
            }
            catch (Exception ex)
            {
                Logger.Error($"Config save failed: {ex.Message}");
            }
        }

        // Zombie Settings
        public int TargetZombieCount { get; set; } = 80;
        public int MaxZombies { get; set; } = 120;
        public float ZombieSpawnRadius { get; set; } = 200f;
        public float ZombieDespawnRadius { get; set; } = 300f;
        public float MinSpawnDistance { get; set; } = 80f;
        public bool NightRunnersEnabled { get; set; } = true;
        public float NightRunnerChance { get; set; } = 0.4f;
        public int ZombieHealth { get; set; } = 100;
        public int ZombieDamage { get; set; } = 10;
        public bool ShowZombieBlips { get; set; } = true;

        // World Settings
        public bool IsApocalypseMode { get; set; } = true;
        public bool ClearTraffic { get; set; } = true;
        public bool DisableCops { get; set; } = true;
        // Global game-time scale, applied per-frame via SET_TIME_SCALE (see WorldSystem).
        // 1.0 = normal GTA time; lower values slow game time (0.0 would freeze it).
        public float TimeScale { get; set; } = 1.0f;

        // Faction Settings
        public float FactionSpawnChance { get; set; } = 0.3f;

        // Survivor Settings
        public bool EnableSurvivors { get; set; } = true;
        public int SurvivorCount { get; set; } = 4;
        public float SurvivorCowardChance { get; set; } = 0.25f;
        public float SurvivorDespawnDistance { get; set; } = 300f;
        public bool ShowSurvivorBlips { get; set; } = true;

        // Horde Settings
        public bool EnableHordeEvents { get; set; } = true;
        public int HordeEventIntervalMinutes { get; set; } = 15;
        public int MaxHordeSize { get; set; } = 40;
        public int WanderingHordeSize { get; set; } = 20;
        public int HordeNightSize { get; set; } = 40;
        public float HordeNightChance { get; set; } = 0.5f;
        public float HordeSpawnDistanceMin { get; set; } = 100f;
        public float HordeSpawnDistanceMax { get; set; } = 220f;
        public float HordeDespawnDistance { get; set; } = 400f;
        public float HordeRunnerChance { get; set; } = 0.15f;
        // Red skull map blip (BlipSprite.TeamDeathmatch, ID 378) tracking each active horde.
        public bool ShowHordeBlips { get; set; } = true;
        // Hordes only surge toward the player while engaged within this radius (§3.4).
        public float HordeEngageDistance { get; set; } = 400f;
        // The Horder (horde center) takes no damage while its horde lives (§3.6).
        public bool HorderInvulnerableWithHorde { get; set; } = true;

        // Territory Settings
        public int MaxPlayerZones { get; set; } = 5;

        // Phase 4 Faction Settings (DOCS/06-Roadmap.md §4)
        public bool EnableFactions { get; set; } = true;
        public int FactionSpawnTarget { get; set; } = 10;
        public int OutpostDefenderCount { get; set; } = 20;
        // Minutes between The Families' gang-car encounters near the player (the "Families
        // Encounter Interval (min)" slider). 1-2 cars full of GSF crew cruise the area and chase
        // the player on sight; 5 min keeps the map active, tune up if it feels constant. The
        // "Bandit" prefix is the legacy config/save key - do not rename (INI compatibility).
        public int BanditPatrolIntervalMinutes { get; set; } = 5;
        public bool ShowFactionBlips { get; set; } = true;
        public int MaxRecruits { get; set; } = 4;
        public float RecruitCombatRange { get; set; } = 100f;
        public bool ShowTerritoryBlips { get; set; } = true;

        // Phase 4 territory fight + debug settings (DOCS/06-Roadmap.md §4.13). ShowTerritoryRadii
        // draws a per-zone ring on the map so zone boundaries are visible while playtesting;
        // the remaining settings drive the faction counter-attack waves and defender respawns.
        public bool ShowTerritoryRadii { get; set; } = true;
        // Draws the player position/heading/game-zone and live territory distances on screen
        // (Debug menu "Show Player Coordinates") so zone centers, radius overlaps, and safe
        // wave-spawn spots can be mapped while playtesting.
        public bool ShowPlayerCoordinates { get; set; } = false;
        public float TerritoryRadius { get; set; } = 250f;
        // Cap on ambient zombies allowed inside a faction-held (hostile) territory ring while
        // the player is in it. The global TargetZombieCount (80) fills a ring to 80 eventually
        // (a probability gate only slowed it), swarming the garrison before the player engages.
        // While the player stands in a hostile ring, ambient spawns stop once this many zombies
        // are already inside the ring and resume when the count drops back under it - the
        // garrison fights a bounded stream instead of being overrun. Owned zones are fully
        // suppressed regardless (safe territory). Default 30 = just over a third of the ambient target.
        public int HostileTerritoryZombieCap { get; set; } = 30;
        public int DefenderRespawnCooldownSeconds { get; set; } = 30;
        public int TerritoryFightKillQuota { get; set; } = 10;
        public int TerritoryFightWaveSize { get; set; } = 20;
        // Kill-triggered defense-reinforcement waves a hostile zone sends per takeover visit
        // before running dry (each tops defenders back up to OutpostDefenderCount). Caps the
        // escalation so a territory can always be cleared and captured.
        public int TerritoryFightReinforcementWaves { get; set; } = 3;
        // Minutes between hostile counter-attacks on player-owned zones (the "Territory Attack
        // Interval (min)" slider). 15 min = a raid roughly every quarter hour while the player
        // camps an outpost. Timer-only since round 4: kills near owned zones no longer accelerate
        // raids (that kill quota now drives hostile-zone defense waves instead).
        public int TerritoryFightCooldownMinutes { get; set; } = 15;

        // Gameplay Settings (Phase 5 polish)
        // Locks the player to their current character: the character wheel (Alt) and the direct
        // select-character keys are disabled every frame so switching characters mid-session is
        // impossible. The player is NOT forced onto Trevor - this only stops switching.
        public bool DisableCharacterSwitching { get; set; } = true;
        // Disables the in-game phone entirely (called every frame; re-enables when disabled).
        public bool DisablePhone { get; set; } = true;

        // UI Settings
        public Keys OpenMenuKey { get; set; } = Keys.F10;
        public Keys OpenTrainerKey { get; set; } = Keys.F5;
        // Press SoftAimKey (H) to toggle the aim reticule at a survivor, then RecruitKey (X) to recruit.
        public Keys RecruitKey { get; set; } = Keys.X;
        public Keys SoftAimKey { get; set; } = Keys.H;
    }
}
