using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The Sky quest helper's brain: watches the log for lines NAMING a known
/// quest dropper — a /con line or a damage line either way — and answers
/// "what does this mob drop that I still need?" for the panel's card.
///
/// The con line is confirmed from real logs:
///   "A zol ghoul knight scowls at you, ready to attack -- looks like it
///    would wipe the floor with you! (Lvl: 40)"
/// — mob first, a small set of regard verbs, "(Lvl: N)" as the anchor.
///
/// Honest limits, stated: wind runes drop from ANY Sky mob and island-only
/// wiki entries name no mob — neither can light a card; they live in the
/// tracked Hunting list instead. A mob nobody cons or hits prints nothing.
/// Live-only (not fed on catch-up): stale lines would mint stale cards.
/// </summary>
public sealed class SkyHelper
{
    private readonly SkyQuests _sky;

    /// <summary>The player's classes from /who ("SHD/ROG/SHM" split); empty =
    /// no filter. Supplied as a func so a late /who needs no re-wiring.</summary>
    public Func<IReadOnlyList<string>>? ClassesProvider { get; set; }

    /// <summary>Admit items whose quest is already completed (config; default
    /// off — a done quest's drop is noise).</summary>
    public bool ShowCompleted { get; set; }

    /// <summary>A known dropper was named in the log and has card-worthy
    /// items: (mob display name). Throttled per mob (2s).</summary>
    public event Action<string>? Sighted;

    /// <summary>Zoning — the card's mob is gone.</summary>
    public event Action? Cleared;

    public sealed record CardItem(string Item, string Quest, string Class, string Island,
        int Held, int Need, bool QuestDone);

    private sealed class MobWatch
    {
        public required string Name;
        public required Regex ConRx;
        public required Regex ActorRx;
        public required Regex TargetRx;
        public DateTime LastSighted = DateTime.MinValue;
    }

    private readonly List<MobWatch> _mobs = new();

    // Same melee grammar the respawn learner trusts (both tenses).
    private const string Verbs =
        "hits?|slash(?:es)?|pierces?|crush(?:es)?|bash(?:es)?|bites?|kicks?|mauls?"
        + "|gores?|stings?|claws?|slams?|rends?|cleaves?|punch(?:es)?|strikes?"
        + "|backstabs?|reaves?";

    // The regard verbs a /con prints; "(Lvl:" anchors the tail.
    private const string ConVerbs =
        "scowls at you|glowers at you|glares at you|regards you|looks upon you"
        + "|judges you|kindly considers you|regards you as an ally";

    public SkyHelper(SkyQuests sky)
    {
        _sky = sky;
        RebuildMobs();
    }

    /// <summary>One watch per distinct dropper name across ALL quests (class
    /// and completion filter at card time, so a /who mid-session needs no
    /// rebuild). ~10 named Sky bosses.</summary>
    private void RebuildMobs()
    {
        _mobs.Clear();
        var names = _sky.Quests
            .SelectMany(q => q.Items)
            .SelectMany(i => i.Mobs)
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        const RegexOptions opts = RegexOptions.Compiled | RegexOptions.CultureInvariant
            | RegexOptions.IgnoreCase;
        foreach (var name in names)
        {
            string n = Regex.Escape(name);
            _mobs.Add(new MobWatch
            {
                Name = name,
                ConRx = new Regex($@"^{n} (?:{ConVerbs})\b.*\(Lvl: \d+\)", opts),
                ActorRx = new Regex(
                    $@"^{n} (?:(?:{Verbs}) |tr(?:y|ies) to \w+ |begins? (?:casting|singing)"
                    + $@"|activates |has taken \d|resisted |healed )", opts),
                TargetRx = new Regex(
                    $@"(?:(?:{Verbs}|healed) {n} for \d|tr(?:y|ies) to \w+ {n}, but| by {n}[.!]$)",
                    opts),
            });
        }
    }

    private static readonly Regex TimestampPrefix = new(@"^\[.+?\]\s?", RegexOptions.Compiled);

    public void ProcessLine(string rawLine)
    {
        string body = TimestampPrefix.Replace(rawLine, "", 1);

        if (body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            Cleared?.Invoke();
            return;
        }

        foreach (var w in _mobs)
        {
            // Cheap gate before any regex — few mobs, many lines.
            if (body.IndexOf(w.Name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!w.ConRx.IsMatch(body) && !w.ActorRx.IsMatch(body) && !w.TargetRx.IsMatch(body))
                continue;
            var now = DateTime.Now;
            if ((now - w.LastSighted).TotalSeconds < 2) return;
            w.LastSighted = now;
            // Only raise when the card would say something.
            if (ItemsFor(w.Name).Count > 0) Sighted?.Invoke(w.Name);
            return;
        }
    }

    /// <summary>What this mob drops for the player's quests: class-filtered,
    /// completed quests admitted only when configured. Empty = no card.</summary>
    public List<CardItem> ItemsFor(string mob)
    {
        var classes = ClassesProvider?.Invoke() ?? Array.Empty<string>();
        var items = new List<CardItem>();
        foreach (var q in _sky.Quests)
        {
            if (classes.Count > 0
                && !classes.Contains(Abbr(q.Class), StringComparer.OrdinalIgnoreCase))
                continue;
            bool done = _sky.IsCompleted(q);
            if (done && !ShowCompleted) continue;
            foreach (var it in q.Items)
            {
                if (!it.Mobs.Contains(mob, StringComparer.OrdinalIgnoreCase)) continue;
                items.Add(new CardItem(it.Name, q.Name, Abbr(q.Class), it.Where,
                    Math.Min(it.Count, _sky.HeldCount(it)), it.Count, done));
            }
        }
        // Still-needed first, then ready-to-hand-in, then completed-quest info.
        return items
            .OrderBy(i => i.QuestDone ? 2 : i.Held >= i.Need ? 1 : 0)
            .ThenBy(i => i.Quest, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The game's class abbreviation ("Shadow Knight" → "SHD") —
    /// matches what /who prints, which is what ClassesProvider carries.</summary>
    public static string Abbr(string className) => className switch
    {
        "Warrior" => "WAR", "Cleric" => "CLR", "Paladin" => "PAL", "Ranger" => "RNG",
        "Shadow Knight" => "SHD", "Druid" => "DRU", "Monk" => "MNK", "Bard" => "BRD",
        "Rogue" => "ROG", "Shaman" => "SHM", "Necromancer" => "NEC", "Wizard" => "WIZ",
        "Magician" => "MAG", "Enchanter" => "ENC", "Beastlord" => "BST", "Berserker" => "BER",
        _ => className,
    };
}
