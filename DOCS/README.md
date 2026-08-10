# GTAZ — Documentation

GTA V zombie mod (GTAZ). A .NET Framework 4.8 ScriptHookVDotNet3 (SHVDN3) script + LemonUI menu
that deploys `ZombieMod.dll` directly into the GTA V `scripts/` folder.

All project documentation lives in this `DOCS/` directory. `AGENTS.md` stays at the repo root —
it is the operational cheat-sheet the coding agent reads before touching code.

## Why this structure

The old docs (`ARCHITECTURE.md`, `MOD_PLAN.md`, `TECHNICAL_LESSONS.md`, `NPC_MODELS.md`) grew
jumbled as the project grew: current state, design history, and failed experiments were all
interleaved in the same files, and round-by-round playtest notes buried the facts. This directory
splits them into three layers:

- **Reference (01–05)** — what the code **is** right now. Source of truth for adding features.
- **Direction (06)** — the roadmap: where the project is and where it's going.
- **Knowledge (07–08)** — the rules and the lessons. Why things are done the way they are.
- **History (09)** — what changed and why, round by round. Context, not current state.
- **Archive (10)** — designs that **failed** or were **removed**. Read before re-adding anything.

## Index

| File | What it is | Read when… |
|---|---|---|
| `01-Architecture.md` | How the mod is organized: folder structure, module responsibilities, data flow, key design decisions | Starting work on the codebase |
| `02-Systems-Reference.md` | Deep reference per system: budget, spawn queue, zombie/horde/survivor/faction AI, territory fights, persistence | Touching a specific system |
| `03-File-Reference.md` | One line per source file: what the class does and owns | Finding where a feature lives |
| `04-Config-Reference.md` | Every `ModConfig` option (grouped), the LemonUI menu layout, trainer features, keybinds | Wiring new settings/menu items |
| `05-Data-Reference.md` | Ped model pools per faction, the 12 territories + wave spawn points | Changing models, zones, or spawns |
| `06-Roadmap.md` | Vision, priorities, scope in/out, phase status, Phase 5 (next) checklist, deferred ideas | Deciding what to build next |
| `07-Scripting-Rules.md` | **Hard rules** for GTA 5 scripting with SHVDN3 (the do/never-do list) | Writing any game-facing code |
| `08-Lessons-Learned.md` | The 40 verified bug/fix lessons, numbered (stable), grouped by topic | Debugging something weird |
| `09-Development-History.md` | Design log: what changed per phase/round and why | Understanding why a design is the way it is |
| `10-Archive.md` | Failed fixes and removed features — what to **not** re-add or repeat | "Should we re-add corpse infection / hold-timer capture…?" |
| `11-Refactoring-Plan.md` | Forward-looking plan: dead code, misnames, duplication, FactionSystem splits, code/doc drift — phased, build-verified | Starting a Phase 5 refactor |

## Project status (v1 shipped)

- **Phase 1–3** complete (foundation, zombie core, horde events).
- **Phase 4** (Factions & Territory) **COMPLETE — v1 shipped**. Seven playtest-fix rounds landed,
  incl. territory mapping (all 12 zones), instant-on-clear capture, faction roll-out (all five
  factions live), zombie idle fixes, guard patrols, defenders at zone center, game autosave.
- **Phase 5** (Polish & Balance) is the next step — see `06-Roadmap.md`.
- A **refactoring plan** is the planned follow-up to this documentation revamp — see `11-Refactoring-Plan.md`.

## Maintenance rules (read before editing these docs)

1. **The code is the source of truth.** If a doc disagrees with `GTAZ/` source, fix the doc.
   Config defaults live in `GTAZ/Core/Config.cs`; API names live in the code.
2. **Reference docs (01–05) describe the current state only.** When a feature/design changes:
   - Update the relevant reference doc immediately.
   - Append the change to `09-Development-History.md`.
   - If the old approach **failed**, add a short entry to `10-Archive.md` (and a lesson if it's a
     general trap).
3. **Lesson numbers are stable (1–40).** Code comments reference them (`TECHNICAL_LESSONS #33`,
   `lesson 34`). Never renumber — append.
4. **History and archive are append-only.** Don't rewrite the past to make it look clean; add a
   new entry noting the correction.
5. When you remove a feature, also remove every surface of it from the docs (config table, menu
   layout, reference prose) and grep the source to prove it's gone — a feature that stays in the
   docs but not the code is how stale docs happen.
