using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EQLOverlay.Services;

/// <summary>
/// The tradeskill helper's engine (owner, 21 Sep: "today I want to work on
/// brewing, I pop up the panel and get to work"). Reads the combine lines the
/// log prints — the skill-up with its new value, the success and fail lines
/// naming the product, the game's own "You can no longer advance your skill
/// from making this item." (trivial), vendor purchases — and keeps two kinds
/// of state: what it LEARNED (your skill values, which vendor sold you what;
/// persisted, also fed on replays) and the SESSION on the skill you opened
/// (combines, skill-ups, coin spent, the recipe you are on). The window reads
/// a <see cref="Snapshot"/>.
/// </summary>
public sealed class TradeskillWatch
{
    private static readonly Regex SkillUpRx = new(@"^You have become better at (.+?)! \((\d+)\)$", RegexOptions.Compiled);
    private static readonly Regex SuccessRx = new(@"^You have fashioned the items together to create (?:something new|an alternate product): (.+)\.$", RegexOptions.Compiled);
    private static readonly Regex FailRx = new(@"^You lacked the skills to fashion (.+)\.$", RegexOptions.Compiled);
    private static readonly Regex PurchaseRx = new(@"^You purchased (?:(\d+) )?(.+?) from (.+?) for\s+(.+?)\.?$", RegexOptions.Compiled);
    private static readonly Regex CoinRx = new(@"(\d+) (platinum|gold|silver|copper)", RegexOptions.Compiled);
    private static readonly Regex ZoneRx = new(@"^You have entered (.+)\.$", RegexOptions.Compiled);
    private const string TrivialLine = "You can no longer advance your skill from making this item.";
    private const string MissingLine = "Sorry, but you don't have everything you need for this recipe";
    private const double TrivialPairSec = 3; // the trivial line precedes its success line, same second

    public sealed record SkillValue(int Value, DateTime At);
    public sealed record VendorMemory(string Npc, string Zone, DateTime At);
    public sealed record Purchase(DateTime At, string Item, int Count, string Npc, long Copper);

    private sealed class Doc
    {
        public Dictionary<string, SkillValue> Skills { get; set; } = new();
        public Dictionary<string, VendorMemory> Vendors { get; set; } = new();
    }

    private readonly TradeskillData _data;
    private readonly string? _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, SkillValue> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VendorMemory> _vendors = new(StringComparer.OrdinalIgnoreCase);
    private string _zone = "";

    // ---- the session on the opened skill ----
    private string? _selected;
    private DateTime _sessionStart;
    private readonly Dictionary<string, (int Ok, int Fail)> _byRecipe = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Purchase> _purchases = new();
    private readonly HashSet<string> _trivialSaid = new(StringComparer.OrdinalIgnoreCase);
    private int _combines, _ok, _fail, _skillUps;
    private string? _lastProduct;
    private DateTime? _lastCombineAt;
    private DateTime? _trivialPending;
    private string? _trivialProduct;
    private DateTime? _missingAt;

    public event Action? Changed;
    /// <summary>The game said a recipe is trivial for you now (once per product per session).</summary>
    public event Action<string>? WentTrivial;
    /// <summary>A skill value changed (name, value).</summary>
    public event Action<string, int>? SkillChanged;

    public TradeskillWatch(TradeskillData data, ConfigService? config, string? pathOverride = null)
    {
        _data = data;
        _path = pathOverride ?? (config is null ? null : Path.Combine(config.ConfigDirectory, "tradeskill-log.json"));
        Load();
    }

    public TradeskillData Data => _data;
    public string? Selected => _selected;
    public IReadOnlyDictionary<string, SkillValue> Skills => _skills;
    public IReadOnlyList<Purchase> Purchases => _purchases;

    /// <summary>Your value for a skill (game or wiki name), null when never seen.</summary>
    public int? ValueOf(string skill) =>
        _skills.TryGetValue(TradeskillData.Canonical(skill), out var v) ? v.Value : null;

    /// <summary>The Manager's manual set (a skill the log never saw go up).</summary>
    public void SetValue(string skill, int value, DateTime? at = null)
    {
        string k = TradeskillData.Canonical(skill);
        if (value <= 0) { if (_skills.Remove(k)) { Save(); Changed?.Invoke(); } return; }
        _skills[k] = new SkillValue(value, at ?? DateTime.Now);
        Save();
        SkillChanged?.Invoke(k, value);
        Changed?.Invoke();
    }

    /// <summary>Where you last bought an item (npc, zone) — null when never.</summary>
    public VendorMemory? VendorFor(string item) => _vendors.TryGetValue(item, out var v) ? v : null;

    /// <summary>Open the helper on a skill: a fresh session (counters, spend, the recipe you are on).</summary>
    public void StartSession(string skill, DateTime? now = null)
    {
        _selected = TradeskillData.Canonical(skill);
        _sessionStart = now ?? DateTime.Now;
        _byRecipe.Clear(); _purchases.Clear(); _trivialSaid.Clear();
        _combines = _ok = _fail = _skillUps = 0;
        _lastProduct = null; _lastCombineAt = null; _trivialPending = null; _trivialProduct = null; _missingAt = null;
        Changed?.Invoke();
    }

    public void EndSession()
    {
        _selected = null;
        Changed?.Invoke();
    }

    /// <summary>Feed a log line. <paramref name="live"/> false (replays) only
    /// LEARNS — skill values and vendors — and never touches the session.</summary>
    public void ProcessLine(string line, bool live = true)
    {
        if (line.Length < 28 || line[0] != '[') return;
        int close = line.IndexOf(']');
        if (close < 0) return;
        string body = line[(close + 1)..].TrimStart();
        DateTime time = ParseTime(line) ?? DateTime.Now; // the log's own clock, live or replayed

        if (body.StartsWith("You have entered ", StringComparison.Ordinal))
        {
            var zm = ZoneRx.Match(body);
            if (zm.Success) _zone = zm.Groups[1].Value;
            return;
        }

        var su = SkillUpRx.Match(body);
        if (su.Success)
        {
            string name = TradeskillData.Canonical(su.Groups[1].Value);
            if (_data.Find(name) is null) return; // Bash, Meditate… not ours
            int value = int.Parse(su.Groups[2].Value);
            if (!_skills.TryGetValue(name, out var old) || old.Value != value || !live)
            {
                if (old is null || value >= old.Value || live) _skills[name] = new SkillValue(value, time);
                if (live) Save();
            }
            if (live && _selected is not null && name.Equals(_selected, StringComparison.OrdinalIgnoreCase))
            {
                _skillUps++;
                SkillChanged?.Invoke(name, value);
                Changed?.Invoke();
            }
            else if (!live) { /* replays batch their save at the end */ }
            return;
        }

        var pu = PurchaseRx.Match(body);
        if (pu.Success)
        {
            string item = pu.Groups[2].Value.Trim();
            string npc = pu.Groups[3].Value.Trim();
            int count = pu.Groups[1].Success ? int.Parse(pu.Groups[1].Value) : 1;
            long copper = ParseCoins(pu.Groups[4].Value);
            _vendors[item] = new VendorMemory(npc, _zone, time);
            if (live)
            {
                Save();
                if (_selected is not null) { _purchases.Add(new Purchase(time, item, count, npc, copper)); Changed?.Invoke(); }
            }
            return;
        }

        if (!live) return; // the rest is session state

        if (body == TrivialLine) { _trivialPending = time; return; }
        if (body.StartsWith(MissingLine, StringComparison.Ordinal)) { _missingAt = time; Changed?.Invoke(); return; }

        var ok = SuccessRx.Match(body);
        if (ok.Success) { NoteCombine(ok.Groups[1].Value.Trim(), true, time); return; }
        var bad = FailRx.Match(body);
        if (bad.Success) { NoteCombine(bad.Groups[1].Value.Trim(), false, time); return; }
    }

    private void NoteCombine(string product, bool success, DateTime time)
    {
        _lastProduct = product;
        _lastCombineAt = time;
        _missingAt = null;
        if (_selected is not null)
        {
            _combines++;
            if (success) _ok++; else _fail++;
            var e = _byRecipe.GetValueOrDefault(product);
            _byRecipe[product] = success ? (e.Ok + 1, e.Fail) : (e.Ok, e.Fail + 1);
        }
        if (success && _trivialPending is { } tp && (time - tp).TotalSeconds <= TrivialPairSec)
        {
            _trivialProduct = product;
            if (_trivialSaid.Add(product)) WentTrivial?.Invoke(product);
        }
        else if (success && !product.Equals(_trivialProduct, StringComparison.OrdinalIgnoreCase))
        {
            _trivialProduct = null; // a different recipe: the amber state ends
        }
        _trivialPending = null;
        Changed?.Invoke();
    }

    /// <summary>After a replay: persist what it learned.</summary>
    public void SaveLearned() => Save();

    // ---- the view ------------------------------------------------------------------

    public sealed record Snapshot(
        TradeskillData.Skill Skill, int? Value,
        TradeskillData.Step? Current, TradeskillData.Recipe? CurrentRecipe, TradeskillData.Tier? CurrentTier, int TierIndex, int TierCount,
        bool Trivial, TradeskillData.Step? Next, TradeskillData.Recipe? NextRecipe,
        int Combines, int Ok, int Fail, int SkillUps, long SpentCopper,
        double? CombinesPerPoint, int? EstimateCombines, string? LastProduct, DateTime? LastCombineAt, bool Missing)
    {
        public int Remaining => Current is null || Value is null ? 0 : Math.Max(0, Current.To - Value.Value);
        /// <summary>0..1 along the current step (From → To).</summary>
        public double Fraction
        {
            get
            {
                if (Current is null || Value is null) return 0;
                double span = Math.Max(1, Current.To - Current.From);
                return Math.Clamp((Value.Value - Current.From) / span, 0, 1);
            }
        }
    }

    public Snapshot? Take(DateTime now)
    {
        if (_selected is null) return null;
        var skill = _data.Find(_selected);
        if (skill is null) return null;
        int? value = ValueOf(_selected);
        var steps = skill.Steps.ToList();

        // The step you are on: the last product you combined this session
        // if it is on the ladder, else the first step your skill has not
        // passed, else the first step.
        TradeskillData.Step? current = null;
        if (_lastProduct is not null) current = _data.StepFor(skill, _lastProduct, value);
        if (current is null && value is int v) current = steps.FirstOrDefault(s => s.To > v) ?? steps.LastOrDefault(); // past the last step: it stays, trivial
        current ??= steps.FirstOrDefault(); // skill unknown: the ladder's foot

        var recipe = current is null ? null : _data.RecipeFor(current.Recipe);
        // Trivial: the game said so for this product, or your skill has
        // reached the guide's mark for the step (the guide's number is the
        // ladder's truth; item pages sometimes disagree with it).
        bool trivial = current is not null && (
            (_trivialProduct is not null && _trivialProduct.Equals(_data.ProductOf(current), StringComparison.OrdinalIgnoreCase))
            || (value is int vv && vv >= current.To));
        int idx = current is null ? -1 : steps.IndexOf(current);
        var next = idx >= 0 && idx + 1 < steps.Count ? steps[idx + 1] : null;
        var tier = current is null ? null : skill.TierOf(current);
        int tierIdx = tier is null || current is null ? 0 : tier.Steps.IndexOf(current) + 1;

        double? cpp = _skillUps >= 3 && _combines > 0 ? (double)_combines / _skillUps : null;
        int? est = cpp is double c && current is not null && value is int val && current.To > val ? (int)Math.Ceiling((current.To - val) * c) : null;
        long spent = _purchases.Sum(p => p.Copper);
        bool missing = _missingAt is { } m && (now - m).TotalSeconds < 30;

        return new Snapshot(skill, value, current, recipe, tier, tierIdx, tier?.Steps.Count ?? 0, trivial, next,
            next is null ? null : _data.RecipeFor(next.Recipe),
            _combines, _ok, _fail, _skillUps, spent, cpp, est, _lastProduct, _lastCombineAt, missing);
    }

    /// <summary>Per-recipe session tally — the ladder rows show it.</summary>
    public (int Ok, int Fail) Tally(string recipe) => _byRecipe.GetValueOrDefault(recipe);

    // ---- helpers ---------------------------------------------------------------------

    public static long ParseCoins(string s)
    {
        long c = 0;
        foreach (Match m in CoinRx.Matches(s))
        {
            long n = long.Parse(m.Groups[1].Value);
            c += m.Groups[2].Value switch { "platinum" => n * 1000, "gold" => n * 100, "silver" => n * 10, _ => n };
        }
        return c;
    }

    /// <summary>"12p 4g" — the two biggest denominations that are non-zero.</summary>
    public static string Coins(long copper)
    {
        if (copper <= 0) return "—";
        long p = copper / 1000, g = copper % 1000 / 100, s = copper % 100 / 10, c = copper % 10;
        var parts = new List<string>();
        if (p > 0) parts.Add($"{p}p");
        if (g > 0) parts.Add($"{g}g");
        if (p == 0 && s > 0) parts.Add($"{s}s");
        if (p == 0 && g == 0 && c > 0) parts.Add($"{c}c");
        return string.Join(" ", parts.Take(2));
    }

    private static DateTime? ParseTime(string line)
    {
        int close = line.IndexOf(']');
        if (close < 5) return null;
        return DateTime.TryParseExact(line[1..close], "ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var t) ? t : null;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var doc = JsonSerializer.Deserialize<Doc>(File.ReadAllText(_path), JsonOpts);
            if (doc is null) return;
            foreach (var (k, v) in doc.Skills) _skills[k] = v;
            foreach (var (k, v) in doc.Vendors) _vendors[k] = v;
        }
        catch (Exception ex) { Log.Warn("tradeskill-log.json unreadable: " + ex.Message); }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new Doc
            {
                Skills = new Dictionary<string, SkillValue>(_skills),
                Vendors = new Dictionary<string, VendorMemory>(_vendors),
            }, JsonOpts));
        }
        catch (Exception ex) { Log.Warn("tradeskill-log.json not saved: " + ex.Message); }
    }
}
