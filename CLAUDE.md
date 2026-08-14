# CLAUDE.md — project guide for AI-assisted development

EQL Assistant is a Windows 11 WPF overlay suite for the MMO *EQ Legends*. It
works **exclusively by parsing the game's `eqlog_*.txt` log file** — no
injection, no memory reading, ever. Features: trigger-driven buff bars &
matrices, repop timer, DPS meter with skills + proc watcher, scrolling combat
text, flash alerts, death recap, raid kill tracker, loot history, Plane of Sky
quest tracker, spell library, learned buff durations, self-updater.

.NET 9 (`net9.0-windows`), WPF + WinForms (tray icon only), single project.
Code namespaces are `EQLOverlay` (historical, internal only); the product,
exe and repo say **EQL Assistant**.

## Build, test, run

```
dotnet build EQL_Assistant.csproj
```

The user runs `bin\Debug\net9.0-windows\EQL_Assistant.exe`. **The build fails
with MSB3027 while the app is running** — ask them to close it, or build to a
scratch output folder (`-o <tmp>`) for verification.

Selftest suites are gated exe arguments; results land in `%TEMP%`:

| arg | result file |
|---|---|
| `--selftest` | `eql_selftest.txt` (OK / FAIL+exception) |
| `--selftest-engine` | `eql_selftest_engine.txt` |
| `--selftest-meter` | `eql_selftest_meter.txt` |
| `--selftest-loadout` | `eql_selftest_loadout.txt` |
| `--selftest-repop` | `eql_selftest_repop.txt` |
| `--replay <log>` | `eql_replay.txt` (parser coverage report on a real log) |
| `--render-glyphs [png]` | raid-badge contact sheet (iterate vectors visually) |

**CRITICAL: the exe is a GUI-subsystem app — PowerShell `&` does NOT wait for
it.** Reading the result file immediately returns a STALE pass from a previous
run. Always `Start-Process -FilePath <exe> -ArgumentList '--selftest' -Wait`
before trusting result files. Every code change gets selftest coverage; run
the affected suites (with `-Wait`) before committing.

## Branch & release workflow

- `dev` = daily branch. **Commit each logical change on dev as you go**
  (standing authorization). Present-tense, story-telling commit subjects.
- `main` = release-only. Never commit to it directly.
- **Only cut a release when the user explicitly says "cut X.Y"**. Ritual:
  bump `<Version>` in the csproj → commit dev → `git switch main` → merge dev
  → `git tag vX.Y.Z` → back to dev → publish:
  `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist`
  → smoke-test ALL selftest suites on `dist\EQL_Assistant.exe` (with `-Wait`)
  → push main+dev+tag →
  `gh release create vX.Y.Z dist\EQL_Assistant.exe --title "EQL Assistant vX.Y" --notes-file <notes>`.
  The release asset is the **PLAIN-NAMED exe** — the self-updater overwrites
  in place, so versioned filenames go stale on user machines. Notes lead with
  a download-first intro (SmartScreen hint), then New/Fixed, then data credits.
- Multi-machine: push when you stop, pull when you start. All app data
  consumers dedupe, so log histories from several PCs merge safely
  (Manager → Data → "Merge in another log file…").

## Architecture map

`Services/` — the brains, all fed line-by-line from `LogWatcher` via `LogBus`:
- `TriggerEngine` — bars/matrix/flash/repop triggers; cast-anchor gate;
  learned-duration hook. `CombatParser` — fights, drill-down, SCT events,
  death recap, session skills + proc watcher. `RaidKills`, `LootTracker`,
  `SkyQuests`, `SpellLibrary` (embedded `data/spell-library.json`, 1438
  spells), `SpellDurations` (observed-duration learner), `TriggerColors`
  (type→color), `ConfigService` (all persistence), `AlertService` (TTS/wav),
  `UpdateService` (GitHub-releases self-update).

`Views/` — each overlay panel is its own window with a `PanelPlacement`
corner-anchor (`window-<key>.json`). `TriggerManagerWindow` is the Manager
(sidebar pages). `RaidGlyphs` holds the hand-drawn vector badges.

Storage: `%APPDATA%\EQL_Assistant\` — `config.json` (no triggers),
`loadouts\<slug>.json`, `respawns.json`, `raid-targets.json`,
`raid-kills.json`, `loot.json`, `fights.json` (★-kept), `seen-spells.json`,
`spell-durations.json`, `window-*.json`, `merged-logs\` (timestamped copies
of merged-in log files — Reset & rebuild replays the followed log + all of
these, so merged cross-machine history survives a reset).

## Design rules that keep recurring

- **Types own colors** (`TriggerColors`): buffs blue, HoTs green, DoTs red,
  debuffs yellow, cooldowns purple. No per-trigger color picking.
- **Alerts are two notices** (2.11+): *notify before it fades* (default 15s)
  and *notify when it faded* (doubles as cooldown "ready"), each a toggle +
  Phrase OR Sound (one channel, never both). Phrases prefill "<Name> is about
  to end" / "<Name> faded" ("… is ready" for Cooldowns) and follow renames
  until hand-edited. Pre-2.11 single-payload configs migrate in
  `ConfigService.NormalizeAlert` (idempotent, runs in `CompileOne`). The
  editor hides manual-only tooling (Show in, Type, cooldown reducer, live
  log) for library triggers.
- **Cast anchor**: several spells share landing sentences (all hastes print
  "You feel much faster."). Anchored triggers only fire within 15s of the
  player's own "You begin casting <name>." — an unanchored ambiguous landing
  starts NOTHING (a guessed bar lies). **Solo-first** (2.11+): auto anchors
  EVERY `lib-*` trigger, shared text or not — a groupmate's buff landing on
  you starts nothing by default; untick per trigger for group play. Manual
  triggers stay unanchored on auto (their names often aren't castable spell
  names). `CastAnchored` tri-state overrides. Exception: **Quick Buff** ("You
  activate Quick Buff.") lands the whole spellbar with no cast lines — an 8s
  activation window admits anchored landings when the spell is plausibly the
  player's own (cast this session / bar already running / known to the
  duration learner). Others' activations ("X activates …") open nothing.
- **Duration learning**: sample = cast-anchored landing→wear-off span;
  death/zoning/external re-land/own re-cast discard the cycle instead of
  minting a wrong number; estimate = MAX over recent 5 samples (early breaks
  read short and must never drag the bar down). Ranks pool ("Quickness II"
  teaches "Quickness"); ranks run base→X, `[IVX]{1,7}` covers them.
- **Proc detection**: a spell damage/heal line of yours with no own begin-cast
  within 12s. Never DoT ticks, never thorns; a HEAL you've EVER cast this
  session is your spell (HoT ticks arrive outside any window). Rates hide
  below sample floors instead of lying.
- **Enemy DoTs**: automatic per-mob bars ("a froglok 01/02") grouped per
  spell, driven by own tick lines. Instance identity = tick heartbeat: a tick
  belongs to the instance DUE one (≥4.5s since its last); nobody due = new
  mob = next free number. NON-ticking debuffs enter via your begin-cast +
  the spell's third-person landing suffix ("A froglok has been poisoned.",
  SpellLibrary.OtherLanding); anonymous landings without your cast are
  ignored. Censors: wear-off closes the OLDEST bar, mob death clears
  single-instance groups (twins wait for silence), zoning, own death; ticking
  bars die by 13s silence, landing-only ones by duration+60s (90s hygiene cap
  when no duration). Unknown durations count UP, never guess.
- **Overrun state**: any bar with a fade line (trigger bars with EndPattern,
  enemy-DoT bars) that expires WITHOUT the fade being witnessed grays out and
  counts up ("+14s"; auto-learn bars say "learning +14s") instead of
  vanishing — removed by the real fade line, a re-cast (refresh in place), or
  an unwitnessed cull at max(60s, the bar's own estimate) (enemy DoTs keep
  the flat 60s). Own death strips all non-Cooldown bars + both matrices
  (buffs die with you — and death is the usual eater of fade lines, which is
  what makes the generous cap honest). Never delete on a mere estimate when
  the log can still contradict it.
- **88 library spells have junk landing text** ("You ."): their triggers
  anchor on `^You begin (?:casting|singing) <name>(?: [IVX]{1,7})?\.` instead.
  `SpellLibrary.MessageCorrections` overrides junk with sentences OBSERVED in
  real logs (never inferred — a guessed line silently never fires; the game's
  own typos like "You **being** to feel healed by the slug." are preserved).
  `SpellLibrary.HealLibraryTriggers` repairs old/broken library triggers on
  every load (types + patterns; corrected spells graduate from begin-cast to
  landing timing); hand-edited values are never touched.
- **Panels**: every panel window with a `DispatcherTimer` MUST stop it on
  `Closed`; prefer in-place `ApplySettings` over rebuild for stateful panels
  (rebuilding the repop watch once caused ghost beeps). Manager saves must
  never blank running bars/timers (preserve state across `UpdateConfig`).
- Kept fights live in ONE shared list (`ConfigService.SavedFights`) — the
  history window and raid auto-keep write through the same instance.
- Every retroactive consumer (loot, kills, durations, sky) DEDUPES, so
  reparse/merge/catch-up can run any number of times.

## Confirmed EQL log line formats

Timestamps: `[ddd MMM d(d) HH:mm:ss yyyy] `. Key bodies (see
`CombatParser`/`SpellDurations` for the exact compiled regexes):
- Spell dmg: `<att> hit <tgt> for N points of <school> damage by <Spell>.`
- DoT tick: `<tgt> has taken N damage from <Spell> by <att>.` (+ short own
  form `... from your <Spell>.`)
- Melee: `<att> <verb(s)> <tgt> for N points of damage.` (misses: `tries to`)
- Heal: `<att> healed <tgt> for N hit points by <Spell>.` (spell optional)
- Thorns DS: `<tgt> is pierced by YOUR thorns for N points of non-melee damage.`
- Casts: `You begin casting <Spell> <rank>.` · deaths: `You have slain <mob>!`
  / `<mob> has been slain by <who>!` · zone: `You have entered <zone>.` ·
  crits: ` (Critical)` suffix · loot: `You looted a <item> from <mob>'s corpse...`

## Environment gotchas

- **AI agent shells may run under an MSIX-packaged host**: `%APPDATA%` reads/
  writes can be VIRTUALIZED phantom copies that contradict reality. The app's
  own log (`bin\...\eql_assistant.log`) is ground truth; to read the real
  config use a UNC path (`\\localhost\C$\Users\<user>\AppData\Roaming\EQL_Assistant`).
- WPF: `VirtualizationMode="Recycling"` + `IsVirtualizingWhenGrouping` silently
  skips rows in grouped lists — use Standard. Custom TabControl templates need
  `PART_SelectedContentHost` or UIA loses the content. Owned windows appear
  UNDER their owner in the UIA tree. Overlay-spawned dialogs need
  `NativeMethods.ForceForeground`. `string.GetHashCode` is per-process
  randomized — never use it for cross-process names (mutexes).

## Data & design credits

Spell library, Plane of Sky quest data, and the duration-learning +
proc-detection designs come from
[jmoyers/everquest-companion](https://github.com/jmoyers/everquest-companion)
(MIT, © Josh Moyers) — keep the credit in README and release notes.
