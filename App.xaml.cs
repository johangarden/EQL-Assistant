using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EQLOverlay.Services;
using EQLOverlay.Views;

namespace EQLOverlay;

public partial class App : Application
{
    private Mutex? _instanceMutex; // held for the app's lifetime (single-instance guard)

    protected override void OnStartup(StartupEventArgs e)
    {
        // Self-update finisher: this process IS the freshly downloaded exe in
        // %TEMP%. Overwrite the real exe once the old app exits, relaunch it, die.
        int fu = Array.IndexOf(e.Args, "--finish-update");
        if (fu >= 0 && fu + 2 < e.Args.Length)
        {
            string? err = Services.UpdateService.FinishUpdate(e.Args[fu + 1], e.Args[fu + 2]);
            if (err is not null)
                MessageBox.Show(
                    $"Update failed:\n{err}\n\nGrab the new version manually from\n" +
                    $"github.com/{Services.UpdateService.Repo}/releases",
                    "EQL Assistant updater", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // Gated smoke test: construct the manager window (forces XAML parse) and
        // exit. Used to verify the build without a human clicking. Not user-facing.
        int rg = Array.IndexOf(e.Args, "--render-glyphs");
        if (rg >= 0)
        {
            try
            {
                RenderGlyphSheet(rg + 1 < e.Args.Length
                    ? e.Args[rg + 1]
                    : Path.Combine(Path.GetTempPath(), "eql_glyphs.png"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_glyphs_error.txt"), ex.ToString());
            }
            Shutdown();
            return;
        }

        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return; // skip base.OnStartup so the normal overlay isn't created
        }

        if (e.Args.Contains("--selftest-engine"))
        {
            RunEngineSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-loadout"))
        {
            RunLoadoutSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-overlay"))
        {
            RunOverlaySelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-meter"))
        {
            RunMeterSelfTest();
            return;
        }

        if (e.Args.Contains("--selftest-repop"))
        {
            RunRepopSelfTest();
            return;
        }

        // Gated: replay a whole log file through the combat parser and dump a
        // coverage report — used to validate parsing against real gameplay.
        int replayIdx = Array.IndexOf(e.Args, "--replay");
        if (replayIdx >= 0 && replayIdx + 1 < e.Args.Length)
        {
            RunReplay(e.Args[replayIdx + 1]);
            return;
        }

        // Single instance per exe path — a double-click race otherwise gives two
        // overlays fighting over the same config files. (Keyed on the path so a
        // dev build and a separate copied exe can still run side by side.)
        // NB: not string.GetHashCode — that's randomized per process in .NET.
        string mutexKey = "EQL_Assistant_" + (Environment.ProcessPath ?? "unknown")
            .ToLowerInvariant().Replace('\\', '_').Replace(':', '_').Replace('/', '_');
        _instanceMutex = new Mutex(true, mutexKey, out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show(
                "EQL Assistant is already running — look for it in the system tray.",
                "EQL Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Init();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"===== EQL Assistant v{ver} starting =====");
        Log.Info($"exe: {Environment.ProcessPath}");
        Log.Info($"log: {Log.Path}");
        UpdateService.CleanupTempUpdaters();

        base.OnStartup(e);

        // Don't let a stray exception (e.g. a bad regex in a user-edited config)
        // silently kill the overlay. Show it and keep running where possible.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void RunSelfTest()
    {
        try
        {
            var cs = new ConfigService();
            var cfg = cs.LoadSettings();
            cs.EnsureDefaultLoadout();
            var mgr = new TriggerManagerWindow(cs, cfg, new LogBus(), new AlertService(),
                new RaidKills(cs), new SpellLibrary(cs), new CombatParser(), _ => { });
            mgr.Show();
            mgr.Close();

            // Death recap window builds from a real-log-shaped death.
            var cp = new CombatParser();
            CombatParser.DeathEvent? death = null;
            cp.PlayerDied += d => death = d;
            cp.ProcessLine("[Sat Aug 08 23:21:35 2026] A bok ghoul knight hits YOU for 16 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] A zol ghoul knight hits YOU for 49 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] You have been slain by a bok ghoul knight!");
            if (death is null) throw new InvalidOperationException("death recap event did not fire");
            var recap = new Views.DeathRecapWindow(death);
            recap.Show();
            recap.Close();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest.txt"), "OK");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest.txt"), "FAIL\n" + ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            Shutdown();
        }
    }

    private void RunEngineSelfTest()
    {
        var report = new System.Text.StringBuilder();
        int failures = 0;
        void Check(string label, bool ok)
        {
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok) failures++;
        }

        try
        {
            string now = DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);

            var cfg = new Models.AppConfig();
            cfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "sow", Name = "Spirit of Wolf", Category = "Buffs",
                StartPattern = @"You feel the spirit of wolf enter you\.",
                EndPattern = @"Your Spirit of Wolf spell has worn off\.",
                DurationSeconds = 1800,
            });
            cfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "hot", Name = "HoT", Category = "HoTs",
                StartPattern = @"(?<target>\w+) begins to regenerate\.",
                DurationSeconds = 60,
            });
            foreach (var t in cfg.Triggers) ConfigService.CompileOne(t);

            var engine = new TriggerEngine(cfg, new AlertService());

            engine.ProcessLine($"[{now}] You feel the spirit of wolf enter you.");
            Check("SoW land -> 1 bar", engine.Bars.Count == 1);
            Check("SoW bar name", engine.Bars.Count == 1 && engine.Bars[0].Name == "Spirit of Wolf");
            Check("SoW remaining ~1800", engine.Bars.Count == 1 && engine.Bars[0].RemainingSeconds > 1700);

            engine.ProcessLine($"[{now}] Your Spirit of Wolf spell has worn off.");
            Check("SoW worn off -> 0 bars", engine.Bars.Count == 0);

            engine.ProcessLine($"[{now}] Bob begins to regenerate.");
            Check("HoT target capture -> 1 bar", engine.Bars.Count == 1);
            Check("HoT bar labelled with target", engine.Bars.Count == 1 && engine.Bars[0].Name == "HoT — Bob");

            engine.ProcessLine("a line matching nothing at all");
            Check("non-matching line ignored", engine.Bars.Count == 1);

            // Cooldown reducer: SK Reave shaving time off the Harm Touch cooldown.
            var ht = new Models.TriggerDefinition
            {
                Id = "ht", Name = "Harm Touch", Category = "Cooldowns",
                StartPattern = @"^You begin casting Harm Touch",
                DurationSeconds = 1200,
                ReducePattern = @"^You reave ", ReduceSeconds = 60,
            };
            ConfigService.CompileOne(ht);
            cfg.Triggers.Add(ht); // engine holds the same list
            double reducedTotal = 0; string? reducedName = null;
            engine.BarReduced += (n, s) => { reducedName = n; reducedTotal += s; };

            engine.ProcessLine($"[{now}] You reave a gnoll for 9 points of damage.");
            Check("reducer: no running bar -> nothing to cut", reducedTotal == 0);

            engine.ProcessLine($"[{now}] You begin casting Harm Touch II.");
            var htBar = engine.Bars.First(b => b.Name == "Harm Touch");
            var endBefore = htBar.EndTimeLocal;
            engine.ProcessLine($"[{now}] You reave a gnoll for 24 points of damage.");
            engine.ProcessLine($"[{now}] You reave a gnoll elite for 11 points of damage.");
            Check("reducer: two reaves cut 120s off Harm Touch",
                Math.Abs((endBefore - htBar.EndTimeLocal).TotalSeconds - 120) < 0.01
                && reducedName == "Harm Touch" && reducedTotal == 120);
            engine.ProcessLine($"[{now}] You slash a gnoll for 5 points of damage.");
            Check("reducer: unrelated line cuts nothing",
                Math.Abs((endBefore - htBar.EndTimeLocal).TotalSeconds - 120) < 0.01);

            // Saving in the Manager re-applies config WITHOUT Reset: running
            // bars and active matrix timers must survive; only triggers the
            // new config dropped get pruned.
            var buff = new Models.TriggerDefinition
            {
                Id = "aego", Name = "Aegolism", Panel = Models.Panels.SelfBuffs,
                StartPattern = @"You feel the aura of the faithful\.",
                DurationSeconds = 3600,
            };
            ConfigService.CompileOne(buff);
            cfg.Triggers.Add(buff);
            engine.UpdateConfig(cfg); // pick up the new matrix trigger
            engine.ProcessLine($"[{now}] You feel the aura of the faithful.");
            var aego = engine.SelfCells.First(c => c.Key == "aego");
            Check("save-preserve: matrix cell active before save", aego.IsActive);
            int barsBefore = engine.Bars.Count;

            engine.UpdateConfig(cfg); // what a Manager save now does
            Check("save-preserve: running bars survive a settings save",
                engine.Bars.Count == barsBefore
                && engine.Bars.Any(b => b.Name == "Harm Touch")
                && engine.Bars.Any(b => b.Name == "HoT — Bob"));
            var aego2 = engine.SelfCells.First(c => c.Key == "aego");
            Check("save-preserve: matrix timer survives a settings save",
                aego2.IsActive
                && Math.Abs((aego2.EndTimeLocal - aego.EndTimeLocal).TotalSeconds) < 0.01);

            cfg.Triggers.Remove(ht);
            engine.UpdateConfig(cfg);
            Check("save-preserve: deleted trigger's bar is pruned",
                engine.Bars.All(b => b.Name != "Harm Touch")
                && engine.Bars.Any(b => b.Name == "HoT — Bob"));
            cfg.Triggers.Add(ht);

            // Loot line parsing (all three real forms from the log).
            Check("loot: upgrade form", LootTracker.TryParseLoot(
                "You looted a Platinum Ring +1 from Gynok Moltor's corpse to create a Platinum Ring +4",
                out var lk, out var li, out var lm, out var lr, out _, out _)
                && lk == LootTracker.LootKind.Upgrade && li == "Platinum Ring +1"
                && lm == "Gynok Moltor" && lr == "Platinum Ring +4");
            Check("loot: kept form strips article", LootTracker.TryParseLoot(
                "--You have looted a Raw-Hide Gorget +2 from a ghoul's corpse.--",
                out lk, out li, out lm, out lr, out _, out _)
                && lk == LootTracker.LootKind.Kept && li == "Raw-Hide Gorget +2" && lm == "a ghoul");
            Check("loot: kept stack splits its count", LootTracker.TryParseLoot(
                "--You have looted 2 Bone Chips from an elf skeleton's corpse.--",
                out lk, out li, out lm, out lr, out _, out int lcount)
                && li == "Bone Chips" && lcount == 2 && lm == "an elf skeleton");
            Check("loot: sold form + coin math", LootTracker.TryParseLoot(
                "You looted a Bronze Spear +1 from Priest Amiaz's corpse and sold it for 2 platinum, 2 gold, 1 silver and 4 copper.",
                out lk, out li, out lm, out lr, out long lc, out _)
                && lk == LootTracker.LootKind.Sold && li == "Bronze Spear +1" && lc == 2214);
            Check("loot: coin formatting", LootTracker.FormatCoins(2214) == "2p 2g 1s 4c");
            Check("loot: combat line is not loot", !LootTracker.TryParseLoot(
                "You slash a rat for 5 points of damage.", out lk, out li, out lm, out lr, out _, out _));
            Check("loot: item key strips +N", LootTracker.ItemKey("Sphinx Claw +2") == "sphinx claw"
                && LootTracker.ItemKey("Bone Chips") == "bone chips");

            // (Catch-up prompt/mode checks removed in 2.7 — catch-up always runs.)

            // Death recap: incoming hits/misses/heals buffer up; a death line
            // snapshots them, fires once, and the twin death lines dedupe.
            var cp = new CombatParser();
            var deaths = new List<CombatParser.DeathEvent>();
            cp.PlayerDied += d => deaths.Add(d);
            cp.ProcessLine("[Sat Aug 08 23:21:34 2026] A zol ghoul knight hits YOU for 42 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:34 2026] A zol ghoul knight tries to hit YOU, but misses!");
            cp.ProcessLine("[Sat Aug 08 23:21:35 2026] Nurse heals you for 50 hit points by Minor Healing.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] A bok ghoul knight hits YOU for 24 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] You have been slain by a bok ghoul knight!");
            Check("recap: slain line fires with killer",
                deaths.Count == 1 && deaths[0].Killer == "a bok ghoul knight");
            Check("recap: events captured in order",
                deaths.Count == 1 && deaths[0].Events.Count == 4
                && deaths[0].Events[0] is { Amount: 42, Heal: false, Source: "A zol ghoul knight" }
                && deaths[0].Events[1].Miss
                && deaths[0].Events[2] is { Heal: true, Amount: 50 }
                && deaths[0].Events[3].Amount == 24);
            cp.ProcessLine("[Sat Aug 08 23:21:38 2026] You died.");
            Check("recap: twin death line within 5s is ignored", deaths.Count == 1);
            cp.ProcessLine("[Sat Aug 08 23:25:00 2026] You died.");
            Check("recap: later plain death fires fresh and empty",
                deaths.Count == 2 && deaths[1].Killer == "" && deaths[1].Events.Count == 0);

            // Trigger duration modes: auto-learn follows the estimate in EITHER
            // direction; manual enforces the configured value exactly.
            var modeCfg = new Models.AppConfig();
            modeCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "qk4", Name = "Quickness", StartPattern = @"^Your step quickens\.",
                DurationSeconds = 660, DurationAuto = true,
            });
            modeCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "qk5", Name = "Ironwill", StartPattern = @"^Your will hardens\.",
                DurationSeconds = 60, DurationAuto = false,
            });
            foreach (var t in modeCfg.Triggers) ConfigService.CompileOne(t);
            string ModeTs() => DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);
            var modeEngine = new TriggerEngine(modeCfg, new AlertService())
            {
                LearnedDuration = name => name switch
                {
                    "Quickness" => 590,  // learned BELOW the configured 660
                    "Ironwill" => 300,   // learned above the configured 60
                    _ => null,
                },
            };
            modeEngine.ProcessLine($"[{ModeTs()}] Your step quickens.");
            Check("auto-learn trigger follows the estimate down",
                modeEngine.Bars.Count == 1 && modeEngine.Bars[0].RemainingSeconds is > 580 and < 595);
            modeEngine.ProcessLine($"[{ModeTs()}] Your will hardens.");
            Check("manual trigger enforces its configured time",
                modeEngine.Bars.Count == 2
                && modeEngine.Bars.First(b => b.Name == "Ironwill").RemainingSeconds is > 55 and <= 61);
            Check("triggers default to auto-learn",
                new Models.TriggerDefinition().DurationAuto);

            // Cast-anchored triggers (the Companion's landing gate): four hastes
            // all print "You feel much faster.", so a shared landing only starts
            // the bar whose own begin-cast line it follows — and an unanchored
            // ambiguous landing starts NOTHING (a guessed bar lies about the
            // duration). Auto anchors library (lib-*) triggers with shared text.
            static string Esc(string s) => System.Text.RegularExpressions.Regex.Escape(s);
            string AT(int s) => new DateTime(2026, 8, 10, 23, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            var ancCfg = new Models.AppConfig();
            ancCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-quickness", Name = "Quickness", DurationAuto = false,
                StartPattern = Esc("You feel much faster."),
                EndPattern = Esc("Your speed returns to normal."), DurationSeconds = 660,
            });
            ancCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-alacrity", Name = "Alacrity", DurationAuto = false,
                StartPattern = Esc("You feel much faster."),
                EndPattern = Esc("Your speed returns to normal."), DurationSeconds = 660,
            });
            foreach (var t in ancCfg.Triggers) ConfigService.CompileOne(t);
            var anc = new TriggerEngine(ancCfg, new AlertService())
            {
                IsSharedLanding = _ => true, // the haste line IS shared
            };
            anc.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: unanchored shared landing draws nothing", anc.Bars.Count == 0);
            anc.ProcessLine($"[{AT(10)}] You begin casting Quickness.");
            anc.ProcessLine($"[{AT(13)}] You feel much faster.");
            Check("anchor: own cast resolves the shared landing",
                anc.Bars.Count == 1 && anc.Bars[0].Name == "Quickness");
            anc.ProcessLine($"[{AT(100)}] You begin casting Alacrity.");
            anc.ProcessLine($"[{AT(103)}] Your speed returns to normal.");
            anc.ProcessLine($"[{AT(103)}] You feel much faster.");
            Check("anchor: overwriting haste starts the NEW spell's bar only",
                anc.Bars.Count == 1 && anc.Bars[0].Name == "Alacrity");
            anc.ProcessLine($"[{AT(200)}] You begin casting Quickness II.");
            anc.ProcessLine($"[{AT(203)}] You feel much faster.");
            Check("anchor: cast rank pools onto the base-named trigger",
                anc.Bars.Any(b => b.Name == "Quickness"));
            anc.ProcessLine($"[{AT(300)}] You begin casting Celerity.");
            anc.ProcessLine($"[{AT(340)}] You feel much faster."); // wrong spell AND stale (>15s)
            Check("anchor: stale or foreign cast starts nothing", anc.Bars.Count == 2);

            var offCfg = new Models.AppConfig();
            offCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-quickness", Name = "Quickness", DurationSeconds = 660,
                StartPattern = Esc("You feel much faster."), CastAnchored = false,
            });
            ConfigService.CompileOne(offCfg.Triggers[0]);
            var offEng = new TriggerEngine(offCfg, new AlertService()) { IsSharedLanding = _ => true };
            offEng.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: explicit untick beats auto", offEng.Bars.Count == 1);

            var freeCfg = new Models.AppConfig();
            freeCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "custom-haste", Name = "AnyHaste", DurationSeconds = 60,
                StartPattern = Esc("You feel much faster."),
            });
            ConfigService.CompileOne(freeCfg.Triggers[0]);
            var freeEng = new TriggerEngine(freeCfg, new AlertService()) { IsSharedLanding = _ => true };
            freeEng.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: custom triggers stay unanchored on auto", freeEng.Bars.Count == 1);
            freeCfg.Triggers[0].CastAnchored = true;
            var freeEng2 = new TriggerEngine(freeCfg, new AlertService()) { IsSharedLanding = _ => true };
            freeEng2.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: explicit tick anchors a custom trigger", freeEng2.Bars.Count == 0);
            freeEng2.ProcessLine($"[{AT(10)}] You begin casting AnyHaste.");
            freeEng2.ProcessLine($"[{AT(12)}] You feel much faster.");
            Check("anchor: anchored custom trigger fires after its own named cast",
                freeEng2.Bars.Count == 1);

            // Duration learning: cast-anchored landing -> wear-off mints a sample;
            // unanchored broadcasts don't; early breaks never lower the estimate;
            // death contaminates; ranks pool; samples persist across restarts.
            string durPath = Path.Combine(Path.GetTempPath(), "eql_dur_test.json");
            File.Delete(durPath);
            var lib2 = new SpellLibrary(new ConfigService());

            // Trigger typing: the wiki type wins, classic landing lines fill
            // the gaps, the bucket is the fallback — HoTs are not just buffs.
            string CatOf(string name) => lib2.FindByName(name) is { } sp
                ? SpellLibrary.TriggerCategory(sp) : "?";
            Check("typing: Snails Healing is a HoT (wiki type)", CatOf("Snails Healing") == "HoTs");
            Check("typing: Envenomed Bolt is a DoT (poison landing)", CatOf("Envenomed Bolt") == "DoTs");
            Check("typing: Boil Blood is a DoT (blood boils)", CatOf("Boil Blood") == "DoTs");
            Check("typing: Regeneration is a HoT (regenerate landing)", CatOf("Regeneration") == "HoTs");
            Check("typing: Quickness stays a buff", CatOf("Quickness") == "Buffs");
            var retype = new[]
            {
                new Models.TriggerDefinition { Id = "lib-envenomed-bolt", Name = "Envenomed Bolt", Category = "Debuffs" },
                new Models.TriggerDefinition { Id = "lib-quickness", Name = "Quickness", Category = "Buffs" },
                new Models.TriggerDefinition { Id = "lib-snails-healing", Name = "Snails Healing", Category = "MyOwn" },
                new Models.TriggerDefinition { Id = "custom-1", Name = "Envenomed Bolt", Category = "Debuffs" },
            };
            Check("typing: retype heals lib defaults, spares custom types and ids",
                lib2.RetypeLibraryTriggers(retype) == 1
                && retype[0].Category == "DoTs" && retype[1].Category == "Buffs"
                && retype[2].Category == "MyOwn" && retype[3].Category == "Debuffs");

            Check("anchor: library flags the shared haste landing as ambiguous",
                lib2.IsSharedLanding(Esc("You feel much faster."))
                && !lib2.IsSharedLanding("not a spell line at all"));
            var dur = new SpellDurations(new ConfigService(), lib2, durPath);
            Check("durations: rank suffix pools",
                SpellDurations.BaseKey("Mesmerization VII") == "mesmerization"
                && SpellDurations.BaseKey("Quickness II") == SpellDurations.BaseKey("Quickness"));
            string T(int s) => new DateTime(2026, 8, 9, 20, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            dur.ProcessLine($"[{T(0)}] You begin casting Spirit of Wolf.");
            dur.ProcessLine($"[{T(3)}] You feel the spirit of wolf enter you.");
            dur.ProcessLine($"[{T(2403)}] The spirit of wolf leaves you.");
            Check("durations: full cycle mints a 2400s sample",
                dur.LearnedMaxSeconds("Spirit of Wolf") is double d1 && Math.Abs(d1 - 2400) < 0.01);
            dur.ProcessLine($"[{T(3000)}] You begin casting Spirit of Wolf.");
            dur.ProcessLine($"[{T(3003)}] You feel the spirit of wolf enter you.");
            dur.ProcessLine($"[{T(3100)}] The spirit of wolf leaves you.");
            Check("durations: an early break never lowers the estimate",
                dur.LearnedMaxSeconds("Spirit of Wolf") is double d2 && Math.Abs(d2 - 2400) < 0.01);
            dur.ProcessLine($"[{T(4000)}] You feel the spirit of wolf enter you."); // no cast anchor
            dur.ProcessLine($"[{T(4100)}] The spirit of wolf leaves you.");
            Check("durations: unanchored broadcast teaches nothing",
                dur.SampleCount("Spirit of Wolf") == 2);
            dur.ProcessLine($"[{T(5000)}] You begin casting Spirit of Wolf.");
            dur.ProcessLine($"[{T(5003)}] You feel the spirit of wolf enter you.");
            dur.ProcessLine($"[{T(5050)}] You died.");
            dur.ProcessLine($"[{T(5100)}] The spirit of wolf leaves you.");
            Check("durations: death contaminates the open cycle",
                dur.SampleCount("Spirit of Wolf") == 2);
            dur.ProcessLine($"[{T(6000)}] You begin casting Spirit of Wolf.");
            dur.ProcessLine($"[{T(6003)}] You feel the spirit of wolf enter you.");
            dur.ProcessLine($"[{T(6050)}] LOADING, PLEASE WAIT...");
            dur.ProcessLine($"[{T(9000)}] The spirit of wolf leaves you.");
            Check("durations: zoning contaminates (buff timers pause while zoning)",
                dur.SampleCount("Spirit of Wolf") == 2);
            dur.ProcessLine($"[{T(10000)}] You begin casting Spirit of Wolf.");
            dur.ProcessLine($"[{T(10003)}] You feel the spirit of wolf enter you.");
            dur.ProcessLine($"[{T(10500)}] You feel the spirit of wolf enter you."); // external re-haste
            dur.ProcessLine($"[{T(12403)}] The spirit of wolf leaves you.");
            Check("durations: an external re-land contaminates the cycle",
                dur.SampleCount("Spirit of Wolf") == 2);
            var dur2 = new SpellDurations(new ConfigService(), lib2, durPath);
            Check("durations: samples persist across restarts",
                dur2.LearnedMaxSeconds("Spirit of Wolf") is double d3 && Math.Abs(d3 - 2400) < 0.01);
            dur2.ProcessLine($"[{T(0)}] You begin casting Spirit of Wolf.");
            dur2.ProcessLine($"[{T(3)}] You feel the spirit of wolf enter you.");
            dur2.ProcessLine($"[{T(2403)}] The spirit of wolf leaves you.");
            Check("durations: a replayed line never double-counts (reparse-safe)",
                dur2.SampleCount("Spirit of Wolf") == 2);
            File.Delete(durPath);

            // Engine: a learned duration EXTENDS a bar; the configured value is a floor.
            var learnCfg = new Models.AppConfig();
            learnCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "qk3", Name = "Quickness", Category = "Buffs",
                StartPattern = @"^Your feet move faster\.", DurationSeconds = 60,
            });
            foreach (var t in learnCfg.Triggers) ConfigService.CompileOne(t);
            var learnEngine = new TriggerEngine(learnCfg, new AlertService())
            {
                LearnedDuration = name => name == "Quickness" ? 90 : null,
            };
            string NowTs() => DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);
            learnEngine.ProcessLine($"[{NowTs()}] Your feet move faster.");
            Check("engine: learned duration extends the bar",
                learnEngine.Bars.Count == 1 && learnEngine.Bars[0].RemainingSeconds > 80);
            learnEngine.LearnedDuration = _ => 30; // estimate corrected downward
            learnEngine.ProcessLine($"[{NowTs()}] Your feet move faster.");
            double restartRemaining = (learnEngine.Bars[0].EndTimeLocal - DateTime.Now).TotalSeconds;
            Check("engine: auto-learn refresh follows a corrected estimate",
                restartRemaining is > 25 and <= 31);

            // Loot-per-kill: drops pin to the most recent kill of their mob
            // within the window; strangers/late loot don't; backfill is guarded.
            string rkPath = Path.Combine(Path.GetTempPath(), "eql_rk_test.json");
            File.Delete(rkPath);
            var rk2 = new RaidKills(new ConfigService(), rkPath);
            var killAt = new DateTime(2026, 8, 9, 21, 0, 0);
            rk2.ProcessLine("[x] Lady Vox has been slain by Johan!", killAt);
            Check("kill loot: kept drop attaches to the kill",
                rk2.AttributeLoot(new LootTracker.LootEntry(killAt.AddMinutes(2),
                    "Mystic Cloak", "Lady Vox", "Permafrost", LootTracker.LootKind.Kept))
                && rk2.KillsFor("Lady Vox")[0].Items is [{ Item: "Mystic Cloak", Count: 1 }]);
            Check("kill loot: same item aggregates its count",
                rk2.AttributeLoot(new LootTracker.LootEntry(killAt.AddMinutes(3),
                    "Mystic Cloak", "Lady Vox", "Permafrost", LootTracker.LootKind.Kept))
                && rk2.KillsFor("Lady Vox")[0].Items is [{ Count: 2 }]);
            Check("kill loot: unlisted mob is ignored",
                !rk2.AttributeLoot(new LootTracker.LootEntry(killAt.AddMinutes(2),
                    "Bone Chips", "a rat", "Permafrost", LootTracker.LootKind.Kept)));
            Check("kill loot: loot outside the window is ignored",
                !rk2.AttributeLoot(new LootTracker.LootEntry(killAt.AddHours(2),
                    "Late Item", "Lady Vox", "Permafrost", LootTracker.LootKind.Kept)));
            rk2.BackfillLoot(new[] { new LootTracker.LootEntry(killAt.AddMinutes(4),
                "Backfill Item", "Lady Vox", "Permafrost", LootTracker.LootKind.Kept) });
            Check("kill loot: backfill skips once items exist",
                rk2.KillsFor("Lady Vox")[0].Items.All(i => i.Item != "Backfill Item"));

            // Fight link: an archived raid fight stamps its kill with the
            // time-to-kill + the history key; "+N" multi-pull labels resolve.
            Check("fight link: labels resolve raid targets",
                rk2.IsTarget("Lady Vox") && rk2.IsTarget("Lady Vox +2") && !rk2.IsTarget("a rat"));
            Check("fight link: fight stamps TTK onto the kill",
                rk2.AttachFight("Lady Vox +1", killAt.AddSeconds(20), 185)
                && rk2.KillsFor("Lady Vox")[0] is { FightSeconds: 185, FightLabel: "Lady Vox +1" }
                && rk2.KillsFor("Lady Vox")[0].FightEndedAt == killAt.AddSeconds(20));
            Check("fight link: unknown label attaches nothing",
                !rk2.AttachFight("a rat +1", killAt, 30));
            Check("fight link: far-away fight attaches nothing",
                !rk2.AttachFight("Lady Vox", killAt.AddHours(3), 60));
            File.Delete(rkPath);

            // Type-owned colors (2.9): the category keyword decides — and the
            // order traps matter ("Debuffs" contains "buff", "HoTs" ≠ "DoTs").
            Check("colors: buffs blue / hots green / dots red / debuffs yellow",
                TriggerColors.ForCategory("Buffs") == TriggerColors.Buff
                && TriggerColors.ForCategory("HoTs") == TriggerColors.Heal
                && TriggerColors.ForCategory("Heals over time") == TriggerColors.Heal
                && TriggerColors.ForCategory("DoTs") == TriggerColors.Dot
                && TriggerColors.ForCategory("Debuffs") == TriggerColors.Debuff
                && TriggerColors.ForCategory("Cooldowns") == TriggerColors.Cooldown
                && TriggerColors.ForCategory("Whatever") == TriggerColors.Other);
            Check("colors: panels override — flash amber, repop teal, matrices typed",
                TriggerColors.For(Models.Panels.Flash, "Buffs") == TriggerColors.Flash
                && TriggerColors.For(Models.Panels.TimerAuto, "") == TriggerColors.Repop
                && TriggerColors.For(Models.Panels.SelfBuffs, "") == TriggerColors.Buff
                && TriggerColors.For(Models.Panels.TargetDebuffs, "") == TriggerColors.Debuff);

            // A speak phrase with no timing defaults to the expiry alert.
            var mute = new Models.TriggerDefinition
            {
                Id = "qk", Name = "Quickness", StartPattern = "x",
                Alert = new Models.AlertConfig { Speak = "Quickness faded" },
            };
            ConfigService.CompileOne(mute);
            Check("alert: speak with no timing fires on expire", mute.Alert!.OnExpire);
            var timed = new Models.TriggerDefinition
            {
                Id = "qk2", Name = "Quickness", StartPattern = "x",
                Alert = new Models.AlertConfig { Speak = "fading", AtSeconds = 20 },
            };
            ConfigService.CompileOne(timed);
            Check("alert: timed speak is left alone", !timed.Alert!.OnExpire);

            // Self-update: tag parsing, release-JSON asset picking, compare, copy-swap.
            Check("update: tags parse normalized",
                UpdateService.TryParseVersion("v2.4.0", out var uv) && uv == new Version(2, 4, 0, 0)
                && UpdateService.TryParseVersion("2.10", out var uv2) && uv2 == new Version(2, 10, 0, 0)
                && !UpdateService.TryParseVersion("beta", out _)
                && !UpdateService.TryParseVersion("", out _));
            var rel = UpdateService.ParseRelease(
                """{"tag_name":"v9.9.0","html_url":"https://x/rel","assets":[{"name":"notes.txt","size":5,"browser_download_url":"https://x/n"},{"name":"EQL_Assistant-v9.9.exe","size":123,"browser_download_url":"https://x/e"}]}""");
            Check("update: release json picks the exe asset",
                rel is { AssetName: "EQL_Assistant-v9.9.exe", AssetSize: 123, Tag: "v9.9.0" }
                && rel.Version == new Version(9, 9, 0, 0));
            Check("update: exe-less release is rejected",
                UpdateService.ParseRelease("""{"tag_name":"v9.9.0","assets":[{"name":"a.zip","size":1,"browser_download_url":"u"}]}""") is null);
            Check("update: newer/equal compare",
                UpdateService.IsNewer(new Version(99, 0, 0, 0))
                && !UpdateService.IsNewer(UpdateService.CurrentVersion));
            string swapSrc = Path.Combine(Path.GetTempPath(), "eql_swap_src.txt");
            string swapDst = Path.Combine(Path.GetTempPath(), "eql_swap_dst.txt");
            File.WriteAllText(swapSrc, "NEW");
            File.WriteAllText(swapDst, "OLD");
            Check("update: copy-swap overwrites in place",
                UpdateService.CopyWithRetry(swapSrc, swapDst) is null
                && File.ReadAllText(swapDst) == "NEW");
            File.Delete(swapSrc);
            File.Delete(swapDst);

            // Friendly durations (trigger/respawn fields + repop prompts).
            Check("duration: parses all the friendly forms",
                DurationText.Parse("660") == 660
                && DurationText.Parse("11m") == 660
                && DurationText.Parse("9m12s") == 552
                && DurationText.Parse("9:12") == 552
                && DurationText.Parse("1h20m5s") == 4805
                && DurationText.Parse("1:20:05") == 4805
                && DurationText.Parse("90s") == 90);
            Check("duration: junk is rejected",
                DurationText.Parse("") is null
                && DurationText.Parse("banana") is null
                && DurationText.Parse("9:75") is null
                && DurationText.Parse("0") is null);
            Check("duration: compact round-trip",
                DurationText.Compact(660) == "11m"
                && DurationText.Compact(552) == "9m12s"
                && DurationText.Compact(45) == "45s"
                && DurationText.Compact(4805) == "1h20m5s"
                && DurationText.Parse(DurationText.Compact(1200)) == 1200);
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            failures++;
        }

        string result = (failures == 0 ? "ALL PASS\n" : $"{failures} FAILURE(S)\n") + report;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest_engine.txt"), result);
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown();
    }

    private void RunLoadoutSelfTest()
    {
        var report = new System.Text.StringBuilder();
        int failures = 0;
        void Check(string label, bool ok)
        {
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok) failures++;
        }

        var cs = new ConfigService();
        string? testPath = null;
        try
        {
            string now = DateTime.Now.ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);

            cs.EnsureDefaultLoadout();

            // Create a distinct throwaway loadout on disk.
            var testLo = new Models.Loadout
            {
                Name = "SelfTestLO",
                Triggers =
                {
                    new Models.TriggerDefinition
                    {
                        Id = "sttest", Name = "Test Buff", Category = "Buffs",
                        StartPattern = @"ZZTESTBUFF lands on you\.", DurationSeconds = 30,
                    }
                }
            };
            cs.SaveLoadout(testLo);
            testPath = testLo.FilePath;

            var loaded = cs.LoadLoadout("SelfTestLO");
            Check("test loadout loads from disk", loaded is { Triggers.Count: 1 });

            var cfg = new Models.AppConfig { ActiveLoadout = "SelfTestLO", Triggers = loaded!.Triggers };
            var engine = new TriggerEngine(cfg, new AlertService());

            engine.ProcessLine($"[{now}] ZZTESTBUFF lands on you.");
            Check("test-loadout trigger fires", engine.Bars.Count == 1);

            // Switch to Default: the test trigger must no longer match.
            var def = cs.LoadLoadout("Default");
            Check("Default loadout loads", def is not null);
            cfg.Triggers = def!.Triggers;
            engine.Reset();
            engine.UpdateConfig(cfg);
            Check("Reset clears bars on switch", engine.Bars.Count == 0);

            engine.ProcessLine($"[{now}] ZZTESTBUFF lands on you.");
            Check("test trigger inactive under Default", engine.Bars.Count == 0);

            engine.ProcessLine($"[{now}] You feel the spirit of wolf enter you.");
            Check("Default's SoW trigger fires", engine.Bars.Count == 1);
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            failures++;
        }
        finally
        {
            // Clean up the throwaway loadout so we don't pollute the user's list.
            try { if (testPath != null && File.Exists(testPath)) File.Delete(testPath); } catch { }
        }

        string result = (failures == 0 ? "ALL PASS\n" : $"{failures} FAILURE(S)\n") + report;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest_loadout.txt"), result);
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown();
    }

    private void RunOverlaySelfTest()
    {
        bool failed = false;
        string err = "";

        // Capture any dispatcher exception (e.g. a bad value in a data template)
        // instead of popping a message box, so we can report it and exit.
        DispatcherUnhandledException += (_, ev) => { failed = true; err = ev.Exception.Message; ev.Handled = true; };

        try
        {
            var mw = new MainWindow();
            mw.SuppressStatePersistence = true; // never write window-state.json from a test
            mw.Show();
            // Let Loaded run (engine/vm get created there) before we poke it.
            Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            mw.Left = -20000; // shove off-screen so it doesn't flash
            mw.AddDemoForTest();  // create a bar -> instantiates the bar template
            mw.UpdateLayout();    // force measure/arrange (where the bad Margin threw)
            mw.Close();
        }
        catch (Exception ex)
        {
            failed = true;
            err = ex.ToString();
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest_overlay.txt"),
            failed ? "FAIL\n" + err : "OK");
        Environment.ExitCode = failed ? 1 : 0;
        Shutdown();
    }

    private void RunMeterSelfTest()
    {
        var report = new System.Text.StringBuilder();
        int failures = 0;
        void Check(string label, bool ok)
        {
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok) failures++;
        }
        void CheckNear(string label, double actual, double expected)
        {
            bool ok = Math.Abs(actual - expected) < 0.01;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label} (got {actual}, want {expected})");
            if (!ok) failures++;
        }

        try
        {
            var p = new CombatParser { SelfName = "Johan", PetName = "Jabber" };
            var sct = new List<CombatParser.SctHit>();
            p.SctEvent += hit => sct.Add(hit);

            p.ProcessLine($"[{Ts(0)}] You have entered Clan Crushbone.");
            Check("zone tracked from entry line", p.CurrentZone == "Clan Crushbone");
            string Ts(int sec) => new DateTime(2026, 8, 3, 12, 0, sec)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);

            p.ProcessLine($"[{Ts(0)}] You slash a gnoll pup for 12 points of damage.");
            p.ProcessLine($"[{Ts(2)}] Johan hit a gnoll pup for 30 points of fire damage by Burst of Flame.");
            p.ProcessLine($"[{Ts(4)}] A gnoll pup has taken 10 damage from Flame Lick by Johan.");
            p.ProcessLine($"[{Ts(6)}] Snik kicks a gnoll pup for 8 points of damage.");
            p.ProcessLine($"[{Ts(8)}] A gnoll pup hits YOU for 20 points of damage.");
            p.ProcessLine($"[{Ts(8)}] A gnoll pup bites Jabber for 15 points of damage.");
            p.ProcessLine($"[{Ts(10)}] Malahoja healed Snik for 65 hit points by Light Healing.");
            p.ProcessLine($"[{Ts(10)}] Snik healed himself for 6 hit points by Lifetap.");
            p.ProcessLine($"[{Ts(12)}] You crush a gnoll pup for 0 (65) points of damage.");
            p.ProcessLine($"[{Ts(12)}] A gnoll pup tries to bite YOU, but misses!");

            var dmg = p.GetRows(healing: false);
            var heal = p.GetRows(healing: true);
            double Total(string name) => dmg.FirstOrDefault(r => r.Name == name).Total;

            Check("in combat", p.InCombat);
            CheckNear("duration = activity window", p.DurationSeconds, 12);
            CheckNear("Johan dmg (You+named+DoT, '0 (65)' counts 0)", Total("Johan"), 52);
            CheckNear("Snik melee dmg", Total("Snik"), 8);
            CheckNear("mob's own dmg ranked too", Total("A gnoll pup"), 35);
            Check("target label is the mob", p.TargetLabel == "a gnoll pup");
            CheckNear("incoming self (YOU) total", p.IncomingSelfTotal, 20);
            CheckNear("incoming pet total", p.IncomingPetTotal, 15);
            CheckNear("heal: Malahoja", heal.FirstOrDefault(r => r.Name == "Malahoja").Total, 65);
            CheckNear("heal: himself -> healer", heal.FirstOrDefault(r => r.Name == "Snik").Total, 6);
            CheckNear("Johan dps = 52/12", dmg.FirstOrDefault(r => r.Name == "Johan").Dps, 52.0 / 12);

            // Enemy classification (single word = player-like; spaces = mob).
            Check("mob classified as enemy", p.IsEnemyName("a gnoll pup") && p.IsEnemyName("Lady Vox"));
            Check("players/pet classified friendly",
                !p.IsEnemyName("Johan") && !p.IsEnemyName("Snik") && !p.IsEnemyName("Jabber"));
            Check("enemy flagged in rows", dmg.First(r => r.Name == "A gnoll pup").Enemy
                && !dmg.First(r => r.Name == "Johan").Enemy);
            CheckNear("raid total excludes enemies", p.TotalPerSecond(false) * p.DurationSeconds, 60);

            // Ability drill-down: per-source split + incoming-by-ability
            // (checked before the idle finalize wipes the live fight).
            var selfAb = p.GetAbilityRows("Johan");
            double Ab(string name) => selfAb.FirstOrDefault(r => r.Name == name).Total;
            Check("self abilities: slash 12 / spell 30 / DoT 10 / crush 0",
                Ab("slash") == 12 && Ab("Burst of Flame") == 30 && Ab("Flame Lick") == 10
                && selfAb.Any(r => r.Name == "crush"));
            Check("melee verb normalized (hits -> hit)",
                p.GetIncomingAbilityRows(pet: false).First(r => r.Name == "hit") is { Total: 20, Hits: 1 });
            Check("pet incoming ability (bites -> bite)",
                p.GetIncomingAbilityRows(pet: true) is [{ Name: "bite", Total: 15 }]);

            // SCT events: one per own/pet-relevant combat line, routed by kind.
            Check("SCT: 4 outgoing-self events (incl. the 0 hit)",
                sct.Count(e => e.Kind == CombatParser.SctKind.OutgoingSelf) == 4);
            Check("SCT: incoming-self (hit, 20, melee flavor)",
                sct.Count(e => e.Kind == CombatParser.SctKind.IncomingSelf) == 1
                && sct.Any(e => e is { Kind: CombatParser.SctKind.IncomingSelf, Ability: "hit", Amount: 20, Flavor: CombatParser.SctFlavor.Melee }));
            Check("SCT: incoming-pet (bite, 15)",
                sct.Any(e => e is { Kind: CombatParser.SctKind.IncomingPet, Ability: "bite", Amount: 15 }));
            Check("SCT: no heal-out events (others healed)",
                sct.All(e => e.Kind != CombatParser.SctKind.HealOut));

            // ---- formats confirmed from the real Thorrak log --------------------
            p.ProcessLine($"[{Ts(15)}] Orc legionnaire is pierced by YOUR thorns for 8 points of non-melee damage.");
            p.ProcessLine($"[{Ts(15)}] Ice boned skeleton is pierced by YOUR thorns for 1 point of non-melee damage.");
            p.ProcessLine($"[{Ts(15)}] YOU are pierced by a Teir`Dal ranger's thorns for 6 points of non-melee damage!");
            p.ProcessLine($"[{Ts(16)}] Orc legionnaire has taken 12 damage from your Tainted Breath.");
            p.ProcessLine($"[{Ts(16)}] You healed Johan for 25 hit points.");
            p.ProcessLine($"[{Ts(16)}] You healed Johan over time for 30 hit points by Sprouting Heal.");
            p.ProcessLine($"[{Ts(17)}] Malahoja healed Johan for 40 hit points by Light Healing.");
            p.ProcessLine($"[{Ts(17)}] You bash a willowisp for 1 point of damage.");
            p.ProcessLine($"[{Ts(17)}] You crush a gnoll pup for 44 points of damage. (Critical)");
            p.ProcessLine($"[{Ts(18)}] You were hit by non-melee for 100 damage.");

            var ab3 = p.GetAbilityRows("Johan");
            Check("thorns DS out tracked (8 + singular-point 1)",
                ab3.First(r => r.Name == "thorns") is { Total: 9, Hits: 2 });
            Check("your-DoT form tracked (Tainted Breath 12)",
                ab3.First(r => r.Name == "Tainted Breath") is { Total: 12 });
            Check("singular-point melee tracked (bash 1)",
                ab3.First(r => r.Name == "bash") is { Total: 1 });
            Check("crit flagged on crush",
                ab3.First(r => r.Name == "crush") is { Crits: 1 });
            var inc3 = p.GetIncomingAbilityRows(pet: false);
            Check("thorns DS in tracked (6)",
                inc3.First(r => r.Name == "thorns") is { Total: 6 });
            Check("unattributed non-melee incoming (100)",
                inc3.First(r => r.Name == "non-melee") is { Total: 100 });
            Check("SCT: bare heal fires HealOut with 'heal' label",
                sct.Any(e => e is { Kind: CombatParser.SctKind.HealOut, Ability: "heal", Amount: 25 }));
            Check("SCT: HoT tick fires HealOut with spell label",
                sct.Any(e => e is { Kind: CombatParser.SctKind.HealOut, Ability: "Sprouting Heal", Amount: 30 }));
            Check("SCT: heal from another fires HealIn",
                sct.Any(e => e is { Kind: CombatParser.SctKind.HealIn, Ability: "Light Healing", Amount: 40 }));
            Check("SCT: thorns events carry Proc flavor",
                sct.Any(e => e is { Kind: CombatParser.SctKind.OutgoingSelf, Ability: "thorns", Flavor: CombatParser.SctFlavor.Proc }));
            Check("SCT: crit flag carried",
                sct.Any(e => e is { Ability: "crush", Amount: 44, Crit: true }));

            // Misses, resists, hit% and damage ranges.
            p.ProcessLine($"[{Ts(13)}] You try to slash a gnoll pup, but miss!");
            p.ProcessLine($"[{Ts(13)}] A gnoll pup tries to bite Johan, but Johan dodges!");
            p.ProcessLine($"[{Ts(14)}] Your target resisted the Burst of Flame spell.");
            p.ProcessLine($"[{Ts(14)}] You resisted the Frost Breath spell!");
            p.ProcessLine($"[{Ts(14)}] You resist ice boned skeleton's Ice Bone Frost Burst!");

            var ab2 = p.GetAbilityRows("Johan");
            var slash = ab2.First(r => r.Name == "slash");
            Check("miss tracked: slash 1/2 hit, range 12-12",
                slash is { Hits: 1, Misses: 1, Min: 12, Max: 12, Total: 12 });
            Check("outgoing spell resist tracked",
                ab2.First(r => r.Name == "Burst of Flame") is { Hits: 1, Resists: 1, Total: 30 });
            var inc2 = p.GetIncomingAbilityRows(pet: false);
            Check("incoming: mob melee hit range 20-20",
                inc2.First(r => r.Name == "hit") is { Hits: 1, Min: 20, Max: 20 });
            Check("incoming: avoided bites count as misses on you (missed + dodged)",
                inc2.First(r => r.Name == "bite") is { Hits: 0, Misses: 2, Total: 0 });
            Check("incoming: your spell resist tracked",
                inc2.First(r => r.Name == "Frost Breath") is { Resists: 1, Total: 0 });
            Check("incoming: possessive resist form strips the attacker",
                inc2.First(r => r.Name == "Ice Bone Frost Burst") is { Resists: 1 });

            Check("melee ability classification for proc rates",
                CombatParser.IsMeleeAbility("backstab") && CombatParser.IsMeleeAbility("slash")
                && !CombatParser.IsMeleeAbility("thorns") && !CombatParser.IsMeleeAbility("Tainted Breath"));

            // Plane of Sky quest tracker: data loads, completion watcher works
            // (temp progress path so tests never touch real progress).
            string skyProgress = Path.Combine(Path.GetTempPath(), "eql_sky_test.json");
            if (File.Exists(skyProgress)) File.Delete(skyProgress);
            var skyCs = new ConfigService();
            var sky = new SkyQuests(skyCs, new LootTracker(skyCs), skyProgress);
            Check("sky: quest data loads", sky.Quests.Count >= 90
                && sky.Quests.Select(q => q.Class).Distinct().Count() == 16);
            var bard = sky.Quests.FirstOrDefault(q => q.Name == "Bard Test of Tone");
            Check("sky: known quest parsed fully", bard is not null
                && bard.Giver == "Cilin Spellsinger" && bard.Reward == "Mask of Song"
                && bard.Items.Count == 2 && sky.Progress(bard).Need == 2);
            Check("sky: reward slot parsed from stats", bard is not null && bard.Slot == "FACE");
            sky.ProcessLine("[x] You receive 5 gold and 2 copper from the corpse.");
            Check("sky: coin receive completes nothing", sky.CompletedCount == 0);
            sky.ProcessLine("[x] You receive a Mask of Song!");
            Check("sky: reward receipt completes the quest",
                bard is not null && sky.IsCompleted(bard) && sky.CompletedCount == 1);
            sky.ProcessLine("[x] You receive a Mask of Song!");
            Check("sky: replayed reward line is a no-op", sky.CompletedCount == 1);
            if (bard is not null) sky.SetCompleted(bard, false);
            Check("sky: manual un-complete works", sky.CompletedCount == 0);
            File.Delete(skyProgress);

            // Zone difficulty parse for D0–D4 kill tiers.
            Check("zone difficulty: D0 for plain zones",
                RaidKills.ParseDifficulty("Befallen") == 0
                && RaidKills.ParseDifficulty("The Northern Desert of Ro") == 0);
            Check("zone difficulty: numbered variants map D1–D4",
                RaidKills.ParseDifficulty("Befallen 1 (Awakened)") == 1
                && RaidKills.ParseDifficulty("Blackburrow 1 (Awakened)") == 1
                && RaidKills.ParseDifficulty("Clan Crushbone 4 (Refined)") == 4);

            // Raid-kill death-line parsing (level suffixes stripped).
            Check("raid kill: slain-by line",
                RaidKills.TryParseKill("Lady Vox has been slain by Johan!", out var mob1) && mob1 == "Lady Vox");
            Check("raid kill: you-have-slain line strips level",
                RaidKills.TryParseKill("You have slain a Sage of Innoruuk (17)!", out var mob2)
                && mob2 == "a Sage of Innoruuk");
            Check("raid kill: normal line no match",
                !RaidKills.TryParseKill("You slash a rat for 5 points of damage.", out _));

            // Global respawns: the auto-generated death pattern matches both forms.
            var resp = ConfigService.BuildRespawnTrigger(
                new Models.RespawnEntry { Name = "Lady Vox", Seconds = 400 });
            Check("respawn trigger compiles with derived pattern",
                resp is { Panel: Models.Panels.TimerAuto, DurationSeconds: 400 }
                && resp.StartRegex!.IsMatch("Lady Vox has been slain by Johan!")
                && resp.StartRegex!.IsMatch("You have slain Lady Vox!")
                && !resp.StartRegex!.IsMatch("Lady Vox hits YOU for 10 points of damage."));
            Check("disabled respawn builds no trigger",
                ConfigService.BuildRespawnTrigger(new Models.RespawnEntry { Name = "X", Enabled = false }) is null);

            // Spell library: loads, searches, generates working triggers,
            // and tracks seen spells via exact-message lookup.
            var lib = new SpellLibrary(new ConfigService());
            Check("library loads 1000+ spells", lib.Spells.Count > 1000);
            var sow = lib.Spells.FirstOrDefault(x => x.Name == "Spirit of Wolf");
            Check("SoW record has messages + duration", sow is not null
                && sow.CastOnYou == "You feel the spirit of wolf enter you."
                && sow.WearsOff == "The spirit of wolf leaves you."
                && Math.Abs(sow.DurationSec - 2160) < 1);
            var bar = SpellLibrary.BarTrigger(sow!, spokenWarning: true);
            Check("library bar trigger compiles with duration + fade voice", bar is not null
                && bar.StartRegex!.IsMatch("You feel the spirit of wolf enter you.")
                && bar.EndRegex!.IsMatch("The spirit of wolf leaves you.")
                && bar.DurationSeconds == 2160
                && bar.Alert is { AtSeconds: 20 });
            var fade = SpellLibrary.FadeFlashTrigger(sow!);
            Check("library fade-flash trigger", fade is not null
                && fade.Panel == Models.Panels.Flash
                && fade.StartRegex!.IsMatch("The spirit of wolf leaves you."));
            Check("library search finds Clarity",
                lib.Search("clarity").Any(x => x.Name == "Clarity"));
            lib.MarkSeenFromLine($"[{Ts(50)}] You feel the spirit of wolf enter you.");
            Check("cast message marks spell as seen", lib.IsSeen(sow!));

            // Recent-deaths picker: every parsed death lands in the list, newest
            // first, re-kills dedupe (unlisted mobs are never persisted).
            var rk = new RaidKills(new ConfigService());
            rk.ProcessLine("[x] a rat has been slain by Johan!");
            rk.ProcessLine("[x] a bat has been slain by Johan!");
            rk.ProcessLine("[x] a rat has been slain by Johan!");
            Check("recent deaths: newest first, deduped",
                rk.RecentDeaths.Count == 2
                && rk.RecentDeaths[0].Name == "a rat" && rk.RecentDeaths[1].Name == "a bat");
            rk.ProcessLine("[x] You have entered Blackburrow.");
            rk.ProcessLine("[x] a gnoll pup has been slain by Johan!");
            Check("recent deaths carry the zone they happened in",
                rk.RecentDeaths[0] is { Name: "a gnoll pup", Zone: "Blackburrow" }
                && rk.RecentDeaths[1].Zone == ""); // killed before any zone line

            // Idle finalize archives the fight; a new line starts fresh.
            CombatParser.FightRecord? archived = null;
            p.FightArchived += r => archived = r;
            p.Tick(new DateTime(2026, 8, 3, 12, 0, 30));
            Check("fight ends after 10s idle", !p.InCombat);
            Check("FightArchived fires with the frozen record",
                archived is not null && ReferenceEquals(archived, p.History[0]));
            Check("ended fight archived to history", p.History.Count == 1
                && p.History[0].Label.StartsWith("a gnoll pup") // multi-enemy pull -> "+N" suffix
                && Math.Abs(p.History[0].DurationSeconds - 18) < 0.01 // last activity = the Ts(18) line
                && p.History[0].IncomingSelfTotal == 126 // 20 melee + 6 thorns + 100 non-melee
                && p.History[0].Zone == "Clan Crushbone");

            // Timeline events captured alongside the stats.
            var ev = p.History[0].Events;
            Check("timeline: events recorded across streams", ev.Count > 10
                && ev.Any(x => x is { Stream: CombatParser.FightStream.SelfOut, Amount: > 0 })
                && ev.Any(x => x is { Stream: CombatParser.FightStream.SelfIn, Amount: > 0 })
                && ev.Any(x => x.Stream == CombatParser.FightStream.HealOut)
                && ev.Any(x => x.Stream == CombatParser.FightStream.HealIn));
            Check("timeline: miss/resist/crit flags captured",
                ev.Any(x => x is { Ability: "slash", Miss: true, Stream: CombatParser.FightStream.SelfOut })
                && ev.Any(x => x is { Ability: "Frost Breath", Resist: true, Stream: CombatParser.FightStream.SelfIn })
                && ev.Any(x => x is { Ability: "crush", Crit: true }));
            Check("timeline: offsets inside the fight window",
                ev.All(x => x.T >= 0 && x.T <= p.History[0].DurationSeconds + 0.01)
                && !p.History[0].EventsTruncated);
            p.ProcessLine($"[{Ts(40)}] You slash a rat for 5 points of damage.");
            Check("next combat line starts a fresh fight",
                p.InCombat && p.GetRows(false).Count == 1 && p.IncomingSelfTotal == 0);
            Check("history survives the reset", p.History.Count == 1);

            // Multi-mob pull label: "biggest +N".
            p.ProcessLine($"[{Ts(42)}] You slash a royal guard for 9 points of damage.");
            Check("multi-pull label gets +N", p.TargetLabel is "a rat +1" or "a royal guard +1");

            // Session skill tracker: accumulates ACROSS fights (1 hit + 1 miss in
            // the first fight, 2 more hits in this one) and ignores fight resets.
            Check("session skills accumulate across fights",
                p.GetSessionSkill("slash") is { Hits: 3, Misses: 1 });
            Check("session skills count spell resists as attempts",
                p.GetSessionSkill("Burst of Flame") is { Hits: 1, Resists: 1, Attempts: 2 });
            Check("session skill hit rate", p.GetSessionSkill("slash") is { } sk
                && Math.Abs(sk.HitRate - 0.75) < 0.001);
            Check("unattempted skill is null", p.GetSessionSkill("Kick of Doom") is null);

            // Reave: the real EQL melee form + skill-up lines carry the level.
            p.ProcessLine($"[{Ts(44)}] You reave a royal guard for 24 points of damage.");
            p.ProcessLine($"[{Ts(44)}] You try to reave a royal guard, but miss!");
            p.ProcessLine($"[{Ts(44)}] You have become better at Reave! (3)");
            p.ProcessLine($"[{Ts(45)}] You have become better at Reave! (4)");
            Check("reave parses as a melee hit", p.GetSessionSkill("reave") is { Hits: 1, Misses: 1 }
                && CombatParser.IsMeleeAbility("reave"));
            Check("SCT: reave melee hit fires the outgoing lane",
                sct.Any(e => e is { Kind: CombatParser.SctKind.OutgoingSelf, Ability: "reave",
                    Amount: 24, Flavor: CombatParser.SctFlavor.Melee }));
            Check("skill-ups tracked with level (case-insensitive name)",
                p.GetSessionSkill("REAVE") is { Level: 4, Ups: 2 });

            // Progress lane events: xp / faction (sign colors) / AA — never combat.
            bool wasActive = p.InCombat;
            double durBefore = p.DurationSeconds;
            p.ProcessLine($"[{Ts(46)}] You gain experience! (3.552%)");
            p.ProcessLine($"[{Ts(46)}] Your faction standing with Burning Dead has been adjusted by -2.");
            p.ProcessLine($"[{Ts(46)}] Your faction standing with Steel Warriors has been adjusted by 5.");
            p.ProcessLine($"[{Ts(46)}] You have gained an ability point!  You now have 2 ability points.");
            Check("SCT: xp gain floats with percent text",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Ability: "xp" }
                    && e.Amount > 3.5 && e.Amount < 3.6 && e.Text is not null && e.Text.StartsWith('+')));
            Check("SCT: faction down uses the proc color slot",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Ability: "Burning Dead",
                    Amount: -2, Flavor: CombatParser.SctFlavor.Proc, Text: "-2" }));
            Check("SCT: faction up uses the spell color slot",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Ability: "Steel Warriors",
                    Amount: 5, Flavor: CombatParser.SctFlavor.Spell, Text: "+5" }));
            Check("SCT: AA point floats big",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Crit: true, Text: "AA point!" }));
            p.ProcessLine($"[{Ts(46)}] You have gained an ability point!  You now have 1 ability point.");
            Check("SCT: singular '1 ability point' still floats",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Text: "AA point!", Ability: "1 total" }));
            p.ProcessLine($"[{Ts(46)}] You have improved Mastery of the Past 2 at a cost of 4 ability points.");
            Check("SCT: AA spend floats with the improved ability",
                sct.Any(e => e is { Kind: CombatParser.SctKind.Progress, Text: "-4 AA",
                    Ability: "Mastery of the Past 2", Amount: -4 }));
            Check("progress lines never touch the fight model",
                p.InCombat == wasActive && Math.Abs(p.DurationSeconds - durBefore) < 0.001);

            // Proc watcher: a spell effect with no own cast behind it is a proc;
            // a begin-cast within 12s claims it; DoTs and melee never count.
            var pw = new CombatParser { SelfName = "Johan" };
            string PTs(int s) => new DateTime(2026, 8, 10, 21, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            pw.ProcessLine($"[{PTs(0)}] Johan hit a gnoll pup for 120 points of fire damage by Smiting Strike.");
            pw.ProcessLine($"[{PTs(2)}] Johan hit a gnoll pup for 130 points of fire damage by Smiting Strike. (Critical)");
            Check("procs: cast-less spell damage counts as a proc",
                pw.SessionProcs.TryGetValue("Smiting Strike", out var lane)
                && lane.Count == 2 && Math.Abs(lane.Damage - 250) < 0.01
                && lane.Crits == 1 && Math.Abs(lane.Max - 130) < 0.01);
            pw.ProcessLine($"[{PTs(4)}] You begin casting Sanity Warp.");
            pw.ProcessLine($"[{PTs(6)}] Johan hit a gnoll pup for 55 points of magic damage by Sanity Warp.");
            Check("procs: a hand-cast spell is not a proc", !pw.SessionProcs.ContainsKey("Sanity Warp"));
            pw.ProcessLine($"[{PTs(30)}] Johan hit a gnoll pup for 55 points of magic damage by Sanity Warp.");
            Check("procs: the same spell cast-less later IS one (the Spellblade case)",
                pw.SessionProcs.TryGetValue("Sanity Warp", out var mixed) && mixed.Count == 1);
            pw.ProcessLine($"[{PTs(32)}] A gnoll pup has taken 40 damage from Ignite by Johan.");
            Check("procs: DoT ticks never count", !pw.SessionProcs.ContainsKey("Ignite"));
            pw.ProcessLine($"[{PTs(34)}] You slash a gnoll pup for 15 points of damage.");
            Check("procs: melee never counts", !pw.SessionProcs.ContainsKey("slash"));
            pw.ProcessLine($"[{PTs(36)}] Johan healed himself for 60 hit points by Lifetap Strike.");
            Check("procs: a cast-less heal is a heal proc",
                pw.SessionProcs.TryGetValue("Lifetap Strike", out var lt)
                && lt.Count == 1 && Math.Abs(lt.Heal - 60) < 0.01);
            Check("procs: swings = your melee hits + misses", pw.SessionSwings == 1);
            double liveActive = pw.SessionActiveSeconds;
            Check("procs: active time accrues while fighting", liveActive is > 30 and < 45);
            pw.Tick(new DateTime(2026, 8, 10, 21, 2, 0)); // idle out -> Archive
            Check("procs: an archived fight keeps its active time exactly once",
                Math.Abs(pw.SessionActiveSeconds - liveActive) < 0.5);
            pw.ResetSessionSkills();
            Check("procs: the session reset clears lanes and active time",
                pw.SessionProcs.Count == 0 && pw.SessionActiveSeconds == 0 && pw.SessionSwings == 0);

            // Raid badges: every default target resolves to a drawn silhouette;
            // unknown (user-added) names get a stable monogram fallback.
            var allTargets = new RaidKills(new ConfigService()).GetView()
                .SelectMany(t => t.Targets.Select(x => x.Name)).ToList();
            Check("badges: every default raid target has a silhouette",
                allTargets.Count > 0 && allTargets.All(Views.RaidGlyphs.HasGlyph));
            var fb = Views.RaidGlyphs.For("Some Custom Boss");
            var fb2 = Views.RaidGlyphs.For("a strange mob");
            Check("badges: unknown targets fall back to a monogram",
                fb.Glyph is null && fb.Monogram == "S" && fb2.Monogram == "S"
                && Views.RaidGlyphs.For("Some Custom Boss").Tint == fb.Tint); // stable color

            // Kept-fights persistence: FightRecord must survive a JSON round trip.
            var jsonOpts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };
            string json = System.Text.Json.JsonSerializer.Serialize(p.History.ToList(), jsonOpts);
            var back = System.Text.Json.JsonSerializer
                .Deserialize<List<CombatParser.FightRecord>>(json, jsonOpts);
            Check("fight record JSON round trip", back is { Count: 1 }
                && back[0].Label.StartsWith("a gnoll pup")
                && back[0].IncomingSelfTotal == 126
                && back[0].Damage.Any(r => r.Name == "Johan" && Math.Abs(r.Total - 118) < 0.01 && !r.Enemy)
                && back[0].Damage.Any(r => r.Enemy)
                && back[0].SelfAbilities.Any(r => r.Name == "Burst of Flame" && r.Total == 30)
                && back[0].SelfAbilities.Any(r => r.Name == "crush" && r.Crits == 1)
                && back[0].Events.Count == p.History[0].Events.Count
                && back[0].Events.Any(x => x.Miss)
                && back[0].IncomingSelfAbilities.Any(r => r.Name == "hit" && r.Total == 20));
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            failures++;
        }

        string result = (failures == 0 ? "ALL PASS\n" : $"{failures} FAILURE(S)\n") + report;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest_meter.txt"), result);
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown();
    }

    private void RunReplay(string path)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                Path.GetFileName(path), @"^eqlog_(?<name>[A-Za-z]+)[_.]");
            var p = new CombatParser { SelfName = m.Success ? m.Groups["name"].Value : "You" };
            var sctCounts = new Dictionary<CombatParser.SctKind, int>();
            p.SctEvent += hit => sctCounts[hit.Kind] = 1 + sctCounts.GetValueOrDefault(hit.Kind);

            // Duration learner dry-run against the real log (throwaway store).
            string durReplayPath = Path.Combine(Path.GetTempPath(), "eql_replay_durations.json");
            File.Delete(durReplayPath);
            var replayCs = new ConfigService();
            var replayDur = new SpellDurations(replayCs, new SpellLibrary(replayCs), durReplayPath);
            var learned = new List<string>();
            replayDur.SampleLearned += (spell, sec, n) => learned.Add($"{spell}: {sec:0}s (sample {n})");

            int lines = 0;
            int lootUp = 0, lootKept = 0, lootSold = 0;
            long lootCopper = 0;
            var tsRx = new System.Text.RegularExpressions.Regex(@"^\[.+?\]\s?");
            foreach (var line in File.ReadLines(path))
            {
                p.Replay(line);
                replayDur.ProcessLine(line);
                lines++;
                if (LootTracker.TryParseLoot(tsRx.Replace(line, "", 1), out var lk, out _, out _, out _, out long lc, out _))
                {
                    if (lk == LootTracker.LootKind.Upgrade) lootUp++;
                    else if (lk == LootTracker.LootKind.Kept) lootKept++;
                    else { lootSold++; lootCopper += lc; }
                }
            }
            p.Tick(DateTime.MaxValue);

            report.AppendLine($"lines: {lines}   fights: {p.History.Count}   self: {p.SelfName}");
            report.AppendLine($"--- learned durations ({learned.Count} samples) ---");
            foreach (var l in learned) report.AppendLine("  " + l);
            File.Delete(durReplayPath);
            report.AppendLine("SCT events: " + string.Join("  ",
                sctCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));

            var dmg = new Dictionary<string, double>();
            var abil = new Dictionary<string, (double Total, int Hits, int Misses, int Resists, int Crits)>();
            foreach (var f in p.History)
            {
                foreach (var r in f.Damage.Where(r => !r.Enemy))
                    dmg[r.Name] = dmg.GetValueOrDefault(r.Name) + r.Total;
                foreach (var a in f.SelfAbilities)
                {
                    var cur = abil.GetValueOrDefault(a.Name);
                    abil[a.Name] = (cur.Total + a.Total, cur.Hits + a.Hits, cur.Misses + a.Misses,
                        cur.Resists + a.Resists, cur.Crits + a.Crits);
                }
            }
            report.AppendLine("--- player damage across all fights ---");
            foreach (var kv in dmg.OrderByDescending(kv => kv.Value).Take(8))
                report.AppendLine($"  {kv.Key}: {kv.Value:N0}");
            report.AppendLine("--- your abilities (total / hits / misses / resists / crits) ---");
            foreach (var kv in abil.OrderByDescending(kv => kv.Value.Total).Take(14))
                report.AppendLine($"  {kv.Key}: {kv.Value.Total:N0} / {kv.Value.Hits} / {kv.Value.Misses} / {kv.Value.Resists} / {kv.Value.Crits}");
            double incoming = p.History.Sum(f => f.IncomingSelfTotal);
            report.AppendLine($"--- incoming on you across all fights: {incoming:N0} ---");
            var incAb = new Dictionary<string, double>();
            foreach (var f in p.History)
                foreach (var a in f.IncomingSelfAbilities)
                    incAb[a.Name] = incAb.GetValueOrDefault(a.Name) + a.Total;
            foreach (var kv in incAb.OrderByDescending(kv => kv.Value).Take(8))
                report.AppendLine($"  {kv.Key}: {kv.Value:N0}");
            report.AppendLine($"--- loot: {lootUp} upgrades, {lootKept} kept, {lootSold} vendored for {LootTracker.FormatCoins(lootCopper)} ---");

            // Proc watcher probe (the Companion's table, on OUR log): lanes with
            // counts, damage/heal, and both rates over the session denominators.
            double activeSec = p.SessionActiveSeconds;
            int swings = p.SessionSwings;
            report.AppendLine($"--- procs: active {activeSec / 60:0.0} min · {swings:N0} swings ---");
            foreach (var kv in p.SessionProcs.OrderByDescending(kv => kv.Value.Count).Take(12))
            {
                var v = kv.Value;
                string amounts = v.Damage > 0 ? $"{v.Damage:N0} dmg" : $"{v.Heal:N0} healed";
                report.AppendLine($"  {kv.Key}: x{v.Count} · {amounts} · " +
                    $"{(activeSec >= 10 ? $"{v.Count * 60 / activeSec:0.00}/min" : "-")} · " +
                    $"{(swings >= 20 ? $"{100.0 * v.Count / swings:0.00}/100 swings" : "-")}");
            }
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            Environment.ExitCode = 1;
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_replay.txt"), report.ToString());
        Shutdown();
    }

    /// <summary>Dev tool: render every raid-badge silhouette plus the whole
    /// default target list to a PNG contact sheet, for eyeballing the vectors
    /// without launching the app (`--render-glyphs [out.png]`).</summary>
    private void RenderGlyphSheet(string outPath)
    {
        const int cols = 5, cellW = 130, cellH = 128, stripCell = 150, stripRowH = 44;
        var keys = RaidGlyphs.GlyphKeys.ToList();
        var targets = new RaidKills(new ConfigService()).GetView()
            .SelectMany(t => t.Targets.Select(x => x.Name)).ToList();

        int glyphRows = (keys.Count + cols - 1) / cols;
        int stripCols = 4, stripRows = (targets.Count + stripCols - 1) / stripCols;
        int width = Math.Max(cols * cellW, stripCols * stripCell);
        int height = glyphRows * cellH + 40 + stripRows * stripRowH + 30;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x22)), null,
                new Rect(0, 0, width, height));
            var face = new Typeface("Segoe UI");

            for (int i = 0; i < keys.Count; i++)
            {
                double cx = i % cols * cellW + cellW / 2.0;
                double cy = i / cols * cellH + 52;
                DrawBadge(dc, RaidGlyphs.GlyphFor(keys[i]), Color.FromRgb(0x9F, 0xB6, 0xD4), null, cx, cy, 84);
                var ft = new FormattedText(keys[i], System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 13, Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, cy + 50));
            }

            double stripTop = glyphRows * cellH + 40;
            for (int i = 0; i < targets.Count; i++)
            {
                double x = i % stripCols * stripCell + 24;
                double y = stripTop + i / stripCols * stripRowH + 16;
                var b = RaidGlyphs.For(targets[i]);
                DrawBadge(dc, b.Glyph, b.Tint, b.Monogram, x, y, 26);
                var ft = new FormattedText(targets[i], System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 10, Brushes.Gray, 1.0);
                dc.DrawText(ft, new Point(x + 18, y - ft.Height / 2));
            }
        }

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    /// <summary>The badge exactly as the Raid Kills window draws it: tinted
    /// ring + translucent fill, silhouette (or monogram) in full tint.</summary>
    internal static void DrawBadge(DrawingContext dc, Geometry? glyph, Color tint, string? monogram,
        double cx, double cy, double d)
    {
        var bg = new SolidColorBrush(Color.FromArgb(52, tint.R, tint.G, tint.B));
        var ring = new Pen(new SolidColorBrush(Color.FromArgb(96, tint.R, tint.G, tint.B)),
            Math.Max(1, d / 26));
        dc.DrawEllipse(bg, ring, new Point(cx, cy), d / 2, d / 2);

        if (glyph is not null)
        {
            double s = d * 0.72 / 24.0;
            dc.PushTransform(new TranslateTransform(cx - 12 * s, cy - 12 * s));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(new SolidColorBrush(tint), null, glyph);
            dc.Pop();
            dc.Pop();
        }
        else if (monogram is not null)
        {
            var ft = new FormattedText(monogram, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                d * 0.5, new SolidColorBrush(tint), 1.0);
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
        }
    }

    private void RunRepopSelfTest()
    {
        var report = new System.Text.StringBuilder();
        int failures = 0;
        void Check(string label, bool ok)
        {
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {label}");
            if (!ok) failures++;
        }

        try
        {
            var tw = new TimerWindow(new ConfigService(), new AlertService(), 400, 1.0, null);

            tw.StartWith(200, "Kurven");
            Check("first kill takes the pie",
                tw.BigState is { Mode: "Kurven", Running: true } && tw.SecondaryNames.Count == 0);

            tw.StartWith(400, "Baron"); // longer respawn -> must NOT displace the sooner one
            Check("longer repop stays secondary",
                tw.BigState.Mode == "Kurven" && tw.SecondaryNames is ["Baron"]);

            tw.StartWith(50, "Vox"); // soonest -> takes the pie
            Check("soonest repop claims the pie",
                tw.BigState is { Mode: "Vox", Remaining: <= 50 and > 45 }
                && tw.SecondaryNames is ["Kurven", "Baron"]);

            tw.StartWith(30, "Kurven"); // re-kill of a secondary, now soonest
            Check("re-killed secondary promotes when soonest",
                tw.BigState.Mode == "Kurven" && tw.SecondaryNames is ["Vox", "Baron"]);

            tw.Close();
        }
        catch (Exception ex)
        {
            report.AppendLine("EXCEPTION: " + ex);
            failures++;
        }

        string result = (failures == 0 ? "ALL PASS\n" : $"{failures} FAILURE(S)\n") + report;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_selftest_repop.txt"), result);
        Environment.ExitCode = failures == 0 ? 0 : 1;
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception", e.Exception);
        MessageBox.Show(
            "EQL Assistant hit an unexpected error:\n\n" + e.Exception.Message +
            "\n\n(The overlay will keep running. Check your config.json if this repeats.)",
            "EQL Assistant",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
