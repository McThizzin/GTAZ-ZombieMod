using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTA.Math;
using GTA.Native;
using GTAZ.Core;
using GTAZ.Data;
using GTAZ.Events;
using GTAZ.Extensions;
using GTAZ.Utils;

namespace GTAZ.Systems
{
    // Phase 4 - Factions & Territory (DOCS/06-Roadmap.md §4). Owns the faction/territory
    // definitions (built by the Data catalogs - see FactionCatalog/TerritoryCatalog), the
    // capturable zone list, and all non-recruit faction peds (guards, defenders, patrols).
    // Spawning (ambient/patrol/defender/wave/raid SpawnRequests) lives in FactionSpawner;
    // recruiting (soft-aim + recruit/dismiss + recruit upkeep) lives in RecruitManager; the
    // territory state machine (Zones + blips + capture/raid/defense state) lives in
    // TerritorySystem and calls back here for per-zone ped counts/cleanup. Cross-system queries
    // (ZombieSystem zone suppression, WorldSystem clear-skip) go through the singleton; the live
    // state lives here.
    //
    // Gameplay model: zones start bandit-held. Entering one spawns a defender wave; killing
    // them (plus any ambient bandits occupying the zone) triggers reinforcement waves that home
    // in on the player, and clearing all the waves captures the zone outright - no hold timer
    // (DOCS/06-Roadmap.md §4.8). Captured zones are player-owned: they suppress ambient zombie
    // spawns and spawn friendly survivor guards. Ambient survivor/bandit peds spawn near the
    // player via the shared SpawnRequestQueue (PedCategory.FactionNpc). Press SoftAimKey
    // (default H) to toggle a reticule at a survivor and press RecruitKey (default X) to add
    // them to the player's group as an essential recruit; pressing again removes them.
    public class FactionSystem
    {
        private static FactionSystem _instance;
        public static FactionSystem Instance => _instance ??= new FactionSystem();

        private readonly PedBudgetManager _budget = PedBudgetManager.Instance;
        private readonly Random _rng = new();

        private readonly FactionDef _survivors;
        // The Families (formerly "Bandits" - the internal Id stays "Bandits" so the
        // BanditPatrolIntervalMinutes config key and player saves stay compatible).
        private readonly FactionDef _families;

        private readonly List<FactionNPC> _activePeds = [];

        // Recruiting (soft-aim + recruit/dismiss + recruit upkeep) lives in RecruitManager; it
        // calls back into this class for the _activePeds list and budget-slot release.
        private readonly RecruitManager _recruits;

        // Ambient/patrol/territory spawn construction lives in FactionSpawner; it calls back
        // into this class for the _activePeds list and per-faction population counts, and
        // queries TerritorySystem for the territory membership that keeps spawns out of owned
        // zones and inside hostile turf.
        private readonly FactionSpawner _spawner;

        // Territory state machine (Zones + blips + hostile-zone defense + owned-zone raids +
        // capture/surrender/restore) lives in TerritorySystem; it calls back into this class
        // (via the ctor-delegate fields) for per-zone living-ped counts and zone-ped cleanup,
        // and spawns defenders/raiders/guards through the FactionSpawner.
        private readonly TerritorySystem _territory;

        // Distance past which faction peds fade so exploring keeps producing fresh encounters.
        private const float FactionDespawnDistance = 350f;

        public List<FactionDef> Definitions { get; }
        public List<TerritoryZone> Zones => _territory.Zones;

        public int ActiveCount => _activePeds.Count;
        public int RecruitCount => _recruits.RecruitCount;
        public int PlayerZoneCount => _territory.PlayerZoneCount;
        public bool IsTerritoryFightActive => _territory.IsTerritoryFightActive;

        public FactionSystem()
        {
            Definitions = FactionCatalog.Build();
            _survivors = Definitions.First(d => d.Id == FactionCatalog.SurvivorFactionId);
            _families = Definitions.First(d => d.Id == FactionCatalog.BanditFactionId);
            _recruits = new RecruitManager(_survivors, FindActiveSurvivorNpc,
                npc => _activePeds.Add(npc), npc => _activePeds.Remove(npc), ReleaseSlot);
            _territory = new TerritorySystem(_budget, _rng, _survivors, _families, GetFaction,
                CountLivingZonePeds, CleanupZonePeds);
            _spawner = new FactionSpawner(_budget, SpawnRequestQueue.Instance, _rng, Definitions,
                _families, npc => _activePeds.Add(npc), CountAlive, IsPlayerOwnedZone,
                _territory.TerritoryOwnerAt, IsInAnyTerritory);
            _territory.AttachSpawner(_spawner);
        }

        // RecruitManager callback: finds the survivor-faction FactionNPC wrapping a ped (outpost
        // guards and any adopted ambient survivor). Used by the soft-aim predicate and by the
        // recruit action to see whether the target is already faction-owned.
        private FactionNPC FindActiveSurvivorNpc(Ped ped)
        {
            return _activePeds.FirstOrDefault(n => n.Ped == ped && n.Faction == _survivors);
        }

        public void OnTick()
        {
            if (Game.Player.Character.IsDead || !Game.Player.CanControlCharacter)
                return;

            // Budget reserves only exist while territory content can actually request peds (see
            // PedBudgetManager.SetTerritoryContext). Outside any territory the FactionNpc reserve
            // is dead weight on the zombie budget, so the whole budget is told where the player
            // is before anything spawns this tick.
            _budget.SetTerritoryContext(_territory.IsPlayerInTerritoryContext());

            _territory.InitializeOwnersOnce();

            bool enabled = ModConfig.Current.EnableFactions;
            if (enabled)
            {
                _territory.UpdateZones();
                _territory.UpdateTerritoryFights();
                _recruits.UpdateSoftAim();
                _spawner.TryAmbientSpawn();
                _spawner.TryFamiliesEncounter();
                _territory.UpdateHostileDefenseOverride();
            }
            else
            {
                // Factions toggled off mid-defense: release the borrowed horde budget.
                _territory.ReleaseHordeOverride();
            }

            // Always update peds so recruits stay alive/functional if factions are toggled off.
            UpdatePeds();
        }

        private void UpdatePeds()
        {
            var playerPos = Game.Player.Character.Position;

            for (int i = _activePeds.Count - 1; i >= 0; i--)
            {
                var npc = _activePeds[i];
                npc.Update();

                if (npc.Ped?.Exists() is not true)
                {
                    ReleaseSlot(npc);
                    _activePeds.RemoveAt(i);
                    continue;
                }
                if (npc.Ped.IsDead)
                {
                    if (npc.HomeZone != null && npc.HomeZone.Owner == npc.Faction)
                        _territory.RegisterBanditKillNearTerritory(npc.Ped.Position);
                    DropLoot(npc);
                    FactionEvents.RaiseFactionNPCDeath(npc.Ped, npc.Faction);
                    npc.OnDeath();
                    ReleaseSlot(npc);
                    _activePeds.RemoveAt(i);
                    continue;
                }
                if (npc.HomeZone != null)
                {
                    // Territory-bound peds (defenders/guards/raiders) are essential event peds -
                    // never distance-clean them against the PLAYER while the fight is on. The
                    // old 3D check cleaned defenders spawning at far-side mapped points (up to
                    // ~400m out) mid-fight without counting a kill, so zones fell with no
                    // reinforcement waves. They despawn only once the player leaves the whole
                    // territory well behind.
                    float zoneDist = SpawnUtils.HorizontalDistance(npc.HomeZone.Center, playerPos);
                    if (zoneDist > ModConfig.Current.TerritoryRadius + FactionDespawnDistance)
                    {
                        npc.Cleanup();
                        ReleaseSlot(npc);
                        _activePeds.RemoveAt(i);
                    }
                    continue;
                }
                if ((npc.Ped.Position - playerPos).Length() > FactionDespawnDistance)
                {
                    npc.Cleanup();
                    ReleaseSlot(npc);
                    _activePeds.RemoveAt(i);
                }
            }

            // Recruit upkeep (missing/dead cleanup + persistent re-assert) lives in RecruitManager.
            _recruits.Update(playerPos);
        }

        private void ReleaseSlot(FactionNPC npc)
        {
            if (npc.Slot != null)
                _budget.ReleaseSlot(npc.Slot);
        }

        // --- Territory state (delegated to TerritorySystem) ---
        //
        // Territory logic (Zones + blips, hostile-zone defenders/defense waves, owned-zone
        // raids, capture/surrender/restore) moved into TerritorySystem; these thin delegates
        // keep FactionSystem's public surface stable for ModCore/ZombieSystem/HordeSystem/
        // MenuManager/PersistenceManager. TerritorySystem calls back here (via the ctor-delegate
        // fields) for per-zone living-ped counts and zone-ped cleanup.

        // The hostile (not player-owned) territory ring containing this position, if any. Used
        // by ZombieSystem to cap ambient zombie density inside faction-held turf (only hostile
        // rings need it - owned zones are fully suppressed as safe territory).
        public TerritoryZone HostileTerritoryAt(Vector3 position) => _territory.HostileTerritoryAt(position);

        // True if the position is inside a player-owned zone. ZombieSystem uses this to suppress
        // ambient zombie spawns inside safe territory (hordes are unaffected - they bypass
        // TryAmbientSpawn).
        public bool IsPlayerOwnedZone(Vector3 position) => _territory.IsPlayerOwnedZone(position);

        // True if the position is inside ANY territory (player-owned or faction-held). A
        // position counts if it's in the territory's game zone OR within TerritoryRadius of its
        // center - the radius is what the player actually sees (the territory ring), and
        // round-5 hordes were spawning just outside a game zone boundary while still sitting in
        // the visible ring of a zone the player was fighting over. Hordes never stage/march
        // through territories, ambient faction members never spawn inside them, and bandit
        // patrols never wander into them - territory interiors are event-driven only (owned-zone
        // raids on the attack timer, hostile-zone defender/defense waves).
        public bool IsInAnyTerritory(Vector3 position) => _territory.IsInAnyTerritory(position);

        // Budget-level territory context (PedBudgetManager.SetTerritoryContext): true while the
        // player is near enough to a zone for territory content to request peds (the same
        // "playerNear" gate territory fights use) or while a fight is already active. Defenders
        // and waves are only ever spawned for the player being in a territory, so outside this
        // context the faction budget reserve would just waste zombie slots.
        public bool IsPlayerInTerritoryContext() => _territory.IsPlayerInTerritoryContext();

        // The faction a territory belongs to by default (Territories.txt mapping). For a
        // hostile zone this equals zone.Owner; for a player-owned zone it's the faction that
        // wants the turf back and therefore raids it (raiders attack as the home faction, not
        // the survivor guards who currently own it).
        public FactionDef HomeFaction(TerritoryZone zone) => _territory.HomeFaction(zone);

        public bool IsZoneUnderAttack(TerritoryZone zone) => _territory.IsZoneUnderAttack(zone);

        // Re-applies owned-territory state loaded from a save (the list of owned zone hashes).
        // Runs on the first tick AFTER zones are initialized (never in a ctor - zone data isn't
        // ready, and the relationship natives aren't either), flipping matching zones to Survivor
        // ownership exactly like CaptureZone would. Guards are NOT spawned here: TrySpawnGuards
        // replenishes them automatically whenever the player returns near an owned outpost.
        public void RestoreOwnedTerritories(IEnumerable<int> zoneHashes) => _territory.RestoreOwnedTerritories(zoneHashes);

        // Player surrenders the owned territory they're currently standing in (within
        // TerritoryRadius of its center). The zone reverts to hostile ownership so the slot
        // frees up and the player can capture a different territory (MaxPlayerZones cap).
        // Guards at the outpost are removed and any active raid on the zone is cancelled.
        public bool SurrenderCurrentZone() => _territory.SurrenderCurrentZone();

        // TerritorySystem callbacks: per-zone living-ped counts and zone-ped cleanup against the
        // _activePeds list (defenders/reinforcement/raiders/guards with a HomeZone).
        private int CountLivingZonePeds(TerritoryZone zone, FactionDef faction)
        {
            return _activePeds.Count(n => n.HomeZone == zone && n.Faction == faction
                && n.Ped?.Exists() is true && !n.Ped.IsDead);
        }

        private void CleanupZonePeds(TerritoryZone zone, FactionDef faction)
        {
            for (int i = _activePeds.Count - 1; i >= 0; i--)
            {
                var npc = _activePeds[i];
                if (npc.HomeZone == zone && npc.Faction == faction)
                {
                    npc.Cleanup();
                    ReleaseSlot(npc);
                    _activePeds.RemoveAt(i);
                }
            }
        }

        // --- Recruiting (DOCS/06-Roadmap.md §4.7) ---
        //
        // Recruiting lives in RecruitManager (soft-aim consumer + recruit/dismiss + recruit
        // upkeep); these public methods keep the stable surface called by ModCore (RecruitKey)
        // and MenuManager (dismiss). RecruitManager calls back here for the _activePeds list and
        // budget-slot release.
        public bool TryRecruitAtCrosshair() => _recruits.TryRecruitAtCrosshair();
        public void DismissAllRecruits() => _recruits.DismissAllRecruits();

        public FactionDef GetFaction(string id)
        {
            return Definitions.FirstOrDefault(d => d.Id == id);
        }

        // Ambient/patrol peds for a faction (HomeZone-bound defenders/reinforcements/raiders are
        // event peds managed by the territory system, NOT ambient spawns - counting them against
        // FactionSpawnTarget starved ambient spawns near a faction's own turf, which is why The
        // Lost never appeared on bikes around their home zones).
        public int CountAlive(FactionDef faction)
        {
            return _activePeds.Count(n => n.Faction == faction && n.HomeZone == null);
        }

        // Killed faction members drop their weapon so fighting bandits/survivors pays out
        // (same design as SurvivorSystem.DropLoot - null model = game picks the default mesh).
        private void DropLoot(FactionNPC npc)
        {
            PedLootUtils.DropLoot(npc.Ped, npc.Weapon);
        }

        public void ClearAllFactions()
        {
            // TerritorySystem owns the horde-budget override (hostile-zone defense + territory
            // fights) and the zone blips - ClearAll releases the borrows and deletes the blips.
            _territory.ClearAll();

            foreach (var npc in _activePeds)
                npc.Cleanup();
            _activePeds.Clear();

            _recruits.ClearAll();
        }

        public string GetSummary()
        {
            return $"Factions: {ActiveCount} peds, {RecruitCount} recruits, " +
                   $"{PlayerZoneCount}/{ModConfig.Current.MaxPlayerZones} zones";
        }
    }
}
