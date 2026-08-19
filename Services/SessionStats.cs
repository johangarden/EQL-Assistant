using System.Globalization;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// Leveling-pace analytics for the Session stats panel: XP as "levels of
/// progress" per hour, AA points per hour, a next-level ETA, and mote drop
/// rates — Companion's XP overlay design, reimplemented over our line feed.
///
/// WHAT THE NUMBERS MEAN (the honesty rules, all Companion-measured):
///  • EQL prints a per-kill PERCENTAGE of the current level's bar
///    ("You gain experience! (1.670%)"). lvl/hr = Σ stated % / 100 over the
///    slice — levels of progress, never raw experience points. A percent-less
///    exp line (the cap) is UNKNOWN, never zero: a slice whose exp lines all
///    state no percentage shows no rate at all.
///  • The ETA divides the bar's remainder (percentages since the last ding,
///    strictly after it) by the slice's ELAPSED pace. Seven things can block
///    it (no ding seen, unstated lines, overfull, no pace…) — each renders
///    "–" with the reason, never a guess.
///  • IDLE is present-but-unproductive, OFFLINE is absent — two different
///    claims. Offline = a ≥60s silence that ends in the log's own
///    "Welcome to EverQuest Legends!". Idle = any ≥5min stretch of the
///    remaining time without an exp/kill/loot line (medding, banking, AFK —
///    the log cannot see a chair, so it is called idle, nothing more).
///    ELAPSED basis = duration − offline; ACTIVE basis = elapsed − idle.
///  • "This tier": EQL spells difficulty into the zone name ("Befallen 3
///    (Fused)"). Exact-tier scoping admits only visits spelled exactly like
///    the current zone; every-tier folds the tier and instance away. The
///    admitted visits are the rate's DENOMINATOR too — that is the point.
///
/// Nothing is persisted: the record is rebuilt by catch-up (Reset + refeed)
/// and extended live. Rates under 5 minutes of denominator are not stated —
/// they would be the clock since you arrived, extrapolated.
/// </summary>
public sealed class SessionStats
{
    public enum Slice { Session, ZoneSession, Zone, All }
    public enum Basis { Elapsed, Active }

    public sealed record StatRow(string Label, string Value, string Unit, string Detail, string Tip);

    public sealed record View(
        IReadOnlyList<StatRow> Rows,
        string Caption,
        string Span,
        string LevelText,
        string LevelTip,
        bool Measurable);

    // ---- observed line formats (real Thorrak log, 2026-08) --------------------

    // "You gain experience! (1.670%)" / "You gain party experience! (1.373%)"
    // and the percent-less variants of both (the level cap states no number).
    // Anchored at both ends: chat lines containing "experience" must not count.
    private static readonly Regex ExpRx = new(
        @"^You gain (?<party>party )?experience!(?: \((?<pct>[\d.]+)%\))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "You have gained a level! Welcome to level 33!"
    private static readonly Regex DingRx = new(
        @"^You have gained a level! Welcome to level (?<lvl>\d+)!$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "You have gained an ability point!  You now have 6 ability points."
    // (double space; singular "point" at 1; "2 ability point(s)" for multi).
    private static readonly Regex AaRx = new(
        @"^You have gained (?<n>an|\d+) ability point(?:\(s\))?!\s+You now have \d+ ability point",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "You looted a Mote of Minor Potential from a zol ghoul knight's corpse
    //  and stored it in your currency" — NO trailing period in the real log.
    private static readonly Regex CurrencyLootRx = new(
        @"^You looted (?:(?<n>\d+) |an? |the )?(?<item>.+?) from .+?'s corpse and stored it in your currency\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Own /who row: "[35 SHD/ROG/SHM] Thorrak (Ogre) <The Chosen Alliance> ZONE: …"
    // — the only level statement between rare dings. Only numeric-level rows
    // parse; the name must be the followed character's.
    private static readonly Regex WhoRx = new(
        @"^\[(?<lvl>\d+) (?<cls>[A-Z]{3}(?:/[A-Z]{3}){0,2})\] (?<name>\S+) \(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Class combo from your own /who row ("SHM/SHD/ROG"), "" until
    /// one has been seen. The character sheet's footer wears it.</summary>
    public string WhoClasses { get; private set; } = "";

    private const string ZonePrefix = "You have entered ";
    private const string WelcomeLine = "Welcome to EverQuest Legends!";
    private const string SlainPrefix = "You have slain ";
    private const string LootedDashPrefix = "--You have looted ";

    private const double OfflineGapMinSec = 60;
    private const double IdleGapSec = 5 * 60;
    private const double RateMinSec = 5 * 60;   // below this a rate is not stated
    private const double EtaAbsurdHours = 24;
    private const double LevelStaleHours = 6;

    private const string MotePrefix = "Mote of ";
    private const string MoteSuffix = " Potential";

    // ---- the record (chronological; rebuilt by Reset + refeed) ----------------

    private readonly List<(DateTime Ts, double Pct, bool Unstated)> _exp = new();
    private readonly List<(DateTime Ts, int Level)> _dings = new();
    private readonly List<(DateTime Ts, int Points)> _aa = new();
    private readonly List<(DateTime Ts, string Tier, int Count)> _motes = new();
    private readonly List<(DateTime Ts, string Zone)> _zones = new();
    private readonly List<(DateTime Start, DateTime End)> _offline = new();
    private readonly List<DateTime> _welcomes = new();
    private readonly List<DateTime> _activity = new(); // exp ∪ own kills ∪ loot
    private (DateTime Ts, int Level, bool FromWho)? _levelStatement;
    private DateTime? _first, _last, _lastInWorld;

    /// <summary>The followed character's name (for the /who row).</summary>
    public string SelfName { get; set; } = "";

    /// <summary>True once any tracked event has been recorded.</summary>
    public bool HasData => _first is not null;

    /// <summary>Wipe the record (catch-up rebuilds it from the log).</summary>
    public void Reset()
    {
        _exp.Clear(); _dings.Clear(); _aa.Clear(); _motes.Clear();
        _zones.Clear(); _offline.Clear(); _welcomes.Clear(); _activity.Clear();
        _levelStatement = null;
        _first = _last = _lastInWorld = null;
    }

    public void ProcessLine(string rawLine)
    {
        DateTime ts = ExtractTimestamp(rawLine, out string body);

        if (body.Length == 0) return;

        if (body == WelcomeLine)
        {
            // The gap before a login is absence. Anchored on our own last
            // FIRST-PERSON event, not the previous raw line — the reconnect
            // preamble can print a stranger's kill seconds before the Welcome.
            if (_lastInWorld is DateTime prev && (ts - prev).TotalSeconds >= OfflineGapMinSec)
                _offline.Add((prev, ts));
            _welcomes.Add(ts);
            Note(ts);
            return;
        }

        var m = ExpRx.Match(body);
        if (m.Success)
        {
            bool unstated = !m.Groups["pct"].Success;
            double pct = unstated ? 0
                : double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
            _exp.Add((ts, pct, unstated));
            _activity.Add(ts);
            NoteInWorld(ts);
            return;
        }

        m = DingRx.Match(body);
        if (m.Success)
        {
            int lvl = int.Parse(m.Groups["lvl"].Value, CultureInfo.InvariantCulture);
            _dings.Add((ts, lvl));
            StateLevel(ts, lvl, fromWho: false);
            NoteInWorld(ts);
            return;
        }

        m = AaRx.Match(body);
        if (m.Success)
        {
            string n = m.Groups["n"].Value;
            int pts = n == "an" ? 1 : int.Parse(n, CultureInfo.InvariantCulture);
            _aa.Add((ts, pts));
            NoteInWorld(ts);
            return;
        }

        m = CurrencyLootRx.Match(body);
        if (m.Success)
        {
            _activity.Add(ts);
            NoteInWorld(ts);
            string item = m.Groups["item"].Value;
            if (item.StartsWith(MotePrefix, StringComparison.Ordinal))
            {
                int count = m.Groups["n"].Success
                    ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
                _motes.Add((ts, MoteTier(item), count));
            }
            return;
        }

        if (body.StartsWith(ZonePrefix, StringComparison.Ordinal) && body.EndsWith('.'))
        {
            string zone = body[ZonePrefix.Length..^1];
            // "You have entered an area where levitation is not allowed." is a
            // rule notice, not a zone transition.
            if (zone.StartsWith("an area where", StringComparison.OrdinalIgnoreCase)) return;
            _zones.Add((ts, zone));
            NoteInWorld(ts);
            return;
        }

        if (body.StartsWith(SlainPrefix, StringComparison.Ordinal)
            || body.StartsWith(LootedDashPrefix, StringComparison.Ordinal))
        {
            _activity.Add(ts);
            NoteInWorld(ts);
            return;
        }

        m = WhoRx.Match(body);
        if (m.Success && SelfName.Length > 0
            && m.Groups["name"].Value.Equals(SelfName, StringComparison.OrdinalIgnoreCase))
        {
            WhoClasses = m.Groups["cls"].Value;
            StateLevel(ts, int.Parse(m.Groups["lvl"].Value, CultureInfo.InvariantCulture), fromWho: true);
            NoteInWorld(ts);
        }
    }

    private void StateLevel(DateTime ts, int level, bool fromWho)
    {
        // Later statement wins; a tie goes to /who (it states the truth the
        // ding's tail cannot — loadout swaps restart the bar unlogged).
        if (_levelStatement is { } cur && (ts < cur.Ts || (ts == cur.Ts && !fromWho && cur.FromWho)))
            return;
        _levelStatement = (ts, level, fromWho);
    }

    private void Note(DateTime ts)
    {
        _first ??= ts;
        if (_last is null || ts > _last) _last = ts;
    }

    private void NoteInWorld(DateTime ts)
    {
        Note(ts);
        if (_lastInWorld is null || ts > _lastInWorld) _lastInWorld = ts;
    }

    // ---- mote + zone name folding --------------------------------------------

    /// <summary>"Mote of Minor Potential" → "Minor"; the tierless
    /// "Mote of Potential" keeps the word "Potential".</summary>
    public static string MoteTier(string item)
    {
        if (!item.StartsWith(MotePrefix, StringComparison.Ordinal)) return item;
        string rest = item[MotePrefix.Length..];
        return rest.EndsWith(MoteSuffix, StringComparison.Ordinal) && rest.Length > MoteSuffix.Length
            ? rest[..^MoteSuffix.Length]
            : rest;
    }

    private static readonly Regex SoloGroupRx = new(@"\s*-\s*(Solo|Group)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TierOrdinalRx = new(@"\s+\d+\s*\([^)]*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TierParenRx = new(@"\s+\([^)]*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeparatorsRx = new(@"[\s-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The PLACE a zone name means, tier and instance folded away:
    /// "Befallen 3 (Fused)" and "Befallen - Solo 2 (Refined)" → "befallen".</summary>
    public static string ZoneKey(string zone)
    {
        string s = SoloGroupRx.Replace(zone ?? "", "");
        s = TierOrdinalRx.Replace(s, "");
        s = TierParenRx.Replace(s, "");
        s = SeparatorsRx.Replace(s.ToLowerInvariant(), " ").Trim();
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..].Trim();
        return s;
    }

    /// <summary>Every byte of the spelling, case-folded — exact-tier identity.</summary>
    public static string ZoneIdKey(string zone) => (zone ?? "").Trim().ToLowerInvariant();

    // ---- the snapshot ---------------------------------------------------------

    public View Snapshot(DateTime now, Slice slice, bool exactTier, Basis basis)
    {
        if (_first is not DateTime first)
            return new View(Array.Empty<StatRow>(), "no data yet", "", "", "", false);

        DateTime t1 = now > _last!.Value ? now : _last.Value;
        DateTime sessionStart = _welcomes.Count > 0 ? _welcomes[^1] : first;
        DateTime t0 = slice is Slice.Session or Slice.ZoneSession ? sessionStart : first;
        if (t0 > t1) t0 = t1;

        // Current zone (the last one entered). Zone slices without one fall
        // back to unfiltered — a filter on nothing would be a silent lie.
        string? zoneName = _zones.Count > 0 ? _zones[^1].Zone : null;
        bool zoneFiltered = slice is Slice.Zone or Slice.ZoneSession && zoneName is not null;

        var spans = AdmittedSpans(t0, t1, zoneFiltered ? zoneName : null, exactTier);
        double durationSec = spans.Sum(s => (s.End - s.Start).TotalSeconds);
        double offlineSec = OverlapSec(_offline, spans);
        var onlineSpans = SubtractOffline(spans);
        double idleSec = IdleSec(onlineSpans, t1);
        double elapsedSec = Math.Max(0, durationSec - offlineSec);
        double activeSec = Math.Max(0, elapsedSec - idleSec);

        double denomSec = basis == Basis.Active ? activeSec : elapsedSec;
        bool measurable = denomSec >= RateMinSec;
        double denomHours = denomSec / 3600.0;

        // Sums over the admitted spans.
        double levelEquiv = 0; int expSamples = 0, expUnstated = 0;
        foreach (var (ts, pct, unstated) in _exp)
        {
            if (!Contains(spans, ts)) continue;
            expSamples++;
            if (unstated) expUnstated++; else levelEquiv += pct / 100.0;
        }
        int aaEvents = 0, aaPoints = 0;
        foreach (var (ts, pts) in _aa)
        {
            if (!Contains(spans, ts)) continue;
            aaEvents++; aaPoints += pts;
        }
        var moteDrops = new Dictionary<string, (int Drops, DateTime Last)>(StringComparer.Ordinal);
        foreach (var (ts, tier, count) in _motes)
        {
            if (!Contains(spans, ts)) continue;
            moteDrops[tier] = moteDrops.TryGetValue(tier, out var cur)
                ? (cur.Drops + count, ts > cur.Last ? ts : cur.Last)
                : (count, ts);
        }

        bool levelsUnknown = expSamples > 0 && expSamples == expUnstated;

        var rows = new List<StatRow>();

        // XP — levels of progress per hour.
        {
            string tip = "Σ of the stated level-bar percentages / 100, per hour of the "
                + BasisWord(basis) + " time — levels of progress, not raw experience.";
            string value = !measurable || levelsUnknown ? "–"
                : FormatSmall(levelEquiv / denomHours);
            if (levelsUnknown) tip = "The experience lines here state no percentage (the cap) — unknown, not zero.";
            else if (!measurable) tip = TooShortTip;
            rows.Add(new StatRow("XP", value, "lvl/hr", "", tip));
        }

        // AA — gain lines per hour; points per hour rides as the detail.
        {
            string value = !measurable ? "–" : FormatSmall(aaEvents / denomHours);
            string detail = !measurable || aaPoints == 0 ? ""
                : FormatSmall(aaPoints / denomHours) + " pts/hr";
            rows.Add(new StatRow("AA", value, "AA/hr", detail,
                !measurable ? TooShortTip : "Ability-point gains per hour of the " + BasisWord(basis) + " time."));
        }

        // NEXT LEVEL — the bar's remainder over the ELAPSED pace, always.
        rows.Add(EtaRow(elapsedSec, levelEquiv, measurable, levelsUnknown, t1));

        // Motes — one row per tier seen in the slice, most drops first.
        if (moteDrops.Count == 0)
        {
            rows.Add(new StatRow("MOTES", "–", "", "none here",
                "No mote has dropped in this stretch."));
        }
        else
        {
            foreach (var kv in moteDrops
                         .OrderByDescending(kv => kv.Value.Drops)
                         .ThenByDescending(kv => kv.Value.Last)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // The count is never gated, only the rate: "3×" is a fact of
                // the log; "3.00 drops/hr" over ninety seconds is extrapolation.
                string value = !measurable ? "–" : FormatSmall(kv.Value.Drops / denomHours);
                rows.Add(new StatRow(kv.Key.ToUpperInvariant(), value, "drops/hr",
                    kv.Value.Drops.ToString(CultureInfo.InvariantCulture) + "×",
                    !measurable ? TooShortTip
                        : $"Mote of {kv.Key}{(kv.Key == "Potential" ? "" : " Potential")} — stack-summed drops per hour."));
            }
        }

        string caption = Caption(slice, zoneFiltered, zoneName, exactTier);
        string span = $"over {FmtDuration(denomSec)} {BasisWord(basis)}";
        var (levelText, levelTip) = LevelHeader(t1);

        return new View(rows, caption, span, levelText, levelTip, measurable);
    }

    private const string TooShortTip =
        "This stretch is under 5 minutes long — too little to state as a rate per hour; " +
        "the number would be the clock since you arrived, extrapolated.";

    private StatRow EtaRow(double elapsedSec, double slicePaceEquiv, bool measurable, bool levelsUnknown, DateTime t1)
    {
        const string label = "NEXT LEVEL";
        StatRow Blocked(string why) => new(label, "–", "", "", why);

        if (_dings.Count == 0)
            return Blocked("No level-up has been recorded yet, so your place in the bar is unknown.");

        var (dingTs, dingLevel) = _dings[^1];

        // A loadout swap is never logged — but your own /who is. When a /who
        // NEWER than the ding names a different level, the bar restarted
        // where the log cannot see: no honest ETA exists until the next ding.
        if (_levelStatement is { FromWho: true } w && w.Ts > dingTs && w.Level != dingLevel)
            return Blocked($"Your /who says level {w.Level} but the last level-up said {dingLevel} — "
                + "a loadout swap restarted the bar where the log cannot see. "
                + "The ETA returns at your next level-up.");

        // Bar position: stated percentages STRICTLY after the ding — its own
        // exp line shares the ding's second and belongs to the OLD bar.
        double equiv = 0; int unstated = 0;
        for (int i = _exp.Count - 1; i >= 0 && _exp[i].Ts > dingTs; i--)
        {
            if (_exp[i].Unstated) unstated++;
            else equiv += _exp[i].Pct / 100.0;
        }
        if (unstated > 0)
            return Blocked("Experience lines since your last level-up stated no percentage — unknown, not zero.");
        if (equiv >= 1)
            return Blocked("The percentages since your last level-up already exceed a full level.");
        if (!measurable || levelsUnknown)
            return Blocked(TooShortTip);
        double paceHr = slicePaceEquiv / (elapsedSec / 3600.0);
        if (double.IsNaN(paceHr) || paceHr <= 0)
            return Blocked("This stretch states no levels of progress.");

        double hours = (1 - equiv) / paceHr;
        string value = hours > EtaAbsurdHours ? ">1 day" : "~" + FmtDuration(hours * 3600);
        // No target number: with loadouts, "which level" is a claim the log
        // can't back — the countdown itself is the information. A stale ding
        // still gets the /who caveat on the tip.
        bool staleDing = (t1 - dingTs).TotalHours >= LevelStaleHours;
        string tip = $"The remaining {100 - equiv * 100:0.#}% of the bar at this stretch's elapsed pace."
            + (staleDing
                ? $" The level-up behind this is {FmtDuration((t1 - dingTs).TotalSeconds)} old and a loadout "
                  + "swap is never logged — type /who in game to confirm which loadout's bar this is."
                : "");
        return new StatRow(label, value, "", "", tip);
    }

    /// <summary>The level line for other windows (the character sheet's
    /// footer): same ding//who machinery as the panel header.</summary>
    public (string Text, string Tip) LevelInfo(DateTime now) => LevelHeader(now);

    private (string Text, string Tip) LevelHeader(DateTime t1)
    {
        if (_levelStatement is not { } s) return ("", "");
        double ageHours = Math.Max(0, (t1 - s.Ts).TotalHours);
        string cue = s.FromWho ? " /who" : "";
        if (ageHours >= LevelStaleHours) cue += " " + FmtDuration(ageHours * 3600);
        string source = s.FromWho ? "your own /who row" : "your last level-up line";
        string hint = ageHours >= LevelStaleHours
            ? " A loadout swap is never logged — type /who in game to refresh."
            : "";
        return ($"lvl {s.Level}{cue}",
            $"From {source}, {FmtDuration(ageHours * 3600)} ago on the log's clock.{hint}");
    }

    private string Caption(Slice slice, bool zoneFiltered, string? zoneName, bool exactTier)
    {
        string tierPhrase = exactTier ? "this tier only" : "every tier";
        return slice switch
        {
            Slice.All => "the whole record",
            Slice.Session => "this session",
            Slice.Zone when zoneFiltered => $"{zoneName}, {tierPhrase}",
            Slice.ZoneSession when zoneFiltered => $"{zoneName} this session, {tierPhrase}",
            // A zone slice before any zone line falls back to unfiltered.
            Slice.Zone => "the whole record (zone unknown)",
            _ => "this session (zone unknown)",
        };
    }

    // ---- time-span machinery --------------------------------------------------

    private readonly record struct Span(DateTime Start, DateTime End);

    /// <summary>The slice's time, zone-narrowed when asked: consecutive zone
    /// entries bound visit segments; a visit is admitted when its zone matches
    /// the current one (folded, or exact spelling under exact-tier). Time
    /// before the first zone line has no zone and is admitted only unfiltered.</summary>
    private List<Span> AdmittedSpans(DateTime t0, DateTime t1, string? zoneName, bool exactTier)
    {
        var spans = new List<Span>();
        if (zoneName is null)
        {
            if (t0 < t1) spans.Add(new Span(t0, t1));
            return spans;
        }

        string wantKey = ZoneKey(zoneName);
        string wantExact = ZoneIdKey(zoneName);
        for (int i = 0; i < _zones.Count; i++)
        {
            var (ts, zone) = _zones[i];
            DateTime end = i + 1 < _zones.Count ? _zones[i + 1].Ts : t1;
            if (ZoneKey(zone) != wantKey) continue;
            if (exactTier && ZoneIdKey(zone) != wantExact) continue;
            DateTime s = ts > t0 ? ts : t0;
            DateTime e = end < t1 ? end : t1;
            if (s < e) spans.Add(new Span(s, e));
        }
        return spans;
    }

    private static bool Contains(List<Span> spans, DateTime ts)
    {
        foreach (var s in spans)
            if (ts >= s.Start && ts < s.End) return true;
        return false;
    }

    private static double OverlapSec(List<(DateTime Start, DateTime End)> cuts, List<Span> spans)
    {
        double sum = 0;
        foreach (var c in cuts)
            foreach (var s in spans)
            {
                DateTime a = c.Start > s.Start ? c.Start : s.Start;
                DateTime b = c.End < s.End ? c.End : s.End;
                if (a < b) sum += (b - a).TotalSeconds;
            }
        return sum;
    }

    private List<Span> SubtractOffline(List<Span> spans)
    {
        var result = new List<Span>();
        foreach (var s in spans)
        {
            var pieces = new List<Span> { s };
            foreach (var c in _offline)
            {
                var next = new List<Span>();
                foreach (var p in pieces)
                {
                    if (c.End <= p.Start || c.Start >= p.End) { next.Add(p); continue; }
                    if (c.Start > p.Start) next.Add(new Span(p.Start, c.Start));
                    if (c.End < p.End) next.Add(new Span(c.End, p.End));
                }
                pieces = next;
            }
            result.AddRange(pieces);
        }
        return result;
    }

    /// <summary>Idle time inside the given online spans: every gap over 5
    /// minutes between consecutive activity points (span edges included)
    /// contributes its WHOLE length — a classifier, not a grace period.
    /// Silence at the live edge is idle too: the log cannot see a chair.</summary>
    private double IdleSec(List<Span> onlineSpans, DateTime t1)
    {
        double idle = 0;
        foreach (var s in onlineSpans)
        {
            DateTime prev = s.Start;
            foreach (var ts in _activity)
            {
                if (ts < s.Start || ts >= s.End) continue;
                double gap = (ts - prev).TotalSeconds;
                if (gap > IdleGapSec) idle += gap;
                if (ts > prev) prev = ts;
            }
            double tail = (s.End - prev).TotalSeconds;
            if (tail > IdleGapSec) idle += tail;
        }
        return idle;
    }

    // ---- formatting -----------------------------------------------------------

    private static string BasisWord(Basis basis) => basis == Basis.Active ? "active" : "elapsed";

    /// <summary>≥100 → integer, ≥10 → one decimal, else two.</summary>
    public static string FormatSmall(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n)) return "–";
        double v = Math.Abs(n);
        if (v >= 100) return n.ToString("0", CultureInfo.InvariantCulture);
        if (v >= 10) return n.ToString("0.0", CultureInfo.InvariantCulture);
        return n.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>"2d 3h" / "3h 25m" / "19m" / "42s".</summary>
    public static string FmtDuration(double seconds)
    {
        long total = Math.Max(0, (long)Math.Round(seconds));
        long hrs = total / 3600;
        if (hrs >= 48) return $"{hrs / 24}d {hrs % 24}h";
        long mins = total % 3600 / 60;
        if (hrs > 0) return $"{hrs}h {mins}m";
        return mins > 0 ? $"{mins}m" : $"{total % 60}s";
    }

    /// <summary>Test hook: a believable evening — one session, one zone,
    /// steady kills with motes, one ding.</summary>
    public void AddDemo(DateTime now)
    {
        Reset();
        var start = now.AddMinutes(-42);
        ProcessDemo(start, WelcomeLine);
        ProcessDemo(start.AddSeconds(5), "You have entered Befallen 3 (Fused).");
        var rng = 0;
        for (var t = start.AddMinutes(1); t < now; t = t.AddSeconds(95))
        {
            ProcessDemo(t, "You gain experience! (1.670%)");
            if (++rng % 6 == 0)
                ProcessDemo(t.AddSeconds(2),
                    "You looted a Mote of Minor Potential from a zol ghoul knight's corpse and stored it in your currency");
            if (rng % 9 == 0)
                ProcessDemo(t.AddSeconds(3),
                    "You have gained an ability point!  You now have 6 ability points.");
        }
        ProcessDemo(start.AddMinutes(20), "You have gained a level! Welcome to level 35!");
    }

    private void ProcessDemo(DateTime ts, string body) =>
        ProcessLine($"[{ts.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture)}] {body}");

    private static readonly Regex TimestampPrefix = new(@"^\[(?<ts>.+?)\]\s?", RegexOptions.Compiled);
    private static readonly string[] TimestampFormats =
    {
        "ddd MMM d HH:mm:ss yyyy",
        "ddd MMM dd HH:mm:ss yyyy",
    };

    private static DateTime ExtractTimestamp(string line, out string body)
    {
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            body = line[m.Length..];
            string ts = Regex.Replace(m.Groups["ts"].Value.Trim(), @"\s+", " ");
            if (DateTime.TryParseExact(ts, TimestampFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                return parsed;
        }
        else
        {
            body = line;
        }
        return DateTime.Now;
    }
}
