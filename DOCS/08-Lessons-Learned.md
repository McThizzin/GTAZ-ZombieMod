# GTAZ — Lessons Learned (1–40)

Hard-won, verified lessons from all phases. **The numbers are stable — code comments reference them
(lesson 34, `TECHNICAL_LESSONS #33`).** Never renumber; append new lessons at the end. The rules
distilled from these live in `07-Scripting-Rules.md`; failed/removed designs live in `10-Archive.md`.

---

## Animations, clipsets & pose

### 1. PED_FLAG_FREEZE (292) causes instant T-pose
**Symptom:** Zombies spawn in T-pose, never animate.
**Root cause:** `SET_PED_CONFIG_FLAG(ped, 292, true)` freezes the ped's skeleton at spawn — locked
in raw bind pose forever.
**Fix:** Don't set flag 292 as a freeze. (Flag 292 is `DisableAmbientSpeech` — lesson 20.)
**Lesson:** Config flag numbers are opaque. When something looks like an anim problem, check
`SET_PED_CONFIG_FLAG` calls before touching clipsets.

### 2. Clipset skeleton compatibility
**Symptom:** Clipset loads (`HAS_CLIP_SET_LOADED` true) but ped still T-poses.
**Root cause:** `HAS_CLIP_SET_LOADED` confirms the data is in memory, not that it's rigged for the
ped's skeleton. The DLC zombie clipset targets Rockstar's dedicated zombie ped model, which has a
different skeleton than generic civilians.
**Fix:** Use `move_m@drunk@verydrunk` (or another vanilla humanoid clipset).
**Lesson:** A loaded clipset is not a compatible clipset. Test fancier clipsets in-game on the
actual target models first.

### 3. Clipsets must be loaded before applying
**Symptom:** T-pose on spawn even with a correct clipset name.
**Root cause:** Applying an unloaded clipset silently does nothing.
**Fix:** `REQUEST_CLIP_SET` + wait for `HAS_CLIP_SET_LOADED` **before** `SET_PED_MOVEMENT_CLIPSET`.
Load once at startup (first tick, not constructor). **Critical:** `REQUEST_CLIP_SET` must be called
**every frame** inside the wait loop — request-once then poll often never completes. Fallback to
`move_m@drunk@verydrunk` if the DLC clipset fails.

### 11. Attack animation not playing
**Symptom:** Zombies deal damage but no visible swing.
**Root cause:** `TASK_PLAY_ANIM` used `melee@unarmed@streamed_core` but the dict was never
`REQUEST_ANIM_DICT`'d anywhere — silent failure, damage still applies.
**Fix:** Load the dict at startup and gate `TASK_PLAY_ANIM` on `HAS_ANIM_DICT_LOADED`.

### 12. Facial animation name mismatch
**Symptom:** `SET_FACIAL_IDLE_ANIM_OVERRIDE` silently does nothing.
**Root cause:** `"mood_pain_1"` isn't a real facial mood. Valid names:
`mood_injured_1`, `mood_angry_1`, `mood_drunk_1`, `mood_happy_1`, `mood_sad_1`, `mood_scared_1`.
**Fix:** Use `"mood_injured_1"`.

### 14. Death rocket (custom clipset physics freakout)
**Symptom:** Zombies launch into the air / ragdoll violently on death.
**Root cause:** Pre-death clipset reset was at `Health <= 0` — too late (death physics already fired).
**Fix:** Reset clipsets to `move_m@generic` and disable ragdoll when `Health <= 30`.
**Note:** For one-shot kills (>30 damage) the rocket can still rarely occur.

### When something looks like an anim problem
Check in this order: ① `SET_PED_CONFIG_FLAG` calls (especially 292) ② clipsets actually loaded
before applying ③ clipset compatible with the ped skeleton ④ spawn position valid (not mid-air or
embedded) ⑤ tasks reissued too frequently. Don't assume it's the clipset name.

---

## Spawning & placement

### 4. GET_SAFE_COORD_FOR_PED vs. World.GetGroundHeight
**Symptom:** Zombies spawn mid-air, embedded, or T-pose.
**Root cause:** `World.GetGroundHeight()` samples collision directly under the point; at 50–200m
from the player that collision is frequently not streamed. On failure it silently falls back to the
player's Z at a random X/Y — mid-air or embedded peds, plus freefall/penetration can lose the
animation task → T-pose.
**Fix:** `GET_SAFE_COORD_FOR_PED` (walks the navmesh for a real walkable spot) with flags
`GSC_FLAG_NOT_ISOLATED | GSC_FLAG_NOT_INTERIOR | GSC_FLAG_NOT_WATER`. It returns failure cleanly
instead of a bogus position.

### 6. CLEAR_AREA_OF_PEDS deletes zombies too
**Symptom:** Zombies disappear immediately after spawning.
**Root cause:** `CLEAR_AREA_OF_PEDS` deletes ALL peds in radius, including zombies.
**Fix:** Iterate `World.GetNearbyPeds`/`GetNearbyVehicles` and `MarkAsNoLongerNeeded()` non-zombies
only.

### 34. Flat-anchor Z and 3D range checks: territory centers hardcoded to Z=40 broke every spawn and every range gate
**Symptom:** (a) `"Defender wave FAILED to spawn ... no safe positions"` for almost every hostile
territory (low-elevation zones worked); (b) the owned-zone attack/repel loop persisted because zero
raiders could materialize; (c) hostile-zone defense quota logged `1/4` on every kill near a zone
instead of accumulating; (d) `BuildZones` comment labels didn't match the resolved game zone.
**Root cause (a):** every center was hardcoded to `Z=40`; `GET_SAFE_COORD_FOR_PED` flood-fills the
navmesh from the given Z, so a candidate hundreds of meters below real ground has no navmesh → all
attempts fail. **Fix:** `FindSafePosition` resolves the terrain height at each candidate first
(`GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD`, fallback `GET_GROUND_Z_FOR_3D_COORD`) and starts the
search at `terrainZ + 1` (or `around.Z + 100` if unstreamed, descending onto the navmesh).
**Root cause (b):** the loop was a symptom of (a) — with `0 queued` raiders granted a spawn, the
fight correctly ends the instant the grace expires. **Lesson:** an event loop that "starts and ends
3s later with no actors" is usually a spawn failure in disguise — fix the spawn path before adding
end-condition guards.
**Root cause (c):** "inside the territory" gated on the game-zone hash while the player fights
inside the `TerritoryRadius` ring; the ring-outside-zone branch zeroed `FightKills` every tick.
**Fix:** `playerInside` is ring-based (`HorizontalDistance(center, pos) <= TerritoryRadius`), and
`IsPlayerOwnedZone` is ring-based too. **Lesson:** use the thing the player can see (the ring) as
the source of truth.
**Second lesson (Z vs distance):** with a flat Z anchor, **every 3D `Length()` against
`zone.Center` is wrong** — hillsides inflate distances by hundreds of meters. All territory range
checks use `SpawnUtils.HorizontalDistance` (X/Y only).
**Third lesson (mapping pass):** with real centers in place, wave spawns now come from **player-mapped
points** (`Territories.txt` ↔ `FactionSystem.GetWaveSpawns`; 12 territories × 4 points). `GetWaveSpawn`
picks a random mapped point (excluding the one closest to the player) instead of a safe-position
search that can fail. **Lesson:** once you've verified safe spots by hand, use them directly — a
spawn path with zero failure modes beats one you have to keep patching.

### 40. Hostile-zone defenders: spawn at the zone center, distance-clean territory-bound peds by the zone
**Symptom:** hostile zones captured after a couple of kills ("2 groups of 10 spawned, 3 kills,
captured") with no reinforcement waves.
**Root cause:** two bugs sharing one mechanism (far defenders cleaned without registering a kill):
① initial defenders spawned at far mapped points (up to ~400m out) outside the player's fight, so
`FightKills` never met the quota, and the 3D distance check cleaned them at range without a kill;
② the cleanup check was 3D against the player, not the zone.
**Fix:** `SpawnDefenders` uses `FindSafePosition(zone.Center, 5f, 40f)` (same center-anchored search
as survivor guards) with a mapped-point fallback; territory-bound peds are cleaned by
**zone-center distance** (`HorizontalDistance(zone.Center, player) > TerritoryRadius + FactionDespawnDistance`,
X/Y only).
**Lesson:** an event's peds must be reachable from the event's ground and cleaned relative to the
event's ground — otherwise the kill-quota/wave economy silently short-circuits.

---

## AI tasks

### 5. Task reissue pattern
**Symptom:** Zombies never wander — idle in place or stutter when chasing.
**Root cause:** Reissuing `TASK_WANDER_IN_AREA` / `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` every tick
interrupts the ped mid-action.
**Fix:** Only reissue when the task slot is free: `GET_SCRIPT_TASK_STATUS(ped, task) != 1`.
Applies to wander, follow-to-entity, go-to-entity, etc.

### 37. Zombies standing idle: a dead target never got cleared, and a cleared wander task was never re-issued
**Symptom:** lots of ambient zombies just stand idle — don't wander, don't chase. They re-aggro fine
when the *player* kills a zombie's NPC target, but after the zombie kills one itself it stands there.
**Root cause:** two independent bugs. ① **Dead-target stick:** `UpdateTarget` only *replaced* a dead
target — when nothing else was in range it kept the corpse as `Target`, so the state machine stayed
stuck in Attacking/Chasing against a dead ped. ② **Dead wander task:** `TASK_WANDER_IN_AREA` was
issued once per state entry; the task self-sustains while running, but the engine can clear it, and
the reissue gate (`justEnteredState`) never fires again.
**Fix:** ① drop `Target = null` and fall back to `Wandering` when the target is dead/missing and
nothing new is found; ② re-issue the wander task on a cadence (`_wanderReissueTimer`, every 120
ticks / ~2s). A wander re-issue just re-picks a random point — it does NOT cause the every-tick
restart-stutter of lesson 5.
**Lesson:** "self-sustaining" script tasks are only self-sustaining while the engine leaves them
alone. Long-running movement tasks need a stuck fallback or periodic re-issue, and a state machine
must have a defined exit back to idle when the reason it left idle is gone.

### 38. Survivor guards frozen by ambient scenarios — with BlockPermanentEvents=false, interrupted tasks need a recovery path
**Symptom:** at an owned territory only a few survivor guards walked; the rest stood forever.
**Root cause:** two compounding gaps in `FactionAI`. ① Survivors run with `BlockPermanentEvents=false`
(so they take damage/die for loot), letting the engine interrupt their patrol with an ambient
scenario; `TASK_GUARD_CURRENT_POSITION` (the old guard idle) is a single static task that never came
back. ② `FactionAI.Update()` only applied the idle task on **combat exit** — a fresh guard with no
enemy had `_mode == None` and no task at all, so the engine drove it as a plain ambient ped.
**Fix:** issue the idle task whenever `_mode == Mode.None` (covers spawn and role-changes); switch
guards from `TASK_GUARD_CURRENT_POSITION` to `TASK_WANDER_IN_AREA` (100m around the post); add a
stuck-scenario fallback — if a guard/patrol isn't in combat/vehicle and `Speed < 0.5f` for ~1.5s
(`_stuckTicks > 90`), `ClearAll()` + re-issue the idle task.
**Lesson:** whenever a ped class deliberately disables `BlockPermanentEvents`, the engine will
interrupt your scripted tasks — those interrupts need a recovery path, and the "only issue on state
transition" pattern fails for peds whose state never transitions because the task got hijacked.

---

## Budget

### 7. Budget leaks without PeriodicRescore()
**Symptom:** Spawns stop after a while even though zombies are dying. ~120 zombies ever, then nothing.
**Root cause:** `PeriodicRescore()` was never called; dead peds leaked their slots until the budget
was exhausted.
**Fix:** Call `PedBudgetManager.PeriodicRescore()` every tick in `ZombieSystem.OnTick()`.
**Lesson:** Dead peds don't automatically free their budget — you have to rescore.

---

## Peds & config flags

### 16. Hash casting for uint values
Some `PedHash` values are `uint` and exceed `int.MaxValue`; natives expect `int`.
Use `unchecked((int)PedHash.X)` (e.g. `Zombie01`).

### 19. Money rain on zombie death
Zombies spawn with default ped money → `SET_PED_MONEY(ped, 0)` in `MakeZombieLike()`.

### 20. Disable ambient speech for zombies
Set config flag 292 (`DisableAmbientSpeech`) in `MakeZombieLike()` + `STOP_PED_SPEAKING` every
frame. **Note:** 292 is `DisableAmbientSpeech`, not `PED_FLAG_FREEZE` (confusingly also 292 in some
documentation) — test carefully (see lesson 1).

### 24. Zombies must be blocked from ladders twice
**Symptom:** zombies climb ladders and tower over players on rooftops.
**Root cause:** two independent paths onto a ladder — the pathfinder can *route* onto it and the ped
can *grab* it directly; blocking only one leaves the other open.
**Fix:** both in `MakeZombieLike()`: config flag 146 = `CPED_CONFIG_FLAG_DisableLadderClimbing`, and
`SET_PED_PATH_CAN_USE_LADDERS(ped, false)` (exists in this SHVDN3's `Hash` enum — reflect before
assuming a manual hash is needed, lesson 23).

---

## SHVDN3 API & compile-time

### 15. Ped equality in SHVDN
SHVDN's `Ped`/`Entity` override `==`/`Equals` to compare by native handle, not object reference —
`ped == Game.Player.Character` works correctly. Trust it.

### 21. PostTicker requires the isImportant argument
`CS7036`: in this SHVDN3 version `PostTicker(string, bool isImportant, bool cacheMessage = true)` —
`isImportant` has no default. The reference trainer mods call `Notification.Show(...)`, which
doesn't even exist. **Fix:** `PostTicker("text", false)`.
**Lesson:** don't trust sample-mod APIs — check the real DLL (lesson 23).

### 22. GTA.Chrono.GameClock API
`CS0103: 'Chrono' does not exist`; `GameClockDate` is NOT `DateTime` and has no time members.
There is no `GTA.Chrono.Chrono` static class — it's `GTA.Chrono.GameClock`. Freeze: `GameClock.IsPaused`;
set: `GameClock.Hour/Minute/Second` (all writable).

### 23. Inspect the SHVDN3 API by reflection (no XML docs)
No documentation ships with the DLL. Reflect over `ScriptHookVDotNet3.dll` in the GTA V folder:
- `GetTypes()` throws `ReflectionTypeLoadException` (SHVDN3 depends on native ScriptHookV) — catch
  it and read `$_.Exception.Types`.
- `Join-String` is PowerShell 7-only; on 5.1 use a `ForEach-Object` loop.
- `scripts/LemonUI.SHVDN3.xml` documents LemonUI, not SHVDN — don't look for game APIs there.
**Lesson:** the installed DLL is the source of truth, not sample mods and not online docs.

### 26. Verify enum member names + native hashes before using them (Phase 4 build fixes)
**Symptom:** first Phase 4 compile failed with a wall of `CS0117`/`CS0103`/`CS1061`:
`PedHash.MexThug01AMM` doesn't exist, `WeaponHash.SawedOffShotgun` doesn't exist,
`Hash.RELATIONSHIP_GET_BETWEEN_GROUPS` doesn't exist, and `FactionSystem` couldn't see
`Function`/`Hash` at all.
**Fixes (verified by reflection):**
- `MexThug01AMM`→`MexThug01AMY`, `Chip01AMY`→`Chip`, `Family01GMY`→`Famfor01GMY`,
  `Vagos01GMY`→`MexGang01GMY`, `Soldier01`→`Blackops01SMY`, `Acult02AMM`→`Acult02AMO`,
  `Scientist01AMM`→`Scientist01SMM`.
- The shotgun is `WeaponHash.SawnOffShotgun` and `PickupType.WeaponSawnoffShotgun` (no "Sawed").
- There is **no** `RELATIONSHIP_GET_BETWEEN_GROUPS` native in the `Hash` enum. Use
  `ped.RelationshipGroup.GetRelationshipBetweenGroups(other.RelationshipGroup) == Relationship.Hate`.
- `RaycastResult` property is `DidHit`; `is not Ped target` inside an `||` chain doesn't guarantee
  definite assignment — split the check or use `var target = hit.HitEntity as Ped`.
- Missing `using GTA.Native;` and `using System.Linq;` are silent until needed.
**Lesson:** every enum member/native against a third-party assembly is unverified until it compiles;
dump the enum before guessing.

### 28. ADD_BLIP_FOR_RADIUS is `World.CreateBlip(pos, radius)` in installed SHVDN3
`Blip.Create(Vector3, float)` (modern upstream) doesn't exist. `World.CreateBlip(Vector3)` and
`World.CreateBlip(Vector3, float)` exist and aren't `[Obsolete]`; the float overload maps to
`ADD_BLIP_FOR_RADIUS`. A radius blip has no sprite — only `Color`/`SecondaryColor`/`Alpha` apply;
`IsHiddenOnLegend` keeps it out of the legend; the radius is fixed at creation (recreate to change it).

### 29. `SET_BLIP_SCALE` sets the *radius* on an `ADD_BLIP_FOR_RADIUS` blip — and `IsShortRange` hides it from the pause map
**Symptom:** territory radius rings configured and enabled, nothing visible on either map.
**Root cause:** `blip.Scale = 1.2f` on a radius blip replaces the ring radius in game units (1.2m —
invisible); `IsShortRange = true` hides radius blips from the pause map entirely.
**Fix:** never set `Scale` on a radius blip (radius comes solely from `CreateBlip(pos, radius)`);
drop `IsShortRange`; raise `Alpha` to ~160; keep `IsHiddenOnLegend` (hides the legend row, not the
ring).

---

## Serialization & deployment

### 25. ScriptSettings.GetValue<T> breaks when T isn't the property's real type
**Symptom:** config loads as garbage, non-string properties throw, everything silently falls back to
defaults after a "working" save.
**Root cause:** `ScriptSettings.GetValue<T>` only casts correctly when `T` is the concrete type;
passing an `object` default returned every value as a string, and casting strings to
`int`/`float`/`bool`/`Keys` failed.
**Fix:** skip `GetValue<T>` — pull the raw string and parse by the property's own `PropertyType`
(Vector3 → `ParseVector3`, enums → `Enum.Parse`, else `Convert.ChangeType` with invariant culture).
**Lesson:** treat `ScriptSettings` as a string bag; serialize with invariant culture and parse back
by real type, or version bumps silently wipe user settings.

### 36. Removed features persist in the running mod = stale deployed DLL
**Symptom:** a slider (e.g. "Zone Capture Time") still appears in the menu after its property, code
path, and menu item were all deleted from source.
**Root cause:** incremental builds can skip recompiling a file the tooling considers unchanged, so
the deployed `ZombieMod.dll` still contained the old `Config.cs`/`MenuManager.cs`. Grepping the
**source** proves nothing about what's deployed.
**Fix:** verify the deployed binary — `rg "ZoneCaptureTime" ZombieMod.dll` (no hits = gone); if
stale, clean rebuild: `Remove-Item -Recurse -Force .\GTAZ\obj, .\GTAZ\bin` + `dotnet build`.
**Lesson:** when "I removed it but it's still there," the question is what's in the deployed DLL,
not the source.

---

## Relationships & combat

### 27. Neutral peds flip hostile mid-combat — combat attributes 25 & 68
**Symptom:** survivors (neutral to the player) turn hostile when the player shoots the zombie
they're fighting.
**Root cause:** in an active combat task the AI re-evaluates targets and retargets onto *any* non-
companion ped in range — including a neutral player shooting the same zombie; and a stray-bullet
clip provokes retaliation not gated by the neutral relationship. Neither shows in the relationship
matrix (both still `Neutral`).
**Fix:** `SET_PED_COMBAT_ATTRIBUTES`: 25 = `CA_DISABLE_SECONDARY_TARGET`, 68 =
`CA_DISABLE_REACT_TO_BUDDY_SHOT`. Applied in `MakeSurvivorLike`/`MakeFactionLike`. Direct damage
still provokes normal fight-back.
**Lesson:** "relationship says Neutral" isn't enough — combat attributes gate mid-combat retargeting
and friendly-fire retaliation.
**Related:** two recruits set to *Neutral* with each other will fight each other — allies need
`Companion (0)`, not Neutral. That's why recruits get their own `GTAZRecruits` group.

### 30. Scripted `HasBeenDamagedBy(player)` aggro windows misfire on stray bullets — let combat attributes + relationship groups do the work
**Symptom:** after the lesson-27 fix, neutral survivors *still* turned hostile "when attacking
anything," and even recruited survivors eventually turned on the player.
**Root cause:** the ~4s scripted aggro window gated on `Ped.HasBeenDamagedBy(player)` can't
distinguish a direct shot from a stray during a shared zombie fight — the moment any bullet clips a
neutral ped it flipped hostile, and recruits (Companion) went hostile the same way.
**Fix (round 2):** delete the scripted aggro windows entirely; attributes 25/68 + the `GTAZRecruits`
Companion group gate the two real triggers.
**Superseded in round 3 — the only fix that held is relationship-driven:** attributes 25/68 only gate
retargeting/buddy-shot for companions, so a **Neutral** survivor still treats a clipped player as a
legitimate target, and the `Ped.IsInCombat` guard just preserved hostile flips forever. The scripted
`IsInCombat` guards were deleted; survivors became **Respect (1)** vs the player.
**Superseded again in round 4 — Respect still retaliates on direct damage:** a Respect ped in the
native combat AI still fights back when the player damages it. **Companion (0)** is the only
relationship where the native AI *never* targets the player, even under direct damage.
`SetupSurvivorRelationships` now sets Companion (0) both directions and `PlayerRelation.Ally → Companion`.
**Related (budget):** `RebalanceForHorde` must be **ref-counted** when a second consumer (territory
fights) borrows the reserved minimums alongside hordes — a `HordeOverrideActive` gate silently
disabled all counter-attacks whenever a wandering horde was live, and an un-reset `FightKills` quota
re-armed the next siege the instant the cooldown expired. One ref per holder; restore only when refs
hit 0; clamp on over-release.
**Lesson:** "direct damage provokes fight-back" is impossible to attribute reliably with
`HasBeenDamagedBy` once stray bullets exist. If you want peds that never fight the player, change
the relationship itself — **Companion (0), not Respect (1)**.

### 32. "Always hostile" is a relationship bug, and ambient spawners must respect safe zones
**Symptom (fourth playtest):** (a) survivors hostile "until they join/follow"; (b) bandits in the
player's own territory "attacked over and over"; (c) after killing many bandits in enemy territory,
"no player attack initiated and no waves spawning, no notifications or logs"; (d) some bandit blips
had "a friend blip inside them."
**Root cause (a):** Respect (1) isn't safe — a Respect ped the player damages still treats the player
as a combat target. Only Companion (0) never targets the player. (See lessons 27/30.)
**Root cause (b):** ambient bandit spawners ignored owned zones — `TryAmbientSpawn` landed inside
owned territory and `TryFamiliesEncounter` (the former `TryBanditPatrol`) deliberately **anchored
the patrol to the held zone's center**,
so a fresh raiding party every ~2 min plus ambient drops = "attacked over and over." **Fix:** both
spawners refuse hostile factions inside `IsPlayerOwnedZone`, and the patrol no longer anchors to held
zones — owned territory is bandit-free except scripted counter-attack waves (frequency = the
"Territory Attack Interval" slider).
**Root cause (c):** the fight/attack events posted **no notification and no log line** in the enemy-
territory case; kills near owned zones were the only counter and basically never happened. **Fix
(round-4 redesign):** hostile-zone kills fill that zone's defense quota and spawn reinforcement waves
(red ticker + log); owned-zone raids dropped kill acceleration and are timer-only; `StartTerritoryFight`/
`EndTerritoryFight`/`SpawnDefenders` all log.
**Root cause (d):** `ConvertNearbyEngineNpcs` only excluded zombies and existing survivors, so it
double-wrapped faction bandits (two wrappers, two stacked blips). **Fix:** skip `ped.IsFactionPed()`.
**Lesson:** ① the native relationship ladder isn't "lower = safer" — test actual combat behavior;
Companion (0) is the only guaranteed-never-hostile group. ② Ambient/population spawners must check
safe zones — a spawner that deliberately targets a safe area is a bug; scripted events are how to
attack the player's turf. ③ Any event the user should notice needs visible feedback (notification +
log); an event with no log is indistinguishable from an event that never fires.

### 33. Fifth playtest: removed timer gates, stacking reinforcement waves, circular rooftop checks, and constructor-time relationship drops
**Symptom (fifth playtest):** (a) endless `"Territory fight started at ..."` → `"ended at ..."`
pairs 1–3s apart; (b) "waves of reinforcements show up but I never managed to take a territory even
after multiple reinforcements"; (c) "some bandits spawning on roofs, some inside roofs"; (d)
"survivors still hostile" despite the Companion build.
**Root cause (a):** the round-4 rewrite removed the kill-quota condition *and* the
`FightCooldownTicks` gate together — `StartTerritoryFight` then fired every tick near an owned zone,
and the fight died/restarted on the spawn grace. **Fix:** keep a pure timer gate
(`if (zone.FightCooldownTicks > 0) continue;`) in front of a timer-only trigger, and raise the spawn
grace to 300 ticks so a raid isn't declared "repelled" before its queued raiders materialize.
**Lesson:** removing one condition of `if (A && B)` — where A was the gate — silently deletes the
gate too; re-add it as its own statement when a trigger goes timer-only.
**Root cause (b):** reinforcement waves stacked a full `TerritoryFightWaveSize` on top of the base
defenders, so a zone over-stocked and could never be emptied (defenders ≥ quota ⇒ capture impossible).
**Fix:** escalation tops defenders back up to `OutpostDefenderCount` only, skips while a wave is
pending, and is capped per visit by `TerritoryFightReinforcementWaves` (reset on leaving).
**Lesson:** escalation systems must be provably drainable — if each reinforcement round resupplies
more than the kills that trigger it, the state machine can't reach its end state.
**Root cause (c):** the rooftop rejection compared the navmesh candidate Z against
`GET_GROUND_Z_FOR_3D_COORD` directly below it — on a rooftop that probe returns the roof surface
itself, so the check was circular, and on a failed probe it fell back to the raw navmesh point.
**Fix:** probe `GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD` (terrain with buildings excluded) as the
primary gate; fall back only when unstreamed. **Lesson:** `GET_GROUND_Z_FOR_3D_COORD` is a
collision-surface probe, not a terrain probe — use the excluding-objects variant to compare against
actual terrain.
**Root cause (d):** player-facing relationships were bound **inside the `ModCore` constructor**.
SHVDN3 can run script constructors before the game is fully ready, and relationship natives issued
that early are **silently dropped** — the real `PLAYER` group kept its defaults, so survivors stayed
effectively Neutral (armed peds engage the player mid-fight) and bandits only fought on retaliation.
**Fix:** `EnsurePlayerFacingRelationships()` re-asserts the stances against the live player group as
a one-shot on the first tick; it's idempotent when the ctor bind stuck, and the diagnostic logs the
ctor vs first-tick group hashes so `GTAZ.log` proves which case you're in.
**Lesson:** five things — ① when a feature goes timer-only, keep the timer gate as its own
statement; ② any spawn/upgrade loop that can grow unbounded is a state machine that never terminates
— bound it; ③ "surface below me" checks are useless for detecting rooftops — probe terrain excluding
objects; ④ never read `Game.Player`-anything in a script constructor — early natives are silently
dropped (same trap as lesson 13, but silent instead of throwing); defer player-dependent setup to the
first tick; ⑤ when the code provably implements the designed behavior but the report persists, add a
runtime diagnostic that prints the actual game-state rather than guessing.

---

## Territory & fight mechanics

### 31. Kill-quota-only triggers fail in practice — pair a quota with a timer fallback; remove dead mechanics instead of porting them
**Symptom (third playtest):** territory counter-attack waves never spawned even after the player
"killed more than 4 faction members," and the corpse-infection mechanic was deemed unwanted.
**Root cause — waves:** `UpdateTerritoryFights` only started a fight at `FightKills >=
TerritoryFightKillQuota`, and kills only counted within `TerritoryRadius` of an **owned** zone —
which almost never happens (bandits fight you at *their* zones). The quota was met roughly never.
Any end-user-visible event that depends on a condition the user can't reliably satisfy is a bug,
not tuning.
**Fix — waves:** fires when the player is near an owned zone if the quota is hit (immediate) **or**
when `FightCooldownTicks` expires (`TerritoryFightCooldownSeconds`; raised 60→120→**300** so quota +
timer don't double-fire; round 4 reworked it to timer-only). Kills accelerate, the timer guarantees.
`RegisterBanditKillNearOwnedZone` logs each kill + quota for traceability.
**Root cause — infection:** the corpse-infection subsystem (`ScanForCorpseInfection`/
`ReanimateCorpse`/`OnInfectionAttempt` + events + config + menu + `PlayerData.Infection` +
`UIUtils.InfectionColor`) dated from when ambient spawns were thin; it only compounded clone floods.
**Fix — infection:** delete the subsystem outright (user request); grep for
`Infection|Reanimate|_infectedPeds` across `GTAZ/` now returns nothing.
**Lesson:** for anything that should happen "soon-ish," a quota/counter alone is insufficient — add
a time-based fallback so the event is *guaranteed* and the counter merely accelerates it. And when a
mechanic is no longer wanted, delete every surface of it (config, events, UI, save data) in one
pass, then grep to prove it's gone.

### 33. (also covers territory fight gates / reinforcement stacking / rooftop spawns) — see "Relationships & combat" above.

---

## Persistence

### 39. The mod save can't persist weapons — fire the game's autosave too, and don't carry dead fields
**Symptom:** autosave restored health/armor/territories but not weapons/ammo/money.
**Root cause:** the mod save only carries what `PlayerData` holds (position/heading/health/armor/
owned territories). Weapons/ammo/money/purchases live in the GTA save. `PlayerData` also had
`CurrentWeaponHash`/`InventoryItems` — dead fields that serialized as zeros.
**Fix:** fire the game's own autosave alongside every mod save — `Function.Call(Hash.DO_AUTO_SAVE)`
right after the mod state is written, wrapped in try/catch (`PersistenceManager.Save`). Delete the
dead weapon fields so there's exactly one owner of weapon state (the game save).
**Lesson:** a save system that only writes part of the game state silently loses the rest. Decide
per-subsystem who owns each piece; when the game already owns it, trigger the game's save and don't
mirror fields you never restore.

---

## Trainer

### 35. Trainer god modes silently switch off — the game clears invincibility/proof flags on its own
**Symptom:** God Mode / Vehicle God Mode stop working mid-session while their checkboxes stay
checked; only unchecking/rechecking fixes it.
**Root cause:** the toggles wrote the state once (`SET_PLAYER_INVINCIBLE` + proof flags) and never
touched it again. The game engine resets these on its own — vehicle change, teleport, streaming, and
entity re-creation all clear invincibility/proof state. There is no persistent "god" bit.
**Fix:** re-assert every frame while the toggle is on. `PlayerTrainer.GodModeOn` + `ApplyGodMode()`
run from `PlayerTrainer.Tick()` (via `TrainerManager.Tick()`); `VehicleTrainer.VehicleGodModeOn` +
`ApplyVehicleGodMode()` re-apply to the current vehicle every frame (armed while out of a vehicle).
Calls are idempotent and cheap.
**Lesson:** a checkbox that "randomly stops working" while checked means the **game** cleared the
flag, not the menu. Per-frame re-assert while enabled is the only reliable pattern for
invincibility/proof.

---

## Superseded lessons (record only — subsystem deleted)

### 8. Player as infection candidate — *superseded*
The whole corpse-infection/reanimation mechanic was removed in Phase 4 round 3 (ambient spawning is
enough). Lessons 8–10 record bugs in a deleted subsystem (`ZombieSystem.ScanForCorpseInfection`/
`ReanimateCorpse` no longer exist). The bugs: the player's own dead ped was a valid infection
candidate (exclude `Game.Player.Character`), and `OnInfectionAttempt` fired off any attack regardless
of the killing blow.

### 9. Infection retry storm — *superseded*
`ScanForCorpseInfection` ran every tick and skipped without marking a corpse checked, so the same
corpse was re-rolled ~60×/sec until the roll succeeded — clones rained. Fix (record only): one roll
per corpse via a processed set.

### 10. Zombies reanimating themselves — *superseded*
The candidate filter (`IsDead && distance <= range`) included already-zombie corpses, so zombies
cloned themselves back via `CLONE_PED`. Fix (record only): exclude `IsZombie()` corpses.
