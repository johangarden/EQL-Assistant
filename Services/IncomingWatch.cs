namespace EQLOverlay.Services;

/// <summary>
/// The live incoming-damage window (owner request, 11 Sep — "a stance
/// helper in-fight"): every hit on you from the last N seconds, melee or
/// spell, bucketed per second, with the same stance verdict the death recap
/// gives — while you're still alive to act on it. Fed by the parser's SCT
/// hits (IncomingSelf, flavor Melee vs Spell/Proc); never on catch-up.
/// </summary>
public sealed class IncomingWatch
{
    private sealed record Hit(DateTime At, double Amount, bool Spell);

    /// <summary>Per second, oldest first; the last element is "now".</summary>
    public sealed record Snapshot(int WindowSec, double Melee, double Spell, double[] MeleeCols, double[] SpellCols,
        string Stance, string VerdictText, string VerdictKind)
    {
        public double Total => Melee + Spell;
        public bool Any => Total > 0;
        public double SpellShare => Total <= 0 ? 0 : Spell / Total;
        public double MeleeShare => Total <= 0 ? 0 : Melee / Total;
    }

    private readonly List<Hit> _hits = new();
    private const int KeepSec = 60;

    public int WindowSec { get; set; } = 15;

    public void Add(DateTime at, double amount, bool spell)
    {
        if (amount <= 0) return;
        _hits.Add(new Hit(at, amount, spell));
        if (_hits.Count > 4000 || (_hits.Count > 0 && (at - _hits[0].At).TotalSeconds > KeepSec))
            _hits.RemoveAll(h => (at - h.At).TotalSeconds > KeepSec);
    }

    public void Clear() => _hits.Clear();

    public Snapshot Take(DateTime now, string stance)
    {
        int n = Math.Clamp(WindowSec, 5, 60);
        var mc = new double[n];
        var sc = new double[n];
        double melee = 0, spell = 0;
        foreach (var h in _hits)
        {
            double back = (now - h.At).TotalSeconds;
            if (back < 0 || back >= n) continue;
            int col = n - 1 - (int)Math.Floor(back);
            if (h.Spell) { sc[col] += h.Amount; spell += h.Amount; }
            else { mc[col] += h.Amount; melee += h.Amount; }
        }
        var (text, kind) = Verdict(stance, melee, spell);
        return new Snapshot(n, melee, spell, mc, sc, stance, text, kind);
    }

    /// <summary>The wrong-stance notice (owner request 14 Sep): over the last
    /// <paramref name="windowSec"/> seconds, when one kind is at least
    /// <paramref name="minShare"/> of what hit you (0.75 = 75%) and the OTHER
    /// stance would halve it, the stance to switch to — "mage hunter" or
    /// "defensive"; "" when you're already in it, the mix is below the share,
    /// or fewer than <see cref="MinHits"/> hits landed (one stray nuke is not
    /// a pattern).</summary>
    public (string Target, double Share, int Hits) SwitchAdvice(DateTime now, string stance, int windowSec, double minShare)
    {
        int n = Math.Clamp(windowSec, 3, 60);
        double melee = 0, spell = 0; int hits = 0;
        foreach (var h in _hits)
        {
            double back = (now - h.At).TotalSeconds;
            if (back < 0 || back >= n) continue;
            hits++;
            if (h.Spell) spell += h.Amount; else melee += h.Amount;
        }
        double total = melee + spell;
        if (hits < MinHits || total <= 0) return ("", 0, hits);
        string st = (stance ?? "").Trim().ToLowerInvariant();
        double sp = spell / total, mp = melee / total;
        if (sp >= minShare && st != "mage hunter") return ("mage hunter", sp, hits);
        if (mp >= minShare && st != "defensive") return ("defensive", mp, hits);
        return ("", Math.Max(sp, mp), hits);
    }

    public const int MinHits = 3;

    /// <summary>The short in-fight verdict. Kinds: "switch" (another stance
    /// would halve the bigger half), "ok" (you're in the right one), "mixed"
    /// (no stance halves both), "" (nothing taken). Dominant = ≥60%.</summary>
    public static (string Text, string Kind) Verdict(string stance, double melee, double spell)
    {
        double total = melee + spell;
        if (total <= 0) return ("", "");
        double sp = spell / total, mp = melee / total;
        string st = (stance ?? "").Trim().ToLowerInvariant();
        bool defensive = st == "defensive", hunter = st == "mage hunter";
        string Pct(double f) => $"{Math.Round(f * 100):0}%";

        if (sp >= 0.6)
        {
            string lead = $"Spells are {Pct(sp)} of it";
            if (hunter) return ($"{lead} — mage hunter is the right stance. Already halved.", "ok");
            if (defensive) return ($"{lead} — you're in defensive (halves melee). Mage hunter would halve the bigger half.", "switch");
            return ($"{lead} — mage hunter would halve it.", "switch");
        }
        if (mp >= 0.6)
        {
            string lead = $"Melee is {Pct(mp)} of it";
            if (defensive) return ($"{lead} — defensive is the right stance. Already halved.", "ok");
            if (hunter) return ($"{lead} — you're in mage hunter (halves spells). Defensive would halve the bigger half.", "switch");
            return ($"{lead} — defensive would halve it.", "switch");
        }
        return ($"Melee {Pct(mp)} / spells {Pct(sp)} — mixed; no stance halves both.", "mixed");
    }
}
