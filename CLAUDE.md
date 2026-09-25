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
| `--bench <log>` | `eql_bench.txt` — µs/line per live consumer on a real log with the real loadout, headroom vs the log's peak rate (65 lines/s observed) |
| `--sky-audit <log> [item filter]` | `eql_sky_audit.txt` — replays a log through the loot ledger + Sky tracker on scratch files (`SkyAudit`): every quest item's looted / offered / destroyed / held with the lines behind them. The Data page's "Audit quest ledger" runs the same replay on the followed log + merged copies, shows the drift against the live ledger and offers to realign it (`SkyQuests.AdoptFrom`) |
| `--render-glyphs [png]` | raid-badge contact sheet (iterate vectors visually) |
| `--render-manager <page> <png> [--bottom]` | screenshot one Manager page off-screen (compare a build against a design mock); `character:<tab>` renders the Character window, `toolbar` / `toolbar:hidden` / `toolbar:catchup` / `toolbar:working` / `toolbar:labels` the toolbar (eye struck; catch-up progress card; locked + muted + anvil lit + badges; the same with labels under the keys), `quests:lines` / `quests:sky` / `quests:sky:all` / `quests:house` the Quests window on the Notable quests pack / the Plane of Sky pack (class badges) / the same with every card shown (reward links) / quest item housekeeping unfolded (item icons), `recap` a synthetic death recap, `incoming` the incoming-damage panel, `faction` / `faction:maxed` / `faction:bad` / `faction:standing` the faction helper card (a hit · the cap · the wrong way · between hits), `character:races` the Races tab on demo dumps, `meter:incoming` / `meter:incoming:quiet` the DPS meter with that chart docked as its cap (mid-fight / folded), `charm` / `charm:broke` the charm card, `mez` the mez panel, `library:<search>` the spell library window searched as typed (EFFECT column), `levelup` / `levelup:15` the level-up card (SHD/SHM/ENC at 44 / DRU/BRD/WIZ at 15), `tradeskill` / `tradeskill:ladder` / `tradeskill:trivial` the tradeskill helper card (Brewing mid-step / the whole ladder / Blacksmithing gone trivial), `sct` a combat-text lane with a crit frozen mid-flight, `Data:reparse` the Data page with the reparse progress card mid-run |

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
- `ConditionWatcher` — big stun/fear/charm/mez badges (`ConditionsWindow`).
  Landing/wear-off sets DERIVE from the spell library's uniform wear-off
  families ("You are no longer stunned/afraid/charmed/mesmerized.") — never
  keyword guessing. Badge shows landing→wear-off; censors: own death, zoning;
  hygiene caps per condition (stun 30s … charm 21m). Live-only (not fed on
  catch-up).
- `CrowdControl` — charm card + mez panel (CC you put ON MOBS): your
  begin-cast anchors the spell's third-person landing (Beguile Undead →
  "a greater ice bones moans.", observed), "Your X spell has worn off of Y."
  ends it, damage on a mezzed mob breaks its row (and names who), death/
  zoning censor. An AE mez opens a row per landing within 2.5 s of the
  first (twins = two landings on one name within 5 s; anything later on a
  name is a RE-MEZ and refreshes). Clocks: learned MAX of the last 5
  unbroken landing→wear-off spans over the library figure (ranks/focus/AA
  stretch it — Mesmerization VI 24 s, VIII 31 s observed); a row past its
  clock OVERRUNS grey until the wear-off (hygiene max(90 s, 3×)). LOOSE
  adds: a mezzed mob never acts, so a held name hitting/casting (not DoT
  ticks) flags a loose add — damage on the name is the add's (no false
  break), its death spares the rows, the next landing appends. ONE BREAK,
  ONE ROW (23 Sep, rig log): a break prints up to three lines — "Your X
  spell has worn off of <mob>.", "<mob> has been awakened by <who>." and
  the hit — in any order within 3 s (`BreakPairSec`); the first takes a
  row (a wear-off's row waits in `_recentWorn`), the others fill it in.
  A wear-off nobody hits after is an expiry of the OLDEST held row. Unknown
  landings (necro undead charms) open ASSUMED and are LEARNED from the
  emote after the cast once a wear-off names the mob. Both learned sets
  persist in `cc-landings.json`. Live-only. Charm break = badge + phrase.
  Every charm that ends (broke / died / zoned / replaced / you died) is a
  `CharmBook` episode (`charms.json`: pet, spell + cast rank, zone, hold,
  pet hits/dmg/max hit/taken, kills, mob /con level, your level; failed
  attempts per mob too) → Character window "Charmed pets" tab
  (`CharmsView`) + the card's "before:" line; the card counts DOWN a known
  ceiling and overruns grey past it.
- `TradeskillWatch` + `TradeskillData` — the tradeskill helper (21 Sep), a
  card you SHOW ON PURPOSE (toolbar anvil / ☰ → Panels toggle
  `TradeskillVisible`; ✕ hides). The skill is picked on the card's own ▼
  and remembered (`TradeskillOpen`); STEP | LADDER in the header switches
  the step's details and the clean ladder (rows + verdict tags only).
  Data: `data/tradeskills.json` — eqlwiki's leveling ladders for the nine
  skills (hand-curated in the scratch `build-tradeskills.py`; the guides'
  shapes differ too much to parse) + every ladder recipe from the item
  pages' uniform template (ingredients, container, yield, trivial) + where
  each ingredient comes from (vendor · drop with mob/zone · forage · made).
  A step reads ALL BOUGHT only when the whole sub-combine chain is vendor;
  FARM n / FORAGE / CHECK otherwise (`SourcingOf`). Engine lines: skill-up
  `You have become better at <skill>! (N)` (the value; Jewelry Making =
  Jewelcrafting), success `You have fashioned the items together to create
  something new|an alternate product: <item>.`, fail `You lacked the skills
  to fashion <item>.`, trivial `You can no longer advance your skill from
  making this item.` (printed right BEFORE the success line — pairs within
  3 s → amber MOVE ON + notice, once per recipe), `You purchased N <item>
  from <npc> for  <coins>.` (spend + vendor memory with zone). Skill values
  and vendors persist in `tradeskill-log.json` and are LEARNED on replays
  (`live: false`); the session (combines, skill-ups, pace, spend) is live
  only. Fletching's arrows share one product name over several steps —
  recipes are keyed by step label, `StepFor` picks by skill range.
- `RaceBook` + `FactionDumps` — race unlocks (22 Sep). `/outputfile
  achievements` → `<char>_<server>-Achievements.txt` ("Untapped Potential:
  Races": C/I + one tab = a race, two tabs = its conditions — three "Get
  maximum faction with X." lines, or Kerran's task, Half Elf's "unlock
  Human or Wood Elf", the "created as" line marks YOUR race) and
  `/outputfile faction` → `<char>_<server>-<CLASS>-Factions.txt` (ID · Name ·
  StandingValue · PointsToMax; max = value + toMax = 2,000). Found next to
  the inventory dump, re-read when their clocks move. The log keeps them
  live: "Your faction standing with X has been adjusted by N." moves a
  standing (lines AFTER the dump only; deduped by line), "could not
  possibly get any better" marks maxed AND records that standing as YOUR
  cap (`CapOf`, persisted — an Ogre caps Dark Bargainers at −220, far under
  the dump's 2,000; MAXED bars draw full, "your cap −220"), and "You have
  slain X!" / "X has been slain by <pet/group>!" within 3 s before teaches
  mob → faction → hit (persisted in `races.json` with the
  ★-tracked races). Character window "Races" tab (`RacesView`, badges like
  the Sky classes: done first, DONE/YOU/AUTO/TASK) + the faction helper
  card (`FactionHelperWindow`, tracked races only: between hits it stays
  open with the tracked races' unfinished factions as bars — owner, 22 Sep
  "keep open" — a hit takes over for 20 s).
- `TriggerEngine` — bars/matrix/flash/repop triggers; cast-anchor gate;
  learned-duration hook. `CombatParser` — fights, drill-down, SCT events,
  death recap, session skills + proc watcher. `RaidKills`, `LootTracker`,
  `SkyQuests`, `QuestLines` (embedded `data/quest-lines.json`: multi-step
  weapon quests tracked as proven-condition SETS — kills, loot, sealed
  trades, said keywords; right-clicks are owner ticks. INTENT rule (17 Sep):
  only anchoring steps (hand-ins, said keywords) or ticks start a line and
  imply the steps before them; kill/loot steps vouch for nothing and are
  marked only once the line is started (their evidence waits); coins-only
  trades likewise. Old chains implied by a kill are withdrawn on load),
  `SpellLibrary` (embedded `data/spell-library.json`, 1438
  spells; each carries an `effect` — Direct damage / Damage over time /
  Heal / Mez / Snare / Haste… — derived from its eqlwiki page's effect
  slots + target type + duration by the scratch `classify-spells.py`,
  25 Sep; the level-up card and the spell library's EFFECT column show it,
  tinted by `EffectColor`; `TriggerCategory` types by it FIRST — DoTs,
  HoTs, control → Debuffs — and heals/travel/summons fall back to the
  landing rules; library search takes effects + aliases like dd/dot/hot),
  `SpellDurations` (observed-duration learner), `TriggerColors`
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
  debuffs yellow, cooldowns purple. No per-trigger color picking. HoTs =
  SHORT rotational heals only (≤120s): the regen line ("You begin to
  regenerate." — Chloroplast & kin) and any heal effect running longer are
  Buffs (`SpellLibrary.TriggerCategory`; HealLibraryTriggers retypes old
  HoT-typed regen on load). HoT bars render 1.4× tall (`HeightScale`) — the
  stay-alive bars.
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
  PET CYCLES (22 Sep): a re-cast only discards your open cycle if it LANDS
  ON YOU — the same buff cast on your pet right after yourself (Puma) or
  an interrupted re-cast leaves it alone; the pet landing (castOnOther with
  the name free) opens its own cycle, closed by "Your pet's X spell has
  worn off." into the same pool. 20 samples stored per spell = storage,
  the estimate is the MAX of the newest 5. ESTIMATE CHANGES (22 Sep) are
  announced (toast), remembered (`LastChange`: at/from/to on the editor's
  Duration line + a sample strip) and worn ONCE by the next bar (LEARNED
  tag, green clock via `ConsumeFresh`) — only when the move beats the
  log's whole-second jitter: max(3 s, 2 % of the old estimate).
- **Proc detection**: a spell damage/heal line of yours with no own begin-cast
  within 12s. Never DoT ticks, never thorns; a HEAL you've EVER cast this
  session is your spell (HoT ticks arrive outside any window). "You activate
  <X>." (AA/item, no begin-cast line — Leech Touch) opens the same window;
  known BENEFICIAL library spells never count at all (a buff's own component
  landing cast-less — Quick Buff bursts — is a buff, not a proc). Rates hide
  below sample floors instead of lying.
- **Enemy DoTs**: automatic per-mob bars ("a froglok 01/02") grouped per
  spell, driven by own tick lines. Instance identity = tick heartbeat: a tick
  belongs to the instance DUE one (≥4.5s since its last); a never-ticked
  instance (fresh landing) owns its first tick whenever it comes; nobody
  due = new mob = next free number. NON-ticking debuffs enter via your
  begin-cast + the spell's third-person landing suffix ("A froglok has been
  poisoned.", SpellLibrary.OtherLanding); anonymous landings without your
  cast are ignored. **Landings never grow a ghost bar** (Companion JOS-140
  bounded reading): a landing refreshes an instance that's overrun,
  tick-stale (>7.5s silent), or in its last stretch (≤max(6s, 20%)); it
  reads as a SECOND MOB (append) only when every instance runs comfortably
  on a KNOWN clock; unknown duration always refreshes the newest —
  under-counts self-correct via the heartbeat, over-counts never do.
  Refresh resets Ticks to 0 (awaiting first tick again). Censors: wear-off closes the OLDEST bar, mob death clears
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
- **Toolbar chrome** (21 Sep): one dark track, every button a KEY in its own
  hairline frame; STATE lives in the frame colour — gold = a toggle is on
  (locked, panels shown, tradeskill card open), red = something you will
  regret forgetting (muted, panels hidden). Door colours are identities, not
  states. A gold BADGE on a door = news (quests ready to hand in — counted with the
  Quests window's inventory-dump cap, `ApplySkySnapshot`; drops /
  raid kills since that window was last opened — live lines only). The LOG
  DOT by the loadout name: green following, grey nothing read, gold during a
  catch-up (`OverlayViewModel.LogDot`). `ToolbarLabels` (General page) grows
  the keys to 34×32 with a word under each; state keys' words say the state.
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
