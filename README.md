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
- **DPS & Skills** — meter visibility, anchor, the **pet name** (auto-detected
  the moment your pet answers any /pet order — "Following you, Master." names
  it; the field is just the manual override), and the
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
  features), **Merge in another log file…** (replay a log from a second PC —
  the file is **copied into the config folder with a timestamp**, listed
  under *Additional log files*, so the merged history survives even if the
  original is deleted), and **Reset data files & rebuild** (wipe the derived
  files and rebuild them cleanly from the current log **plus every stored
  additional file** — settings, loadouts, respawns and ★-kept fights are
  never touched).
- **Shortcuts** — the global hotkey reference.

Panels stick to their chosen screen corner and grow away from it (Bottom-left
keeps a panel above your hotbar, growing upward). Fine-tune by dragging while
unlocked; positions survive restarts and resolution changes.

## Spell library — one-click triggers

**Triggers page → Library…** opens ~1,400 real EQL spells with cast/wear-off
messages, class levels and durations. Filter by *seen in your log*, buffs,
debuffs or class, then one click adds:

The list is **grouped by level** (the filtered class's level when one is
picked, else the lowest class level), alphabetical inside each level. One
**Add** button per spell: a ready-made countdown bar with the right type,
color and duration, **plus a spoken fade warning** ("Quickness is about to
end", 15s before it drops) — the phrase is editable on the trigger, and the
notice can be toggled off or switched to a notification sound.

Added triggers are normal triggers — edit them like any other.

## Triggers, alerts and loadouts

A trigger = *"when a log line matches `startPattern`, do something"* — show a
countdown bar (with category grouping), a matrix cell, or a screen flash;
optionally cleared early by `endPattern`. Patterns are .NET regex matched after
the `[timestamp]` prefix; a `(?<target>...)` capture gives per-target bars.

The trigger list is **grouped by type** with divider headers (Buffs, HoTs,
DoTs, Debuffs, Cooldowns, then the matrices, repop timers and flash alerts),
and **each type owns its color** — buffs blue, HoTs green, DoTs red, debuffs
yellow, cooldowns purple — everywhere the trigger shows up (bars, flash text,
the list swatch). No per-trigger color picking. Disabled triggers gray out in
place. **HoTs mean short rotational heals** (Slugs Healing and kin, ~24s) —
the regen line (Chloroplast, Regeneration, Regrowth…) and other long-running
heal effects are Buffs. HoT bars render **1.4× taller** than the rest:
they're the ones keeping you alive.

Per-trigger alerts are **two independent notices**, each with its own toggle
and its own channel — a spoken phrase (Windows TTS, prefilled "X is about to
end" / "X faded") **or** a Windows notification sound, never both at once:
*notify before it fades* (default 15s) and *notify when it fades* (also the
cooldown "ready" announcement). *Remind me to rebuff when missing* adds a
pulsing REBUFF bar + periodic nudge, only after the buff has been seen up
once. Library triggers keep the editor lean: **Show in**, **Type** and the
cooldown reducer only appear on manually created triggers.

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

The meter has two scopes (**SOLO/GROUP** button, solo by default — EQL is a
solo-first game). **SOLO** ranks *your own abilities* live: every spell, melee
skill, DoT and proc as its own bar with real-time DPS, total and share, plus
hits/crits/misses/range on hover. A detected pet gets a fold-out row of its
own (click the ▶ to expand its per-ability split). **GROUP** ranks everyone
the log shows — nearby players' melee and spells appear in your log, so this
is the classic party meter. Both scopes honor the **DPS/HPS** toggle (solo
HPS = your healing per spell), keep your character highlighted, merge
same-named mobs into a combined **Enemies** row (log lines can't tell two
"a royal guard" apart), and show the **DAMAGE TAKEN** footer for you and your
pet. A fight ends ~10s after combat goes quiet.

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

Missing-buff **REBUFF reminders live in their own movable panel** (anchor on
the Bars & matrices page) instead of squatting inside the buff bars; it only
appears while something is missing. Ctrl+Alt+T also seeds **demo enemy-DoT
bars** so that panel can be placed without picking a fight, and the tray's
Panels menu gained **Enemy DoTs** and **DPS meter · proc watcher** toggles.
The Manager page is now called **DPS + Skills, Procs**.

A **SKILLS section on the DPS meter** for grinding skills: configure the
abilities to watch (Manage → Skill tracker — e.g. `backstab, reave, Smite`)
and each gets a bar filled by its **session-wide** hit rate, colored red →
amber → green, with `hits/attempts · % · crit %` on the right and
misses/resists/max in the tooltip. Counts accumulate across fights — only the
section's **⟲** button resets them. Spell resists count as failed attempts.
Toggle from the tray.

## Enemy DoTs (automatic)

Tab-dotting a camp of same-named mobs is unreadable in the game UI — so the
**Enemy DoTs panel fills itself in**: every DoT of *yours* ticking on an enemy
gets its **own bar per mob**, grouped under the spell — "CURSE: a froglok 01 ·
24s / a froglok 02 · 18s" — no triggers needed. The trick: every tick line
names spell + mob, and each live application ticks on its own ~6-second
heartbeat, so a tick belongs to the bar that's *due* one — and a tick when
nobody is due means a **new** mob (the next bar; freed numbers are reused).
EQ logs genuinely can't tell twins apart, but their heartbeats can. Bars
clear on the wear-off line ("Your Curse spell has worn off of …" — the
oldest bar fades first), the mob's death, zoning, your death, or ~13 seconds
of tick silence.

**Non-ticking debuffs get bars too**: your begin-cast arms a detector, and the
spell's third-person landing ("A froglok has been poisoned.") opens a per-mob
bar — landings without your cast are someone else's and are ignored. Since
these never tick, they're culled by the overrun cap instead of silence.

## Condition badges (stun / fear / charm / mez)

Raid bosses stun. When you are **stunned, feared, charmed or mesmerized**, a
**big glyph badge** (starburst / warning triangle / heart / crescent) appears
in its own movable panel and stays on screen for the **entire duration** —
from the landing line ("You are struck by a sudden force.") to the wear-off
line ("You are no longer stunned."), counting the seconds. Detection is
derived from the spell library's wear-off families, so buff lines never
false-positive. Dying or zoning clears the badges; a per-condition hygiene
cap covers a missed wear-off. Toggle under tray → Panels → **Condition
badges**; place it by unlocking (Ctrl+Alt+L) and dragging — Ctrl+Alt+T shows
demo badges.

**The overrun state (everywhere bars have a fade line):** when a bar's
learned/estimated timer runs out *before* the real fade message arrives, it
doesn't vanish — it **grays out and counts up** (auto-learn bars say
"**learning +14s**") until the fade line, death, or a cull cap closes it. The
cap scales with the bar: up to the bar's own estimated duration again (60s
minimum), so a library starting value that runs short by minutes can't
vanish a buff that's demonstrably still up. The bar stops claiming precision
and shows "still there, still learning" — and the eventual fade is exactly
what teaches the learner the true duration. A re-cast landing on an overrun
bar refreshes it in place. **Dying strips your bars** (except Cooldowns,
which tick through death) — buffs die with you, and that's also the usual
reason a fade line never printed.
Countdowns use learned/library durations and count *up* when the duration is
unknown rather than guessing. The panel only appears while it has rows (or
while unlocked, so you can place it); toggle and anchor live on the
Manager's Bars & matrices page, plus a tray Panels toggle.

## Proc watcher

An optional **PROCS section on the DPS meter** (Manage → DPS & Skills) that
fills itself in — no configuration. A proc is a spell effect of yours that
lands with **no cast of yours behind it** (detection design from
[everquest-companion](https://github.com/jmoyers/everquest-companion)'s
proc-analytics plan, MIT): "You begin casting X." within 12 seconds marks X
as hand-cast; anything else that lands — weapon procs, poison strikes, buff
procs like Reaving Strike or Blood Siphon Strike — counts. Each lane shows
session count, **procs per minute of active combat**, and **procs per 100
melee swings** (the mechanically-honest rate for chance-on-hit procs), plus
damage/healing totals. Rates stay blank until there's enough data — 1 proc in
a 5-second pull is not "12/min". DoT ticks and thorns never count (DoTs are
cast-detached by construction; thorns rides incoming swings). The skills ⟲
resets procs too.

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
  lines and remembered forever. A **This week / All time** toggle scopes the
  whole view to the current **loot lockout week** (resets Tuesday 08:00
  Pacific — per boss, per difficulty; the header counts down to the reset), so
  "what's still worth killing this week" is one glance. Every target wears a **hand-drawn vector badge**
  (dragon, skull, demon, golem, spiroc, wasp, eye, spirit, claw — original
  silhouettes, no game assets) in its own tint: full color once defeated, a
  faded tease while it still lives. Targets you add yourself get a monogram
  badge automatically. Each killed target shows **D0–D4 badges** for
  the zone difficulties you've beaten it at (difficulty is read from the zone
  name — "Befallen 4 (Refined)" = D4; kills recorded before v2.3 count as D0).
  Every kill also records **what it dropped** (loot lines name the corpse),
  and past loot history is stitched onto past kills automatically on first run.
  **Click a defeated target to unfold its kill history** — timestamp, zone,
  difficulty, time-to-kill and the drops per kill — and hit **fight ↗** to jump
  straight to that kill's DPS breakdown in the fight history. Raid-target
  fights are **auto-★-kept forever** the moment they end (no manual Keep
  needed), which is also what powers the time-to-kill and the fight link;
  kills merged in from old logs show without them (ancient fights aren't
  replayed).
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

### Cast-anchored triggers (solo-first)

Several spells print the *same* landing sentence — Quickness, Alacrity,
Celerity and Swift Like The Wind all say "You feel much faster." A plain
pattern trigger can't tell them apart, so casting Alacrity would restart your
Quickness bar with the wrong duration. Triggers therefore support a **cast
anchor** (same ruling as the Companion's): the start pattern only counts when
it follows *your own* "You begin casting &lt;spell&gt;." within 15 seconds,
and an ambiguous landing with no anchor starts **nothing** — a bar that
guesses which haste landed would lie about the time left.

EQL is a solo-first game, so **every library-added trigger anchors itself by
default** — a groupmate's buff landing on you starts nothing (you couldn't
refresh it anyway, and the duration would be theirs, not yours). Playing
grouped and want those bars? Untick the checkbox under the start pattern per
trigger. Manually created triggers default the other way (unanchored), since
their names often aren't castable spell names.

## Death recap

When a death line appears ("You have been slain by …" / "You died."), a recap
window pops up over the game (without stealing focus) covering the **last 15
seconds** — sized for a raid, where 15 *events* used to be four seconds of
misses. It answers the question in layers:

- **The story** — one line naming the verdict: "*The burst that killed you:
  −595 in the last 2s (an ire ghast · Harm Touch −453 …)*" when a final spike
  carried ≥40% of the damage, or "*Worn down — no single burst*" when it
  didn't.
- **The death graph** — one column per second: damage hangs down in red,
  healing stands up in green, the killing-burst seconds glow brighter. Burst
  or attrition is visible in half a second.
- **The grouped ledger** — repeats of the same *attacker · ability* merge
  into one ×N row with the summed total ("Specter Lifetap ×6 · −294"), the
  killing blow's row is tinted, and **misses collapse into chips** at the
  bottom ("a loathling lich ×7") instead of eating rows.

Reopen the latest recap any time from the tray (**Death recap…**); the
auto-popup can be turned off on the Manager's General page.

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
