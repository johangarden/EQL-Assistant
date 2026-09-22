using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EQLOverlay.Services;

/// <summary>
/// The tradeskill helper's knowledge (owner, 21 Sep): eqlwiki's leveling
/// ladders for the nine tradeskills (hand-curated from each Skill page's
/// guide — their shapes differ too much to parse) and, from the item pages'
/// uniform template, every ladder recipe with its ingredients, container,
/// yield and trivial, plus where each ingredient comes from (vendor, drop
/// with mob and zone, forage, a sub-combine). Embedded as
/// <c>data/tradeskills.json</c>; built by the scratch script
/// <c>build-tradeskills.py</c>.
/// </summary>
public sealed class TradeskillData
{
    public sealed class Ingredient
    {
        public string Item { get; set; } = "";
        public int Count { get; set; } = 1;
        /// <summary>vendor · made · drop · forage · quest · summoned · unknown.</summary>
        public string Source { get; set; } = "";
        public bool Returned { get; set; }
        public List<string>? Alternatives { get; set; }
    }

    public sealed class Recipe
    {
        public string Item { get; set; } = "";
        public string Skill { get; set; } = "";
        public int Trivial { get; set; }
        public int Yield { get; set; } = 1;
        public string Container { get; set; } = "";
        public List<Ingredient> Ingredients { get; set; } = new();
    }

    public sealed class Step
    {
        public string Recipe { get; set; } = "";
        public int From { get; set; }
        public int To { get; set; }
        public string? Label { get; set; }
        public List<string>? Notes { get; set; }
        public string Title => string.IsNullOrEmpty(Label) ? Recipe : Label!;
    }

    public sealed class Tier
    {
        public string Name { get; set; } = "";
        public List<Step> Steps { get; set; } = new();
    }

    public sealed class Skill
    {
        public string Name { get; set; } = "";
        public string Page { get; set; } = "";
        public int Cap { get; set; } = 250;
        public int StartsAt { get; set; }
        public string Classes { get; set; } = "";
        public string Intro { get; set; } = "";
        public List<Tier> Tiers { get; set; } = new();
        public IEnumerable<Step> Steps => Tiers.SelectMany(t => t.Steps);
        public Tier? TierOf(Step s) => Tiers.FirstOrDefault(t => t.Steps.Contains(s));
    }

    public sealed class Vendor { public string Zone { get; set; } = ""; public string Npc { get; set; } = ""; }
    public sealed class Drop { public string Zone { get; set; } = ""; public List<string> Mobs { get; set; } = new(); }

    public sealed class ItemInfo
    {
        public List<Vendor>? Vendors { get; set; }
        public List<Drop>? Drops { get; set; }
        public List<string>? Forage { get; set; }
        public bool Crafted { get; set; }
    }

    private sealed class Doc
    {
        public string Source { get; set; } = "";
        public string Fetched { get; set; } = "";
        public List<Skill> Skills { get; set; } = new();
        public Dictionary<string, Recipe> Recipes { get; set; } = new();
        public Dictionary<string, ItemInfo> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public IReadOnlyList<Skill> Skills { get; }
    private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemInfo> _items = new(StringComparer.OrdinalIgnoreCase);
    public string Fetched { get; }

    /// <summary>The game's skill names where they differ from the wiki's page.</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Jewelry Making"] = "Jewelcrafting",
        ["Jewelry Craft"] = "Jewelcrafting",
        ["Smithing"] = "Blacksmithing",
    };

    public TradeskillData() : this(LoadDoc()) { }

    private TradeskillData(Doc doc)
    {
        Skills = doc.Skills;
        Fetched = doc.Fetched;
        foreach (var (k, v) in doc.Recipes) { if (v.Item.Length == 0) v.Item = k; _recipes[k] = v; }
        foreach (var (k, v) in doc.Items) _items[k] = v;
    }

    private static Doc LoadDoc()
    {
        try
        {
            string diskPath = Path.Combine(AppContext.BaseDirectory, "data", "tradeskills.json");
            string? json = null;
            if (File.Exists(diskPath)) json = File.ReadAllText(diskPath);
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("tradeskills.json", StringComparison.OrdinalIgnoreCase));
                if (res is not null)
                {
                    using var stream = asm.GetManifestResourceStream(res)!;
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                }
            }
            if (json is null) return new Doc();
            return JsonSerializer.Deserialize<Doc>(json, JsonOpts) ?? new Doc();
        }
        catch (Exception ex)
        {
            Log.Warn("tradeskills.json failed to load: " + ex.Message);
            return new Doc();
        }
    }

    /// <summary>The skill by its wiki name or a game alias ("Jewelry Making").</summary>
    public Skill? Find(string name)
    {
        string n = Aliases.TryGetValue(name.Trim(), out var a) ? a : name.Trim();
        return Skills.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    public static string Canonical(string name) => Aliases.TryGetValue(name.Trim(), out var a) ? a : name.Trim();

    /// <summary>A recipe by its key (a step's <see cref="Step.Recipe"/>) or by
    /// the product it makes — Fletching's arrows share one product name over
    /// several steps, each keyed by its label.</summary>
    public Recipe? RecipeFor(string keyOrProduct)
    {
        string k = keyOrProduct.Trim();
        if (_recipes.TryGetValue(k, out var r)) return r;
        return _recipes.Values.FirstOrDefault(x => x.Item.Equals(k, StringComparison.OrdinalIgnoreCase));
    }

    public ItemInfo? Item(string name) => _items.TryGetValue(name.Trim(), out var i) ? i : null;
    public int RecipeCount => _recipes.Count;

    /// <summary>The product a step makes (its recipe's item, else its key).</summary>
    public string ProductOf(Step s) => RecipeFor(s.Recipe)?.Item ?? s.Recipe;

    /// <summary>The ladder step a product belongs to, if any (the step whose
    /// recipe made it; Fletching names one arrow on several steps — the one
    /// whose trivial is highest but not above the skill wins).</summary>
    public Step? StepFor(Skill skill, string product, int? value)
    {
        var hits = skill.Steps.Where(s => ProductOf(s).Equals(product, StringComparison.OrdinalIgnoreCase)
                                          || s.Recipe.Equals(product, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0) return null;
        if (hits.Count == 1 || value is null) return hits[0];
        int v = value.Value;
        return hits.FirstOrDefault(s => s.From <= v && v < s.To) ?? hits.FirstOrDefault(s => s.To > v) ?? hits[^1];
    }

    /// <summary>The nine, for UI that must not load the data (the Manager's table).</summary>
    public static readonly string[] Names =
        { "Alchemy", "Baking", "Blacksmithing", "Brewing", "Fletching", "Jewelcrafting", "Pottery", "Tailoring", "Tinkering" };

    // ---- sourcing ----------------------------------------------------------------

    /// <summary>What a step needs you to do besides shopping.</summary>
    public sealed record Sourcing(List<string> Farm, List<string> Forage, List<string> Unknown, List<string> MadeUnknown)
    {
        public bool AllBought => Farm.Count == 0 && Forage.Count == 0 && Unknown.Count == 0 && MadeUnknown.Count == 0;
        /// <summary>"ALL BOUGHT" · "FARM 1" · "FORAGE" · "CHECK" · "MADE".</summary>
        public string Tag =>
            Farm.Count > 0 ? $"FARM {Farm.Count}"
            : Forage.Count > 0 ? "FORAGE"
            : Unknown.Count > 0 ? "CHECK"
            : MadeUnknown.Count > 0 ? "MADE"
            : "ALL BOUGHT";
        public string Kind => Farm.Count > 0 ? "farm" : Forage.Count > 0 ? "forage" : Unknown.Count > 0 ? "check" : MadeUnknown.Count > 0 ? "made" : "shop";
    }

    /// <summary>Walks the recipe and its sub-combines (two deep): a made
    /// ingredient inherits its own recipe's sources, so a step is ALL BOUGHT
    /// only when the whole chain is.</summary>
    public Sourcing SourcingOf(Recipe recipe)
    {
        var s = new Sourcing(new(), new(), new(), new());
        Walk(recipe, s, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return s;
    }

    private void Walk(Recipe r, Sourcing s, int depth, HashSet<string> seen)
    {
        foreach (var ing in r.Ingredients)
        {
            if (!seen.Add(ing.Item)) continue;
            switch (ing.Source)
            {
                case "vendor": case "summoned": break;
                case "drop": s.Farm.Add(ing.Item); break;
                case "forage": s.Forage.Add(ing.Item); break;
                case "quest": s.Farm.Add(ing.Item); break;
                case "made":
                    var sub = RecipeFor(ing.Item);
                    if (sub is not null && depth < 2) Walk(sub, s, depth + 1, seen);
                    else if (sub is null) s.MadeUnknown.Add(ing.Item);
                    break;
                default: s.Unknown.Add(ing.Item); break;
            }
        }
    }

    /// <summary>"drops from an undead cyclops, Southern Karana" — the first
    /// zone's first mobs; "" when the wiki does not place it.</summary>
    public string WhereLine(string item)
    {
        var info = Item(item);
        if (info is null) return "";
        if (info.Drops is { Count: > 0 } d)
        {
            var first = d[0];
            string mobs = string.Join(", ", first.Mobs.Take(2));
            string more = d.Count > 1 ? $" (+{d.Count - 1} more zones)" : "";
            return $"drops from {mobs}, {first.Zone}{more}";
        }
        if (info.Forage is { Count: > 0 } f) return $"foraged in {string.Join(", ", f.Take(3))}";
        if (info.Vendors is { Count: > 0 } v)
            return $"sold in {string.Join(", ", v.Select(x => x.Zone).Distinct().Take(4))}";
        return "";
    }
}
