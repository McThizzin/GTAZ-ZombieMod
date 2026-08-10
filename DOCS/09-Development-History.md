# GTAZ — Development History (design log)

What changed and why, round by round. This is **context, not current state** — for what the code is
today see the reference docs (01–05). For things that were tried and failed/removed, see
`10-Archive.md`.

---

## Phase 1 — Foundation ✅
ModCore (the only `Script`), ModuleManager (fixed tick order), ModConfig + ScriptSettings
serialization, Logger, WorldSystem (apocalypse mode: traffic/cop/scenario clearing + time/weather),
LemonUI MenuManager, extensions, ModelCache.

## Phase 2 — Zombie core ✅
PedBudgetManager (120-slot budget) + SpawnRequestQueue (3 grants/pass, 8s TTL), ZombieAI state
machine + Walker/Runner, Perception (sight/hearing), ZombieSystem (ambient + corpse reanimation —
later removed, see Phase 4 round 3), ZombieEvents, night runner modifier. Multiple rounds of
AI/animation/spawn fixes (lessons 1–25).

## Phase 3 — Horde events ✅
Wandering hordes on a timer + a rarer once-per-night siege that hunts the player. Announced
(notification + sound) *before* any entity spawns; materialize 200–400m off-screen through the
shared queue; march to a moving destination; fade via `MarkAsNoLongerNeeded`. While any horde is
live, `RebalanceForHorde(true)` borrows the reserved faction/survivor minimums. Members live in
`HordeSystem`'s own list so ambient cleanup never despawns them.

- **First-pass fix:** the announce played but no horde appeared — ambient suppression gated the only
  `SpawnRequestQueue.ProcessQueue` caller, so horde requests expired on the 8s TTL. `HordeSystem`
  now calls `ProcessQueue()` itself while any horde is live.
- **Second-pass feedback:**
  - Removed `SetAmbientSuppressed` entirely — it stopped ALL ambient spawns for the horde duration
    and left the map empty if you couldn't find the horde. Ambient is throttled in `TryAmbientSpawn`
    (distance + target count), never globally suppressed.
  - Rooftop spawns: `FindSafePosition` rejects candidates >4.5m above the ground surface below
    (lesson 34 refinement).
  - Survivors: green blips, fade past `SurvivorDespawnDistance`, `ConvertNearbyEngineNpcs` re-arms
    engine civilians.
  - Survivor combat relationship flip-flopped here (Neutral → Respect → Companion) — full saga in
    lessons 27/30/32 and `10-Archive.md`.

## Phase 4 — Factions & Territory ✅ (v1 shipped)

Faction/territory roll-out: survivor outposts, hostile faction turf, player territory capture +
recruiting. Data layer (`FactionDef`, `TerritoryZone`, `FactionEvents`), mirrored relationship
matrix, `FactionSystem` (5 factions, 12 zones, weighted ambient spawns, patrols, defender waves,
capture loop, counter-attacks, recruiting), `FactionAI` + `FactionNPC`, module/menu integration.

### Round 1 — Playtest-fix
- Combat attributes 25/68 (`CA_DISABLE_SECONDARY_TARGET`, `CA_DISABLE_REACT_TO_BUDDY_SHOT`) in
  `MakeSurvivorLike`/`MakeFactionLike`.
- Recruits moved to their own `GTAZRecruits` group (Companion to player, Like to survivors, Hate to
  zombies/factions) — fixes 2+ recruits fighting each other.
- Soft-aim recruiting (H reticule → X recruit); recruiting never requires aiming a weapon.
- Defender respawns on a cooldown while the player stays in a hostile zone.
- Counter-attack waves with kill quota + horde-budget borrow; hordes never stage in owned zones.

### Round 2 — Second playtest
- **Radius rings invisible:** `Blip.Scale` on an `ADD_BLIP_FOR_RADIUS` blip sets the *radius in game
  units*, not a display multiplier; short-range blips never render on the pause map (lesson 29).
- **No territory waves:** fights gated on `HordeOverrideActive` silently disabled all counter-attacks
  whenever a wandering horde was live; un-reset `FightKills` re-armed the next siege instantly.
  `RebalanceForHorde` became **ref-counted**; `EndTerritoryFight` resets `FightKills`; a
  `FightTicks` grace stops the fight declaring itself "repelled" before queued raiders spawn.
- **Survivors still hostile:** scripted `HasBeenDamagedBy(player)` aggro windows misfire on stray
  bullets — deleted; attributes 25/68 + Companion group do the work (lesson 30).
- Soft-aim redesigned to a screen-center reticule + green cylinder marker.

### Round 3 — Third playtest
- **Survivors → Respect (1)** — superseded next round (lesson 30).
- **Faction waves now guaranteed** — timer fallback added alongside the kill quota
  (`TerritoryFightCooldownSeconds`); kills accelerate, the timer guarantees (lesson 31).
- **Corpse-infection mechanic removed** (user request) — `ScanForCorpseInfection`/`ReanimateCorpse`,
  infection events, config, menu sliders, and `PlayerData.Infection` deleted. Do not re-add.
- RecruitKey back to X (user request) with the H soft-aim flow.

### Round 4 — Fourth playtest
- **Survivors → Companion (0)** — Respect still retaliates on direct damage; Companion never does.
  Final relationship state (lessons 30/32).
- **Owned zones bandit-free between attacks** — `TryAmbientSpawn`/`TryFamiliesEncounter` skip owned
  zones; patrol no longer anchors to a held zone (that was "attacked over and over").
- **Attack frequency is a slider** — `TerritoryFightCooldownSeconds` 120→300 as "Territory Attack
  Interval".
- **Territory events are per-area horde mechanics** (user decision): hostile-zone kills fill a
  defense quota → reinforcement waves; owned-zone raids became **timer-only**; hordes never stage or
  march through any territory.
- **Friend-blip-in-bandit fixed** — `ConvertNearbyEngineNpcs` now skips `IsFactionPed()`.

### Round 5 — Fifth playtest
- **Owned-zone raid loop fixed** — the round-4 rewrite dropped the `FightCooldownTicks` gate with
  the kill-quota condition; restored as its own statement, spawn grace raised to 300 ticks
  (lesson 33).
- **Hostile zones now capturable** — escalation tops defenders back up to `OutpostDefenderCount`
  only (never stacks a full wave on top), capped by `TerritoryFightReinforcementWaves` (lesson 33).
- **No more rooftop/inside-roof spawns** — terrain probe switched to
  `GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD` (lesson 33).
- **Survivor-hostility root-caused** — player-facing relationships were bound in the `ModCore`
  constructor, where SHVDN3 can silently drop natives; `EnsurePlayerFacingRelationships()` re-asserts
  them first-tick (lesson 33).

### Round 6 — Sixth playtest (mapping pass)
- **Real territory centers** — all 12 replaced with player-picked in-game coordinates (no flat
  `Z=40` anchor), keyed by resolved game-zone name (lesson 34).
- **All 12 territories mapped with wave spawns** — 4 player-verified points each in `Territories.txt`
  → `GetWaveSpawns`; waves use mapped points (never a safe-position search), excluding the point
  closest to the player.
- **Capture is instant on a full clear** — `ZoneCaptureTime`/`ZoneCaptureRate` hold-timer removed;
  clear + 1s grace → ownership flips; frozen at `MaxPlayerZones` cap.
- **Hostile zones feel held** — ambient occupants of the holding faction in hostile rings; `FactionAI`
  hostile factions home in on the player (territory peds 300m, ambient 60m).
- **Trainer god modes re-asserted every frame** (lesson 35).
- **Menu alignment** — every `NativeMenu` sets `Alignment = Right` (Center throws).
- **Stale-DLL trap** — capture-time slider persisted after source removal; clean rebuild + binary
  grep became the verification standard (lesson 36).

### Round 7 — v1 polish
- **Ambient zombies standing idle** — dead targets dropped back to Wandering; wander task re-issued
  on a 2s cadence (lesson 37).
- **Survivor guards standing around** — guards patrol `TASK_WANDER_IN_AREA` 100m; idle task issued at
  `_mode == None`; stuck-scenario fallback (`Speed < 0.5f` ~1.5s → `ClearAll()` + re-issue)
  (lesson 38).
- **Autosave** — every mod save fires `DO_AUTO_SAVE`; dead weapon fields removed from `PlayerData`
  (lesson 39).
- **Defenders at the zone center** — initial defenders spawn via
  `FindSafePosition(zone.Center, 5f, 40f)`; territory-bound peds cleaned by zone-center distance
  (lesson 40).
- Faction roll-out completed: The Families (formerly Bandits), Military, Altruists (formerly
  Cultists), and The Lost all live with home-faction zone mapping, Lost ambient riders, and the
  Altruist-zombie truce.

## Post-v1
Documentation revamp (this `DOCS/` directory). Next: Phase 5 (Polish & Balance) + a refactoring
plan for the code — see `06-Roadmap.md`.
