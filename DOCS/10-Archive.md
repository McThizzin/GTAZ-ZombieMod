# GTAZ — Archive: failed fixes & removed features

Designs that were tried and **didn't work**, and features that were **removed**. This is the
anti-pattern list: read it before re-adding anything that sounds like one of these. Each entry says
what failed, why, and what replaced it. The verified lessons behind these are in
`08-Lessons-Learned.md` (numbers referenced inline).

---

## Removed features (do not re-add)

### Corpse infection / reanimation (removed Phase 4 round 3, user request)
- **What:** `CLONE_PED` reanimation of killed corpses (`ScanForCorpseInfection`/`ReanimateCorpse`/
  `OnInfectionAttempt`), infection events, `InfectionChance`/`InfectionScanRange`/`EnableInfection`
  config, menu sliders, `PlayerData.Infection`.
- **Why removed:** with ambient + horde spawning solid it only compounded the same model pool into
  clone floods (lesson 31). Do NOT re-add — ambient + horde spawning is the intended zombie flow.
- **Replaced by:** ambient + horde spawning. Lessons 8–10 (player-as-candidate, retry storm,
  zombies reanimating themselves) are kept as a record of the deleted subsystem.

### Capture hold-timer (removed Phase 4 round 6)
- **What:** `ZoneCaptureTime`/`ZoneCaptureRate` hold-to-capture with a progress bar (config prop,
  `TerritoryZone.CaptureProgress`, menu slider, HUD text).
- **Why removed:** a cleared zone at the `MaxPlayerZones` cap stayed contested instead of capturing,
  and the timer was the confusing part. Capture is now **instant on a full clear** (1s grace →
  `CaptureInProgress` latches → ownership flips).
- **Replaced by:** instant-on-clear capture.

### PlayerData weapon fields (removed v1 polish)
- **What:** `CurrentWeaponHash`/`InventoryItems` on `PlayerData`.
- **Why removed:** dead fields never read/written, serialized as zeros. Weapons/money/ammo live in
  the GTA save; every mod save fires `DO_AUTO_SAVE` (lesson 39).

### Global ambient suppression during hordes (`SetAmbientSuppressed`)
- **What:** a global switch that stopped ALL ambient spawns while a horde was live.
- **Why removed:** if you couldn't find the horde the map went empty. It also starved the only
  `SpawnRequestQueue.ProcessQueue` caller, so horde requests expired on the 8s TTL.
- **Replaced by:** throttling inside `TryAmbientSpawn` (distance + target count) + `HordeSystem`
  calling `ProcessQueue()` itself while a horde is live.

### Aggro-window / IsInCombat guard hostility fixes (removed Phase 4 rounds 2–4)
- **What:** scripted ~4s `HasBeenDamagedBy(player)` aggro windows, then `Ped.IsInCombat` guards.
- **Why removed:** stray bullets misfire the aggro window; the `IsInCombat` guard preserved hostile
  flips forever. Neither could distinguish a direct shot from a stray (lesson 30).
- **Replaced by:** relationship groups (Companion 0) + combat attributes 25/68.

### Zombies fleeing while on fire (scripted flee removed post-v1, playtest)
- **What:** a `Ped.IsOnFire` branch at the top of `ZombieAI.Update` that ran
  `Ped.Task.FleeFrom(player, 80f, -1)` (`_fireFleeing` flag, short-circuit above the state machine,
  `ClearAllImmediately` on fire-out).
- **Why removed:** read as buggy scattering — burning zombies abandoning the attack looked broken,
  not like crowd control. A molotov's value is the burn damage, not panic.
- **Replaced by:** nothing scripted. Burning zombies keep the normal Wandering/Chasing/attack
  behavior — the *engine's* ignited-panic task is suppressed with config flag **430**
  `CPED_CONFIG_FLAG_IgnoreBeingOnFire` in `MakeZombieLike` (flag 118 alone doesn't stop it, lesson 56).
  Because 430 also suppresses the engine fire's damage, `ZombieAI.Update` re-applies scripted burn
  damage on a ~0.5s cadence so flaming zombies still burn to death (lesson 57).

---

## Failed approaches (what didn't work, and the pattern to avoid)

### Player-facing relationships: Neutral → Respect → Companion
- Neutral (3) — ped treats a clipped player as a legitimate combat target mid-fight (lessons 27/30).
- Respect (1) — still retaliates on **direct damage**; the "hostile until they join" bug
  (lessons 30/32). The doc tables say 1 = Respect, but the native combat AI still fights back.
- **Companion (0) — the only relationship where the native AI never targets the player, even under
  direct damage.** Use Companion for friendlies AND recruited group members (`GTAZRecruits` group so
  recruits don't fight each other).

### Combat attributes 25/68 alone (as a "fix" for Neutral peds)
- Only gate retargeting/buddy-shot for **companions** — a Neutral survivor still flips hostile.
  Works only in combination with Companion (0).

### Recruits in the shared survivor relationship group
- Two recruits at Neutral to each other fight each other. Fix: their own `GTAZRecruits` group.

### Kill-quota-only owned-zone raids
- Kills near an **owned** zone almost never happen, so the quota was met roughly never and raids
  never fired — the code looked broken (lesson 31). Rule: quota + **timer fallback** (timer
  guarantees, counter accelerates); owned-zone raids are timer-only today, hostile-zone defense
  waves are quota-driven.

### Removing the `FightCooldownTicks` gate along with the quota condition
- Round 4 made raids timer-only by editing `if (gate && quota)` to `if (quota)`, silently deleting
  the gate → `StartTerritoryFight` ran every tick → endless start/end log pairs (lesson 33). Rule:
  keep gates as their own statements.

### Reinforcement waves stacked on top of base defenders
- A full `TerritoryFightWaveSize` on top of `OutpostDefenderCount` made a zone over-stock and never
  drain → uncapturable (lesson 33). Rule: escalation tops up to a fixed cap only, bounded per visit.

### Initial defenders at far mapped wave points
- Up to ~400m from the fight; killed without registering a kill, so zones "fell" with no waves
  (lesson 40). Initial defenders spawn at the **zone center** now; mapped points are for
  reinforcement/defense/raid waves.

### 3D ped-vs-player cleanup for territory-bound peds
- Evicted far-side defenders mid-fight without a kill. Territory-bound peds are cleaned by
  **zone-center distance**, X/Y only (lessons 34/40).

### Patrol anchored to the held zone's center
- The "bandits attack my territory over and over" complaint — a new raiding party every ~2 min.
  Patrols never anchor to a held zone now (lesson 32).

### `ConvertNearbyEngineNpcs` not skipping faction peds
- Double-wrapped bandits as survivors → green Friend blip stacked on the orange Enemy blip. Now
  skips `IsFactionPed()` (lesson 32).

### Ambient spawners landing inside owned zones
- Ambients and patrols now refuse `IsPlayerOwnedZone` positions; territory ground is event-driven
  (lesson 32).

### Safe-position search from flat placeholder Zs / circular rooftop probe
- `Z=40` anchors + searching from the anchor Z broke spawns on hillsides; probing
  `GET_GROUND_Z_FOR_3D_COORD` returned the roof surface (circular) so rooftops were "safe"
  (lessons 33/34). See `07-Scripting-Rules.md` rules 6–7.

### Binding player-facing relationships in the `ModCore` constructor
- SHVDN3 can run constructors before the game is ready; the natives are **silently dropped**, so
  survivors stayed effectively Neutral ("green-blip survivors shoot me") with the group hash looking
  correct. `EnsurePlayerFacingRelationships()` re-asserts first-tick (lesson 33).

### One-shot god-mode toggles
- The game clears invincibility/proof flags on vehicle change/teleport/entity re-creation. Any god
  toggle must re-assert every frame while enabled (lesson 35).

### Corpses in `CLEAR_AREA_OF_PEDS` / `World.GetGroundHeight()` spawns / per-tick task reissue
- The classic three (lessons 4/5/6): `CLEAR_AREA_OF_PEDS` deletes zombies; `GetGroundHeight` places
  peds mid-air; per-tick task reissue stutters peds into doing nothing. All banned — see rules 6/9/11.

### Stale deployed DLL after feature removal
- Incremental builds skipped recompiling `Config.cs`/`MenuManager.cs`, so removed features stayed in
  the running mod. Clean rebuild + binary grep is the verification standard (lesson 36).
