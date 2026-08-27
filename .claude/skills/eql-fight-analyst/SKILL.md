---
name: eql-fight-analyst
description: The EQ Legends theorycrafter's playbook — which metrics a veteran player actually checks when analysing a fight or optimizing a build, the thresholds that make a number worth flagging, and how to present verdicts. Use this whenever working on EQL Assistant's fight analysis rules (BuildAnalysis), the fight report's tiles/timelines/tables, the DPS meter, session stats, or any feature that turns combat-log data into advice — including deciding WHAT new data the fight recorder should capture.
---

# EQL fight analyst

You are designing for a player who just wiped (or barely won) and wants to know
**what to change before the next pull**. Every metric exists to answer one of
five questions. A number that answers none of them is decoration.

## The five questions

### 1 · "Why did I take so much damage?"
- **School mix of incoming damage** — the top ability's share and school. If one
  school is ≥25% of the incoming, resist gear for that school is the single
  cheapest fix in the game. Say it as advice, not a statistic.
- **Debuff coverage vs damage timing** — slow (and malo/tash for casters' mobs)
  uptime matters less than WHERE the gaps were. Compare damage taken per second
  inside vs outside coverage; flag when the gap share exceeds its time share
  by 10+ points. The gaps are the story, always.
- **CC time** — seconds stunned/feared/mezzed, and the damage share that landed
  while held. CC'd time is time you couldn't answer back.
- **Stance** — defensive vs offensive trade; a fight spent 90% offensive with a
  near-death moment is a stance verdict waiting to be written.
- **Pet as shield** — how much incoming the pet ate; a pet death mid-fight is
  THE disaster moment (compare taken/s before vs after it).

### 2 · "Why was my dps low?"
- **DoT uptime** — the biggest lever for DoT classes. Anything under ~85% on a
  fight longer than 2 duration-cycles is refresh sloppiness. Also flag
  CLIPPING: re-casting a DoT well before it fades throws away paid-for ticks.
- **Resist stick rate per spell** — a spell sticking under ~70% over 3+ casts
  is malo/tash territory or a bad school matchup for THIS mob; name the mob.
- **Melee hit rate** — under ~60% suggests a weapon-skill or level gap; pair
  it with the mob's /con level when known.
- **Interrupted casts** — each one is a whole cast's damage lost; name the
  spell and the moment.
- **Buffs at the pull** — the honest "you fought without haste" comparison
  only works across kills of the same mob; never scold in isolation.
- **Proc economics** — procs/100 swings is the number that says whether a proc
  weapon out-damages a bigger-hitting one. Below the sample floor, stay quiet.

### 3 · "Is this change (item / AA / build) actually better?"
- Same-mob, same-stance comparisons are the gold standard — kill time and
  total dps trend across kills of one target, with the context chips
  (buffs, level, loadout) as the controls.
- One kill proves nothing; call a trend at 3+ comparable kills.

### 4 · "Will I survive the next one?"
- **Biggest hit** — the spike that defines your HP floor.
- **Burst windows** — peak 5s incoming vs your healing rate in that window;
  a sustained stretch where taken/s outruns healing/s is the danger window,
  and it deserves a timestamp.
- **Healer share** — who actually carried the healing (self-only solo,
  group otherwise).

### 5 · "Is the pet build working?" (pet classes only)
- Pet share of team damage, pet share of incoming (tanking transfer),
  pet deaths with before/after taken rates, heals spent on the pet.
- A petless fight shows NONE of this — hide, never zero-fill.

## Thresholds worth flagging (rules of thumb)

| signal | flag when | verdict flavor |
|---|---|---|
| one school's incoming share | ≥25% | "more X resist bites their biggest tool" |
| debuff gap damage share | > time share + 10pts | "the gaps bit" |
| DoT uptime | < ~85% on long fights | refresh discipline |
| spell stick rate | < ~70% over ≥3 casts | malo/tash / school matchup |
| melee hit rate | < ~60% | weapon skill or level gap |
| CC'd time | ≥ 10% of fight | "you were a passenger" |
| taken/s after pet death | > 1.3× before | the pet was the wall |

Below any sample floor: say nothing rather than a shaky number — a wrong
verdict costs trust that a missing one doesn't.

## Presentation doctrine

- **Verdicts, not statistics.** "42% of the damage you took was COLD — more
  Cold resist bites directly into their biggest tool" beats a school pie
  chart. Every claim cites the fight's own numbers in parentheses.
- **Spans for states, marks for events.** Uptimes (DoTs, debuffs, CC, stance)
  are bars with visible GAPS; hits and casts are ticks. The reader's eye
  should line a gap up with a spike without reading a single number.
- **Big numbers answer the five questions at a glance** — tiles are for the
  handful of numbers a player quotes to their group ("265 dps, she dropped
  the cloak"), not for everything measurable.
- **Offence and defence are different mindsets** — keep them separate; a
  player asks one question at a time.
- **Honesty over completeness.** The log has no mana, no HP values, no target
  health %, no positioning. Never synthesize these; when data predates a
  capture feature, say so ("recorded before school capture").

## What the log CAN still give (capture candidates)

When asked "what else could we record", weigh these against the questions
above: kill-shot ability (what actually landed the kill), time-to-percent
pacing via mob flee/low-health emotes if EQL prints them, group composition
from assist/heal lines, per-fight AA/exp gain lines, first-aggro source.
Capture at fight time what cannot be reconstructed later; presentation can
always catch up.
