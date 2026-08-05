using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using EQLOverlay.Services;
using EQLOverlay.Views;

namespace EQLOverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Gated smoke test: construct the manager window (forces XAML parse) and
        // exit. Used to verify the build without a human clicking. Not user-facing.
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

        Log.Init();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"===== EQL Assistant v{ver} starting =====");
        Log.Info($"exe: {Environment.ProcessPath}");
        Log.Info($"log: {Log.Path}");

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
                new RaidKills(cs), new SpellLibrary(cs), _ => { });
            mgr.Show();
            mgr.Close();
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
            var sct = new List<(CombatParser.SctKind Kind, string Ability, double Amount)>();
            p.SctEvent += (k, a, v) => sct.Add((k, a, v));
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
            Check("SCT: incoming-self (hit, 20)",
                sct.Count(e => e.Kind == CombatParser.SctKind.IncomingSelf) == 1
                && sct.Any(e => e is { Kind: CombatParser.SctKind.IncomingSelf, Ability: "hit", Amount: 20 }));
            Check("SCT: incoming-pet (bite, 15)",
                sct.Any(e => e is { Kind: CombatParser.SctKind.IncomingPet, Ability: "bite", Amount: 15 }));
            Check("SCT: no heal-out events (others healed)",
                sct.All(e => e.Kind != CombatParser.SctKind.HealOut));

            // Misses, resists, hit% and damage ranges.
            p.ProcessLine($"[{Ts(13)}] You try to slash a gnoll pup, but miss!");
            p.ProcessLine($"[{Ts(13)}] A gnoll pup tries to bite Johan, but Johan dodges!");
            p.ProcessLine($"[{Ts(14)}] Your target resisted the Burst of Flame spell.");
            p.ProcessLine($"[{Ts(14)}] You resisted the Frost Breath spell!");

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

            // Idle finalize archives the fight; a new line starts fresh.
            p.Tick(new DateTime(2026, 8, 3, 12, 0, 30));
            Check("fight ends after 10s idle", !p.InCombat);
            Check("ended fight archived to history", p.History.Count == 1
                && p.History[0].Label == "a gnoll pup"
                && Math.Abs(p.History[0].DurationSeconds - 14) < 0.01 // miss/resist lines extend activity
                && p.History[0].IncomingSelfTotal == 20);
            p.ProcessLine($"[{Ts(40)}] You slash a rat for 5 points of damage.");
            Check("next combat line starts a fresh fight",
                p.InCombat && p.GetRows(false).Count == 1 && p.IncomingSelfTotal == 0);
            Check("history survives the reset", p.History.Count == 1);

            // Multi-mob pull label: "biggest +N".
            p.ProcessLine($"[{Ts(42)}] You slash a royal guard for 9 points of damage.");
            Check("multi-pull label gets +N", p.TargetLabel is "a rat +1" or "a royal guard +1");

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
                && back[0].Label == "a gnoll pup"
                && back[0].IncomingSelfTotal == 20
                && back[0].Damage.Any(r => r.Name == "Johan" && Math.Abs(r.Total - 52) < 0.01 && !r.Enemy)
                && back[0].Damage.Any(r => r.Enemy)
                && back[0].SelfAbilities.Any(r => r.Name == "Burst of Flame" && r.Total == 30)
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
