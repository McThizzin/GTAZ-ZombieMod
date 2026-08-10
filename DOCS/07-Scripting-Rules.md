# GTAZ — Hard Rules for GTA 5 Scripting (SHVDN3)

The non-negotiables distilled from 40 verified lessons (`08-Lessons-Learned.md`). These are **rules,
not suggestions** — every one of them was learned by shipping a bug that broke the mod. Write new
code against this list, and review changes against it before building.

## Thread & lifecycle

1. **Only `ModCore` extends `Script`.** Every SHVDN `Script` subclass is a separate game thread.
   One script, one thread; all systems ticked by `ModuleManager` in fixed order.
2. **Never call `Script.Wait()` in a constructor.** It throws `InvalidOperationException` outside
   the main loop. Blocking init goes in first-tick actions (`ModuleManager._tickActions`).
3. **Never read `Game.Player.Character`-anything in a constructor either.** SHVDN3 can run
   constructors before the game is fully ready, and natives issued that early are **silently
   dropped** — the relationship bindings don't stick and nothing throws (lesson 33). Anything
   player-dependent must be (re)asserted on the first tick
   (`PedExtensions.EnsurePlayerFacingRelationships()`).
4. **Always release borrowed state on abort.** `RebalanceForHorde(true)` must pair with a `false`
   on abort too, or budget minimums stay borrowed forever. Mirror this pattern for any new ref-counted
   borrow.

## Spawning

5. **Reserve a budget slot before every ped spawn.** `PedBudgetManager` is the #1 constraint
   (`GlobalBudget` 120). No spawn path skips it.
6. **Never use `World.GetGroundHeight()` for spawn positions.** Use `GET_SAFE_COORD_FOR_PED`
   (flags `2 | 4 | 8`) through `SpawnUtils.FindSafePosition`, which probes the terrain Z at each
   candidate first (`GET_GROUND_Z_EXCLUDING_OBJECTS_FOR_3D_COORD`) and searches from `terrainZ + 1`.
   `GET_GROUND_Z_FOR_3D_COORD` returns the roof surface on a rooftop — the excluding-objects probe
   is the terrain probe.
7. **Never spawn "safe" by searching from an anchor Z you haven't verified.** Flat placeholder Zs
   (e.g. a hardcoded `Z=40`) silently fail the safe-position search on hillsides/basins.
8. **Mass spawns go through `SpawnRequestQueue`** (3 grants/pass, 8s TTL). Never `World.CreatePed`
   spam in a loop — it spikes frames.
9. **Never use `CLEAR_AREA_OF_PEDS`.** It deletes zombies too. Iterate nearby peds and
   `MarkAsNoLongerNeeded()` only non-zombies, skipping survivors/faction peds.
10. **Territory range gates use `SpawnUtils.HorizontalDistance` (X/Y only)**, never 3D `Length()`
    against a zone center. Centers can carry flat Zs; 3D distance silently inflates every ring
    check on hillsides.

## AI & tasks

11. **Reissue AI tasks only when the task slot is free** (`GET_SCRIPT_TASK_STATUS != 1`) or on a
    mode/target change — reissuing every tick makes peds stutter and never act.
12. **"Self-sustaining" movement tasks can be cleared by the engine at any time** (blocked events,
    physics, task-slot reuse). Long-running tasks need either a periodic re-issue (`_wanderReissueTimer`)
    or a stuck fallback (`Speed < 0.5f` for ~1.5s → `ClearAll()` + re-issue). Always define an exit
    back to idle when the reason a ped left idle is gone (drop dead targets).
13. **Peds with `BlockPermanentEvents = false` (survivors — needed for loot) will have their
    scripted tasks hijacked** by ambient scenario tasks. That class of ped needs the stuck-scenario
    fallback above.
14. **Clipsets/anim dicts must be requested every frame until loaded, then applied** — and the
    clipset must be skeleton-compatible with the target ped. Fallback: `move_m@drunk@verydrunk`.
    `HAS_CLIP_SET_LOADED` proves loaded, not compatible.
15. **Never set config flag 292 as a freeze flag.** 292 = `DisableAmbientSpeech`. The freeze
    misconception insta-T-poses. Block ladder climbing via flag 146 + `SET_PED_PATH_CAN_USE_LADDERS`.
16. **Reset clipsets to `move_m@generic` and disable ragdoll at `Health <= 30`** or the death-physics
    "rocket" happens.

## Relationships & combat

17. **Friendlies are Companion (0), never Respect (1) or Neutral (3).** The native combat AI treats
    a Respect/Neutral ped the player damages as a combat target ("hostile until they join").
    Companion never does — even on direct damage. This applies to survivors AND recruited group
    members.
18. **`SET_RELATIONSHIP_BETWEEN_GROUPS` is directional — mirror every stance both ways.**
19. **Relationship bindings can be silently dropped at constructor time.** Re-assert player-facing
    stances first-tick (rule 3).
20. **Don't script `HasBeenDamagedBy(player)` aggro windows.** Stray bullets during shared fights
    misfire them. Use relationship groups + combat attributes 25 (`CA_DISABLE_SECONDARY_TARGET`)
    and 68 (`CA_DISABLE_REACT_TO_BUDDY_SHOT`) for allies.

## Config & persistence

21. **`ScriptSettings` is a string bag, not a typed store.** Parse by the property's real type with
    invariant culture — `GetValue<T>` with a wrong `T` silently returns garbage/falls back.
22. **The mod save only carries mod state** (position/heading/health/armor/territories). Weapons/
    money/ammo live in the GTA save — every `PersistenceManager.Save()` also fires `DO_AUTO_SAVE`.
    Do not mirror weapon fields.
23. **Removed features are only gone when they're gone from the deployed DLL.** Incremental builds
    can skip recompiling. Verify with a binary grep (`rg "FeatureName" ZombieMod.dll`) and rebuild
    clean (`Remove-Item -Recurse -Force .\GTAZ\obj, .\GTAZ\bin` + `dotnet build`) after config/menu
    surgery.

## API & compile-time

24. **Treat every SHVDN3 enum member/native as unverified until it compiles.** No XML docs ship
    with the DLL. When an API "should exist", reflect over `ScriptHookVDotNet3.dll`
    (lesson 23). "Obvious" names are often wrong (`WeaponHash.SawnOffShotgun`, not "Sawed";
    `RaycastResult.DidHit`; no `RELATIONSHIP_GET_BETWEEN_GROUPS` native).
25. **Use `unchecked((int)PedHash.X)`** for hashes that overflow `int.MaxValue` (e.g. `Zombie01`).
26. **Game time is `GTA.Chrono.GameClock.Hour`**, never `DateTime`. To set/pause time use
    `GameClock.Hour/Minute/Second` and `GameClock.IsPaused` directly.
27. **Every LemonUI `NativeMenu` sets `Alignment = GTA.UI.Alignment.Right`.** Each menu (including
    dynamically-built submenus) is its own instance; `Center` throws.

## Frame budget

28. **`PedBudgetManager.PeriodicRescore()` runs every tick** or budget slots leak and spawning
    stops.
29. **`GetNearbyPeds` is expensive** — call it once per AI ped per update interval, and use
    update intervals by distance (every 1/3/6/12 ticks).
30. **Keep `ZombieSystem.OnTick` ped-touching code behind the death/control guard**
    (early-return while the player is dead or `!Game.Player.CanControlCharacter`).
