using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Crowd control you PUT ON MOBS — charm (one pet) and mez (a list). Owner
/// request 14 Sep, mocked as the charm card and the mez panel. One engine,
/// two views. Your begin-cast anchors the spell's third-person landing
/// ("a greater ice bones moans." after "You begin casting Beguile Undead.",
/// observed 24 Aug) within the usual 15 s; "Your Beguile Undead spell has
/// worn off of a greater ice bones." ends it; your death and zoning censor
/// everything. A charm break is the loud one (badge + phrase); a mez breaks
/// on the first damage line naming the mob, and the row says who did it.
/// Spells whose landing text the library lacks (the necro's undead charms)
/// open an ASSUMED entry on the cast alone; the wear-off line later names
/// the mob, and the emote that followed the cast is LEARNED as that spell's
/// landing (cc-landings.json) — observed, never inferred — so the next cast
/// is solid. Live-only: never fed on catch-up or reparse.
/// </summary>
public sealed class CrowdControl
{
    public enum Kind { Charm, Mez }

    /// <summary>A CC spell: its landing suffix ("moans." — the line is
    /// "&lt;mob&gt; moans."; "" = unknown, assumed entries) and the library
    /// duration (0 = unknown, counts up).</summary>
    public sealed record Def(string Spell, Kind Kind, string LandingSuffix, double DurationSec);

    /// <summary>The charm card's state. Since = landing (or the cast, when
    /// assumed); Ceiling 0 = unknown, the bar counts up with no end.</summary>
    public sealed class CharmState
    {
        public string Pet = "";
        public string Spell = "";
        public DateTime Since;
        public double Ceiling;
        public bool Assumed;
        public DateTime? BrokeAt;
        public double PetDamage;
        public int PetHits;
        public double MaxHit;
        public double PetTaken; // what the pet took meanwhile
        public int PetKills;
        public DateTime? LastPetHit;
        public string Cast = "";  // the cast as printed, rank included
        public int MobLevel;
        public int YourLevel;
        public bool Recorded;   // its episode went to the CharmBook
    }

    /// <summary>A failed charm attempt — the amber "NO PET" state.</summary>
    public sealed record Attempt(string Spell, string Target, string How, DateTime At);

    public sealed class MezRow
    {
        public string Mob = "";
        public int Instance;            // 0 = the only one; 1, 2… when twins share a name
        public string Spell = "";
        public DateTime Since;
        public double Duration;         // 0 = unknown
        public bool Assumed;
        public DateTime? BrokeAt;
        public string BrokeBy = "";
        public double BrokeAmount;
        public bool Warned;
        public bool Refreshed;          // re-landed in place — the wear-off's span teaches nothing
        public string Label => Instance > 0 ? $"{Mob} {Instance:00}" : Mob;
    }

    public sealed record CharmView(string Pet, string Spell, double Held, double Ceiling, bool Assumed,
        bool Broke, double SinceBreak, double PetDamage, int PetKills, double? SinceLastHit,
        int PetHits = 0, double MaxHit = 0)
    {
        public double DamagePerHit => PetHits > 0 ? PetDamage / PetHits : 0;
        public double Dps => Held > 0 ? PetDamage / Held : 0;
        /// <summary>Seconds left on the ceiling clock; negative past it; null when the ceiling is unknown.</summary>
        public double? Left => Ceiling > 0 ? Ceiling - Held : null;
    }
    public sealed record MezView(string Label, string Spell, double Left, double Duration, double Held,
        bool Assumed, bool Broke, string BrokeBy, double BrokeAmount, double SinceBreak, bool Due, bool Overrun = false, bool Learned = false);
    public sealed record AttemptView(string Spell, string Target, string How, double Ago);

    public sealed record Snapshot(CharmView? Charm, AttemptView? Attempt, IReadOnlyList<MezView> Mez, IReadOnlyList<string> Loose)
    {
        public int Held => Mez.Count(m => !m.Broke);
        public int Broken => Mez.Count(m => m.Broke);
        /// <summary>The unbroken row that runs out first (known clocks only).</summary>
        public MezView? Next => Mez.Where(m => !m.Broke && m.Duration > 0).OrderBy(m => m.Left).FirstOrDefault();
        public bool Any => Charm is not null || Attempt is not null || Mez.Count > 0;
    }

    /// <summary>The charm broke: (pet). The loud one — badge and phrase.</summary>
    public event Action<string>? CharmBroke;
    /// <summary>A charm ended, however it ended — the ledger's episode.</summary>
    public event Action<CharmBook.Episode>? CharmEnded;
    /// <summary>A charm never landed: (mob, spell as cast, how, when, zone).</summary>
    public event Action<CharmBook.Attempt>? CharmAttemptFailed;
    /// <summary>The mob's /con level and your own level — set by the host (parser).</summary>
    public Func<string, int>? LevelLookup { get; set; }
    public Func<int>? OwnLevel { get; set; }
    private string _zone = "";
    private string _pendingCast = "";
    /// <summary>A mez broke early: (mob label, who hit it).</summary>
    public event Action<string, string>? MezBroke;
    /// <summary>A mez entered its last stretch: (mob label) — "re-mez now".</summary>
    public event Action<string>? MezDue;
    /// <summary>A same-name mob ACTED while every row of that name is held —
    /// an unmezzed add is loose: (mob name). The recast cue.</summary>
    public event Action<string>? MezLoose;
    /// <summary>Anything changed — the panels repaint at once.</summary>
    public event Action? Changed;

    /// <summary>Your own name(s) — the pet's hits on YOU are not pet damage.</summary>
    public Func<string, bool> IsSelf { get; set; } = n => n.Equals("You", StringComparison.OrdinalIgnoreCase);

    public const double CastWindowSec = 15;     // cast-anchor rule
    public const double AeSpreadSec = 2.5;      // an AE's landings print within this of the first
    public const double TwinWindowSec = 5;      // two landings on one name this close = twins, not a re-mez
    public const int SampleKeep = 5;
    public const double MinSampleSec = 3;
    public const double BrokeLingerSec = 60;    // the red charm card
    public const double MezBrokeLingerSec = 8;  // the red mez row
    public const double AttemptLingerSec = 30;
    public const double UnknownCullSec = 120;   // hygiene for count-up rows

    private readonly Dictionary<string, Def> _defs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _learned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _tails = new(StringComparer.OrdinalIgnoreCase);
    private (Def Def, DateTime At)? _pending;
    /// <summary>When the pending cast first landed: an AE mez lands on every
    /// mob within a breath of the first, so further landings are taken for
    /// <see cref="AeSpreadSec"/> after it and no longer (a stranger's mez on a
    /// different mob later in the window is not ours).</summary>
    private DateTime? _pendingLandedAt;
    /// <summary>Learned durations per base spell: the last few landing→wear-off
    /// spans of UNBROKEN rows; the estimate is the MAX (early breaks read
    /// short and must never drag the clock down). Ranks, focus effects and AAs
    /// stretch the real clock past the library figure (Mesmerization VI runs
    /// 24 s, VIII 31 s in the owner's log) — the log teaches it.</summary>
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MezRow> _mez = new();
    /// <summary>Names with a known LOOSE add — a mezzed mob never acts, so a
    /// same-name mob attacking or casting while every row of that name is
    /// held proves an unmezzed one (owner, 15 Sep: adds forcing a recast).
    /// While a name is loose: damage on the name is the loose one's, not a
    /// break; the next landing on the name APPENDS (the add got mezzed) and
    /// clears it; a death of the name is the loose one's.</summary>
    private readonly HashSet<string> _loose = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;

    public CharmState? Charm { get; private set; }
    public Attempt? LastAttempt { get; private set; }
    public IReadOnlyList<MezRow> MezRows => _mez;
    public IReadOnlyCollection<Def> Defs => _defs.Values;
    public int LearnedCount => _learned.Count;

    // Hand additions: the necro's undead charm line is not in the wiki scrape.
    // Only Beguile Undead's landing is OBSERVED (24 Aug: "a greater ice bones
    // moans."); the others open assumed until a log teaches them.
    private static readonly Def[] Additions =
    {
        new("Dominate Undead", Kind.Charm, "", 0),
        new("Beguile Undead", Kind.Charm, "moans.", 0),
        new("Cajole Undead", Kind.Charm, "", 0),
        new("Thrall of Bones", Kind.Charm, "", 0),
    };

    private static readonly HashSet<string> CharmOff = new(StringComparer.Ordinal)
        { "You are no longer charmed.", "Your charm spell has worn off." };
    private static readonly HashSet<string> MezOff = new(StringComparer.Ordinal)
        { "You are no longer mesmerized.", "You are no longer entranced.", "You are no longer enthralled." };

    private static readonly Regex BeginCastRx = new(@"^You begin (?:casting|singing) (?<s>.+?)\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WornOffOfRx = new(@"^Your (?<s>.+?) spell has worn off of (?<m>.+?)\.$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InterruptRx = new(@"^Your (?<s>.+?) spell is interrupted\.", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ResistRx = new(@"^(?:(?<m>.+?) resisted your (?<s>.+?)!|Your target resisted the (?<s2>.+?) spell\.)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SlainByRx = new(@"^(?<v>.+?) has been slain by (?<k>.+?)!", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex YouSlewRx = new(@"^You have slain (?<v>.+?)!", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);
    private static readonly string[] TimestampFormats = { "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy" };
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public CrowdControl(SpellLibrary? library, string? learnedPath)
    {
        _path = learnedPath;
        if (library is not null)
        {
            foreach (var s in library.Spells)
            {
                Kind? kind = null;
                if (CharmOff.Contains(s.WearsOff) || s.CastOnYou.StartsWith("You have been charmed", StringComparison.Ordinal))
                    kind = Kind.Charm;
                else if (MezOff.Contains(s.WearsOff) || s.CastOnYou.StartsWith("You are mesmerized", StringComparison.Ordinal)
                         || s.CastOnYou.StartsWith("You have been enthralled", StringComparison.Ordinal))
                    kind = Kind.Mez;
                if (kind is null) continue;
                string suffix = "";
                const string prefix = "Someone ";
                if (s.CastOnOther.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string t = s.CastOnOther[prefix.Length..].Trim();
                    if (t.Length >= 4 && !SpellLibrary.JunkMessage(t)) suffix = t;
                }
                _defs[s.Name] = new Def(s.Name, kind.Value, suffix, Math.Max(0, s.DurationSec));
            }
        }
        foreach (var a in Additions) _defs.TryAdd(a.Spell, a);
        LoadLearned();
    }

    public Def? Find(string spell)
    {
        if (_defs.TryGetValue(spell.Trim(), out var d)) return d;
        string baseName = SpellDurations.BaseName(spell);
        return _defs.TryGetValue(baseName, out d) ? d : null;
    }

    /// <summary>The landing suffix in force for a spell (library or learned; "" unknown).</summary>
    public string Landing(string spell) => Find(spell)?.LandingSuffix ?? "";

    // ---- the line feed ----------------------------------------------------------

    public void ProcessLine(string rawLine)
    {
        DateTime time = ExtractTimestamp(rawLine, out string body);
        if (body.Length == 0) return;

        // Censors: your death and zoning end everything you hold.
        if (body.StartsWith("You died.", StringComparison.Ordinal)
            || body.StartsWith("You have been slain", StringComparison.Ordinal)
            || body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            bool had = Charm is not null || _mez.Count > 0 || LastAttempt is not null;
            EndCharm(body.StartsWith("You have entered ", StringComparison.Ordinal) ? "zoned" : "you died", time);
            if (body.StartsWith("You have entered ", StringComparison.Ordinal) && body.EndsWith('.'))
                _zone = body["You have entered ".Length..^1];
            Charm = null; LastAttempt = null; _mez.Clear(); _pending = null; _loose.Clear();
            if (had) Changed?.Invoke();
            return;
        }

        // Your cast of a CC spell arms the landing detector.
        if (body.StartsWith("You begin ", StringComparison.Ordinal) && BeginCastRx.Match(body) is { Success: true } cast)
        {
            var def = Find(cast.Groups["s"].Value);
            if (def is null) return;
            _pending = (def, time);
            _pendingCast = cast.Groups["s"].Value.Trim();
            _pendingLandedAt = null;
            _tails[def.Spell] = new List<string>();
            if (def.LandingSuffix.Length == 0) OpenAssumed(def, time);
            return;
        }

        // A held name acting — casting, or swinging and missing (a hit comes
        // through NoteDamage) — is a loose add. Cheap gates: only while rows exist.
        if (_mez.Count > 0)
        {
            int ci = body.IndexOf(" begins casting ", StringComparison.Ordinal);
            if (ci > 0) NoteActing(body[..ci], time);
            else
            {
                int ti = body.IndexOf(" tries to ", StringComparison.Ordinal);
                if (ti > 0) NoteActing(body[..ti], time);
            }
        }

        if (_pending is { } pe && ((time - pe.At).TotalSeconds > CastWindowSec
                                   || (_pendingLandedAt is { } la && (time - la).TotalSeconds > AeSpreadSec)))
            _pending = null;

        // The lines after a cast — an unknown landing is learned from them
        // when the wear-off later names the mob.
        if (_pending is { } pt && _tails.TryGetValue(pt.Def.Spell, out var tail) && tail.Count < 80)
            tail.Add(body);

        if (body.StartsWith("Your ", StringComparison.Ordinal))
        {
            if (body.EndsWith("interrupted.", StringComparison.Ordinal) && InterruptRx.Match(body) is { Success: true } im)
            {
                var def = Find(im.Groups["s"].Value);
                if (def is null) return;
                Failed(def, "", "interrupted", time);
                return;
            }
            if (body.Contains(" spell has worn off of ", StringComparison.Ordinal) && WornOffOfRx.Match(body) is { Success: true } wo)
            {
                var def = Find(wo.Groups["s"].Value);
                if (def is null) return;
                WornOff(def, wo.Groups["m"].Value.Trim(), time);
                return;
            }
            if (body.StartsWith("Your spell fizzles", StringComparison.Ordinal) && _pending is { } pf)
            {
                Failed(pf.Def, "", "fizzled", time);
                return;
            }
        }

        if (body.Contains("resisted", StringComparison.Ordinal) && ResistRx.Match(body) is { Success: true } rm)
        {
            string spell = rm.Groups["s"].Success ? rm.Groups["s"].Value : rm.Groups["s2"].Value;
            var def = Find(spell);
            if (def is null) return;
            Failed(def, rm.Groups["m"].Success ? rm.Groups["m"].Value.Trim() : "", "resisted", time);
            return;
        }

        // The landing: "<mob> <suffix>" within the cast window.
        if (_pending is { } p && p.Def.LandingSuffix.Length > 0
            && body.Length > p.Def.LandingSuffix.Length + 1
            && body.EndsWith(p.Def.LandingSuffix, StringComparison.Ordinal))
        {
            string mob = body[..^p.Def.LandingSuffix.Length].Trim();
            Start(p.Def, mob, time, assumed: false);
            // A charm takes one pet — done. A mez may be an AE: keep the cast
            // armed for the spread so every "<mob> has been mesmerized." of
            // this cast opens its own row.
            if (p.Def.Kind == Kind.Charm) _pending = null;
            else _pendingLandedAt ??= time;
            return;
        }

        // Deaths: a mezzed mob's row goes; the pet's kills count, the pet's death clears the card.
        if (body.Contains(" has been slain by ", StringComparison.Ordinal) && SlainByRx.Match(body) is { Success: true } sb)
        {
            OnDeath(sb.Groups["v"].Value.Trim(), sb.Groups["k"].Value.Trim(), time);
            return;
        }
        if (body.StartsWith("You have slain ", StringComparison.Ordinal) && YouSlewRx.Match(body) is { Success: true } ys)
        {
            OnDeath(ys.Groups["v"].Value.Trim(), "You", time);
        }
    }

    /// <summary>Every damage line, from the parser (attacker, target, amount):
    /// the pet's hits feed the card, a hit on a mezzed mob breaks its row.</summary>
    public void NoteDamage(string attacker, string target, double amount, DateTime time, bool dot = false)
    {
        if (Charm is { BrokeAt: null } c && Same(attacker, c.Pet) && !IsSelf(target))
        {
            c.PetDamage += amount;
            c.PetHits++;
            if (amount > c.MaxHit) c.MaxHit = amount;
            c.LastPetHit = time;
        }
        else if (Charm is { BrokeAt: null } ct && Same(target, ct.Pet) && !Same(attacker, ct.Pet))
            ct.PetTaken += amount; // a same-name mob hitting the pet reads as the pet taking it — the honest half
        // A DoT keeps ticking on a mezzed mob's behalf — not an act. A melee hit
        // or a nuke from a held name is.
        if (!dot && _mez.Count > 0) NoteActing(attacker, time);
        // Damage on a name with a loose add is the add being fought, not a break.
        if (_loose.Contains(target.Trim())) return;
        var row = _mez.Where(r => r.BrokeAt is null && Same(r.Mob, target)).OrderBy(r => r.Since).FirstOrDefault();
        if (row is not null)
        {
            row.BrokeAt = time;
            row.BrokeBy = attacker;
            row.BrokeAmount = amount;
            MezBroke?.Invoke(row.Label, attacker);
            Changed?.Invoke();
        }
    }

    // ---- state changes -----------------------------------------------------------

    private void NoteActing(string name, DateTime time)
    {
        name = name.Trim();
        if (name.Length == 0 || IsSelf(name)) return;
        var rows = _mez.Where(r => Same(r.Mob, name)).ToList();
        if (rows.Count == 0 || rows.Any(r => r.BrokeAt is not null)) return; // a broken one may well be the actor
        if (!_loose.Add(rows[0].Mob)) return;
        Log.Info($"[cc] {rows[0].Mob} acted while every {rows[0].Mob} row is held — a loose add");
        MezLoose?.Invoke(rows[0].Mob);
        Changed?.Invoke();
    }

    /// <summary>Names with a known loose add (selftest / header).</summary>
    public IReadOnlyCollection<string> LooseNames => _loose;

    private void OpenAssumed(Def def, DateTime time) => Start(def, "your target", time, assumed: true);

    private void Start(Def def, string mob, DateTime time, bool assumed)
    {
        if (def.Kind == Kind.Charm)
        {
            EndCharm("replaced", time); // a new charm while one holds: the old one's episode closes
            Charm = new CharmState
            {
                Pet = mob, Spell = def.Spell, Since = time, Ceiling = DurationFor(def), Assumed = assumed,
                Cast = _pendingCast.Length > 0 ? _pendingCast : def.Spell,
                MobLevel = LevelLookup?.Invoke(mob) ?? 0, YourLevel = OwnLevel?.Invoke() ?? 0,
            };
            LastAttempt = null;
        }
        else
        {
            // A re-mez is the common case, twins the rare one: a landing on a
            // name refreshes that name's oldest row — unless every row of the
            // name landed within the last few seconds (this very cast, an AE
            // hitting two mobs of one name), which makes it a twin with the
            // next number. Broken and assumed rows are always taken over first.
            double duration = DurationFor(def);
            var same = _mez.Where(r => Same(r.Mob, mob) || (assumed && r.Assumed)).OrderBy(r => r.Since).ToList();
            bool wasLoose = _loose.Remove(mob.Trim()); // the add got mezzed: its own row, never a refresh
            var refresh = same.FirstOrDefault(r => r.BrokeAt is not null || r.Assumed)
                ?? (wasLoose || (same.Count > 0 && same.All(r => (time - r.Since).TotalSeconds < TwinWindowSec)) ? null : same.FirstOrDefault());
            if (refresh is not null)
            {
                refresh.Mob = mob; refresh.Spell = def.Spell; refresh.Since = time; refresh.Duration = duration;
                refresh.Assumed = assumed; refresh.BrokeAt = null; refresh.BrokeBy = ""; refresh.BrokeAmount = 0; refresh.Warned = false;
                refresh.Refreshed = true; // its old span is no sample
            }
            else
            {
                if (same.Count > 0)
                {
                    foreach (var r in same.Where(r => r.Instance == 0)) r.Instance = 1;
                    int next = same.Max(r => r.Instance) + 1;
                    _mez.Add(new MezRow { Mob = mob, Instance = next, Spell = def.Spell, Since = time, Duration = duration, Assumed = assumed });
                }
                else _mez.Add(new MezRow { Mob = mob, Spell = def.Spell, Since = time, Duration = duration, Assumed = assumed });
            }
        }
        Changed?.Invoke();
    }

    private void Failed(Def def, string target, string how, DateTime time)
    {
        bool pendingWas = _pending is { } p && p.Def.Spell.Equals(def.Spell, StringComparison.OrdinalIgnoreCase);
        // A charm's failure is the cast's failure. A mez may be an AE: one mob
        // resisting says nothing about the others, the cast stays armed.
        if (def.Kind == Kind.Charm || how != "resisted") _pending = null;
        if (def.Kind == Kind.Charm)
        {
            // An assumed card opened on this very cast was a guess — it's off.
            if (Charm is { Assumed: true } c && pendingWas && (time - c.Since).TotalSeconds <= CastWindowSec) Charm = null;
            if (Charm is null) LastAttempt = new Attempt(def.Spell, target, how, time);
            if (target.Length > 0)
                CharmAttemptFailed?.Invoke(new CharmBook.Attempt(target, _pendingCast.Length > 0 ? _pendingCast : def.Spell, how, time, _zone));
        }
        else
        {
            var assumed = _mez.LastOrDefault(r => r.Assumed && pendingWas && (time - r.Since).TotalSeconds <= CastWindowSec);
            if (assumed is not null) _mez.Remove(assumed);
        }
        Changed?.Invoke();
    }

    private void WornOff(Def def, string mob, DateTime time)
    {
        if (def.Kind == Kind.Charm)
        {
            if (Charm is null) return;
            if (Charm.Assumed) Charm.Pet = mob; // the wear-off names the pet we only assumed
            if (Charm.BrokeAt is null)
            {
                Charm.BrokeAt = time;
                CharmBroke?.Invoke(Charm.Pet);
                EndCharm("broke", time);
            }
        }
        else
        {
            var row = _mez.Where(r => r.BrokeAt is null && (Same(r.Mob, mob) || r.Assumed)).OrderBy(r => r.Since).FirstOrDefault();
            if (row is null) return;
            if (!row.Refreshed && !row.Assumed) LearnDuration(def, (time - row.Since).TotalSeconds);
            _mez.Remove(row);
        }
        Learn(def, mob);
        Changed?.Invoke();
    }

    /// <summary>The clock a new row runs on: the learned estimate (MAX of the
    /// last <see cref="SampleKeep"/> unbroken spans) over the library figure.</summary>
    public double DurationFor(Def def) =>
        _samples.TryGetValue(def.Spell, out var s) && s.Count > 0 ? Math.Max(s.Max(), def.DurationSec) : def.DurationSec;

    public double? LearnedDuration(string spell) =>
        Find(spell) is { } d && _samples.TryGetValue(d.Spell, out var s) && s.Count > 0 ? s.Max() : null;

    private void LearnDuration(Def def, double seconds)
    {
        if (seconds < MinSampleSec) return;
        if (!_samples.TryGetValue(def.Spell, out var list)) _samples[def.Spell] = list = new List<double>();
        list.Add(Math.Round(seconds, 1));
        while (list.Count > SampleKeep) list.RemoveAt(0);
        Log.Info($"[cc] {def.Spell} ran {seconds:0} s to its wear-off — clock now {DurationFor(def):0} s");
        SaveLearned();
    }

    private void OnDeath(string victim, string killer, DateTime time)
    {
        bool changed = false;
        if (Charm is { } c && Same(victim, c.Pet))
        {
            // Same-name pet and mob (24 Aug: a greater ice bones slew a greater
            // ice bones): the pet is the KILLER when the names match; you never
            // kill your own pet; anyone else killing "the pet's name" reads as
            // the pet dying.
            if (Same(killer, c.Pet) && c.BrokeAt is null) { c.PetKills++; changed = true; }
            else if (killer == "You" || IsSelf(killer)) { /* the mob died — the pet lives */ }
            else { EndCharm("died", time); Charm = null; changed = true; }
        }
        else if (Charm is { BrokeAt: null } c2 && Same(killer, c2.Pet)) { c2.PetKills++; changed = true; }

        if (_loose.Remove(victim.Trim())) { changed = true; } // the loose add died — the held rows stand
        else
        {
            var row = _mez.Where(r => Same(r.Mob, victim)).OrderBy(r => r.Since).FirstOrDefault();
            if (row is not null) { _mez.Remove(row); changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Close the running charm's episode (once) and hand it to the
    /// ledger: how long it held, how it ended, what the pet did. An assumed
    /// pet never named ("your target") is no episode.</summary>
    private void EndCharm(string how, DateTime time)
    {
        if (Charm is not { } c || c.Recorded) return;
        c.Recorded = true;
        if (c.Pet == "your target") return;
        DateTime end = c.BrokeAt ?? time;
        int level = c.MobLevel > 0 ? c.MobLevel : LevelLookup?.Invoke(c.Pet) ?? 0; // a /con mid-charm counts
        CharmEnded?.Invoke(new CharmBook.Episode(c.Pet, c.Spell, _zone, c.Since, end, how,
            c.PetDamage, c.PetHits, c.MaxHit, c.PetKills, level, c.Cast, c.PetTaken, c.YourLevel));
    }

    /// <summary>An unknown landing learns itself: among the lines that followed
    /// the cast, the one that starts with the mob's name and reads as an emote
    /// (no numbers, no combat verbs) is the landing. Exactly one candidate or
    /// nothing — never a guess.</summary>
    private void Learn(Def def, string mob)
    {
        if (def.LandingSuffix.Length > 0 || !_tails.TryGetValue(def.Spell, out var tail)) return;
        var candidates = tail
            .Where(l => l.Length > mob.Length + 2 && l.StartsWith(mob, StringComparison.OrdinalIgnoreCase)
                        && l[mob.Length] == ' ' && l.EndsWith('.') && !l.Any(char.IsDigit)
                        && !l.Contains(" tries to ", StringComparison.Ordinal)
                        && !l.Contains(" begins casting ", StringComparison.Ordinal)
                        && !l.Contains(" has been ", StringComparison.Ordinal)
                        && !l.Contains(" resisted ", StringComparison.Ordinal))
            .Select(l => l[mob.Length..].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count != 1) return;
        string suffix = candidates[0];
        _learned[def.Spell] = suffix;
        _defs[def.Spell] = def with { LandingSuffix = suffix };
        Log.Info($"[cc] learned the landing of {def.Spell}: \"<mob> {suffix}\"");
        SaveLearned();
    }

    // ---- the views ------------------------------------------------------------------

    public static double LastStretch(double duration) => Math.Max(6, duration * 0.2);

    public Snapshot Take(DateTime now)
    {
        CharmView? cv = null;
        if (Charm is { } c)
        {
            if (c.BrokeAt is { } b && (now - b).TotalSeconds > BrokeLingerSec) Charm = null;
            else cv = new CharmView(c.Pet, c.Spell, Math.Max(0, ((c.BrokeAt ?? now) - c.Since).TotalSeconds), c.Ceiling, c.Assumed,
                c.BrokeAt is not null, c.BrokeAt is { } bb ? (now - bb).TotalSeconds : 0,
                c.PetDamage, c.PetKills, c.LastPetHit is { } lh ? (now - lh).TotalSeconds : null, c.PetHits, c.MaxHit);
        }
        AttemptView? av = null;
        if (Charm is null && LastAttempt is { } a)
        {
            if ((now - a.At).TotalSeconds > AttemptLingerSec) LastAttempt = null;
            else av = new AttemptView(a.Spell, a.Target, a.How, (now - a.At).TotalSeconds);
        }

        var due = new List<string>();
        // Past its clock with no wear-off seen, a row OVERRUNS (grey, counting
        // "+Ns") instead of vanishing — the clock was an estimate, the wear-off
        // line is the truth and teaches the next one. Hygiene: max(90 s, 3×).
        _mez.RemoveAll(r =>
            (r.BrokeAt is { } b && (now - b).TotalSeconds > MezBrokeLingerSec)
            || (r.BrokeAt is null && r.Duration > 0 && (now - r.Since).TotalSeconds > Math.Max(90, r.Duration * 3))
            || (r.BrokeAt is null && r.Duration <= 0 && (now - r.Since).TotalSeconds > UnknownCullSec));
        var views = new List<MezView>();
        foreach (var r in _mez)
        {
            double held = Math.Max(0, (now - r.Since).TotalSeconds);
            double left = r.Duration > 0 ? r.Duration - held : 0;
            bool overrun = r.BrokeAt is null && r.Duration > 0 && left < 0;
            bool isDue = r.BrokeAt is null && r.Duration > 0 && !overrun && left <= LastStretch(r.Duration);
            if (isDue && !r.Warned) { r.Warned = true; due.Add(r.Label); }
            bool learned = _samples.TryGetValue(r.Spell, out var sm) && sm.Count > 0;
            views.Add(new MezView(r.Label, r.Spell, left, r.Duration, held, r.Assumed, r.BrokeAt is not null,
                r.BrokeBy, r.BrokeAmount, r.BrokeAt is { } bb ? (now - bb).TotalSeconds : 0, isDue, overrun, learned));
        }
        var ordered = views.OrderByDescending(v => v.Broke)
            .ThenBy(v => v.Duration > 0 ? v.Left : double.MaxValue)
            .ThenBy(v => v.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var d in due) MezDue?.Invoke(d);
        _loose.RemoveWhere(n => !_mez.Any(r => Same(r.Mob, n))); // no held row left — nothing to be loose from
        return new Snapshot(cv, av, ordered, _loose.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public void Clear()
    {
        Charm = null; LastAttempt = null; _mez.Clear(); _pending = null; _loose.Clear();
        Changed?.Invoke();
    }

    private bool _demo;

    /// <summary>Ctrl+Alt+T's demo is over: drop it (a real charm or mez that
    /// arrived meanwhile has already replaced the demo rows).</summary>
    public void ClearDemo()
    {
        if (!_demo) return;
        _demo = false;
        Clear();
    }

    /// <summary>Demo state for the render targets, the selftest and Ctrl+Alt+T.
    /// The demo charm is marked recorded so it never becomes a ledger episode.</summary>
    public void SeedDemo(DateTime now, bool broke)
    {
        _demo = true;
        Charm = new CharmState { Pet = "a greater ice bones", Spell = "Beguile Undead", Since = now.AddSeconds(-41), Ceiling = 480, PetDamage = 1240, PetHits = 9, MaxHit = 212, PetKills = 2, LastPetHit = now.AddSeconds(-3), Recorded = true };
        if (broke) { Charm.Since = now.AddSeconds(-10); Charm.BrokeAt = now.AddSeconds(-4); }
        _mez.Clear();
        _mez.Add(new MezRow { Mob = "a greater ice bones", Instance = 1, Spell = "Mesmerization", Since = now.AddSeconds(-20), Duration = 24 });
        _mez.Add(new MezRow { Mob = "a greater ice bones", Instance = 2, Spell = "Mesmerization", Since = now.AddSeconds(-9), Duration = 24, BrokeAt = broke ? now.AddSeconds(-2) : null, BrokeBy = "Garn", BrokeAmount = 58 });
        _mez.Add(new MezRow { Mob = "an ice bones", Spell = "Mesmerization", Since = now.AddSeconds(-3), Duration = 24 });
        _mez.Add(new MezRow { Mob = "your target", Spell = "Numbing Cold", Since = now.AddSeconds(-6), Duration = 24, Assumed = true });
        Changed?.Invoke();
    }

    // ---- helpers -----------------------------------------------------------------------

    private static bool Same(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    private sealed class LearnedFile
    {
        public Dictionary<string, string> Landings { get; set; } = new();
        public Dictionary<string, List<double>> Durations { get; set; } = new();
    }

    private void LoadLearned()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            string json = File.ReadAllText(_path);
            LearnedFile? f = null;
            try { f = JsonSerializer.Deserialize<LearnedFile>(json); } catch { /* the first format below */ }
            if (f is null || (f.Landings.Count == 0 && f.Durations.Count == 0))
            {
                // v2.57 wrote a bare spell → suffix map.
                var bare = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                f = new LearnedFile { Landings = bare ?? new() };
            }
            foreach (var (spell, suffix) in f.Landings)
            {
                if (string.IsNullOrWhiteSpace(suffix)) continue;
                _learned[spell] = suffix;
                if (_defs.TryGetValue(spell, out var def) && def.LandingSuffix.Length == 0)
                    _defs[spell] = def with { LandingSuffix = suffix };
            }
            foreach (var (spell, samples) in f.Durations)
                if (samples.Count > 0) _samples[spell] = samples.TakeLast(SampleKeep).ToList();
        }
        catch (Exception ex) { Log.Warn("cc-landings.json unreadable: " + ex.Message); }
    }

    private void SaveLearned()
    {
        if (_path is null) return;
        try
        {
            var f = new LearnedFile
            {
                Landings = new Dictionary<string, string>(_learned, StringComparer.OrdinalIgnoreCase),
                Durations = _samples.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(f, JsonOpts));
        }
        catch (Exception ex) { Log.Warn("cc-landings.json not saved: " + ex.Message); }
    }

    private static DateTime ExtractTimestamp(string rawLine, out string body)
    {
        var m = TimestampPrefix.Match(rawLine);
        body = m.Success ? rawLine[m.Length..] : rawLine;
        if (m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, TimestampFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t))
            return t;
        return DateTime.Now;
    }
}
