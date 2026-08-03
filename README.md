# EQL Overlay

A local, log-file-based buff / heal-over-time (HoT) / DoT **timer overlay** for
EQ Legends — in the spirit of GINA/GamParse for classic EverQuest. It reads the
game's log file (no injection, no memory reading), matches lines you define, and
draws depleting countdown bars in a transparent, always-on-top window over the
game.

## Requirements

- Windows 10/11
- .NET 9 SDK/runtime (already installed here)
- **The game must run in *Windowed* or *Borderless Windowed* mode.** True
  exclusive fullscreen will hide any overlay — this is a Windows limitation, not
  a bug. (Borderless Windowed looks/feels like fullscreen and works great.)
- In-game logging must be **on**. In classic EverQuest that's the `/log on`
  command; do that once so the game writes an `eqlog_*.txt` file.

## Run it

From the project folder:

```bash
dotnet run
```

Or launch the built exe directly:

```bash
bin\Debug\net9.0-windows\EQLOverlay.exe
```

On first launch the overlay starts **unlocked** (you'll see a small toolbar and a
status line) and creates its config at:

```
%APPDATA%\EQLOverlay\config.json
```

## Standalone build (portable exe)

To produce a single, self-contained exe that runs without .NET installed:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist
```

Result: `dist\EQLOverlay.exe` (~70 MB). The build **version** (shown as `v1.0` in
the toolbar and stamped into the exe) comes from `<Version>` in
[EQLOverlay.csproj](EQLOverlay.csproj) — **bump it before each standalone build**
to keep track, e.g. `1.1.0`, then rename the output like `EQLOverlay-v1.1.exe`.

## First-time setup

1. Open **Manage** (toolbar button) → **Settings** tab → **Browse** to your log
   folder (e.g. `…\EverQuest Legends\Logs`) and **Save**. The newest
   `eqlog_*.txt` there is followed automatically, so it "just works" across
   characters. The status line should read `Following eqlog_YourChar.txt`.
2. Build your triggers in the Manager (see below), then **lock** the overlay
   (padlock, or Ctrl+Alt+L) so clicks pass through to the game, and play.

## Hotkeys (work globally, even while the game is focused)

| Hotkey | Action |
|---|---|
| **Ctrl+Alt+L** | Lock / unlock. Locked = click-through + no toolbar. Unlocked = movable, toolbar shown. |
| **Ctrl+Alt+T** | Spawn a demo bar (see the overlay without being in-game). |
| **Ctrl+Alt+H** | Hide / show the overlay. |
| **Ctrl+Alt+S** | Mute / unmute all alerts. |
| **Ctrl+Alt+Q** | Quit the overlay. |

**System tray icon:** right-click for Show/Hide, Lock/Unlock, Loadout, Manage,
Mute, **Open config folder**, **Reset position**, and Quit — your fallback while
the overlay is locked (no visible chrome). Double-click toggles show/hide.

**Toolbar** (unlocked only): red **✕** (quit) top-left, a **Loadout ▾** dropdown,
an accented **Manage** button, and a **🔒 padlock** (lock) top-right. A **🔇**
shows when muted. Opening the config folder now lives in the tray menu.

**Settings** (Manage → Settings) adds **Opacity** (whole-overlay see-through),
**Start locked** (launch game-ready), plus bar height/font for a compact look.

Move it by dragging the toolbar while unlocked. Position and lock state are
remembered.

## Loadouts (class combos)

EQL is one character but many class combinations, so triggers are organised into
**loadouts** — named sets you switch between. Each loadout is its own file in
`%APPDATA%\EQLOverlay\loadouts\<name>.json` (easy to back up or share).

- Switch in-game with **Ctrl+Alt+P** or the **⇄** button on the toolbar — the
  active loadout name is shown on the overlay. Switching clears the previous
  combo's bars and loads the new set instantly.
- Create / rename / duplicate / delete loadouts at the top of the Trigger
  Manager (Ctrl+Alt+M). *Duplicate* is handy: build one combo, copy it, tweak.
- Your previous single trigger list was migrated into a loadout named **Default**.

## Managing triggers — the visual editor

Press **Ctrl+Alt+M** (or the *Manage* button on the toolbar) to open the Trigger
Manager. No JSON editing required:

- **Trigger list** on the left — Add / Duplicate / Delete / reorder.
- **Details form** on the right — name, category, duration, color (with preset
  swatches + live preview), start/end regex, and all the alert options.
- **Live log feed** at the bottom — lines stream in as they happen in-game.
  Click a line, then **→ Start pattern** / **→ End pattern** to auto-fill the
  regex from that exact line (this is the easiest way to make a trigger — just
  point at the line that scrolled by).
- **Test matches** — paste/select a line and see which triggers fire on it.
- **Settings tab** — log folder (Browse), sizes, warn/reminder timings, mute.

Click **Save** to write everything and apply it to the running overlay live.
(Saving from the GUI rewrites `config.json`, so inline comments are replaced —
the documented reference stays in `config.default.json`.)

## Alerts, cooldowns, and missing-buff reminders

Each trigger has optional alerts (set them in the Manager's *Alerts* section):

- **Sound / voice when a buff is about to drop** — set *"warn N seconds before it
  drops"* and give it a **spoken phrase** (Windows text-to-speech, e.g. "Clarity
  fading") and/or a **`.wav` file**.
- **Cooldown timers** — make a trigger in category `Cooldowns` whose start
  pattern is your *cast* line and whose duration is the recast time, then tick
  **"alert when it reaches 0"** with a phrase like "Gate ready".
- **Missing-buff reminder** — tick **"remind me to rebuff when this is missing"**.
  Once that buff has been up at least once, whenever it's *not* active you get a
  pulsing red **REBUFF** bar plus a spoken nudge every N seconds (interval in
  Settings). It won't nag before it's seen the buff once, so it stays quiet until
  it's relevant.

Mute everything anytime with **Ctrl+Alt+S**.

## How triggers work

Each entry in `triggers` says: *"when a log line matches `startPattern`, start a
countdown bar of `durationSeconds`; optionally clear it early when a line matches
`endPattern`."* The log doesn't report remaining time, so — exactly like GINA —
you supply each spell's known duration and the bar counts down from when the
trigger fired.

```json
{
  "id": "sow",
  "name": "Spirit of Wolf",
  "category": "Buffs",
  "startPattern": "You feel the spirit of wolf enter you\\.",
  "endPattern": "Your Spirit of Wolf spell has worn off\\.",
  "durationSeconds": 1800,
  "color": "#4FC3F7",
  "refreshOnRetrigger": true
}
```

- **`category`** — free text; bars are grouped under it (`Buffs`, `HoTs`, `DoTs`,
  `Cooldowns`, whatever you like).
- **`startPattern` / `endPattern`** — .NET regex, matched against the log line
  *after* the `[timestamp]` prefix is stripped. Escape regex specials (`.` →
  `\\.`).
- **Per-target bars** — add a named capture group `(?<target>...)` and each
  target gets its own bar, labelled with the captured name. Great for HoTs/DoTs
  you land on different people/mobs.
- **`refreshOnRetrigger`** — recasting resets the bar to full.
- Bars flash red under `overlay.warnSeconds` (default 6s) so you can refresh in
  time.

The shipped patterns use classic EverQuest wording as a starting point — **adjust
them to match your actual log lines.**

## Tuning to your log

The single thing that makes this accurate is matching your real log text. Enable
logging, do a few casts/heals, then look at the lines in your `eqlog_*.txt` (or
send them over) and copy the exact wording into `startPattern` / `endPattern`.
```
