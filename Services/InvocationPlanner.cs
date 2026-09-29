using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Which invocation a stretch of YOUR fighting wanted (owner, 29 Sep: "base it
/// on the last 30/60/90 minutes, or record fights, and show what the optimal
/// invocation would have been"). The stretch is read from the log itself —
/// every cast that landed at its rank, the fights, the damage, the resists,
/// the invocation that was up — then replayed under each invocation your
/// combo can recite, by eqlwiki's Stances &amp; Invocations rules. Mana never
/// prints in the log: costs come from the wiki, your base regen from the
/// character sheet — and the log proves a floor under it (you never ran dry,
/// so the regen was at least what paid for your longest fight).
/// </summary>
public static class InvocationPlanner
{
    public const string Recovery = "Recovery", ArcaneMastery = "Arcane Mastery", Empower = "Empower",
        Inversion = "Inversion", OverChannel = "Over Channel";
    public static readonly string[] Names = { Recovery, ArcaneMastery, Empower, Inversion, OverChannel };

    private static readonly string[] IntClasses = { "ENC", "MAG", "NEC", "WIZ" };
    private static readonly string[] WisClasses = { "CLR", "DRU", "SHM" };
    private static readonly string[] CasterAll = { "BRD", "BST", "PAL", "CLR", "DRU", "ENC", "MAG", "NEC", "RNG", "SHD", "SHM", "WIZ" };
    private static readonly Dictionary<string, string[]> Who = new()
    {
        [Recovery] = CasterAll,
        [ArcaneMastery] = new[] { "ENC", "MAG", "NEC", "SHD", "WIZ" },
        [Empower] = new[] { "CLR", "DRU", "ENC", "MAG", "NEC", "SHM", "WIZ" },
        [Inversion] = CasterAll,
        [OverChannel] = CasterAll,
    };

    /// <summary>The invocations this combo can recite (among the five that trade mana, speed and damage).</summary>
    public static List<string> Available(IReadOnlyCollection<string> combo) =>
        Names.Where(n => combo.Any(c => Who[n].Contains(c, StringComparer.OrdinalIgnoreCase))).ToList();

    /// <summary>The combo-dependent depths: extra pure INT classes deepen Arcane
    /// Mastery, extra pure casters deepen Empower and Over Channel.</summary>
    public sealed record Rules(int IntCount, int PureCount, double AmSpeed, double AmSave, double EmpDamage, double EmpHeal, int OcAdjust);

    public static Rules RulesFor(IReadOnlyCollection<string> combo)
    {
        int nInt = combo.Count(c => IntClasses.Contains(c, StringComparer.OrdinalIgnoreCase));
        int nPure = nInt + combo.Count(c => WisClasses.Contains(c, StringComparer.OrdinalIgnoreCase));
        int xi = Math.Max(0, nInt - 1), xp = Math.Max(0, nPure - 1);
        return new Rules(nInt, nPure, 0.20 + 0.10 * xi, 0.10 + 0.05 * xi, 0.20 + 0.10 * xp, 0.10 + 0.05 * xp, 150 + 15 * nPure);
    }

    public static string RuleText(string name, Rules r) => name switch
    {
        Recovery => "regen ×2 in fights · −5% mana",
        ArcaneMastery => $"casts {Pct(r.AmSpeed)} faster · detrimental −{Pct(r.AmSave)} mana",
        Empower => $"damage +{Pct(r.EmpDamage)} for +20% mana · heals +{Pct(r.EmpHeal)} for +10%",
        Inversion => "−20% mana (paid as half in endurance) · no interrupts",
        OverChannel => $"resist −{r.OcAdjust} · 10% of the mana as endurance",
        _ => "",
    };

    /// <summary>"20%" in every culture (P0 puts a space — and a Danish one — in).</summary>
    public static string Pct(double v) => (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    // ---- reading a stretch --------------------------------------------------------

    public sealed record Cast(DateTime At, string Spell, int Rank, bool Landed, string Invocation);

    public sealed class Stretch
    {
        public DateTime From { get; init; }
        public DateTime To { get; init; }
        public List<Cast> Casts { get; } = new();
        public List<(DateTime Start, DateTime End)> Fights { get; } = new();
        public List<(DateTime At, int Amount)> Damage { get; } = new();
        public List<DateTime> OutOfMana { get; } = new();
        /// <summary>Your detrimental casts and the resists they drew, by the invocation that was up.</summary>
        public Dictionary<string, (int Casts, int Resists)> ResistsBy { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Lines { get; set; }
    }

    private static readonly Regex TsRx = new(@"^\[(?<t>\w{3} \w{3} +\d{1,2} \d\d:\d\d:\d\d \d{4})\] (?<b>.*)$", RegexOptions.Compiled);
    private static readonly Regex InvRx = new(@"^You begin reciting the (?<i>.+?) invocation\.$", RegexOptions.Compiled);
    private static readonly Regex CastRx = new(@"^You begin casting (?<s>.+?)\.$", RegexOptions.Compiled);
    private static readonly Regex IntrRx = new(@"^Your (?<s>.+?) spell is interrupted\.$", RegexOptions.Compiled);
    private static readonly Regex HitRx = new(@"^You hit .+? for (?<n>\d+) points of .+? damage by ", RegexOptions.Compiled);
    private static readonly Regex TickRx = new(@"^.+? has taken (?<n>\d+) damage from your ", RegexOptions.Compiled);
    private static readonly Regex ResistRx = new(@"^.+? resisted your (?<s>.+?)!$", RegexOptions.Compiled);
    private const string OomLine = "Insufficient Mana to cast this spell!";
    public const double FightGapSec = 15;

    /// <summary>The log's invocation names → the planner's ("overchannel" → Over Channel).</summary>
    public static string Canon(string logName) => logName.Trim().ToLowerInvariant() switch
    {
        "recovery" => Recovery, "arcane mastery" => ArcaneMastery, "empowering" or "empower" => Empower,
        "inversion" => Inversion, "overchannel" or "over channel" => OverChannel,
        var x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x),
    };

    public static DateTime? LineTime(string line)
    {
        int close = line.IndexOf(']');
        if (line.Length < 20 || line[0] != '[' || close < 5) return null;
        return DateTime.TryParseExact(line[1..close], "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces, out var t) ? t : null;
    }

    /// <summary>Read a stretch out of log lines (the invocation up at its start is
    /// taken from the last switch before it, when the lines reach back that far).</summary>
    public static Stretch Parse(IEnumerable<string> lines, DateTime from, DateTime to, SpellLibrary? library = null)
    {
        var st = new Stretch { From = from, To = to };
        string inv = "";
        var marks = new List<DateTime>();
        foreach (var line in lines)
        {
            var m = TsRx.Match(line);
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups["t"].Value, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var t)) continue;
            string b = m.Groups["b"].Value;
            Match x;
            if (b.StartsWith("You begin reciting ", StringComparison.Ordinal) && (x = InvRx.Match(b)).Success) { inv = Canon(x.Groups["i"].Value); continue; }
            if (t < from || t > to) continue;
            st.Lines++;
            if (b.StartsWith("You begin casting ", StringComparison.Ordinal) && (x = CastRx.Match(b)).Success)
            {
                string s = x.Groups["s"].Value;
                st.Casts.Add(new Cast(t, SpellDurations.BaseName(s), SpellYield.RankOf(s), true, inv));
                if (library?.FindByBaseName(SpellDurations.BaseName(s)) is { Bucket: "Debuff" })
                {
                    var r = st.ResistsBy.GetValueOrDefault(inv);
                    st.ResistsBy[inv] = (r.Casts + 1, r.Resists);
                }
            }
            else if (b.EndsWith(" spell is interrupted.", StringComparison.Ordinal) && (x = IntrRx.Match(b)).Success)
            {
                string key = SpellDurations.BaseKey(x.Groups["s"].Value);
                for (int i = st.Casts.Count - 1; i >= 0; i--)
                    if (st.Casts[i].Landed && SpellDurations.BaseKey(st.Casts[i].Spell) == key) { st.Casts[i] = st.Casts[i] with { Landed = false }; break; }
            }
            else if (b.StartsWith("You hit ", StringComparison.Ordinal) && (x = HitRx.Match(b)).Success)
            { st.Damage.Add((t, int.Parse(x.Groups["n"].Value))); marks.Add(t); }
            else if (b.Contains(" damage from your ", StringComparison.Ordinal) && (x = TickRx.Match(b)).Success)
            { st.Damage.Add((t, int.Parse(x.Groups["n"].Value))); marks.Add(t); }
            else if (b.EndsWith("!", StringComparison.Ordinal) && b.Contains(" resisted your ", StringComparison.Ordinal) && ResistRx.IsMatch(b))
            {
                var r = st.ResistsBy.GetValueOrDefault(inv);
                st.ResistsBy[inv] = (r.Casts, r.Resists + 1);
            }
            else if (b == OomLine) st.OutOfMana.Add(t);
            else if (b.Contains(" YOU for ", StringComparison.Ordinal) || b.Contains(" YOU, but ", StringComparison.Ordinal)
                     || b.StartsWith("You slash ", StringComparison.Ordinal) || b.StartsWith("You try to ", StringComparison.Ordinal))
                marks.Add(t);
        }
        // Fights: a damage line keeps you "in combat" 6 s; 15 s of quiet ends the fight.
        marks.Sort();
        foreach (var mk in marks)
        {
            if (st.Fights.Count > 0 && (mk - st.Fights[^1].End).TotalSeconds <= FightGapSec)
                st.Fights[^1] = (st.Fights[^1].Start, Max(st.Fights[^1].End, mk.AddSeconds(6)));
            else st.Fights.Add((mk, mk.AddSeconds(6)));
        }
        return st;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    // ---- the replay ------------------------------------------------------------------

    public sealed record Ledger(string Name, double Spent, double Regen, double VsRecovery, double Damage, int Won, double CastSec);
    public sealed record FightPick(DateTime Start, DateTime End, int Casts, double ManaNeeded, string Pick, bool Affordable, double Damage)
    {
        public double Seconds => (End - Start).TotalSeconds;
    }
    public sealed record Result(List<Ledger> Ledgers, List<FightPick> Fights, int ProvenRegen, int RegenUsed,
        double CombatSec, int Casts, int Unpriced, string ResistNote, double OcFactor, Rules Rules, List<string> Available)
    {
        public Ledger? Best => Ledgers.OrderByDescending(l => l.Won).ThenByDescending(l => l.VsRecovery).FirstOrDefault();
    }

    private sealed record Priced(Cast C, double Mana, bool Detrimental, string Kind, double CastSec);

    /// <summary>One cast's mana under an invocation.</summary>
    private static double Cost(string inv, Priced p, Rules r) => inv switch
    {
        Recovery => p.Mana * 0.95,
        ArcaneMastery => p.Detrimental ? p.Mana * (1 - r.AmSave) : p.Mana,
        Empower => p.Kind == "dmg" ? p.Mana * 1.2 : p.Kind == "heal" ? p.Mana * 1.1 : p.Mana,
        Inversion => p.Mana * 0.8,
        _ => p.Mana,
    };

    public const int MinResistSample = 20;

    /// <summary>Replay a stretch under every invocation the combo recites. A fight's
    /// pick is the most spell damage it could have paid for from a full pool,
    /// else the one that spends least. Regen used = the larger of what you
    /// typed and what the log proves.</summary>
    public static Result Replay(Stretch st, SpellLibrary library, IReadOnlyCollection<string> combo, int typedRegen, int pool)
    {
        var rules = RulesFor(combo);
        var names = Available(combo);
        pool = Math.Max(1, pool);
        var priced = new List<Priced>();
        int unpriced = 0;
        foreach (var c in st.Casts.Where(c => c.Landed))
        {
            var s = library.FindByBaseName(c.Spell);
            if (s is null || s.Mana <= 0) { unpriced++; continue; }
            string kind = SpellEfficiency.IsDamage(s.Effect) ? "dmg" : SpellEfficiency.IsHealing(s.Effect) ? "heal" : "other";
            priced.Add(new Priced(c, s.Mana * (1 - 0.02 * c.Rank), s.Bucket == "Debuff", kind, s.CastSec * (1 - 0.02 * c.Rank)));
        }

        // Over Channel from your own resists in this stretch — or no change assumed.
        var oc = st.ResistsBy.GetValueOrDefault(OverChannel);
        var rest = st.ResistsBy.Where(kv => kv.Key != OverChannel).Aggregate((Casts: 0, Resists: 0), (a, kv) => (a.Casts + kv.Value.Casts, a.Resists + kv.Value.Resists));
        double ocFactor = 1; string resistNote;
        if (oc.Casts >= MinResistSample && rest.Casts >= MinResistSample)
        {
            double b = (double)rest.Resists / rest.Casts, o = (double)oc.Resists / oc.Casts;
            ocFactor = Math.Max(0.5, (1 - o) / Math.Max(0.01, 1 - b));
            resistNote = $"your resists here: {(b * 100).ToString("0.0", CultureInfo.InvariantCulture)}% → {(o * 100).ToString("0.0", CultureInfo.InvariantCulture)}% in Over Channel";
        }
        else resistNote = "too few Over Channel casts here to measure its resists — scored as no change";

        // The floor the log proves: in fights you didn't run dry in, what you spent
        // (as the invocation that was up priced it) past a full pool came from regen.
        int proven = 0;
        foreach (var f in st.Fights)
        {
            if (st.OutOfMana.Any(o => o >= f.Start && o <= f.End)) continue;
            double len = Math.Max(6, (f.End - f.Start).TotalSeconds);
            var fc = priced.Where(p => p.C.At >= f.Start.AddSeconds(-3) && p.C.At <= f.End).ToList();
            double spend = fc.Sum(p => names.Contains(p.C.Invocation) ? Cost(p.C.Invocation, p, rules) : p.Mana);
            double doubled = fc.Count > 0 && fc.Count(p => p.C.Invocation == Recovery) * 2 >= fc.Count ? 2 : 1;
            proven = Math.Max(proven, (int)Math.Ceiling((spend - pool) / (doubled * len / 6)));
        }
        int regen = Math.Max(typedRegen, proven);

        double RegenMult(string n) => n == Recovery ? 2 : 1;
        double DmgMult(string n) => n == Empower ? 1 + rules.EmpDamage : n == OverChannel ? ocFactor : 1;

        var won = names.ToDictionary(n => n, _ => 0);
        var regenBy = names.ToDictionary(n => n, _ => 0.0);
        var picks = new List<FightPick>();
        foreach (var f in st.Fights)
        {
            double len = (f.End - f.Start).TotalSeconds;
            var fc = priced.Where(p => p.C.At >= f.Start.AddSeconds(-3) && p.C.At <= f.End).ToList();
            double dmg = st.Damage.Where(d => d.At >= f.Start.AddSeconds(-3) && d.At <= f.End.AddSeconds(3)).Sum(d => d.Amount);
            var opts = names.Select(n =>
            {
                double spend = fc.Sum(p => Cost(n, p, rules)), rg = regen * RegenMult(n) * len / 6;
                return (Name: n, Drain: spend - rg, Regen: rg, Damage: dmg * DmgMult(n));
            }).ToList();
            foreach (var o in opts) regenBy[o.Name] += o.Regen;
            if (opts.Count == 0) continue;
            var afford = opts.Where(o => o.Drain <= pool).ToList();
            var pick = afford.Count > 0
                ? afford.OrderByDescending(o => o.Damage).ThenBy(o => o.Drain).First()
                : opts.OrderBy(o => o.Drain).First();
            won[pick.Name]++;
            picks.Add(new FightPick(f.Start, f.End, fc.Count, fc.Sum(p => p.Mana), pick.Name, afford.Count > 0, pick.Damage));
        }

        double totalDmg = st.Damage.Sum(d => d.Amount);
        var ledgers = names.Select(n => new Ledger(n, priced.Sum(p => Cost(n, p, rules)), regenBy[n], 0, totalDmg * DmgMult(n), won[n],
            priced.Sum(p => p.CastSec) * (n == ArcaneMastery ? 1 - rules.AmSpeed : 1))).ToList();
        var rec = ledgers.FirstOrDefault(l => l.Name == Recovery);
        if (rec is not null)
            ledgers = ledgers.Select(l => l with { VsRecovery = (l.Regen - l.Spent) - (rec.Regen - rec.Spent) }).ToList();
        return new Result(ledgers.OrderByDescending(l => l.Won).ThenByDescending(l => l.VsRecovery).ToList(), picks, proven, regen,
            st.Fights.Sum(f => (f.End - f.Start).TotalSeconds), priced.Count, unpriced, resistNote, ocFactor, rules, names);
    }

    // ---- reading the log's tail ----------------------------------------------------------

    /// <summary>The followed log's lines from <paramref name="since"/> on, read
    /// BACKWARDS from the end in chunks — a 90-minute stretch of a multi-GB log
    /// costs a few MB. Includes a little before <paramref name="since"/> so the
    /// invocation up at the start is known (the last switch within 20 min).</summary>
    public static List<string> ReadSince(string path, DateTime since, long maxBytes = 96L << 20)
    {
        var want = since.AddMinutes(-20);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = fs.Length, pos = end;
        const int chunk = 1 << 20;
        var parts = new List<byte[]>();
        while (pos > 0 && end - pos < maxBytes)
        {
            int n = (int)Math.Min(chunk, pos);
            pos -= n;
            var buf = new byte[n];
            fs.Seek(pos, SeekOrigin.Begin);
            int read = 0;
            while (read < n) { int r = fs.Read(buf, read, n - read); if (r <= 0) break; read += r; }
            parts.Insert(0, buf);
            // The first COMPLETE line of this chunk: old enough?
            int nl = Array.IndexOf(buf, (byte)'\n');
            if (nl >= 0 && nl + 1 < buf.Length)
            {
                int e2 = Array.IndexOf(buf, (byte)'\n', nl + 1);
                string first = Encoding.UTF8.GetString(buf, nl + 1, (e2 > nl ? e2 : buf.Length) - nl - 1).TrimEnd('\r');
                if (LineTime(first) is { } ft && ft < want) break;
            }
        }
        var all = parts.SelectMany(p => p).ToArray();
        string text = Encoding.UTF8.GetString(all);
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            if (LineTime(l) is { } t && t >= want) lines.Add(l);
        }
        return lines;
    }
}
