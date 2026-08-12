# GTAZ — Horde Overhaul Plan (Phase 5)

> **IMPLEMENTED (Aug 2026)** — all changes in §3 except §3.5 (night siege, deferred to a follow-up)
> landed in the "Horde overhaul round". §2 below now describes the *pre-overhaul* implementation it
> was written against; the live behavior is in AGENTS.md ("Phase 5 horde overhaul"), the round log in
> DOCS/09-Development-History.md, and lesson 51. The §6 verification checklist was completed in-game
> before this round was marked done.
>
> **Scope:** horde *feel* and *encounterability* only. Budget/queue/territory plumbing stays as-is
> unless a change below requires touching it (each change notes if it does).

---

## 0. Playtest 1 results & fixes (Aug 2026)

The §6 checklist passed in the first in-game pass: debug-spawned hordes showed the skull blip at
announce, materialized 20/20 around the Horder, the Horder took no damage while members lived, and
the swept crossing + flowing march felt like a single pack. The scheduler path exposed a hard bug
and the pack behavior needed tuning:

1. **Scheduled hordes never survived their first tick (the "no blip, 20 orphans" report).**
   `ModuleManager` ticks `Zombies.OnTick` (runs `SpawnRequestQueue.ProcessQueue`) *before*
   `Horde.OnTick`, so on the frame `StartHorde` enqueues its members the queue has already granted
   that pass — members materialize next frame. `UpdateActiveHordes` ran the same frame with
   `Members.Count == 0` and `IsDepleted` was just `Members.Count == 0` → the horde was removed
   instantly; the later grants spawned as 20 orphan zombies with a dead horde reference (no blip,
   nobody ticking them). The debug trigger escaped it only because menu activation runs *after*
   Horde.OnTick. Fix: `ActiveHorde.PendingSpawns` (incremented per enqueue, decremented per grant
   *and* per request expiry/failure) → `IsDepleted = Members.Count == 0 && PendingSpawns == 0`.
   Lesson 52.
2. **Hordes got distracted by random peds before crossing the player** and a chasing horde lost
   most of its strength to the peds it chased (8/20 arrived). Fix: horde members only ever target
   the **player** — `FindBestTarget` returns `null` (skips the nearest-ped scan) for any member with
   `HordeMoveTarget != null`. Ambient zombies keep the full scan. Lesson 53.
3. **The Horder died to peds mid-fight.** Two contributors: (a) the immunity proofs were only
   applied on the Horder's first `Update()` — a one-frame un-armed window at materialization; now
   applied in the constructor too. (b) the distance/territory **fade loop** could remove an engaged
   member mid-fight (e.g. it stepped inside a territory ring), dropping the "any other member alive"
   count that gates immunity while the horde was visibly still fighting — the fade loop now skips
   `Chasing`/`Attacking` members. Lesson 53.
4. **Stragglers stuttered** ("step one way, stop, step the other way, repeat"): the pull-back ran
   every tick and flipped `HordeMoveTarget` between center and destination for a member hovering on
   the 25m boundary — a task reissue every behavior tick (lesson 5 in a new shape). Fix:
   hysteresis — pull only past 25m (tracked by `ZombieAI.HordePullingBack`), release only inside
   15m, and while pulling re-point only when the flowing center moved > 5m. Lesson 54.
5. **Spawn tuning:** the horde spawned a little too far and members materialized spread out (on
   rooftops in built-up areas). `HordeSpawnDistanceMax` 300 → 220; member cluster 5–25m → 4–12m so
   the pack forms tight around the Horder.

---

## 1. The two problems (from playtest)

1. **Hordes don't feel like a horde.** A horde is currently a bunch of independent `ZombieAI`
   peds that share a `HordeMoveTarget` and march with `TASK_GO_TO_COORD_ANY_MEANS`. There is no
   cohesion: members spread out, string into a line, and lag behind; faster runners peel ahead.
   When they engage, only the perceiving members chase/attack — the rest keep marching, so the
   player fights stragglers, not a mob.
2. **The player never encounters them.** A wandering horde spawns 100–300m away and marches on a
   **random heading** (`StartHorde`, `destination = spawnCenter + heading * 300m`). It can just walk
   *away* from the player, then despawn past `HordeDespawnDistance` (400m). There is no map marker —
   the only signal is the notification ticker. Net result: "I see the notification, but I don't
   know if I've ever actually run into one."

---

## 2. Current implementation (verified)

### 2.1 HordeSystem (`GTAZ/Systems/HordeSystem.cs`)

- **Two kinds** (`HordeKind`): `Wandering` (timer, `HordeEventIntervalMinutes`, ~20 zombies) and
  `Night` (once per night window, `HordeNightChance`, ~40 zombies, hunts the player).
- **Scheduling** (`UpdateScheduler`): wandering on a real-time interval with jitter; night siege
  rolled once on entering 20:00–05:00. Gated on no horde active + no territory fight borrowing the
  budget (`_budget.HordeOverrideActive`).
- **Start** (`StartHorde`): pick a safe `spawnCenter` 100–300m out (never inside any territory),
  random `heading`, `destination = spawnCenter + heading * 300f` (`HordeSystem.cs:295-296`), then
  enqueue `size` zombie spawns through `ZombieSystem.EnqueueZombieSpawn` (shared `SpawnRequestQueue`,
  3 grants/pass → ~17 passes for a 50-horde). Members filed in `horde.Members` (own list), *not*
  `ZombieSystem._activeZombies`.
- **Announce** (`Announce`): ticker + distant gunfire sound + gameplay-cam shake. (The old
  `_survivors.SpawnAt(position, 2)` survivor drop was removed in playtest 2 — see §0.)
- **Update** (`UpdateHorde`): tick members, remove dead; fade members past `HordeDespawnDistance` or
  inside any territory; night hordes re-point `Destination` at the player every 120 ticks; wandering
  hordes advance destination on `CentroidReached` (< 25m from destination).
- **End** (`IsExpired`): night hordes end at daybreak; both kinds end after `LifespanSeconds`
  (night 1200s, wandering 600s). `DespawnHorde` fades all members, then the budget ref is released.

### 2.2 ZombieAI horde-march mode (`GTAZ/AI/ZombieAI.cs`)

- `HordeMoveTarget` (`Vector3?`) + `HordeArriveRadius` (5f) — while set, the **Wandering** state
  issues `TASK_GO_TO_COORD_ANY_MEANS` to the target (`ZombieAI.cs:216-237`).
- Task is only (re)issued when the target changes or on state entry (lesson 5 — reissuing every
  tick restarts the nav task).
- When the zombie perceives the player it breaks into normal Chasing/Attacking; when the target is
  lost it returns to marching. No horde-level awareness exists — each member is purely
  self-interested.

### 2.3 Config & menu

- `ModConfig` (`GTAZ/Core/Config.cs:72-82`): `EnableHordeEvents`, `HordeEventIntervalMinutes`,
  `MaxHordeSize`, `WanderingHordeSize`, `HordeNightSize`, `HordeNightChance`,
  `HordeSpawnDistanceMin/Max`, `HordeDespawnDistance`, `HordeRunnerChance`.
- LemonUI Horde submenu (`MenuManager.cs:388-445`) + Debug triggers (`Trigger Wandering Horde`,
  `Trigger Horde Night`, `Clear All Hordes`) at `MenuManager.cs:520-522`.

---

## 3. Proposed changes

### 3.1 Sweep-path migration (wandering hordes march across the player)

**Problem:** random heading ⇒ horde can march away and never be seen.

**Change:** the wandering horde's destination is placed on the **far side of the player** so the
march path sweeps across the player's area:

- `Destination = playerPos + (heading * reach)` where `reach ≈ HordeSpawnDistanceMax + some margin`
  (~450–600m), so the horde forms up off-screen and then marches *through* the player's vicinity.
- The advance logic (`AdvanceWanderingDestination`) picks each next waypoint from the **current
  centroid** continuing along the same heading (not a fresh random point from the old destination),
  so the horde keeps moving in one direction across the map rather than drifting aimlessly.
- Territory avoidance (`IsInAnyTerritory`) still applies to every waypoint — if the heading would
  cross a territory, re-roll the heading.

**Files touched:** `HordeSystem.cs` only (`StartHorde`, `AdvanceWanderingDestination`).

### 3.2 Horde map blip

**Problem:** no in-world signal the horde exists.

**Change:** each `ActiveHorde` gets a red map blip at its **centroid** (average position of alive
members), updated each tick, short-range, gated by a new config flag `ShowHordeBlips` (default
true). Use the `BlipHelper.Ensure` pattern — one `Blip` field on `ActiveHorde`, created/deleted per
the flag so toggling applies immediately. Blip is deleted on `DespawnHorde`/`ClearAllHordes`.

- **Sprite choice:** `BlipSprite.TeamDeathmatch` (**ID 378** — the skull-and-crossbones with two
  skulls behind it; user-specified). Distinct from the per-ped zombie dots (`ZombieAI.UpdateBlip`).
- **Anchor:** by default the blip tracks the horde **centroid**; once the Horder zombie type lands
  (§3.6) the blip anchors on the **Horder** instead — it's the visual center of the horde.
- **Live vs announced:** the blip should be **visible at announce time** (the ticker tells you a
  horde is coming; the blip tells you where) and track the horde as it marches.
- A dead horde's blip fades when the last member dies (`IsDepleted` already triggers despawn).

**Files touched:** `HordeSystem.cs` (blip field + update in `UpdateHorde`),
`GTAZ/Core/Config.cs` (`ShowHordeBlips`), `GTAZ/UI/MenuManager.cs` (checkbox in Horde submenu),
`DOCS/04-Config-Reference.md`.

### 3.3 Flowing-march cohesion

**Problem:** the group strings out; members lag or sprint ahead and wait.

**Change — flow the destination instead of wait-on-arrival:**

- Replace the `CentroidReached` (arrive-and-wait) trigger with a **cadence-based re-push**:
  every ~4–6s, push the horde destination to a point ~120–150m **ahead of the current centroid**
  along the march heading. Because the target always stays ahead of the group, no member ever
  "arrives" and stands; the mass flows forward as one body.
- `TASK_GO_TO_COORD_ANY_MEANS` (or `TASK_FOLLOW_TO_OFFSET_OF_ENTITY` on the *centroid*, see below)
  is issued per-member on target-change only (unchanged lesson-5 discipline).

**Two cohesion options (pick one or combine):**

- **Option A — shared point, flowing:** everyone marches to the same flowing destination. Simple;
  keeps the group tight-ish but members still take their own paths.
- **Option B — follow-the-centroid offset:** each member's effective target is
  `centroid + (marchDirection * personalOffset)` where the offset is small and personal (per-member
  `HordeArriveRadius`/stagger). The group becomes a genuine blob. Costs a centroid computation per
  push (cheap) and per-member target variation.

**Decision (user, §7 Q1):** **Option A** for the base march, plus the **grouping pull-back** — a
member that drifts far from the center (> 25m) is reeled back toward it (Option B's offset applied
only to stragglers, not every member every push). Keeps the march cheap and simple while preventing
the stretched-line look.

**Files touched:** `HordeSystem.cs` (cadence push + centroid + optional pull-back),
`ZombieAI.cs` (only if Option B's per-member offset needs a second target field).

### 3.4 Engagement cascade (the mob attacks together)

**Problem:** only perceiving members engage; the rest keep marching.

**Change:** when **any** horde member is `Chasing`/`Attacking` (already computed as
`horde.IsEngaged`), re-point the horde's march target at the **player's position** so the whole
horde surges toward the fight:

- In `UpdateHorde`, when `IsEngaged`, push `Destination = player.Position` (same cadence as the
  flow push — don't spam `LastPushedDestination` churn).
- Members that perceive the player break into chase naturally; members that don't still *march*
  toward the player instead of marching away — so reinforcements visibly converge on the player.
- The surge ends when no member is engaged (target drops dead / lost), returning to the flow march.

**Caveat:** this makes hordes *much* more threatening (that's the point) — but it also means a
wandering horde near the player can snowball into a full engagement. Keep the surge distance-gated:
only surge toward the player if the horde is within some radius (reuse `HordeDespawnDistance` or a
new `HordeEngageDistance`), so a horde 800m away doesn't all start marching at you from off-map.

**Files touched:** `HordeSystem.cs` (`UpdateHorde` push logic), optionally
`GTAZ/Core/Config.cs` + menu for `HordeEngageDistance`.

### 3.6 New zombie type: Horder (the horde center)

**User request:** a new zombie type **`Horder`** that uses the big armored `g_m_m_zombie_02` model,
removed from the runner pool. The Horder is the horde's **center point** — the visual and behavioral
anchor the horde forms around — and is **invulnerable while its horde is alive** (`maybe it doesn't
take damage while it has its horde alive`).

**Current state (verified):** `g_m_m_zombie_02` (`0x98549CC4`, not in the SHVDN3 `PedHash` enum —
use `unchecked((int)0x98549CC4)`) is in `ZombieSystem.RunnerModels` (`ZombieSystem.cs:54`). Removing
it leaves the runner pool as `Zombie01` + Juggalo01AMY + zombie_dj + zombie_04 + zombie_05.
`ZombieType` is `{ Walker, Runner }` (`ZombieAI.cs:13-17`); concrete classes are
`WalkerAI`/`RunnerAI` in `GTAZ/AI/ZombieTypes/`.

**Changes:**

1. **`ZombieType.Horder`** — add to the enum; new `HorderAI : ZombieAI` in
   `GTAZ/AI/ZombieTypes/HorderAI.cs`.
   - Model: `g_m_m_zombie_02`. **Speed (user, §7 Q4):** can't be too slow — the horde groups *to*
     it, so a slow Horder would drag the runners down to its pace. It should be fast enough that
     runners can still run around/alongside it: `MoveSpeed ≈ 1.2–1.4f` (faster than walker 0.8,
     below runner 1.8; exact value tuned in playtest so the blob moves as one without stranding
     the runners). `_updateInterval = 5–6`, wide `AttackRange`.
   - **Health (user, §7 Q4):** NOT a real tankiness issue — the Horder is undamageable while its
     horde lives (§3.6 #5), so its `MaxHealth` is only relevant for the post-depletion fight.
     Keep `MaxHealth` ≈ `ZombieHealth` (no `HorderHealth` config needed; drop from §4) — the
     "tankiness" is the immunity, not a health pool.
2. **Remove `g_m_m_zombie_02` from `RunnerModels`** — the Horder model never spawns as an ambient
   runner again. (Keeps `WalkerModels` untouched; no overlap there.)
3. **Horde spawns a Horder:** `StartHorde` enqueues **one** Horder per horde. **Decision (user,
   §7 Q6): the Horder IS one of the `size` members — no extra budget.** Spawn it at/near
   `spawnCenter` so the rest materialize around it; the `size` roll covers the other members.
4. **Horder = horde center (replaces the pure-centroid logic):**
   - The march `Destination` is pushed to the **Horder's position** (or just ahead of it), and
     member targets in §3.3 offset from the **Horder**, not a computed centroid — the Horder leads,
     the horde follows it.
   - The §3.2 blip anchors on the Horder (§3.2 already updated to say this).
   - `CentroidReached`/flowing-march math (§3.3) uses the Horder as its reference point when one is
     alive, falling back to the centroid when the Horder dies.
5. **Invulnerable while the horde is alive — CONFIRMED (user, §7 Q5):**
   - Rule: the Horder takes **no damage while any horde member is alive** (including itself — while
     its own horde lives it's immune); once the horde is depleted, it becomes damageable. This is
     the intended design: the player must clear the horde first, then the Horder is killable — the
     Horder is a boss-like center, NOT a snipeable weak link.
   - Implementation notes (verify at build time): use `Ped.CanBeDamaged` or native proof flags
     (`SET_ENTITY_PROOFS`/`SET_PED_CAN_BE_KNOCKED_OFF_VEHICLE` are NOT this — the relevant flags are
     bullet/fire/explosion proofs, and `CanBeDamaged` in SHVDN3). The trainer god-mode lesson 35
     applies: **re-assert every frame while active** — the engine clears proof/invincible flags on
     entity re-creation, so a once-set flag silently stops working. HorderAI should re-apply the
     immunity in its `Update()` while the horde is alive (it needs a way to know — see below).
   - Wiring: `HordeSystem` already owns `horde.Members` and computes `IsDepleted`; it can set the
     Horder's immunity on spawn and clear it on `DespawnHorde`/depletion. The Horder's `Update()`
     can also ask its horde (a back-reference) whether members remain, so the immunity is
     self-maintaining each tick.

**Files touched:** `ZombieAI.cs` (enum), `GTAZ/AI/ZombieTypes/HorderAI.cs` (new), `ZombieSystem.cs`
(runner pool), `HordeSystem.cs` (spawn a Horder + center-point logic + immunity), `ActiveHorde`
(back-reference / Horder member), blip anchor (§3.2).

### 3.5 Night siege overhaul (staged, after the above lands)

> Deferred — the user picks scope later. Ideas recorded here so the plan is complete:

- **Formation, not teleport-target:** night hordes form up at a visible edge point (e.g. the
  territory ring / a road) and *march in* rather than `Destination = player.Position` every 120
  ticks, which currently reads as the horde "tracking" the player's exact coordinate.
- **Staged waves:** the siege arrives in 2–3 waves (advance guard, main body, stragglers) with a
  short gap, so it feels like a deliberate assault instead of one 40-ped dump.
- **Night-only ambience:** heavier runner mix at night, and/or the horde makes noise (moans) so the
  player can hear it coming even before the blip renders.

---

## 4. Config & menu additions (draft)

| Config key | Default | Menu | Notes |
|---|---|---|---|
| `ShowHordeBlips` | `true` | Horde submenu checkbox | §3.2 — hide/show horde map blips (`BlipSprite.TeamDeathmatch`, ID 378) |
| `HordeEngageDistance` | `400f` (reuse `HordeDespawnDistance`?) | Horde submenu slider | §3.4 — hordes only surge toward the player within this radius |
| `HorderInvulnerableWithHorde` | `true` | Horde submenu checkbox | §3.6 — Horder takes no damage while its horde lives |

No `HorderHealth` — the Horder's `MaxHealth` stays ≈ `ZombieHealth` (the tankiness is the immunity,
not a health pool; §3.6 #1).

If §3.5 (night siege) is taken on later, it may add wave-gap/runner-mix options — kept out for now.

---

## 5. Risks & constraints (checked against the codebase)

- **Budget/queue:** no new spawn paths — all members still go through
  `ZombieSystem.EnqueueZombieSpawn` + the shared `SpawnRequestQueue`. The flow push (3.3) and surge
  (3.4) only change *targets*, never spawn counts. Budget impact: none beyond current behavior.
- **Task discipline (lesson 5 / 37):** movement tasks re-issued **only** on target change. The
  cadence push changes `Destination`, which naturally changes `HordeMoveTarget` → a legit re-issue.
  Do NOT re-issue the march task every tick; the cadence IS the reissue trigger.
- **Self-sustaining tasks can be engine-cleared (lesson 37/38/50):** the horde march task is the
  same class as wandering/chasing — a horde member whose `TASK_GO_TO_COORD_ANY_MEANS` gets cleared
  will stand still mid-march. The horde march needs the same stuck-speed fallback (`Speed < 0.5f`
  for ~30 behavior ticks → re-issue march) as the ambient wander/chase fixes. **Add this to 3.3.**
- **Territories:** every waypoint and the surge target must stay `!IsInAnyTerritory`. The surge
  toward the player is the exception that needs thought: if the player stands *inside* a territory,
  surging the horde there violates the "hordes never trample territory" rule. Decision needed:
  surge toward the player but **stop at the territory edge** (march to the nearest point outside the
  ring), or allow hordes to enter owned zones only during a siege. Default (recommended): stop at
  the edge; the player lured the horde and can lure it back out.
- **`IsEngaged` semantics:** currently computed as "any member Chasing/Attacking." After 3.4 this
  drives the surge — make sure it's recomputed *after* the member-update loop each tick (it already
  is, `HordeSystem.cs:159`).
- **Blip + per-ped blips:** horde members already render red per-ped zombie blips
  (`ZombieAI.UpdateBlip`, `ShowZombieBlips`). The horde group blip is extra — two toggles now
  control similar visuals; make the group blip distinct enough (sprite + scale) not to blend with
  the per-ped dots. The Horder itself is a horde member → it gets the per-ped dot AND the anchor
  blip; suppress its per-ped dot (`ShowZombieBlips` skip for Horder) so the skull blip is the only
  marker on it.
- **Horder model is not in `PedHash`:** `g_m_m_zombie_02` must be spawned with
  `unchecked((int)0x98549CC4)` (same pattern as `zombie_01`/`zombie_02` already in the pools) —
  `PedHash.Zombie02` doesn't exist in SHVDN3 (verified).
- **Horder immunity is a re-asserted flag, not a one-shot (lesson 35):** the engine clears
  proof/invincible flags on entity re-creation, so the Horder's "no damage while horde alive" must
  be re-applied in `HorderAI.Update()` every tick while the horde lives (and cleared when the horde
  dies). Same trap as the trainer god modes.
- **Horder death vs horde depletion:** CONFIRMED (user, §7 Q5) — the Horder is immune until the
  horde dies; the player must clear the members first, then the Horder becomes vulnerable. It's a
  boss-like center, not a snipeable weak link.
- **One Horder per horde:** `StartHorde` enqueues exactly one Horder; ambient zombie spawning never
  produces one (model removed from `RunnerModels`, not added to `WalkerModels`). The Horder counts
  as one of the `size` members — no extra budget (user, §7 Q6).

---

## 6. Verification checklist (in-game)

- [x] Trigger a wandering horde from Debug. Blip appears at announce, tracks the centroid as it
      moves. (Playtest 1 ✓ — debug trigger; scheduler path needed the §0 #1 fix first.)
- [ ] With the horde marching, stand still near its path — it sweeps **across** you, not away from
      you. (Re-test after §0 #5 spawn tuning.)
- [ ] Watch the horde from a distance (binoculars/zoom): it moves as a blob, not a stretched line;
      stragglers > 25m from the centroid get reeled back in. (Re-test after §0 #4 hysteresis fix.)
- [x] Engage a few members: the rest of the horde turns and surges toward you (within
      `HordeEngageDistance`); hordes farther away do not. (Playtest 1 ✓ — "the Horder is chasing.")
- [ ] Kill the engaged members: the surge drops back to flow-march (no zombie frozen mid-march —
      lesson-50 fallback working).
- [ ] Stand inside a territory and lure the horde: members stop at the territory edge instead of
      marching in (per §5 decision).
- [ ] Night siege: still fires once per night, still ends at daybreak; blip tracks it; no budget
      ref leaks (`GetSummary` shows correct counts; `RebalanceForHorde(false)` released on end).
- [ ] Toggle `ShowHordeBlips` off/on mid-horde: group blip disappears/reappears next tick.
- [x] Horder: trigger a horde — exactly one `g_m_m_zombie_02` spawns at the center, the rest
      materialize around it; the group skull blip (ID 378) anchors on the Horder. (Playtest 1 ✓.)
- [x] Horder immunity: shoot the Horder while members are alive — no damage taken. Clear the
      members, then shoot it again — it takes damage and dies. (Playtest 1 ✓; §0 #3 hardened it.)
- [ ] Ambient spawns: `g_m_m_zombie_02` no longer appears as an ambient runner.
- [ ] Performance: a 40-strong horde flowing + surging holds the same frame budget as today (no
      per-tick task churn; verify no `TASK_GO_TO_COORD_ANY_MEANS` spam in the log).

---

## 7. Open decisions for the user

> **RESOLVED** — all decisions below were answered by the user (Aug 2026). Recorded here for
> traceability; nothing in this section is still pending.

1. **§3.3 cohesion:** **Option A (shared flowing point) + grouping pull-back** for stragglers
   > 25m. (Chosen: Option A + pull-back.)
2. **§3.4 surge + territories:** **stop at the territory edge** (recommended option).
3. **§3.5 night siege:** **follow-up round** (deferred). Plan keeps it separable.
4. **§3.6 Horder health/speed:** speed fast enough that runners can still run alongside
   (`MoveSpeed ≈ 1.2–1.4f`, not a slow drag); `MaxHealth` ≈ `ZombieHealth` — the tankiness is the
   immunity, so no separate health pool/config.
5. **§3.6 Horder immunity scope:** **undamageable while the horde is alive** (clear the horde,
   then it dies) — confirmed, not the enrage alternative.
6. **§3.6 Horder budget:** **the Horder is one of the `size` members** — no extra budget.
7. **§3.2 blip sprite:** locked to `BlipSprite.TeamDeathmatch` (ID 378) per user.
