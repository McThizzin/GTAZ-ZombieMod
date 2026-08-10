using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Events;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    // Phase 4 territory state machine (DOCS/11-Refactoring-Plan.md §3.4), split out of
    // FactionSystem. Owns the 12 TerritoryZones (built by TerritoryCatalog), their blips, and all
    // capture/raid/defense state: hostile-zone takeover (defenders, reinforcement waves, capture),
    // owned-zone timer raids (territory fights), surrender, save-restore, and the per-ring
    // territory queries ZombieSystem/HordeSystem use. FactionSystem keeps the peds; this class
    // reaches into them only through the countLivingZonePeds / cleanupZonePeds callbacks and
    // spawns defenders/raiders/guards through the FactionSpawner (attached after both exist, since
    // the spawner's ambient/patrol path queries this class for territory membership).
    //
    // Budget: hostile-zone defenses and owned-zone raids both borrow the horde budget
    // (PedBudgetManager.RebalanceForHorde, ref-counted), so hordes and fights coexist without
    // stacking minimums. _hostileOverrideHeld tracks the defense ref; _territoryFights tracks the
    // fight ref. Both must be released on ClearAll so factions toggling off never leak the borrow.
    public class TerritorySystem(PedBudgetManager budget, Random rng, FactionDef survivors,
        FactionDef families, Func<string, FactionDef> getFaction,
        Func<TerritoryZone, FactionDef, int> countLivingZonePeds,
        Action<TerritoryZone, FactionDef> cleanupZonePeds)
    {
        private FactionSpawner _spawner;

        // Zones currently under hostile counter-attack (Phase 4 territory fights). At most one is
        // active at a time - a fight borrows the horde budget (PedBudgetManager.RebalanceForHorde)
        // and hordes are suppressed while it runs, so they can't stack on the shared budget.
        private readonly List<TerritoryZone> _territoryFights = [];

        private int _guardTimer;

        // Bandit ownership is assigned on first tick (BuildZones runs in the ctor, before
        // relationship setup, so Owner is filled lazily).
        private bool _zonesInitialized;

        // True while a hostile-zone takeover defense is active and the horde budget override is
        // borrowed (RebalanceForHorde, ref-counted alongside owned-zone raids). Suppresses new
        // wandering/night hordes mid-fight - the playtest log showed hordes starting while the
        // player defended a hostile zone (Pacific Bluffs 10:59, CMSW 11:42/11:54/11:57, Galileo
        // 12:12) because only the owned-zone raid path held the override.
        private bool _hostileOverrideHeld;

        public List<TerritoryZone> Zones { get; } = TerritoryCatalog.BuildZones();

        public int PlayerZoneCount => Zones.Count(z => z.IsPlayerOwned);
        public bool IsTerritoryFightActive => _territoryFights.Count > 0;
        public bool HostileOverrideHeld => _hostileOverrideHeld;

        // Called once both TerritorySystem and FactionSpawner exist (FactionSystem ctor): the
        // spawner needs this class's territory queries at construction, and this class needs the
        // spawner's SpawnFactionPed for defenders/raiders/guards. OnTick never runs before this.
        public void AttachSpawner(FactionSpawner spawner) => _spawner = spawner;

        // First-tick zone ownership (never in a ctor - zone data isn't ready, and the relationship
        // natives aren't either). Idempotent.
        public void InitializeOwnersOnce()
        {
            if (_zonesInitialized)
                return;
            _zonesInitialized = true;
            foreach (var zone in Zones)
                zone.Owner = getFaction(zone.HomeFactionId) ?? families;
        }

        public void UpdateZones()
        {
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;

            bool showBlips = config.ShowTerritoryBlips;
            bool showRadii = config.ShowTerritoryRadii;

            foreach (var zone in Zones)
            {
                if (showBlips)
                    zone.CreateBlip();
                else
                    zone.DeleteBlip();

                if (showRadii)
                    zone.CreateRadiusBlip(config.TerritoryRadius);
                else
                    zone.DeleteRadiusBlip();

                // "Inside the territory" means inside the visible TerritoryRadius ring around the
                // center - that's what the player sees and it matches the radius bandit kills are
                // counted against (RegisterBanditKillNearTerritory). The game-zone membership test
                // is narrower, so a player standing in the ring but outside the game zone reset
                // FightKills/ReinforcementWavesSent every tick (repeated "defense quota 1/4" lines
                // in GTAZ.log) and defenders never escalated. Distance is horizontal: centers carry
                // a flat Z (40) and 3D length would push players out of the ring on hillsides.
                bool playerInside = SpawnUtils.HorizontalDistance(zone.Center, playerPos) <= config.TerritoryRadius;

                if (zone.IsPlayerOwned)
                {
                    zone.PlayerAwayTicks = 0;
                    TrySpawnGuards(zone);
                    continue;
                }

                if (!playerInside)
                {
                    zone.PlayerAwayTicks++;
                    if (zone.PlayerAwayTicks > 120)
                    {
                        zone.ResistTicks = 0;
                        zone.RespawnCooldownTicks = 0;
                        zone.PendingDefenderSpawns = 0;
                        zone.DefendersPending = false;
                        zone.CaptureInProgress = false;
                        zone.CapBlockedNotified = false;
                    }
                    // Leaving the territory resets the takeover attempt: kills stop counting
                    // toward a defense wave the moment the player steps out of the zone, and
                    // the reinforcement-wave budget refills for the next visit.
                    zone.FightKills = 0;
                    zone.ReinforcementWavesSent = 0;
                    continue;
                }

                zone.PlayerAwayTicks = 0;

                // Takeover-defense escalation (per-area horde mechanic): killing bandits inside
                // a hostile zone fills its defense quota, and each time it's met the bandits top
                // back up to full defender strength (OutpostDefenderCount) to defend the takeover
                // attempt. Capped per visit by TerritoryFightReinforcementWaves and never allowed
                // to exceed OutpostDefenderCount, so sustained fighting runs the zone dry and a
                // full clear + hold is always winnable. Stops the moment the player proves the
                // clear (CaptureInProgress) so the capture bar always fills. Round-4 shipped
                // waves that stacked a full extra WaveSize (8) on top of the base defenders (6),
                // so a hostile zone could never be cleared (DOCS/08-Lessons-Learned.md #33).
                int defenders = countLivingZonePeds(zone, zone.Owner);
                if (zone.FightKills >= config.TerritoryFightKillQuota
                    && zone.ReinforcementWavesSent < config.TerritoryFightReinforcementWaves
                    && defenders < config.OutpostDefenderCount
                    && !zone.DefendersPending
                    && !zone.CaptureInProgress)
                {
                    SpawnReinforcementWave(zone, config.OutpostDefenderCount - defenders);
                    zone.ReinforcementWavesSent++;
                    zone.FightKills = 0;
                }

                if (defenders > 0)
                {
                    // Wave on the ground: keep the respawn clock full so the capture gap only
                    // starts when the last defender drops.
                    zone.RespawnCooldownTicks = config.DefenderRespawnCooldownSeconds * 60;
                    zone.ResistTicks = 0;
                    continue;
                }

                if (zone.DefendersPending)
                    continue; // defender spawns queued but not on the ground yet

                // No defenders alive: count down the respawn gap, then spawn a fresh wave so a
                // hostile zone always fights back while the player stays in it. Once the player
                // has proven a full clear (CaptureInProgress) respawns stop for the remainder of
                // the visit, so the capture completes on the clear.
                if (zone.RespawnCooldownTicks > 0)
                {
                    zone.RespawnCooldownTicks--;
                }
                else if (!zone.CaptureInProgress)
                {
                    SpawnDefenders(zone);
                    zone.RespawnCooldownTicks = config.DefenderRespawnCooldownSeconds * 60;
                    continue;
                }

                // Inside the respawn gap: hold the cleared zone past a short grace, then capture
                // immediately - ownership is earned by defeating the waves, not by standing in
                // the zone for ZoneCaptureTime (the hold-timer is gone).
                zone.ResistTicks++;
                if (zone.ResistTicks < 60)
                    continue; // 1s grace so capture doesn't start the instant the last defender drops

                // The player has proven a full clear: latch the takeover so defender respawns
                // and reinforcement waves stop (a wave can't re-materialize mid-capture), then
                // flip ownership right away - no capture timer.
                zone.CaptureInProgress = true;

                if (PlayerZoneCount >= config.MaxPlayerZones)
                {
                    // At the territory cap the clear is real but ownership is capped - the zone
                    // is being held cleared, not failed. Round-5 silently skipped CaptureZone
                    // here, which left the player grinding an un-capturable zone (the CMSW/
                    // Galileo sessions showed 89/33 defense kills with zero captures - the zones
                    // were clearing fine, the cap was silently blocking). Tell the player once
                    // per visit what's actually happening.
                    if (!zone.CapBlockedNotified)
                    {
                        zone.CapBlockedNotified = true;
                        GTA.UI.Notification.PostTicker($"~y~{zone.DisplayName} cleared, but you already own {config.MaxPlayerZones} territories.", false);
                        Logger.Info($"Capture blocked at {zone.DisplayName}: player owns {PlayerZoneCount}/{config.MaxPlayerZones} zones (cap)");
                    }
                    continue; // at cap - hold the cleared zone instead of silently failing
                }

                CaptureZone(zone);
            }
        }

        // Hostile-zone takeover defenses borrow the horde budget just like owned-zone raids
        // (RebalanceForHorde is ref-counted, so a raid + defense can overlap without stacking
        // minimums). Runs AFTER UpdateZones so the freshly-set defender state is visible. The
        // override is held while the player stands inside a hostile zone that is in any active
        // defense state (defenders alive, spawns queued, respawn gap, or capture hold) - a zone
        // that's cleared-but-quiet at the cap still counts as active so a horde can't spawn in
        // on top of the player mid-hold.
        public void UpdateHostileDefenseOverride()
        {
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;

            bool busy = false;
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned)
                    continue;
                if (SpawnUtils.HorizontalDistance(zone.Center, playerPos) > config.TerritoryRadius)
                    continue;

                bool hasDefenders = countLivingZonePeds(zone, zone.Owner) > 0;
                if (hasDefenders || zone.DefendersPending || zone.RespawnCooldownTicks > 0 || zone.ResistTicks > 0)
                {
                    busy = true;
                    break;
                }
            }

            if (busy && !_hostileOverrideHeld)
            {
                budget.RebalanceForHorde(true);
                _hostileOverrideHeld = true;
            }
            else if (!busy && _hostileOverrideHeld)
            {
                budget.RebalanceForHorde(false);
                _hostileOverrideHeld = false;
            }
        }

        // Factions toggled off mid-defense: release the borrowed horde budget.
        public void ReleaseHordeOverride()
        {
            if (!_hostileOverrideHeld)
                return;
            _hostileOverrideHeld = false;
            budget.RebalanceForHorde(false);
        }

        private void SpawnDefenders(TerritoryZone zone)
        {
            int count = ModConfig.Current.OutpostDefenderCount;
            for (int i = 0; i < count; i++)
            {
                // Defenders hold the ZONE CENTER (like survivor guards at owned outposts) rather
                // than the far mapped wave points: a garrison scattered across the mapped points
                // (up to ~400m out at Hawick) left most defenders out of reach of the player -
                // they sat outside FactionDespawnDistance, got distance-cleaned without counting
                // a kill, and the zone fell after a couple of kills with no reinforcement waves
                // (FightKills never met the quota). Fall back to a mapped point only if the
                // safe-position search around the center fails.
                var pos = SpawnUtils.FindSafePosition(zone.Center, 5f, 40f);
                if (pos == Vector3.Zero)
                    pos = GetWaveSpawn(zone, 5f, 40f);
                if (pos == Vector3.Zero)
                    continue;

                zone.PendingDefenderSpawns++;
                zone.DefendersPending = true;
                _spawner.SpawnFactionPed(zone.Owner, pos, FactionRole.Guard, zone,
                    onSpawned: _ => DecrementPendingDefenders(zone),
                    onExpired: () => DecrementPendingDefenders(zone));
            }

            // Round-5 "zone taken with no fight": if every safe-position probe failed nothing was
            // queued, so the zone sat defender-free and the capture bar filled on its own. Log it
            // so a trivially-capturable zone is diagnosable instead of looking like a design win.
            if (zone.PendingDefenderSpawns == 0)
                Logger.Info($"Defender wave FAILED to spawn at {zone.DisplayName} - no safe positions around {zone.Center}");
            else
                Logger.Info($"Defender wave spawned at {zone.DisplayName} ({count} {zone.Owner?.DisplayName ?? "defenders"})");
        }

        private void DecrementPendingDefenders(TerritoryZone zone)
        {
            zone.PendingDefenderSpawns = Math.Max(0, zone.PendingDefenderSpawns - 1);
            if (zone.PendingDefenderSpawns == 0)
                zone.DefendersPending = false;
        }

        // Kill-triggered reinforcement burst at a hostile zone (the takeover-defense mechanic).
        // Distinct from SpawnDefenders' respawn cooldown: this one escalates with player kills
        // while attacking the holder's turf. It tops the zone back UP to OutpostDefenderCount
        // rather than stacking a full extra wave on top, and spawns slightly further out so the
        // new defenders flank rather than materialize on top of the player. Posting a ticker
        // keeps the escalation visible in-game (the "no notifications or logs" complaint).
        private void SpawnReinforcementWave(TerritoryZone zone, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var pos = GetWaveSpawn(zone, 40f, 90f);
                if (pos == Vector3.Zero)
                    continue;
                _spawner.SpawnFactionPed(zone.Owner, pos, FactionRole.Guard, zone);
            }

            string name = zone.Owner?.DisplayName ?? "hostile faction";
            GTA.UI.Notification.PostTicker($"~r~{name} is reinforcing {zone.DisplayName}!", false);
            Logger.Info($"Reinforcement wave spawned at {zone.DisplayName} ({count} {name})");
        }

        // Replenishes survivor guards at a player-owned outpost while the player is near it.
        private void TrySpawnGuards(TerritoryZone zone)
        {
            var playerPos = Game.Player.Character.Position;
            if (SpawnUtils.HorizontalDistance(zone.Center, playerPos) > 150f)
                return;

            if (++_guardTimer < 120)
                return;
            _guardTimer = 0;

            int alive = countLivingZonePeds(zone, survivors);
            int target = ModConfig.Current.OutpostDefenderCount;
            if (alive >= target)
                return;

            for (int i = 0; i < target - alive; i++)
            {
                var pos = SpawnUtils.FindSafePosition(zone.Center, 5f, 40f);
                if (pos == Vector3.Zero)
                    continue;
                _spawner.SpawnFactionPed(survivors, pos, FactionRole.Guard, zone);
            }
        }

        // --- Territory fights (DOCS/06-Roadmap.md §4.13) ---
        //
        // A counter-attack hits a player-owned zone while the player is near it. Bandit deaths
        // near the zone (FightKills) accelerate the attack, but it ALSO triggers on a timer
        // (TerritoryFightCooldownMinutes) so waves reliably appear even if the player never
        // fights bandits near their outpost - previously the fight was gated purely on a kill
        // quota that required bandits to die within TerritoryRadius, which in practice often
        // never happened ("still no faction waves"). The raid borrows the horde budget
        // (RebalanceForHorde(true), ref-counted) so it can push peds without blowing the cap.
        public void UpdateTerritoryFights()
        {
            var config = ModConfig.Current;
            var playerPos = Game.Player.Character.Position;

            for (int i = _territoryFights.Count - 1; i >= 0; i--)
            {
                var zone = _territoryFights[i];
                zone.FightTicks++;

                bool playerNear = SpawnUtils.HorizontalDistance(zone.Center, playerPos) < config.TerritoryRadius + 100f;
                // Grace window so queued raider spawns (3 grants/pass) can materialize before
                // the "all raiders dead" check can end the fight - without it a fight could
                // start and be declared repelled before its own spawns were on the ground. The
                // fight also stays open while any raider spawn is still queued (PendingRaiderSpawns),
                // so the "attack/repel loop" (fight started then ended 3s later, raiders never
                // on the ground) can't happen - the raid ends only once every queued raider has
                // either spawned or failed to materialize.
                bool raidersAlive = zone.FightTicks < 300
                    || zone.PendingRaiderSpawns > 0
                    || countLivingZonePeds(zone, HomeFaction(zone)) > 0;

                if (!playerNear || !raidersAlive)
                    EndTerritoryFight(zone);
            }

            foreach (var zone in Zones)
            {
                if (zone.FightCooldownTicks > 0)
                    zone.FightCooldownTicks--;

                if (!zone.IsPlayerOwned || _territoryFights.Contains(zone))
                    continue;

                bool playerNear = SpawnUtils.HorizontalDistance(zone.Center, playerPos) < config.TerritoryRadius + 100f;
                if (!playerNear)
                    continue;

                // Cooldown gate (DOCS/08-Lessons-Learned.md #33): the round-4 rewrite dropped the kill-quota
                // condition and took this gate with it, so StartTerritoryFight ran every tick the
                // player stood near an owned zone - fights died on the grace expiry and instantly
                // restarted (endless "started/ended" pairs in GTAZ.log). Timer-only means the gate
                // stays: raids fire only after the attack interval elapses.
                if (zone.FightCooldownTicks > 0)
                    continue;

                // Owned-zone raids are timer-only: they fire once the attack interval elapses
                // while the player is near an owned zone (the "Territory Attack Interval (min)"
                // slider). Kills no longer accelerate raids - that quota now drives hostile-zone
                // defense waves instead (see UpdateZones / RegisterBanditKillNearTerritory).
                StartTerritoryFight(zone);
            }
        }

        private void StartTerritoryFight(TerritoryZone zone)
        {
            var config = ModConfig.Current;

            // Hordes and fights share the borrowed budget. RebalanceForHorde is ref-counted in
            // PedBudgetManager, so a fight CAN start while a horde is live (they don't stack
            // minimums twice); HordeSystem still suppresses NEW hordes while a fight holds the
            // override. Previously this bailed on HordeOverrideActive, which meant the common
            // case of a live wandering horde silently disabled all territory fights.
            if (SpawnUtils.HorizontalDistance(zone.Center, Game.Player.Character.Position) > config.TerritoryRadius + 100f)
                return;

            zone.FightKills = 0;
            zone.FightTicks = 0;
            zone.FightCooldownTicks = config.TerritoryFightCooldownMinutes * 60 * 60;
            _territoryFights.Add(zone);

            if (_territoryFights.Count == 1)
                budget.RebalanceForHorde(true);

            string raiderName = HomeFaction(zone)?.DisplayName ?? "a hostile faction";
            GTA.UI.Notification.PostTicker($"~r~{raiderName} is attacking {zone.DisplayName}! Defend your territory.", false);

            int count = config.TerritoryFightWaveSize;
            for (int i = 0; i < count; i++)
            {
                var pos = GetWaveSpawn(zone, 60f, 140f);
                if (pos == Vector3.Zero)
                    continue;

                // Raiders get the ZONE CENTER as their Origin (not their spawn point) so the
                // FactionAI wander task walks them INTO the outpost to actually attack the
                // guards - previously they wandered 60-140m from the center and the "raid"
                // never reached anyone. Track each queued spawn so the fight can't end
                // "repelled" before the raiders are on the ground.
                zone.PendingRaiderSpawns++;
                _spawner.SpawnFactionPed(HomeFaction(zone), pos, FactionRole.Raider, zone,
                    onSpawned: _ => zone.PendingRaiderSpawns = Math.Max(0, zone.PendingRaiderSpawns - 1),
                    onExpired: () => zone.PendingRaiderSpawns = Math.Max(0, zone.PendingRaiderSpawns - 1),
                    originOverride: zone.Center);
            }

            Logger.Info($"Territory fight started at {zone.DisplayName} ({count} {raiderName}, {zone.PendingRaiderSpawns} queued)");
        }

        private void EndTerritoryFight(TerritoryZone zone)
        {
            if (!_territoryFights.Remove(zone))
                return;

            // Reset the kill counter so the 60s cooldown is meaningful - raiders dying during
            // the fight also count toward the quota, so without this an owned zone would be in
            // an endless back-to-back siege the moment the cooldown expired. Also clear any
            // still-queued raider spawns (e.g. the player left mid-raid).
            zone.FightKills = 0;
            zone.PendingRaiderSpawns = 0;

            GTA.UI.Notification.PostTicker($"~g~The attack on {zone.DisplayName} has been repelled.", false);
            if (_territoryFights.Count == 0)
                budget.RebalanceForHorde(false);

            Logger.Info($"Territory fight ended at {zone.DisplayName}");
        }

        public bool IsZoneUnderAttack(TerritoryZone zone)
        {
            return _territoryFights.Contains(zone);
        }

        // Defender deaths near a HOSTILE zone count toward that zone's takeover-defense quota
        // (proxy for "killed by the player or their followers" - attribution tracking would need
        // damage-source plumbing that isn't worth it here). When the quota is met the zone spawns
        // a reinforcement wave to defend the player's takeover attempt. Owned zones are skipped -
        // raids on player territory are timer-only (the "Territory Attack Interval" slider), so
        // kills there don't accelerate anything. Logs each count so the escalation is traceable
        // in GTAZ.log. Called from FactionSystem.UpdatePeds on every tracked faction-ped death.
        public void RegisterBanditKillNearTerritory(Vector3 position)
        {
            var config = ModConfig.Current;
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned)
                    continue;
                if (SpawnUtils.HorizontalDistance(zone.Center, position) > config.TerritoryRadius)
                    continue;
                zone.FightKills++;
                Logger.Info($"Defender killed near {zone.DisplayName}: defense quota {zone.FightKills}/{config.TerritoryFightKillQuota}");
            }
        }

        private void CaptureZone(TerritoryZone zone)
        {
            zone.Owner = survivors;
            zone.IsPlayerOwned = true;
            zone.ResistTicks = 0;
            zone.RespawnCooldownTicks = 0;
            zone.FightKills = 0;
            zone.ReinforcementWavesSent = 0;
            zone.CaptureInProgress = false;
            zone.PendingRaiderSpawns = 0;
            // Fresh owner gets the full raid cooldown before the first counter-attack: capture set
            // this to 0, so a just-taken zone was raided on the very next tick (log showed "Zone
            // captured" and "Territory fight started" in the same second).
            zone.FightCooldownTicks = ModConfig.Current.TerritoryFightCooldownMinutes * 60 * 60;
            zone.UpdateBlip();
            zone.UpdateRadiusBlip();

            TrySpawnGuards(zone);

            GTA.UI.Notification.PostTicker($"~g~Zone captured: {zone.DisplayName}", false);
            Logger.Info($"Zone captured: {zone.DisplayName}");
            FactionEvents.RaiseZoneCaptured(zone);
        }

        // Re-applies owned-territory state loaded from a save (the list of owned zone hashes).
        // Runs on the first tick AFTER zones are initialized (never in a ctor - zone data isn't
        // ready, and the relationship natives aren't either), flipping matching zones to Survivor
        // ownership exactly like CaptureZone would. Guards are NOT spawned here: TrySpawnGuards
        // replenishes them automatically whenever the player returns near an owned outpost.
        public void RestoreOwnedTerritories(IEnumerable<int> zoneHashes)
        {
            if (zoneHashes is null)
                return;

            int restored = 0;
            foreach (int hash in zoneHashes)
            {
                var zone = Zones.FirstOrDefault(z => z.ZoneHash == hash);
                if (zone?.IsPlayerOwned is not false)
                    continue;

                zone.Owner = survivors;
                zone.IsPlayerOwned = true;
                zone.ResistTicks = 0;
                zone.RespawnCooldownTicks = 0;
                zone.FightKills = 0;
                zone.ReinforcementWavesSent = 0;
                zone.CaptureInProgress = false;
                zone.PendingRaiderSpawns = 0;
                zone.FightCooldownTicks = ModConfig.Current.TerritoryFightCooldownMinutes * 60 * 60;
                zone.PlayerAwayTicks = 0;
                zone.UpdateBlip();
                zone.UpdateRadiusBlip();
                restored++;
            }

            if (restored > 0)
                Logger.Info($"Restored {restored} owned territor{(restored == 1 ? "y" : "ies")} from save");
        }

        // Player surrenders the owned territory they're currently standing in (within
        // TerritoryRadius of its center). The zone reverts to hostile ownership so the slot
        // frees up and the player can capture a different territory (MaxPlayerZones cap).
        // Guards at the outpost are removed and any active raid on the zone is cancelled.
        public bool SurrenderCurrentZone()
        {
            var playerPos = Game.Player.Character.Position;
            float radius = ModConfig.Current.TerritoryRadius;
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned
                    && SpawnUtils.HorizontalDistance(zone.Center, playerPos) <= radius)
                {
                    SurrenderZone(zone);
                    return true;
                }
            }

            GTA.UI.Notification.PostTicker("~r~Stand inside a territory you own to surrender it.", false);
            return false;
        }

        private void SurrenderZone(TerritoryZone zone)
        {
            // Cancel any raid in progress on the zone first so its budget override releases.
            if (_territoryFights.Remove(zone))
            {
                zone.FightKills = 0;
                zone.PendingRaiderSpawns = 0;
                if (_territoryFights.Count == 0)
                    budget.RebalanceForHorde(false);
            }

            // Despawn the survivor guards at the outpost - they hold the zone's center, so they
            // can't stay as faction members once ownership reverts.
            cleanupZonePeds(zone, survivors);

            // Revert to the territory's home faction from Territories.txt (Military, The Lost,
            // Altruists or The Families) so the zone's defenders/defense waves pick back up
            // with the right faction.
            zone.Owner = HomeFaction(zone);
            zone.IsPlayerOwned = false;
            zone.ResistTicks = 0;
            zone.RespawnCooldownTicks = 0;
            zone.FightKills = 0;
            zone.ReinforcementWavesSent = 0;
            zone.CaptureInProgress = false;
            zone.PendingRaiderSpawns = 0;
            zone.FightCooldownTicks = 0;
            zone.PlayerAwayTicks = 0;
            zone.CapBlockedNotified = false;
            zone.UpdateBlip();
            zone.UpdateRadiusBlip();

            GTA.UI.Notification.PostTicker($"~y~You surrendered {zone.DisplayName}.", false);
            Logger.Info($"Zone surrendered: {zone.DisplayName}");
        }

        // --- Cross-system queries ---

        // The hostile (not player-owned) territory ring containing this position, if any. Used
        // by ZombieSystem to cap ambient zombie density inside faction-held turf (only hostile
        // rings need it - owned zones are fully suppressed as safe territory).
        public TerritoryZone HostileTerritoryAt(Vector3 position)
        {
            float radius = ModConfig.Current.TerritoryRadius;
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned)
                    continue;
                if (SpawnUtils.HorizontalDistance(zone.Center, position) <= radius)
                    return zone;
            }
            return null;
        }

        // True if the position is inside a player-owned zone. ZombieSystem uses this to suppress
        // ambient zombie spawns inside safe territory (hordes are unaffected - they bypass
        // TryAmbientSpawn).
        public bool IsPlayerOwnedZone(Vector3 position)
        {
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned
                    && SpawnUtils.HorizontalDistance(zone.Center, position) <= ModConfig.Current.TerritoryRadius)
                {
                    return true;
                }
            }
            return false;
        }

        // True if the position is inside ANY territory (player-owned or faction-held). A
        // position counts if it's in the territory's game zone OR within TerritoryRadius of its
        // center - the radius is what the player actually sees (the territory ring), and
        // round-5 hordes were spawning just outside a game zone boundary while still sitting in
        // the visible ring of a zone the player was fighting over. Hordes never stage/march
        // through territories, ambient faction members never spawn inside them, and bandit
        // patrols never wander into them - territory interiors are event-driven only (owned-zone
        // raids on the attack timer, hostile-zone defender/defense waves).
        public bool IsInAnyTerritory(Vector3 position)
        {
            float radius = ModConfig.Current.TerritoryRadius;
            foreach (var zone in Zones)
            {
                if (zone.IsInZone(position))
                    return true;
                if (radius > 0f && SpawnUtils.HorizontalDistance(zone.Center, position) <= radius)
                    return true;
            }
            return false;
        }

        // Budget-level territory context (PedBudgetManager.SetTerritoryContext): true while the
        // player is near enough to a zone for territory content to request peds (the same
        // "playerNear" gate territory fights use) or while a fight is already active. Defenders
        // and waves are only ever spawned for the player being in a territory, so outside this
        // context the faction budget reserve would just waste zombie slots.
        public bool IsPlayerInTerritoryContext()
        {
            if (_territoryFights.Count > 0)
                return true;

            var playerPos = Game.Player.Character.Position;
            float radius = ModConfig.Current.TerritoryRadius + 100f;
            foreach (var zone in Zones)
            {
                if (SpawnUtils.HorizontalDistance(zone.Center, playerPos) <= radius)
                    return true;
            }
            return false;
        }

        // The faction a territory belongs to by default (Territories.txt mapping). For a
        // hostile zone this equals zone.Owner; for a player-owned zone it's the faction that
        // wants the turf back and therefore raids it (raiders attack as the home faction, not
        // the survivor guards who currently own it).
        public FactionDef HomeFaction(TerritoryZone zone)
        {
            return getFaction(zone.HomeFactionId) ?? families;
        }

        // The faction holding the hostile territory that contains this position, if any. Used by
        // FactionSpawner.TryAmbientSpawn to force ambient spawns inside hostile turf.
        public FactionDef TerritoryOwnerAt(Vector3 position)
        {
            foreach (var zone in Zones)
            {
                if (zone.IsPlayerOwned)
                    continue;
                if (SpawnUtils.HorizontalDistance(zone.Center, position) <= ModConfig.Current.TerritoryRadius)
                    return zone.Owner ?? families;
            }
            return null;
        }

        // Wave spawn for a zone: a random player-mapped point when available, else a random safe
        // position around the center. Mapped points are player-verified safe spots, so they're
        // used directly - no safe-position search that could fail (lesson 34). The mapped point
        // closest to the player is always excluded from the random pick so a wave never spawns
        // right on top of the player; with a single point left the pick is forced.
        private Vector3 GetWaveSpawn(TerritoryZone zone, float fallbackMinDist, float fallbackMaxDist)
        {
            if (zone.WaveSpawnPoints.Length == 0)
                return SpawnUtils.FindSafePosition(zone.Center, fallbackMinDist, fallbackMaxDist);

            var playerPos = Game.Player.Character.Position;
            float best = float.MaxValue;
            int closestIndex = 0;
            for (int i = 0; i < zone.WaveSpawnPoints.Length; i++)
            {
                float d = SpawnUtils.HorizontalDistance(zone.WaveSpawnPoints[i], playerPos);
                if (d < best)
                {
                    best = d;
                    closestIndex = i;
                }
            }

            if (zone.WaveSpawnPoints.Length == 1)
                return zone.WaveSpawnPoints[0];

            int pick = rng.Next(zone.WaveSpawnPoints.Length - 1);
            return zone.WaveSpawnPoints[pick >= closestIndex ? pick + 1 : pick];
        }

        // Toggled off/aborted (FactionSystem.ClearAllFactions): release the borrowed horde-budget
        // refs (hostile defense + territory fights) and delete zone blips.
        public void ClearAll()
        {
            ReleaseHordeOverride();
            if (_territoryFights.Count > 0)
            {
                _territoryFights.Clear();
                budget.RebalanceForHorde(false);
            }

            foreach (var zone in Zones)
                zone.DeleteBlip();
        }
    }
}
