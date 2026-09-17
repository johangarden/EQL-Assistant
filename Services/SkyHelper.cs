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
    private readonly QuestLines? _lines;

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

    /// <param name="lines">The notable quest lines (17 Sep, owner: "extend the
    /// Sky dropper to all NPCs that drop stuff for quests on the Quests page") —
    /// every kill-and-loot step's mob becomes a dropper too.</param>
    public SkyHelper(SkyQuests sky, QuestLines? lines = null)
    {
        _sky = sky;
        _lines = lines;
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
            .Concat(_lines?.Quests.SelectMany(q => q.Steps).Where(s => s.Loot.Count > 0).SelectMany(s => s.Kill)
                    ?? Enumerable.Empty<string>())
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

    /// <summary>What this mob drops for ANY active quest — never locked to
    /// your /who classes (owner ruling, 6 Sep: the class is a fact on each
    /// line, not a gate). Completed quests admitted only when configured.
    /// Empty = no card.</summary>
    public List<CardItem> ItemsFor(string mob)
    {
        var items = new List<CardItem>();
        foreach (var q in _sky.Quests)
        {
            bool done = _sky.IsCompleted(q);
            if (done && !ShowCompleted) continue;
            foreach (var it in q.Items)
            {
                if (!it.Mobs.Contains(mob, StringComparer.OrdinalIgnoreCase)) continue;
                items.Add(new CardItem(it.Name, q.Name, Abbr(q.Class), it.Where,
                    _sky.AllocatedHeld(q, it), it.Count, done)); // one copy serves one quest
            }
        }
        // The notable quest lines: a kill-and-loot step whose mob this is.
        if (_lines is not null)
            foreach (var q in _lines.Quests)
            {
                bool lineDone = _lines.IsComplete(q);
                if (lineDone && !ShowCompleted) continue;
                for (int i = 0; i < q.Steps.Count; i++)
                {
                    var s = q.Steps[i];
                    if (s.Loot.Count == 0 || !s.Kill.Contains(mob, StringComparer.OrdinalIgnoreCase)) continue;
                    bool stepDone = _lines.IsDone(q, s);
                    if (stepDone && !ShowCompleted) continue;
                    string cls = q.Classes.Count > 0 ? string.Join("/", q.Classes) : "any";
                    foreach (var item in s.Loot)
                        items.Add(new CardItem(item, $"{q.Name} · step {i + 1}", cls, s.Zone,
                            Math.Min(1, _lines.LedgerHeld(item)), 1, lineDone || stepDone));
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
