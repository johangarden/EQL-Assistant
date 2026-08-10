# EQL Assistant

A local, log-file-based **overlay suite** for EQ Legends — in the spirit of
GINA + GamParse for classic EverQuest, in one lightweight native app. It reads
the game's log file (no injection, no memory reading — just `eqlog_*.txt`) and
gives you:

- **Buff / HoT / DoT countdown bars** and present/missing **buff matrices**
- A **spell library** with ~1,400 real EQL spells for one-click triggers
- A **repop watch** with named respawns auto-started by mob death lines
- A **DPS meter** with fight history, side-by-side comparison and an
  ability-level drill-down (hit %, damage ranges, crits, resists, proc rates)
- A per-fight **timeline** — every hit, miss, crit and resist on a time axis
- **Scrolling combat text** in movable lanes
- A **raid kill tracker** and a persistent **loot history** (upgrades, kept
  items, vendor income)
- A **death recap** popup — the last hits and heals on you when you die
- **Flash alerts** + Windows text-to-speech / sound alerts
- **Self-updating** — one prompt when a new release is out, and it swaps
  itself in place

Everything renders in transparent, always-on-top, click-through panels over the
game.

## Requirements

- Windows 10/11 (the standalone exe needs no .NET installed)
- **The game must run in *Windowed* or *Borderless Windowed* mode.** True
  exclusive fullscreen hides any overlay — a Windows limitation, not a bug.
- In-game logging must be **on** (`/log on` once) so the game writes an
  `eqlog_<Char>_<server>.txt` file.

## Run it

Standalone: just run `EQL_Assistant-vX.Y.exe` — a single portable file, no
install. From source:

```bash
dotnet run
```

or the debug build directly:

```bash
bin\Debug\net9.0-windows\EQL_Assistant.exe
```

Config lives in `%APPDATA%\EQL_Assistant\` (created on first launch).

### Standalone build (portable exe)

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Result: `dist\EQL_Assistant.exe` (~71 MB). Bump `<Version>` in
[EQL_Assistant.csproj](EQL_Assistant.csproj) before each cut — it stamps the exe
and shows in the toolbar — then keep a versioned copy like
`EQL_Assistant-v1.8.exe`. The spell library is embedded in the exe, so a lone
copied file carries everything.

## First-time setup

1. Open **Manage** (toolbar button or tray) → **General** page → **Browse** to
   your log folder (e.g. `…\EverQuest Legends\Logs`) and **Save**. The newest
   `eqlog_*.txt` is followed automatically, so it works across characters — and
   your **character name is auto-detected from the log filename** (shown in the
   Manager's sidebar; set a **Pet name** on the DPS meter page if you run one).
2. Recommended, also on the General page:
   - **Start with Windows** — so you never forget to launch it.
   - **Catch-up is automatic** — on every start the app silently rebuilds
     today's fight history, raid kills, loot and seen spells from the log
     (alerts and combat text stay quiet for old lines; everything dedupes).
     Re-run any time: tray → *Catch up from today's log*.
   - **Reparse entire log file** (General page) — replays your *whole* log
     through loot history, raid kills + drops, Sky quests, seen spells and
     duration learning. Run it after an update adds a new log-based feature
     and your history is picked up retroactively; everything dedupes, so it's
     always safe to run again.
3. Add triggers (easiest: the **Library…** button — see below), then **lock**
   the overlay (padlock or Ctrl+Alt+L) and play.

## Hotkeys (global — they work while the game is focused)

| Hotkey | Action |
|---|---|
| **Ctrl+Alt+L** | Lock / unlock all panels. Locked = click-through, no chrome. |
| **Ctrl+Alt+R** | Show / hide the repop timer |
| **Ctrl+Alt+D** | Show / hide the DPS meter |
| **Ctrl+Alt+C** | Show / hide scrolling combat text |
| **Ctrl+Alt+H** | Hide / show the whole overlay |
| **Ctrl+Alt+S** | Mute / unmute all alerts |
| **Ctrl+Alt+T** | Demo data (bars, meter, a fight with timeline — try everything without being in-game) |
| **Ctrl+Alt+Q** | Quit |

**System tray icon:** right-click for **Manage settings** (top), Show/Hide,
Lock/Unlock, Mute, a **Panels ▸** submenu with checkmarks (toolbar / buff bars
/ repop timer / DPS meter / skill tracker / combat text / flash alerts),
**Loadout ▸**, Raid kills, Loot history, Sky quests, Death recap, Catch up
from today's log, Check for updates, Open config folder, **Reset position**
(also unlocks and unhides everything — the fixer if a panel is lost
off-screen), and Quit.

**Toolbar** — a detached command strip that stays visible and clickable even
while locked: ✕ quit · version · **Loadout ▾** picker · **☰ menu** (mirrors
the tray menu exactly) · **Manage** · 🔊 mute · 🔒 padlock (governs all other
panels; glows accent-blue while unlocked as a "lock me before playing"
reminder). Drag to place it; hide it via ☰ → Panels if you want a clean
screen.

## The Manager

**Manage** opens a sidebar window, grouped into sections. Every page carries a
scope chip: triggers are **PER LOADOUT** (switching loadouts switches the set),
everything else is **GLOBAL**.

**TRIGGERS**
- **Triggers** — the trigger editor (list, details form, live log capture).
  Click a line in the live feed → **→ Start pattern** to build a regex from the
  real log line. **Test matches** checks a pasted line against all triggers.
- **Loadouts** — create/rename/duplicate/delete trigger sets; the Triggers
  page edits whichever one is selected (switch the *active* one in game via
  the toolbar's Loadout ▾ or the tray).

**PANELS**
- **Bars & matrices** — countdown-bar sizes, the buff/debuff matrix settings
  (columns, anchors), and the **rebuff reminders** timing (warn threshold +
  repeat interval for triggers marked "remind when missing").
- **Repop timer** — visibility and anchor of the watch panel.
- **DPS & Skills** — meter visibility, anchor, the **pet name**, and the
  **skills section** (which skills to grind-track, with the picker).
- **Combat text** — which lanes exist, sizes, big-hit threshold.
- **Flash alerts** — text size, area width, anchor.
- **Death recap** — the auto-popup toggle.

**KNOWLEDGE**
- **Respawns** — the global named-respawn list (zone-grouped, recent-kills
  picker) that auto-starts the repop watch on death lines.

**APP**
- **General** — start with Windows, log source (auto-follows the newest
  character log), overlay basics.
- **Data** — everything the app knows is derived from your log: **Reparse
  entire log file** (additive backfill after updates add new log-based
  features) and **Reset data files & rebuild** (wipe the derived files and
  rebuild them cleanly from the current log — settings, loadouts, respawns
  and ★-kept fights are never touched).
- **Shortcuts** — the global hotkey reference.

Panels stick to their chosen screen corner and grow away from it (Bottom-left
keeps a panel above your hotbar, growing upward). Fine-tune by dragging while
unlocked; positions survive restarts and resolution changes.

## Spell library — one-click triggers

**Triggers page → Library…** opens ~1,400 real EQL spells with cast/wear-off
messages, class levels and durations. Filter by *seen in your log*, buffs,
debuffs or class, then one click adds:

- **Bar** — countdown with the right duration, cleared early by the wear-off line
- **Bar + voice** — same, plus a spoken warning 20s before it drops
- **Fade flash** — screen flash the moment the wear-off line appears

Added triggers are normal triggers — edit them like any other.

## Triggers, alerts and loadouts

A trigger = *"when a log line matches `startPattern`, do something"* — show a
countdown bar (with category grouping), a matrix cell, or a screen flash;
optionally cleared early by `endPattern`. Patterns are .NET regex matched after
the `[timestamp]` prefix; a `(?<target>...)` capture gives per-target bars.

Per-trigger alerts: spoken phrase (Windows TTS) and/or `.wav`, *warn N seconds
before it drops*, *alert at 0* (cooldown "ready"), *remind me to rebuff when
missing* (pulsing REBUFF bar + periodic nudge, only after the buff has been
seen up once).

Bar triggers can also carry a **cooldown reducer**: while the bar runs, every
log line matching the reducer regex cuts N seconds off it — the bar visibly
jumps and "−Ns" floats in the XP & faction lane. Built for the SK mechanic
where every landed Reave shaves 60s off Harm Touch's 20-minute cooldown:
start `^You begin casting Harm Touch`, duration 1200, reducer `^You reave `
cutting 60, with *alert at 0* announcing it ready.

**Loadouts** hold trigger sets per class combo — create/rename/duplicate/delete
at the top of the Triggers page, switch from the toolbar **Loadout ▾** menu.
Each is a file in `%APPDATA%\EQL_Assistant\loadouts\`.

## Repop watch

A circular Time-Timer-style countdown (own panel + anchor): shrinking pie,
big `m:ss`, pulses red near 0 and beeps at 0. Controls: **☰** mode/presets ·
**✏** set duration · **▶/⏸** · **↻**.

**Named respawns** are **global** — independent of loadouts. When the mob's
death line appears in the log, the watch auto-starts with its respawn time.
Adding one is two clicks **right on the watch**: **➕** → pick from your recent
kills → type the respawn time (m:ss, 900s or 15m) — the **zone is filled in
automatically**, and both the Manager list (Repop timer page) and the watch's
☰ menu are **grouped by zone** so long lists stay navigable. With several
running, the watch shows the **soonest spawn** big, the rest as secondary rows.

## DPS meter, fight history and timeline

The meter shows live ranked damage (or healing — toggle **DPS/HPS**) per
source, with your character highlighted, same-named mobs merged into a combined
**Enemies** row (log lines can't tell two "a royal guard" apart), and a
**DAMAGE TAKEN** footer for you and your pet. A fight ends ~10s after combat
goes quiet.

**Fight history** (📜 on the meter) keeps the last 50 fights — Ctrl-click up to
three to compare side by side. **★ Keep** saves a fight permanently, so you can
compare this week's Vox kill against last week's. Each fight card shows three
sections:

- **Damage dealt** — ranked sources, then your/pet abilities with
  `40/52 hit (77%) · 32–78 · 13 crit · 2 resisted · 30/min · 11,5/100 swings`
  detail lines (per-100-swings = the "is this proc weapon worth it" number).
- **Damage taken** — you/pet totals plus *what* hit you — melee vs breath
  weapon tells you whether to stack AC or resists.
- **Healing** — ranked healers.

Fights are tagged with the **zone** they happened in.

**Timeline** opens the selected fight as a second-by-second visual: a
**rolling-DPS graph** on top (5s window — one curve each for your damage, pet,
damage taken and healing, with the fight's peak), and below it one mark lane
per ability, grouped into *Your damage / Pet / Damage taken / Healing*. Mark
height scales with the amount; crits are wider and brighter, misses gray,
resists purple. Hover any mark for the exact number. Open two fights side by
side to compare pulls.

## Skill tracker

A **SKILLS section on the DPS meter** for grinding skills: configure the
abilities to watch (Manage → Skill tracker — e.g. `backstab, reave, Smite`)
and each gets a bar filled by its **session-wide** hit rate, colored red →
amber → green, with `hits/attempts · % · crit %` on the right and
misses/resists/max in the tooltip. Counts accumulate across fights — only the
section's **⟲** button resets them. Spell resists count as failed attempts.
Toggle from the tray.

## Plane of Sky quest tracker

Every class's Test quests (~95 quests) with **have/need chips per turn-in
item**, counted automatically from your loot history. Open it from the tray
(*Sky quests…*) or the Fight History window (*Sky*). Filter by class or
search; quests sort **closest-to-done**; hover an item for who drops it and
where, hover the reward for its full stats. A quest **checks itself off when
its reward item appears in the log** (with a celebration flash) — or tick it
manually. Progress persists in `sky-progress.json`.

## Raid kills and loot history

Both open from the Fight History window (and raid kills from the tray):

- **Raid kills** — a tiered target list (Open World, Fear, Hate, Sky — edit
  `raid-targets.json` to taste) with kill counts and dates, detected from death
  lines and remembered forever. Each killed target shows **D0–D4 badges** for
  the zone difficulties you've beaten it at (difficulty is read from the zone
  name — "Befallen 4 (Refined)" = D4; kills recorded before v2.3 count as D0).
  Every kill also records **what it dropped** (loot lines name the corpse) —
  hover a killed target for the per-kill drop list, and past loot history is
  stitched onto past kills automatically on first run.
- **Loot** — every item you loot, persisted forever: **upgrades** (gold, with
  the "+N → +M" chain), **kept** items, and auto-**vendored** drops with their
  sale price. Search by item/mob/zone, filter by kind, and watch the running
  totals ("251 upgrades · vendored 217p 8g 6s 6c").

## Learned buff durations

The app learns your spells' real durations from the log (an idea borrowed from
[everquest-companion](https://github.com/jmoyers/everquest-companion)): a
sample is the span from a cast-anchored landing ("You begin casting X." then
its landing line) to its wear-off line — so only *your own* casts teach, and
anything that contaminates a cycle (death, zoning, an external re-buff, your
own re-cast) discards it instead of recording a wrong number. Early clicks and
breaks can never drag the estimate down (it's a max over recent clean samples),
while real corrections in either direction — AA/focus extensions *or*
level-scaled durations shorter than the library's number — win once observed.

Each bar/matrix trigger chooses its mode in the editor: **Auto-learn**
(default) starts from the configured/library duration and follows the learned
estimate as samples arrive — the editor shows "Learned so far: 9m53s (9
samples)" under the field. **Manual** (untick) enforces the exact configured
time; learning still runs in the background so you can switch back any time.
Ranks pool ("Quickness II" teaches "Quickness III"), and samples persist in
`spell-durations.json`.

## Death recap

When a death line appears ("You have been slain by …" / "You died."), a recap
window pops up over the game (without stealing focus) showing the last 15
things that happened **to you**: hits with attacker + ability + amount, misses
dimmed, heals in green — each with the time offset back from the moment of
death. The biggest hit is tinted, and the header sums damage taken (and
healing received) over the visible window. Reopen the latest recap any time
from the tray (**Death recap…**); the auto-popup can be turned off on the
Manager's General page.

## Scrolling combat text

Floating numbers in up to seven fixed lanes, each its own movable panel with
its own anchor: incoming (you/pet), outgoing (you/pet), your heals, heals on
you, and an **XP & faction** lane — xp gains float gold ("+3,5% xp"), faction
adjustments teal/red by sign with the faction name, and AA points float big.
Melee, spells/DoTs and procs get distinct colors per lane; crits render big
with a "!". Big-hit threshold, number size and lane sizes are on the Combat
text page. Master toggle: Ctrl+Alt+C.

## Updates

The app checks [GitHub Releases](https://github.com/johangarden/EQL-Assistant/releases)
shortly after startup (and on tray → **Check for updates…**). When a newer
version exists it asks once — accept, and it downloads the new exe, restarts
itself, and swaps the file in place. Settings, triggers, history and panel
positions are untouched (they live in the config folder, not the exe). If
anything goes wrong, grab the exe manually from the releases page — the app
never updates without asking.

## Config folder

`%APPDATA%\EQL_Assistant\` holds everything — copy it to move machines:

| File | Contents |
|---|---|
| `config.json` | settings |
| `loadouts\*.json` | trigger sets |
| `respawns.json` | global named respawns |
| `fights.json` | ★-kept fights (including their timelines) |
| `raid-kills.json` / `raid-targets.json` | raid progression / target list |
| `loot.json` | loot history |
| `sky-progress.json` | Plane of Sky item counts + completed quests |
| `seen-spells.json` | which library spells appeared in your log |
| `window-*.json` | panel positions |

## Credits

The spell library (`data\spell-library.json` — names, cast/wear-off messages,
class levels and durations) and the Plane of Sky quest data
(`data\sky-quests.json` — quests, turn-in items, droppers and reward stats)
are converted from
[jmoyers/everquest-companion](https://github.com/jmoyers/everquest-companion)
(MIT License, Copyright 2026 Josh Moyers), whose data was in turn sourced from
eqlwiki.com and wiki.project1999.com. Thanks!
