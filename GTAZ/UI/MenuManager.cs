using System;
using System.Linq;
using GTA;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Events;
using GTAZ.Systems;
using GTAZ.Utils;
using LemonUI;
using LemonUI.Menus;

namespace GTAZ.UI
{
    public class MenuManager
    {
        private static MenuManager _instance;
        public static MenuManager Instance => _instance ??= new MenuManager();

        private WorldSystem _world;
        private ZombieSystem _zombies;
        private HordeSystem _horde;
        private SurvivorSystem _survivors;
        private FactionSystem _factions;
        private PersistenceManager _persistence;

        public ObjectPool Pool { get; } = [];

        public NativeMenu MainMenu { get; }
        public NativeMenu FactionMenu { get; }
        public NativeMenu TerritoryMenu { get; }
        public NativeMenu SettingsMenu { get; }
        public NativeMenu DebugMenu { get; }

        // Settings: Zombies submenu
        private NativeSliderItem _targetCount;
        private NativeSliderItem _maxBudget;
        private NativeSliderItem _spawnRadius;
        private NativeSliderItem _despawnRadius;
        private NativeSliderItem _minSpawnDistance;
        private NativeCheckboxItem _nightRunners;
        private NativeSliderItem _nightRunnerChance;
        private NativeSliderItem _territoryZombieCap;
        private NativeCheckboxItem _showBlips;
        private NativeSliderItem _zombieHealth;
        private NativeSliderItem _zombieDamage;

        // Settings: World submenu
        private NativeCheckboxItem _apocalypseMode;
        private NativeCheckboxItem _clearTraffic;
        private NativeCheckboxItem _disableCops;
        private NativeSliderItem _timeScale;

        // Settings: Survivors submenu
        private NativeCheckboxItem _survivorsEnabled;
        private NativeSliderItem _survivorCount;
        private NativeSliderItem _cowardChance;
        private NativeSliderItem _survivorDespawn;
        private NativeCheckboxItem _showSurvivorBlips;

        // Faction submenu
        private NativeCheckboxItem _enableFactions;
        private NativeSliderItem _factionSpawnTarget;
        private NativeSliderItem _outpostDefenders;
        private NativeSliderItem _banditPatrol;
        private NativeSliderItem _maxRecruits;
        private NativeSliderItem _recruitCombatRange;
        private NativeCheckboxItem _showFactionBlips;
        private NativeItem _factionStatusItem;

        // Territory submenu
        private NativeSliderItem _maxZones;
        private NativeSliderItem _territoryAttackInterval;
        private NativeSliderItem _reinforcementWaves;
        private NativeSliderItem _territoryKillQuota;
        private NativeSliderItem _leaveGraceSeconds;
        private NativeCheckboxItem _showTerritoryBlips;
        private NativeCheckboxItem _showTerritoryRadii;
        private NativeItem _zoneStatusItem;

        // Settings: Horde submenu
        private NativeCheckboxItem _hordeEnabled;
        private NativeSliderItem _hordeInterval;
        private NativeSliderItem _maxHordeSize;
        private NativeSliderItem _wanderingHordeSize;
        private NativeSliderItem _hordeNightSize;
        private NativeSliderItem _hordeNightChance;
        private NativeSliderItem _hordeSpawnMin;
        private NativeSliderItem _hordeSpawnMax;
        private NativeSliderItem _hordeDespawn;
        private NativeSliderItem _hordeRunnerChance;
        private NativeCheckboxItem _showHordeBlips;
        private NativeSliderItem _hordeEngageDistance;
        private NativeCheckboxItem _horderInvulnerable;

        // Debug
        private NativeItem _statusItem;
        private NativeCheckboxItem _showPlayerCoords;
        private NativeCheckboxItem _statusOverlay;
        private NativeCheckboxItem _showSpawnRadiusRing;
        private NativeSliderItem _spawnRadiusRingMeters;
        private Blip _playerRingBlip;
        private float _playerRingRadius;

        public bool IsAnyMenuOpen => Pool.AreAnyVisible;

        public MenuManager()
        {
            // All menus right-aligned so they don't overlap the top-left debug coordinates HUD
            // (Show Player Coordinates). Submenus are separate NativeMenu instances and must be
            // aligned individually - Alignment.Center throws, only Left/Right are valid.
            MainMenu = new("GTAZ", "ZOMBIE SURVIVAL") { Alignment = GTA.UI.Alignment.Right };
            FactionMenu = new("GTAZ", "FACTIONS") { Alignment = GTA.UI.Alignment.Right };
            TerritoryMenu = new("GTAZ", "TERRITORY") { Alignment = GTA.UI.Alignment.Right };
            SettingsMenu = new("GTAZ", "SETTINGS") { Alignment = GTA.UI.Alignment.Right };
            DebugMenu = new("GTAZ", "DEBUG") { Alignment = GTA.UI.Alignment.Right };

            Pool.Add(MainMenu);
            Pool.Add(FactionMenu);
            Pool.Add(TerritoryMenu);
            Pool.Add(SettingsMenu);
            Pool.Add(DebugMenu);

            MainMenu.AddSubMenu(SettingsMenu);
            MainMenu.AddSubMenu(DebugMenu);
        }

        public void Initialize(WorldSystem world, ZombieSystem zombies, HordeSystem horde,
            SurvivorSystem survivors, FactionSystem factions, PersistenceManager persistence)
        {
            _world = world;
            _zombies = zombies;
            _horde = horde;
            _survivors = survivors;
            _factions = factions;
            _persistence = persistence;

            BuildFactionMenu();
            BuildTerritoryMenu();
            BuildSettingsMenu();
            BuildDebugMenu();

            ApplyAllToSystems();
        }

        private void BuildFactionMenu()
        {
            var config = ModConfig.Current;

            _enableFactions = new NativeCheckboxItem("Enable Factions", "Faction peds, outposts, and territory capture", config.EnableFactions);
            _factionSpawnTarget = new NativeSliderItem("Per-Faction Spawn Target", "Max ambient peds each active faction keeps near you", 10, 1)
            {
                Value = config.FactionSpawnTarget
            };
            _outpostDefenders = new NativeSliderItem("Outpost Defender Count", "Holding faction's defenders at hostile zones and survivor guards at your outposts", 25, 1)
            {
                Value = config.OutpostDefenderCount
            };
            _banditPatrol = new NativeSliderItem("Families Encounter Interval (min)", "Minutes between Families gang-car patrols that cruise the area and chase you on sight", 120, 5)
            {
                Value = config.BanditPatrolIntervalMinutes
            };
            _maxRecruits = new NativeSliderItem("Max Recruits", "How many survivors can follow you at once", 8, 1)
            {
                Value = config.MaxRecruits
            };
            _recruitCombatRange = new NativeSliderItem("Recruit Combat Range (m)", "How far recruits engage zombies", 100, 5)
            {
                Value = (int)config.RecruitCombatRange
            };
            _showFactionBlips = new NativeCheckboxItem("Show Faction Blips", "Colored markers for faction peds", config.ShowFactionBlips);
            _factionStatusItem = new NativeItem("Faction Status", "Live faction state");

            var recruitInfo = new NativeItem("Recruiting",
                $"Press {config.SoftAimKey} to toggle a reticule at a survivor (no weapon raised), then press {config.RecruitKey} to recruit. {config.RecruitKey} also works at the plain crosshair. Press again on a recruit to dismiss.");

            _enableFactions.CheckboxChanged += (s, a) =>
            {
                config.EnableFactions = _enableFactions.Checked;
                if (!config.EnableFactions)
                    _factions.ClearAllFactions();
                GTA.UI.Notification.PostTicker($"~b~Factions: {(_enableFactions.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _factionSpawnTarget.ValueChanged += (s, a) => config.FactionSpawnTarget = _factionSpawnTarget.Value;
            _outpostDefenders.ValueChanged += (s, a) => config.OutpostDefenderCount = _outpostDefenders.Value;
            _banditPatrol.ValueChanged += (s, a) => config.BanditPatrolIntervalMinutes = _banditPatrol.Value;
            _maxRecruits.ValueChanged += (s, a) => config.MaxRecruits = _maxRecruits.Value;
            _recruitCombatRange.ValueChanged += (s, a) => config.RecruitCombatRange = _recruitCombatRange.Value;
            _showFactionBlips.CheckboxChanged += (s, a) =>
            {
                config.ShowFactionBlips = _showFactionBlips.Checked;
                GTA.UI.Notification.PostTicker($"~b~Faction Blips: {(_showFactionBlips.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };

            FactionMenu.Add(_enableFactions);
            FactionMenu.Add(_factionSpawnTarget);
            FactionMenu.Add(_outpostDefenders);
            FactionMenu.Add(_banditPatrol);
            FactionMenu.Add(_maxRecruits);
            FactionMenu.Add(_recruitCombatRange);
            FactionMenu.Add(_showFactionBlips);
            FactionMenu.Add(recruitInfo);
            FactionMenu.Add(_factionStatusItem);

            FactionMenu.Closed += (s, a) => ModConfig.Current.Save();
        }

        private void BuildTerritoryMenu()
        {
            var config = ModConfig.Current;

            _maxZones = new NativeSliderItem("Max Player Zones", "Hard cap on zones you can own", 10, 1)
            {
                Value = config.MaxPlayerZones
            };
            _territoryAttackInterval = new NativeSliderItem("Territory Attack Interval (min)", "Minutes between hostile counter-attacks on your zones", 120, 5)
            {
                Value = config.TerritoryFightCooldownMinutes
            };
            _reinforcementWaves = new NativeSliderItem("Defense Reinforcement Waves", "Reinforcement waves a hostile zone sends per takeover attempt before running dry", 10, 1)
            {
                Value = config.TerritoryFightReinforcementWaves
            };
            _territoryKillQuota = new NativeSliderItem("Defense Kill Quota", "Defender kills in a hostile ring before a reinforcement wave spawns. Higher = bigger, rarer waves (each refills to the defender count)", 20, 1)
            {
                Value = config.TerritoryFightKillQuota
            };
            _leaveGraceSeconds = new NativeSliderItem("Leave Grace (s)", "How long a hostile-zone takeover stays alive after you step out of the ring (with an on-screen countdown) before it resets", 30, 1)
            {
                Value = config.TerritoryLeaveGraceSeconds
            };
            _showTerritoryBlips = new NativeCheckboxItem("Show Territory Blips", "Safehouse markers colored by owner", config.ShowTerritoryBlips);
            _showTerritoryRadii = new NativeCheckboxItem("Show Territory Radii", "Debug circle showing the capture/attack radius (Phase 4)", config.ShowTerritoryRadii);
            _zoneStatusItem = new NativeItem("Territory Status", "Live zone ownership");

            _maxZones.ValueChanged += (s, a) => config.MaxPlayerZones = _maxZones.Value;
            _territoryAttackInterval.ValueChanged += (s, a) => config.TerritoryFightCooldownMinutes = _territoryAttackInterval.Value;
            _reinforcementWaves.ValueChanged += (s, a) => config.TerritoryFightReinforcementWaves = _reinforcementWaves.Value;
            _territoryKillQuota.ValueChanged += (s, a) => config.TerritoryFightKillQuota = _territoryKillQuota.Value;
            _leaveGraceSeconds.ValueChanged += (s, a) => config.TerritoryLeaveGraceSeconds = _leaveGraceSeconds.Value;
            _showTerritoryBlips.CheckboxChanged += (s, a) =>
            {
                config.ShowTerritoryBlips = _showTerritoryBlips.Checked;
                GTA.UI.Notification.PostTicker($"~b~Territory Blips: {(_showTerritoryBlips.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _showTerritoryRadii.CheckboxChanged += (s, a) =>
            {
                config.ShowTerritoryRadii = _showTerritoryRadii.Checked;
                GTA.UI.Notification.PostTicker($"~b~Territory Radii: {(_showTerritoryRadii.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };

            var surrenderTerritory = new NativeItem("Surrender Territory", "Gives up the territory you're standing in, freeing a slot to capture another");
            surrenderTerritory.Activated += (s, a) => _factions.SurrenderCurrentZone();
            TerritoryMenu.Add(surrenderTerritory);

            TerritoryMenu.Add(_maxZones);
            TerritoryMenu.Add(_territoryAttackInterval);
            TerritoryMenu.Add(_reinforcementWaves);
            TerritoryMenu.Add(_territoryKillQuota);
            TerritoryMenu.Add(_leaveGraceSeconds);
            TerritoryMenu.Add(_showTerritoryBlips);
            TerritoryMenu.Add(_showTerritoryRadii);
            TerritoryMenu.Add(_zoneStatusItem);

            TerritoryMenu.Closed += (s, a) => ModConfig.Current.Save();
        }

        private void BuildSettingsMenu()
        {
            var config = ModConfig.Current;

            var zombiesMenu = new NativeMenu("GTAZ", "ZOMBIES") { Alignment = GTA.UI.Alignment.Right };
            var worldMenu = new NativeMenu("GTAZ", "WORLD") { Alignment = GTA.UI.Alignment.Right };
            var hordeMenu = new NativeMenu("GTAZ", "HORDE") { Alignment = GTA.UI.Alignment.Right };
            var survivorsMenu = new NativeMenu("GTAZ", "SURVIVORS") { Alignment = GTA.UI.Alignment.Right };
            Pool.Add(zombiesMenu);
            Pool.Add(worldMenu);
            Pool.Add(hordeMenu);
            Pool.Add(survivorsMenu);
            SettingsMenu.AddSubMenu(zombiesMenu);
            SettingsMenu.AddSubMenu(worldMenu);
            SettingsMenu.AddSubMenu(hordeMenu);
            SettingsMenu.AddSubMenu(survivorsMenu);

            // --- Zombie settings ---
            _targetCount = new NativeSliderItem("Target Active Zombies", "How many zombies the ambient spawner keeps alive", 100, 10)
            {
                Value = config.TargetZombieCount
            };

            _maxBudget = new NativeSliderItem("Max Peds (Budget)", "Hard cap for the global ped budget", 200, 10)
            {
                Value = config.MaxZombies
            };

            _spawnRadius = new NativeSliderItem("Spawn Radius (m)", "Distance from the player that zombies spawn", 500, 50)
            {
                Value = (int)config.ZombieSpawnRadius
            };

            _despawnRadius = new NativeSliderItem("Despawn Radius (m)", "Distance beyond which zombies are cleaned up", 500, 50)
            {
                Value = (int)config.ZombieDespawnRadius
            };

            _minSpawnDistance = new NativeSliderItem("Min Spawn Distance (m)", "Closest distance the spawner will place zombies", 150, 10)
            {
                Value = (int)config.MinSpawnDistance
            };

            _territoryZombieCap = new NativeSliderItem("Territory Zombie Cap", "Max ambient zombies inside a faction-held territory ring while you're in it (0 = none, 80 = full pressure)", 80, 5)
            {
                Value = config.HostileTerritoryZombieCap
            };


            _nightRunners = new NativeCheckboxItem("Night Runners", "Runners spawn between 20:00 and 05:00", config.NightRunnersEnabled);

            _nightRunnerChance = new NativeSliderItem("Night Runner Chance (%)", "Chance an ambient night spawn is a runner", 100, 10)
            {
                Value = (int)(config.NightRunnerChance * 100f)
            };

            _showBlips = new NativeCheckboxItem("Show Zombie Blips", "Red markers on the minimap for every zombie", config.ShowZombieBlips);

            _zombieHealth = new NativeSliderItem("Zombie Health", "Hit points for newly spawned zombies", 500, 10)
            {
                Value = config.ZombieHealth
            };

            _zombieDamage = new NativeSliderItem("Zombie Damage", "Melee damage dealt per hit by newly spawned zombies", 100, 10)
            {
                Value = config.ZombieDamage
            };

            _targetCount.ValueChanged += (s, a) => { config.TargetZombieCount = _targetCount.Value; _zombies.TargetZombieCount = _targetCount.Value; };
            _maxBudget.ValueChanged += (s, a) => { config.MaxZombies = _maxBudget.Value; PedBudgetManager.Instance.GlobalBudget = _maxBudget.Value; };
            _spawnRadius.ValueChanged += (s, a) => { config.ZombieSpawnRadius = _spawnRadius.Value; _zombies.SpawnRadius = _spawnRadius.Value; };
            _despawnRadius.ValueChanged += (s, a) => { config.ZombieDespawnRadius = _despawnRadius.Value; _zombies.DespawnRadius = _despawnRadius.Value; };
            _minSpawnDistance.ValueChanged += (s, a) => { config.MinSpawnDistance = _minSpawnDistance.Value; _zombies.MinSpawnDistance = _minSpawnDistance.Value; };
            _territoryZombieCap.ValueChanged += (s, a) => config.HostileTerritoryZombieCap = _territoryZombieCap.Value;
            _nightRunners.CheckboxChanged += (s, a) =>
            {
                config.NightRunnersEnabled = _nightRunners.Checked;
                _zombies.NightRunnersEnabled = _nightRunners.Checked;
                GTA.UI.Notification.PostTicker($"~b~Night Runners: {(_nightRunners.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _nightRunnerChance.ValueChanged += (s, a) => { config.NightRunnerChance = _nightRunnerChance.Value / 100f; _zombies.NightRunnerChance = _nightRunnerChance.Value / 100f; };
            _showBlips.CheckboxChanged += (s, a) =>
            {
                config.ShowZombieBlips = _showBlips.Checked;
                GTA.UI.Notification.PostTicker($"~b~Zombie Blips: {(_showBlips.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _zombieHealth.ValueChanged += (s, a) => config.ZombieHealth = _zombieHealth.Value;
            _zombieDamage.ValueChanged += (s, a) => config.ZombieDamage = _zombieDamage.Value;

            zombiesMenu.Add(_targetCount);
            zombiesMenu.Add(_maxBudget);
            zombiesMenu.Add(_spawnRadius);
            zombiesMenu.Add(_despawnRadius);
            zombiesMenu.Add(_minSpawnDistance);
            zombiesMenu.Add(_territoryZombieCap);
            zombiesMenu.Add(_nightRunners);
            zombiesMenu.Add(_nightRunnerChance);
            zombiesMenu.Add(_showBlips);
            zombiesMenu.Add(_zombieHealth);
            zombiesMenu.Add(_zombieDamage);

            // --- World settings ---
            _apocalypseMode = new NativeCheckboxItem("Apocalypse Mode", "Clears traffic, cops, and ambient peds", config.IsApocalypseMode);
            _clearTraffic = new NativeCheckboxItem("Clear Traffic", "Removes vehicles and ambient peds around the player", config.ClearTraffic);
            _disableCops = new NativeCheckboxItem("Disable Cops", "Locks the wanted level to zero", config.DisableCops);
            _timeScale = new NativeSliderItem("Time Scale (%)", "Game-time speed: 100% = normal, lower = slow-motion", 200, 10)
            {
                Value = (int)(config.TimeScale * 100f)
            };

            _apocalypseMode.CheckboxChanged += (s, a) =>
            {
                config.IsApocalypseMode = _apocalypseMode.Checked;
                _world.IsApocalypseMode = _apocalypseMode.Checked;
                GTA.UI.Notification.PostTicker($"~b~Apocalypse Mode: {(_apocalypseMode.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _clearTraffic.CheckboxChanged += (s, a) =>
            {
                config.ClearTraffic = _clearTraffic.Checked;
                _world.ClearTraffic = _clearTraffic.Checked;
                GTA.UI.Notification.PostTicker($"~b~Clear Traffic: {(_clearTraffic.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _disableCops.CheckboxChanged += (s, a) =>
            {
                config.DisableCops = _disableCops.Checked;
                _world.DisableCops = _disableCops.Checked;
                GTA.UI.Notification.PostTicker($"~b~Disable Cops: {(_disableCops.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _timeScale.ValueChanged += (s, a) => config.TimeScale = _timeScale.Value / 100f;

            worldMenu.Add(_apocalypseMode);
            worldMenu.Add(_clearTraffic);
            worldMenu.Add(_disableCops);
            worldMenu.Add(_timeScale);

            // --- Horde settings ---
            _hordeEnabled = new NativeCheckboxItem("Enable Horde Events", "Wandering hordes and horde night sieges", config.EnableHordeEvents);
            _hordeInterval = new NativeSliderItem("Horde Interval (min)", "Minutes between wandering hordes", 120, 5)
            {
                Value = config.HordeEventIntervalMinutes
            };
            _maxHordeSize = new NativeSliderItem("Max Horde Size", "Hard cap on any single horde", 100, 5)
            {
                Value = config.MaxHordeSize
            };
            _wanderingHordeSize = new NativeSliderItem("Wandering Horde Size", "Zombies in a wandering horde", 100, 1)
            {
                Value = config.WanderingHordeSize
            };
            _hordeNightSize = new NativeSliderItem("Horde Night Size", "Zombies in a horde night siege", 120, 1)
            {
                Value = config.HordeNightSize
            };
            _hordeNightChance = new NativeSliderItem("Horde Night Chance (%)", "Chance a night has a horde siege", 100, 10)
            {
                Value = (int)(config.HordeNightChance * 100f)
            };
            _hordeSpawnMin = new NativeSliderItem("Spawn Distance Min (m)", "Closest a horde materializes", 200, 50)
            {
                Value = (int)config.HordeSpawnDistanceMin
            };
            _hordeSpawnMax = new NativeSliderItem("Spawn Distance Max (m)", "Furthest a horde materializes", 400, 50)
            {
                Value = (int)config.HordeSpawnDistanceMax
            };
            _hordeDespawn = new NativeSliderItem("Horde Despawn Distance (m)", "Distance at which horde members fade", 1000, 50)
            {
                Value = (int)config.HordeDespawnDistance
            };
            _hordeRunnerChance = new NativeSliderItem("Horde Runner Chance (%)", "Chance a horde member is a runner", 100, 10)
            {
                Value = (int)(config.HordeRunnerChance * 100f)
            };
            _showHordeBlips = new NativeCheckboxItem("Show Horde Blips", "Red skull blip tracking each active horde", config.ShowHordeBlips);
            _hordeEngageDistance = new NativeSliderItem("Horde Engage Distance (m)", "Hordes surge toward the player within this radius", 1000, 50)
            {
                Value = (int)config.HordeEngageDistance
            };
            _horderInvulnerable = new NativeCheckboxItem("Horder Invulnerable With Horde", "The horde's center takes no damage while its horde lives", config.HorderInvulnerableWithHorde);

            _hordeEnabled.CheckboxChanged += (s, a) =>
            {
                config.EnableHordeEvents = _hordeEnabled.Checked;
                GTA.UI.Notification.PostTicker($"~b~Horde Events: {(_hordeEnabled.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _hordeInterval.ValueChanged += (s, a) => config.HordeEventIntervalMinutes = _hordeInterval.Value;
            _maxHordeSize.ValueChanged += (s, a) => config.MaxHordeSize = _maxHordeSize.Value;
            _wanderingHordeSize.ValueChanged += (s, a) => config.WanderingHordeSize = _wanderingHordeSize.Value;
            _hordeNightSize.ValueChanged += (s, a) => config.HordeNightSize = _hordeNightSize.Value;
            _hordeNightChance.ValueChanged += (s, a) => config.HordeNightChance = _hordeNightChance.Value / 100f;
            _hordeSpawnMin.ValueChanged += (s, a) => config.HordeSpawnDistanceMin = _hordeSpawnMin.Value;
            _hordeSpawnMax.ValueChanged += (s, a) => config.HordeSpawnDistanceMax = _hordeSpawnMax.Value;
            _hordeDespawn.ValueChanged += (s, a) => config.HordeDespawnDistance = _hordeDespawn.Value;
            _hordeRunnerChance.ValueChanged += (s, a) => config.HordeRunnerChance = _hordeRunnerChance.Value / 100f;
            _showHordeBlips.CheckboxChanged += (s, a) => config.ShowHordeBlips = _showHordeBlips.Checked;
            _hordeEngageDistance.ValueChanged += (s, a) => config.HordeEngageDistance = _hordeEngageDistance.Value;
            _horderInvulnerable.CheckboxChanged += (s, a) => config.HorderInvulnerableWithHorde = _horderInvulnerable.Checked;

            hordeMenu.Add(_hordeEnabled);
            hordeMenu.Add(_hordeInterval);
            hordeMenu.Add(_maxHordeSize);
            hordeMenu.Add(_wanderingHordeSize);
            hordeMenu.Add(_hordeNightSize);
            hordeMenu.Add(_hordeNightChance);
            hordeMenu.Add(_hordeSpawnMin);
            hordeMenu.Add(_hordeSpawnMax);
            hordeMenu.Add(_hordeDespawn);
            hordeMenu.Add(_hordeRunnerChance);
            hordeMenu.Add(_showHordeBlips);
            hordeMenu.Add(_hordeEngageDistance);
            hordeMenu.Add(_horderInvulnerable);

            // --- Survivor settings ---
            _survivorsEnabled = new NativeCheckboxItem("Enable Survivors", "Armed NPCs that fight or flee zombies", config.EnableSurvivors);
            _survivorCount = new NativeSliderItem("Survivor Count", "Armed survivors wandering near the player", 30, 1)
            {
                Value = config.SurvivorCount
            };
            _cowardChance = new NativeSliderItem("Coward Chance (%)", "Chance a survivor flees instead of fighting", 100, 10)
            {
                Value = (int)(config.SurvivorCowardChance * 100f)
            };
            _survivorDespawn = new NativeSliderItem("Despawn Distance (m)", "Survivors fade once the player moves past this", 1000, 50)
            {
                Value = (int)config.SurvivorDespawnDistance
            };
            _showSurvivorBlips = new NativeCheckboxItem("Show Survivor Blips", "Green markers on the minimap for every survivor", config.ShowSurvivorBlips);

            _survivorsEnabled.CheckboxChanged += (s, a) =>
            {
                config.EnableSurvivors = _survivorsEnabled.Checked;
                if (!config.EnableSurvivors)
                    _survivors.ClearAllSurvivors();
                GTA.UI.Notification.PostTicker($"~b~Survivors: {(_survivorsEnabled.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _survivorCount.ValueChanged += (s, a) => config.SurvivorCount = _survivorCount.Value;
            _cowardChance.ValueChanged += (s, a) => config.SurvivorCowardChance = _cowardChance.Value / 100f;
            _survivorDespawn.ValueChanged += (s, a) => config.SurvivorDespawnDistance = _survivorDespawn.Value;
            _showSurvivorBlips.CheckboxChanged += (s, a) =>
            {
                config.ShowSurvivorBlips = _showSurvivorBlips.Checked;
                GTA.UI.Notification.PostTicker($"~b~Survivor Blips: {(_showSurvivorBlips.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };

            survivorsMenu.Add(_survivorsEnabled);
            survivorsMenu.Add(_survivorCount);
            survivorsMenu.Add(_cowardChance);
            survivorsMenu.Add(_survivorDespawn);
            survivorsMenu.Add(_showSurvivorBlips);

            var dismissRecruits = new NativeItem("Dismiss All Recruits", "Releases every survivor following you");
            dismissRecruits.Activated += (s, a) => _factions.DismissAllRecruits();
            survivorsMenu.Add(dismissRecruits);

            // Factions and Territory are siblings of the other Settings submenus (not nested in
            // Survivors): they're added last so they sit at the bottom of the Settings list -
            // MAIN > SETTINGS > [ZOMBIES, WORLD, HORDE, SURVIVORS, FACTIONS, TERRITORY].
            SettingsMenu.AddSubMenu(FactionMenu);
            SettingsMenu.AddSubMenu(TerritoryMenu);

            // Persist settings when the player leaves a settings screen. The sliders/checkboxes
            // write into ModConfig.Current on every change; saving once on menu close instead of
            // on every ValueChanged tick avoids churning the ini file while dragging a slider.
            zombiesMenu.Closed += (s, a) => ModConfig.Current.Save();
            worldMenu.Closed += (s, a) => ModConfig.Current.Save();
            hordeMenu.Closed += (s, a) => ModConfig.Current.Save();
            survivorsMenu.Closed += (s, a) => ModConfig.Current.Save();
            SettingsMenu.Closed += (s, a) => ModConfig.Current.Save();
        }

        private void BuildDebugMenu()
        {
            var config = ModConfig.Current;
            var spawnZombie = new NativeItem("Spawn Zombie", "Spawns a walker just in front of the player");
            var spawnRunner = new NativeItem("Spawn Runner", "Spawns a runner just in front of the player");
            var killAll = new NativeItem("Kill All Zombies", "Kills every active zombie");
            var clearAll = new NativeItem("Clear All Zombies", "Deletes every active zombie");
            var saveGame = new NativeItem("Save Game", "Saves player position, health, and armor to manual_save");
            var loadGame = new NativeItem("Load Game", "Restores the last manual save");
            var triggerWander = new NativeItem("Trigger Wandering Horde", "Spawns a wandering horde now");
            var triggerNight = new NativeItem("Trigger Horde Night", "Spawns a horde night siege now");
            var clearHordes = new NativeItem("Clear All Hordes", "Fades out all active hordes");
            _statusItem = new NativeItem("Status", "Live system status");
            _showPlayerCoords = new NativeCheckboxItem("Show Player Coordinates", "Draws position, heading, game zone, and live distance to every territory (for mapping zone centers, radius overlaps, and wave-spawn spots)", config.ShowPlayerCoordinates);
            _statusOverlay = new NativeCheckboxItem("Show Status Overlay", "Draws the live system status on screen even while the menu is closed (same top-left area as Show Player Coordinates)", config.ShowStatusOverlay);
            _showSpawnRadiusRing = new NativeCheckboxItem("Show Spawn Radius Ring", "Radius ring around the player on the minimap - measure what a player-relative faction-wave spawn radius covers before wiring it into the spawn code", config.ShowSpawnRadiusRing);
            _spawnRadiusRingMeters = new NativeSliderItem("Spawn Radius Ring (m)", "Size of the player radius ring", 300, 5)
            {
                Value = (int)config.SpawnRadiusRingMeters
            };

            spawnZombie.Activated += (s, a) =>
            {
                SpawnDebugZombie(false);
                GTA.UI.Notification.PostTicker("~g~Spawned a zombie.", false);
            };
            spawnRunner.Activated += (s, a) =>
            {
                SpawnDebugZombie(true);
                GTA.UI.Notification.PostTicker("~g~Spawned a runner.", false);
            };
            killAll.Activated += (s, a) =>
            {
                _zombies.KillAllZombies();
                GTA.UI.Notification.PostTicker("~b~Killed all zombies.", false);
            };
            clearAll.Activated += (s, a) =>
            {
                _zombies.ClearAllZombies();
                GTA.UI.Notification.PostTicker("~b~Cleared all zombies.", false);
            };
            saveGame.Activated += (s, a) =>
            {
                _persistence.Save("manual_save.ini");
                GameEvents.RaiseGameSaved();
            };
            loadGame.Activated += (s, a) =>
            {
                _persistence.LoadAndApply("manual_save.ini");
                GTA.UI.Notification.PostTicker("~b~Loaded manual save.", false);
            };
            triggerWander.Activated += (s, a) =>
            {
                GTA.UI.Notification.PostTicker(_horde.StartHorde(HordeKind.Wandering)
                    ? "~o~Wandering horde inbound."
                    : "~r~Could not start horde.", false);
            };
            triggerNight.Activated += (s, a) =>
            {
                GTA.UI.Notification.PostTicker(_horde.StartHorde(HordeKind.Night)
                    ? "~r~Horde night inbound."
                    : "~r~Could not start horde.", false);
            };
            clearHordes.Activated += (s, a) =>
            {
                _horde.ClearAllHordes();
                GTA.UI.Notification.PostTicker("~b~Cleared all hordes.", false);
            };
            _showPlayerCoords.CheckboxChanged += (s, a) =>
            {
                config.ShowPlayerCoordinates = _showPlayerCoords.Checked;
                GTA.UI.Notification.PostTicker($"~b~Player Coordinates: {(_showPlayerCoords.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _statusOverlay.CheckboxChanged += (s, a) =>
            {
                config.ShowStatusOverlay = _statusOverlay.Checked;
                GTA.UI.Notification.PostTicker($"~b~Status Overlay: {(_statusOverlay.Checked ? "~g~Enabled" : "~r~Disabled")}", false);
            };
            _showSpawnRadiusRing.CheckboxChanged += (s, a) => config.ShowSpawnRadiusRing = _showSpawnRadiusRing.Checked;
            _spawnRadiusRingMeters.ValueChanged += (s, a) => config.SpawnRadiusRingMeters = _spawnRadiusRingMeters.Value;

            DebugMenu.Add(spawnZombie);
            DebugMenu.Add(spawnRunner);
            DebugMenu.Add(killAll);
            DebugMenu.Add(clearAll);
            DebugMenu.Add(saveGame);
            DebugMenu.Add(loadGame);
            DebugMenu.Add(triggerWander);
            DebugMenu.Add(triggerNight);
            DebugMenu.Add(clearHordes);
            DebugMenu.Add(_statusItem);
            DebugMenu.Add(_showPlayerCoords);
            DebugMenu.Add(_statusOverlay);
            DebugMenu.Add(_showSpawnRadiusRing);
            DebugMenu.Add(_spawnRadiusRingMeters);

            DebugMenu.Closed += (s, a) => ModConfig.Current.Save();
        }

        private void SpawnDebugZombie(bool runner)
        {
            var player = Game.Player.Character;
            if (player?.Exists() is not true || player.IsDead)
                return;
            _zombies.SpawnZombieAt(player.Position + (player.ForwardVector * 4f), runner);
        }

        // Debug HUD for mapping territories: player position/heading/game zone plus a live line
        // per territory (distance from its center, radius, owner). The '*' prefix marks zones the
        // player is currently inside the visible TerritoryRadius ring of - the ring (not the game
        // zone) is what the player actually sees, so overlap is judged against that distance.
        // Returns the next free y so the status overlay can stack below it.
        private float DrawPlayerCoordinates(float y)
        {
            var player = Game.Player.Character;
            if (player?.Exists() is not true)
                return y;

            var config = ModConfig.Current;
            var pos = player.Position;
            string zone = Function.Call<string>(Hash.GET_NAME_OF_ZONE, pos.X, pos.Y, pos.Z);

            var lines = new System.Collections.Generic.List<string>
            {
                $"X {pos.X:F2}   Y {pos.Y:F2}   Z {pos.Z:F2}   Hdg {player.Heading:F1}",
                $"Zone {zone}"
            };

            if (_factions != null)
            {
                foreach (var z in _factions.Zones)
                {
                    float dist = SpawnUtils.HorizontalDistance(z.Center, pos);
                    string inside = dist <= config.TerritoryRadius ? "*" : " ";
                    string owner = z.IsPlayerOwned ? "You" : (z.Owner?.DisplayName ?? "Neutral");
                    lines.Add($"{inside} {z.DisplayName}: {dist:F0}m (R{config.TerritoryRadius:F0}) {owner}");
                }
            }

            foreach (var line in lines)
            {
                DrawDebugText(line, 0.012f, y, 0.42f, 255, 255, 255, 235);
                y += 0.028f;
            }
            return y;
        }

        // Always-on status (Debug > "Show Status Overlay"): the same text as the Debug > Status
        // item description, drawn in the top-left HUD block below any coordinate lines. Built
        // fresh each frame so it stays live while the menu is closed.
        private float DrawStatusLines(float y)
        {
            if (_zombies is null || _survivors is null)
                return y;

            foreach (var line in BuildStatusText().Split('\n'))
            {
                DrawDebugText(line, 0.012f, y, 0.42f, 255, 255, 255, 235);
                y += 0.028f;
            }
            return y;
        }

        // Gameplay feedback (not debug): while the player is outside a hostile zone's ring but its
        // takeover attempt is in the leave-grace window, draw a centered countdown offering them
        // a chance to step back in and keep the fight alive.
        private void DrawLeaveGraceCountdown()
        {
            if (_factions is null)
                return;

            var graces = _factions.GetActiveLeaveGraces();
            if (graces.Count == 0)
                return;

            float y = 0.2f;
            foreach (var (name, seconds) in graces)
            {
                DrawCenteredText($"~ Return to {name} to continue the fight - {seconds}s", y, 0.55f, 255, 255, 255, 235);
                y += 0.032f;
            }
        }

        // On-screen debug text (GTA text commands; screen coords are 0-1 fractions from the
        // top-left). Font 0 = the standard sans-serif; outline keeps it readable over any ground.
        private void DrawDebugText(string text, float x, float y, float scale,
            int r, int g, int b, int a)
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, a);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y);
        }

        // Same text commands centered horizontally (used for the leave-grace countdown).
        private void DrawCenteredText(string text, float y, float scale,
            int r, int g, int b, int a)
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, a);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.SET_TEXT_CENTRE, 1);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, y);
        }

        // Debug player radius ring on the minimap (ADD_BLIP_FOR_RADIUS, same technique as the
        // territory rings): follows the player and resizes when the slider changes. Used to
        // measure what a player-relative faction-wave spawn radius would cover in a territory
        // before the safe wave-spawn rework wires the value into the spawn code.
        private void UpdatePlayerRadiusRing()
        {
            var config = ModConfig.Current;
            var player = Game.Player.Character;
            if (!config.ShowSpawnRadiusRing || player?.Exists() is not true)
            {
                _playerRingBlip?.Delete();
                _playerRingBlip = null;
                return;
            }

            if (_playerRingBlip?.Exists() is not true || _playerRingRadius != config.SpawnRadiusRingMeters)
            {
                // Radius blips take their size from the create call (Scale would set the ring to
                // that many meters) - resize by recreating.
                _playerRingBlip?.Delete();
                _playerRingBlip = World.CreateBlip(player.Position, config.SpawnRadiusRingMeters);
                _playerRingBlip.Alpha = 160;
                _playerRingBlip.IsHiddenOnLegend = true;
                _playerRingBlip.Color = BlipColor.White;
                _playerRingBlip.SecondaryColor = System.Drawing.Color.Cyan;
                _playerRingBlip.Name = "Spawn radius";
                _playerRingRadius = config.SpawnRadiusRingMeters;
            }

            _playerRingBlip.Position = player.Position;
        }

        private void ApplyAllToSystems()
        {
            var config = ModConfig.Current;

            _zombies.TargetZombieCount = config.TargetZombieCount;
            _zombies.SpawnRadius = config.ZombieSpawnRadius;
            _zombies.DespawnRadius = config.ZombieDespawnRadius;
            _zombies.MinSpawnDistance = config.MinSpawnDistance;
            _zombies.NightRunnersEnabled = config.NightRunnersEnabled;
            _zombies.NightRunnerChance = config.NightRunnerChance;

            PedBudgetManager.Instance.GlobalBudget = config.MaxZombies;

            _world.IsApocalypseMode = config.IsApocalypseMode;
            _world.ClearTraffic = config.ClearTraffic;
            _world.DisableCops = config.DisableCops;
        }

        private string BuildStatusText()
        {
            var budget = PedBudgetManager.Instance;
            var queue = SpawnRequestQueue.Instance;

            return $"Zombies: {_zombies.ActiveCount}/{_zombies.TargetZombieCount}   Survivors: {_survivors?.ActiveCount ?? 0}\n" +
                   $"Queue: {queue.PendingCount} pending ({queue.MaxGrantsPerPass}/pass)   Peds: {budget.CurrentCount}/{budget.GlobalBudget}\n" +
                   $"{_horde?.GetSummary()}\n" +
                   $"{_factions?.GetSummary()}\n" +
                   $"{budget.GetBudgetSummary()}";
        }

        private string BuildFactionStatusText()
        {
            if (_factions is null)
                return "FactionSystem not initialized";

            var lines = _factions.Definitions
                .Select(d => $"{d.DisplayName}: {_factions.CountAlive(d)}");
            return string.Join("\n", lines) +
                   $"\nRecruits: {_factions.RecruitCount}/{ModConfig.Current.MaxRecruits}\n" +
                   $"Zones owned: {_factions.PlayerZoneCount}/{ModConfig.Current.MaxPlayerZones}";
        }

        private string BuildTerritoryStatusText()
        {
            if (_factions is null)
                return "FactionSystem not initialized";

            var lines = _factions.Zones.Select(z =>
            {
                string owner = z.IsPlayerOwned ? "You" : (z.Owner?.DisplayName ?? "Neutral");
                return $"{z.DisplayName}: {owner}";
            });
            return string.Join("\n", lines);
        }

        public void Tick()
        {
            var config = ModConfig.Current;

            // Debug HUD: coordinates then the always-on status, stacked in the same top-left block.
            float y = 0.02f;
            if (config.ShowPlayerCoordinates)
                y = DrawPlayerCoordinates(y);
            if (config.ShowStatusOverlay)
                DrawStatusLines(y);

            // Gameplay text (leave-grace countdown) and the debug minimap ring render every frame,
            // independent of menu visibility.
            DrawLeaveGraceCountdown();
            UpdatePlayerRadiusRing();

            if (Pool.AreAnyVisible)
            {
                _statusItem!.Description = BuildStatusText();
                _factionStatusItem!.Description = BuildFactionStatusText();
                _zoneStatusItem!.Description = BuildTerritoryStatusText();
            }
        }

        public void ToggleMainMenu()
        {
            Pool.HideAll();
            MainMenu.Visible = true;
        }

        public void Process()
        {
            Pool.Process();
        }
    }
}
