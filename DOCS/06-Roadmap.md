# GTAZ — Roadmap

Vision, priorities, scope, and where the project is going. For the round-by-round history of what
was already done, see `09-Development-History.md`.

---

## 1. Vision

A single-player zombie outbreak mod for GTA 5 that makes Los Santos feel like it's actually falling
apart. The player walks out into a city that's been quietly overrun — ambient zombies roaming
streets, hordes migrating at night, survivors holed up in contested zones, cops and traffic gone.
**Authentic outbreak atmosphere first; survival mechanics are a distant second.** The mod should
feel good with vanilla GTA 5 mechanics (weapons, driving, combat) without requiring the player to
manage hunger/thirst/crafting to have fun.

## 2. Priorities (ordered)

1. **Atmosphere** — world feels like an outbreak (no traffic, no cops, ambient zombies, weather/time control)
2. **Zombie AI** — believable behavior (wander, chase, attack, horde)
3. **Performance** — stable at 60+ zombies on screen, no crashes, no T-poses
4. **Horde events** — scripted dynamic threats (night hordes, migrations, sieges)
5. **Factions & territory** — survivor outposts, faction raids, contested zones
6. **Player survival** — hunger/thirst/crafting/building *(deferred indefinitely)*

## 3. Scope

**In (v1, shipped):** ambient spawning with ped budget management; Walker + Runner zombie types;
perception + state-machine AI; world clearing (traffic/cops/scenarios); apocalypse atmosphere
(time/weather); LemonUI config/debug menu; horde events (wandering + night siege); faction roll-out
(all five factions live); territory capture + defense/raid waves; survivor recruiting; save/load +
game autosave; auxiliary trainer (F5).

**Out (v2+, deferred):** player survival stats (hunger/thirst/stamina); inventory/crafting/base
building; squad commands (hold/attack positions); recruited-NPC vehicles; faction-vs-horde
battles; territory upgrades; advanced zombie types (Tank/Screamer/Spitter/Crawler — require custom
anims); multiplayer / FiveM; custom map support.

**Will not be re-added:** corpse infection / `CLONE_PED` reanimation (removed at the user's
request — ambient + horde spawning is the zombie flow). See `10-Archive.md`.

## 4. Phase status

| Phase | Scope | Status |
|---|---|---|
| 1 | Foundation (ModCore, ModuleManager, config, world, menu) | ✅ Complete |
| 2 | Zombie core (budget, AI, ambient spawning, night runners) | ✅ Complete |
| 3 | Horde events (wandering hordes, horde night) | ✅ Complete |
| 4 | Factions & Territory | ✅ **COMPLETE — v1 shipped** (7 playtest-fix rounds) |
| 5 | Polish & Balance | ⏭️ **Next** |
| 6 | Player Survival | ⏸️ Deferred |

## 5. Next: Phase 5 — Polish & Balance

- [x] Attack anim alternatives (biting/clawing vs. punch) — **done**: 4 `heavy_punch_*_var_*`
      variations random-picked per swing (`ZombieAI.AttackAnimVariations`), anim dict preloaded
      (TASK_PLAY_ANIM needs REQUEST_ANIM_DICT first)
- [ ] Facial expression tuning — **rework in progress (lesson 58)**: the old idle override was
      cleared by the engine on every task start (zombies are never idle) and `burning_1` never
      rendered. Layered approach: random lesson-12-verified `mood_*` per zombie (`ApplyZombieFace`),
      mid-task `PLAY_FACIAL_ANIM` one-shots, + permanent gore damage packs (`ApplyZombieFaceGore`)
- [ ] Spawn position LOS/walkable check
- [ ] Playtesting, density/perf tuning at 80 zombies
- [ ] v1 regression pass: retest hordes, ambient zombies, transient survivors, territory capture,
      recruiting, trainer, autosave after the Phase 4 polish rounds
- [ ] Follow-up: **refactoring plan** for the codebase (the docs revamp made the target structure
      clear; the code grew organically and has cleanup to match)

## 6. Deferred: Phase 6 — Player Survival

- [ ] PlayerStats (hunger/thirst/stamina)
- [ ] Inventory, crafting, looting
- [ ] Survival menu, HUD TimerBars

## 7. Success criteria (v1 — all verified in playtesting)

- Player can spawn into a city that feels abandoned and overrun
- ~70–80 zombies active at once, stable at 60fps
- Zombies wander, chase, attack, and horde believably
- No T-poses, no flying zombies, no infinite spawn loops
- Horde events trigger dynamically at night
- Menu allows tuning all major parameters
- Clean save/load across sessions

## 8. References

- [GangWar](https://github.com/lucasvinbr/GTA5GangMod.git) — architecture inspiration
- [SimpleZombies](https://github.com/ParisRP/GTAV-Simple-Zombies.NET.git) — zombie AI reference
- [open-trainer-V](https://github.com/slashury-dev/open-trainer-V.git) — menu/UI reference
