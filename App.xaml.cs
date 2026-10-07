using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        EnsureStandardMenuAlignment();

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

        // Every headless mode runs gagged: no TTS, no wav — a background
        // selftest must never speak into the owner's meeting (8 Sep).
        if (e.Args.Any(a => a.StartsWith("--selftest", StringComparison.Ordinal)
                            || a.StartsWith("--render", StringComparison.Ordinal)
                            || a == "--replay"))
            AlertService.Silenced = true;

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

        // Gated: `--sky-audit <log> [item filter]` — replay a log through the
        // loot ledger and the Sky tracker on scratch files and print every quest
        // item's looted / offered / destroyed / held with the lines behind them.
        // The owner's "it says I still have it" reports start here.
        int sa = Array.IndexOf(e.Args, "--sky-audit");
        if (sa >= 0 && sa + 1 < e.Args.Length)
        {
            try { RunSkyAudit(e.Args[sa + 1], sa + 2 < e.Args.Length ? e.Args[sa + 2] : ""); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_sky_audit.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        // Gated: render one Manager page to a PNG (`--render-manager <page> <out.png>`)
        // — lets a build be eyeballed against a design mock without a human.
        int rm = Array.IndexOf(e.Args, "--render-manager");
        if (rm >= 0)
        {
            try
            {
                RenderManagerPage(rm + 1 < e.Args.Length ? e.Args[rm + 1] : "General",
                    rm + 2 < e.Args.Length ? e.Args[rm + 2] : Path.Combine(Path.GetTempPath(), "eql_manager.png"),
                    e.Args.Contains("--bottom"));
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_manager_error.txt"), ex.ToString());
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
        // Gated: time every live-line consumer on a real log with the real
        // loadout (`--bench <log>`): the answer to "is the pipeline keeping up".
        int benchIdx = Array.IndexOf(e.Args, "--bench");
        if (benchIdx >= 0 && benchIdx + 1 < e.Args.Length)
        {
            try { RunBench(e.Args[benchIdx + 1]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_bench.txt"), "FAIL\n" + ex); }
            Shutdown();
            return;
        }

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

    /// <summary>Touch/pen-capable machines often report LEFT-HANDED menu
    /// alignment ("show menus to the left of the hand"), and WPF is nearly
    /// the only UI stack that honors it — every popup and submenu then opens
    /// leftward while the rest of the desktop opens right. The standard
    /// workaround: flip WPF's cached flag so menus behave like every other
    /// app on the machine. (Near the screen's right edge menus still flip
    /// left to stay on screen — that part is correct everywhere.)</summary>
    private static void EnsureStandardMenuAlignment()
    {
        try
        {
            if (!SystemParameters.MenuDropAlignment) return;
            typeof(SystemParameters)
                .GetField("_menuDropAlignment",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.SetValue(null, false);
        }
        catch { /* cosmetic only — never block startup over it */ }
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

            // Sky window builds its class-badge strip (arcs included).
            var skyWin = new Views.SkyWindow(new SkyQuests(cs, new LootTracker(cs)));
            // Class badges (22 Sep): no MINE, ALL first, then the classes most-complete first.
            {
                var order = skyWin.BadgeOrderForTest;
                if (order.Count != 17 || order[0] != "ALL" || order.Contains("MINE"))
                    throw new Exception("sky badges: ALL + 16 classes, no MINE — got " + string.Join(",", order));
                var ranked = Views.SkyWindow.RankClasses(new[] { ("PAL", 2, 4), ("MNK", 6, 6), ("SHD", 5, 7), ("WAR", 5, 6), ("RNG", 3, 6), ("SHM", 6, 6) }, x => (x.Item2, x.Item3))
                    .Select(x => x.Item1).ToList();
                if (string.Join(",", ranked) != "MNK,SHM,WAR,SHD,PAL,RNG")
                    throw new Exception("sky badges: rank by share, then fewest left, then class order — got " + string.Join(",", ranked));
            }
            skyWin.Show();
            skyWin.Close();

            // Session stats panel renders a demo evening (rows + pills + caption).
            var demoStats = new SessionStats();
            demoStats.AddDemo(DateTime.Now);
            var statsWin = new Views.SessionStatsWindow(demoStats, cs, 1.0,
                SessionStats.Slice.ZoneSession, exactTier: true, SessionStats.Basis.Elapsed);
            statsWin.Show();
            statsWin.Refresh();
            statsWin.Close();

            // Inventory window renders the ledger from a real-format dump —
            // and the empty-state instructions when no dump exists.
            string invDir = Path.Combine(Path.GetTempPath(), "eql_selftest_inv");
            Directory.CreateDirectory(invDir);
            File.WriteAllText(Path.Combine(invDir, "Testchar_paineel-Inventory.txt"),
                "Location\tName\tID\tCount\tSlots\r\nHead\tValorium Helmet +1\t4851\t1\t10\r\n");
            string hbPath = Path.Combine(Path.GetTempPath(), "eql_selftest_char_charms.json");
            try { File.Delete(hbPath); } catch { /* fresh */ }
            var hb = new CharmBook(null, hbPath);
            hb.Add(new CharmBook.Episode("a greater ice bones", "Beguile Undead", "Permafrost Caverns", DateTime.Now.AddMinutes(-10), DateTime.Now.AddMinutes(-4), "broke", 1240, 9, 212, 2, 44, "Beguile Undead", 380, 47));
            hb.AddAttempt(new CharmBook.Attempt("a greater ice bones", "Beguile Undead", "resisted", DateTime.Now.AddMinutes(-12), "Permafrost Caverns"));
            var invWin = new Views.InventoryWindow(invDir, "Testchar", "paineel", null, hb);
            invWin.Show();
            invWin.ShowTab("sheet"); // the sheet is the Character window's only tab now (3 Oct) — the rest is in Tools
            invWin.UpdateLayout();
            if (Views.InventoryWindow.HostedTabs is not ["sheet"]) throw new Exception("character: the Character window should host the sheet alone");
            invWin.Close();
            try { File.Delete(hbPath); } catch { /* temp */ }

            // The Loot window hosts the browser tabs of the same panel: the
            // ledger list, exaltations and the armor-set board (owns a
            // Valorium piece) must all render from the dump.
            var lootItems = new Views.LootWindow(new LootTracker(new ConfigService()),
                invDir, "Testchar", "paineel");
            lootItems.Show();
            foreach (var view in new[] { "items", "exalt", "sets" })
            {
                lootItems.ShowView(view);
                lootItems.UpdateLayout();
            }
            lootItems.Close();

            // Notable quests view renders inside the Quests window.
            {
                string qlPath2 = Path.Combine(Path.GetTempPath(), "eql_test_questlines_view.json");
                try { File.Delete(qlPath2); } catch { /* fresh */ }
                var qlv = new QuestLines(new ConfigService(), null, qlPath2);
                var skyLines = new Views.SkyWindow(new SkyQuests(new ConfigService(), new LootTracker(new ConfigService())), null, () => "SHD/SHM/NEC", qlv);
                skyLines.Show();
                skyLines.ShowPack("lines");
                skyLines.UpdateLayout();
                if (skyLines.Pack != "lines") throw new Exception("quest lines: pack did not switch");
                qlv.ProcessLine("[Tue Sep 01 22:40:00 2026] You have slain Brother Hayle!");
                skyLines.UpdateLayout();
                skyLines.ShowPack("sky");
                skyLines.Close();
                try { File.Delete(qlPath2); } catch { /* temp */ }
            }

            // DPS meter, SOLO, folded with a pet (8 Sep): the header keeps the
            // combined number and two total bars beneath it split you / pet.
            {
                var mp = new CombatParser { SelfName = "Thorrak", PetName = "Jobaner" };
                mp.ProcessLine("[Tue Sep 08 20:00:00 2026] Thorrak slashes a rat for 300 points of damage.");
                mp.ProcessLine("[Tue Sep 08 20:00:01 2026] Jobaner bites a rat for 100 points of damage.");
                mp.ProcessLine("[Tue Sep 08 20:00:02 2026] Thorrak slashes a rat for 300 points of damage.");
                var meter = new Views.MeterWindow(new ConfigService(), mp, new LootTracker(new ConfigService()), 1.0,
                    Array.Empty<string>(), false, false, soloMode: true);
                meter.Show();
                meter.SetSelfExpandedForTest(false);
                var folded = meter.RowsForTest;
                if (folded.Count != 2 || folded[0].Name != "Thorrak" || folded[1].Name != "Jobaner (pet)"
                    || !folded[0].ValueText.Contains("86%") || !folded[1].ValueText.Contains("14%") || folded[0].IsFold
                    || Math.Abs(folded[0].Fraction - 6.0 / 7) > 0.01 || Math.Abs(folded[1].Fraction - 1.0 / 7) > 0.01) // shares of the header
                    throw new Exception("meter solo folded: expected [Thorrak, Jobaner (pet)] total bars, got "
                        + string.Join(" | ", folded.Select(r => $"{r.Name} {r.ValueText}")));
                meter.SetSelfExpandedForTest(true);
                if (meter.RowsForTest.All(r => !r.IsFold)) throw new Exception("meter solo expanded: the pet fold row is gone");
                meter.Close();
            }

            // The Resists view renders inside Fight history.
            {
                string rbPath2 = Path.Combine(Path.GetTempPath(), "eql_test_resists_view.json");
                try { File.Delete(rbPath2); } catch { /* fresh */ }
                var rpv = new CombatParser { SelfName = "Thorrak" };
                var rbv = new ResistBook(new ConfigService(), rpv, rbPath2);
                rpv.ProcessLine("[Tue Sep 08 20:00:02 2026] A greater sphinx resisted your Envenomed Breath!");
                var hw = new Views.HistoryWindow(rpv, new ConfigService(), new LootTracker(new ConfigService()), null, rbv);
                hw.Show();
                hw.ShowView("resists");
                hw.UpdateLayout();
                hw.ShowView("fights");
                hw.Close();
                try { File.Delete(rbPath2); } catch { /* temp */ }
            }

            // The incoming-damage panel renders both states.
            {
                var iwv = new IncomingWatch();
                var win = new Views.IncomingWindow(iwv, () => "defensive", new ConfigService(), 1.0, 15) { Left = -9000, Top = -9000 };
                win.SetLocked(false);
                win.Show();
                win.Refresh();
                if (win.LastKind != "") throw new Exception("incoming panel: quiet state should carry no verdict");
                iwv.Add(DateTime.Now.AddSeconds(-2), 400, spell: true);
                iwv.Add(DateTime.Now.AddSeconds(-1), 100, spell: false);
                win.Refresh();
                if (win.LastKind != "switch") throw new Exception("incoming panel: spell-heavy in defensive should read switch, got " + win.LastKind);
                if (win.LastChip != "DEFENSIVE ▸ MAGE HUNTER") throw new Exception("incoming panel: the pill should name the stance to switch to, got " + win.LastChip);
                win.Close();
            }

            // The same chart as the DPS meter's cap (21 Sep): collapsed until
            // hosted, folded to its header while quiet, unfolds and reads the
            // verdict on the first hits, and comes off again.
            {
                var iwv = new IncomingWatch();
                var mp2 = new CombatParser { SelfName = "Thorrak" };
                var meter = new Views.MeterWindow(new ConfigService(), mp2, new LootTracker(new ConfigService()), 1.0,
                    Array.Empty<string>(), false, false, soloMode: true) { Left = -9000, Top = -9000 };
                meter.Show();
                if (meter.IncomingCapShown) throw new Exception("meter cap: shown before the chart was hosted");
                meter.SetIncoming(iwv, () => "defensive", 15, foldQuiet: true);
                if (!meter.IncomingCapShown) throw new Exception("meter cap: hosting should show the cap");
                if (meter.IncomingCapBodyShown) throw new Exception("meter cap: quiet should fold to the header");
                if (meter.IncomingCapKind != "") throw new Exception("meter cap: quiet state should carry no verdict");
                iwv.Add(DateTime.Now.AddSeconds(-2), 400, spell: true);
                iwv.Add(DateTime.Now.AddSeconds(-1), 100, spell: false);
                meter.SetIncoming(iwv, () => "defensive", 15, foldQuiet: true);
                if (!meter.IncomingCapBodyShown) throw new Exception("meter cap: hits should unfold the chart");
                if (meter.IncomingCapKind != "switch") throw new Exception("meter cap: spell-heavy in defensive should read switch, got " + meter.IncomingCapKind);
                if (meter.IncomingCapChip != "DEFENSIVE ▸ MAGE HUNTER") throw new Exception("meter cap: the pill should name the stance, got " + meter.IncomingCapChip);
                meter.SetIncoming(null, () => "defensive", 15, foldQuiet: true);
                if (meter.IncomingCapShown) throw new Exception("meter cap: un-hosting should collapse the cap");
                meter.Close();
            }

            // The toolbar in its card chrome (21 Sep): keys grow with labels, the
            // badges show counts, the log dot follows the status and the catch-up.
            {
                var cfgT = new ConfigService().LoadSettings();
                var vmT = new ViewModels.OverlayViewModel(new TriggerEngine(cfgT, new AlertService()), cfgT) { QuestBadge = 2 };
                var tb = new Views.ToolbarWindow(new ConfigService()) { DataContext = vmT, Left = -9000, Top = -9000 };
                tb.Show();
                tb.UpdateLayout();
                // Bindings and Style DataTriggers resolve on the dispatcher, which
                // the synchronous selftest never pumps — the toolbar:working and
                // toolbar:labels renders cover the badges, colours and key sizes.
                var vmL = new ViewModels.OverlayViewModel(new TriggerEngine(cfgT, new AlertService()), cfgT) { ToolbarLabels = true, Muted = true, Locked = true };
                var tbL = new Views.ToolbarWindow(new ConfigService()) { DataContext = vmL, Left = -9000, Top = -9000 };
                tbL.Show();
                tbL.Close();
                if (vmT.LogDot != "off") throw new Exception("toolbar: the log dot starts off, got " + vmT.LogDot);
                vmT.LogStatus = "Following eqlog_Thorrak_paineel.txt";
                if (vmT.LogDot != "on") throw new Exception("toolbar: following → the dot is on");
                vmT.Progress = new ReparseProgress("x", 1, 1, 1, 2, 10, Verb: "Catching up");
                if (vmT.LogDot != "catch") throw new Exception("toolbar: a catch-up → the dot is gold");
                vmT.Progress = null;
                if (vmT.LogDot != "on" || ViewModels.OverlayViewModel.BadgeText(120) != "99+") throw new Exception("toolbar: the dot returns to on; badges cap at 99+");
                tb.Close();
            }

            // The Races tab and the faction helper card (22 Sep).
            {
                var rb = RaceDemo(DateTime.Now.AddHours(-2));
                var inv = new Views.InventoryWindow(Path.Combine(Path.GetTempPath(), "eql_selftest_inv"), "Testchar", "paineel", null, null, rb) { Left = -9000, Top = -9000 };
                inv.Show();
                inv.ShowTab("races");
                if (inv.RaceRowsForTest != 6) throw new Exception("races tab: six badges expected, got " + inv.RaceRowsForTest);
                if (inv.RacesTabForTest.BadgeOrderForTest[0] is not ("Barbarian" or "Half Elf" or "Ogre")) throw new Exception("races tab: done races lead");
                inv.Close();
                rb.SetTracked("Human (Qeynos)", true);
                var fw = new Views.FactionHelperWindow(rb, new ConfigService(), 1.0) { Left = -9000, Top = -9000 };
                fw.Show();
                fw.Refresh();
                if (!fw.LineTexts.Contains("FACTION · HUMAN (QEYNOS)") || !fw.LineTexts.Contains("Corrupt Qeynos Guard")) throw new Exception("faction card: between hits it shows the tracked race's open factions");
                fw.ShowDemo("hit", "Human (Qeynos)", "Guards of Qeynos", 5, "a gnoll elite");
                if (!fw.LineTexts.Contains("Guards of Qeynos") || !fw.LineTexts.Contains("+5")) throw new Exception("faction card: names the faction and the hit");
                fw.Close();
            }

            // The tradeskill helper card renders the step and the ladder (21 Sep).
            {
                var tsd = new TradeskillData();
                var tsw = new TradeskillWatch(tsd, null);
                tsw.SetValue("Brewing", 87);
                tsw.StartSession("Brewing");
                var tsWin = new Views.TradeskillWindow(tsw, new ConfigService(), 1.0, ladder: false, bagCounts: false) { Left = -9000, Top = -9000 };
                tsWin.Show();
                tsWin.Refresh();
                if (tsWin.LastStep != "Skull Ale") throw new Exception("tradeskill card: Brewing 87 should sit on Skull Ale, got " + tsWin.LastStep);
                if (tsWin.ShowsLadder) throw new Exception("tradeskill card: opens on the step view");
                tsWin.ApplySettings(1.0, ladder: true, bagCounts: false);
                if (!tsWin.LineTexts.Contains("FARM 1") || !tsWin.LineTexts.Contains("ALL BOUGHT")) throw new Exception("tradeskill card: the ladder carries the sourcing tags");
                // A header click landing on a text Run (the "/250") must not crash the drag hit-test (owner, 21 Sep).
                var run = new System.Windows.Documents.Run("x");
                var tbHost = new TextBlock(); tbHost.Inlines.Add(run);
                var btnHost = new Button { Content = tbHost };
                if (Views.TradeskillWindow.FindAncestorButton(run) != btnHost) throw new Exception("tradeskill card: a Run inside a button should find its button");
                if (Views.TradeskillWindow.FindAncestorButton(new System.Windows.Documents.Run("loose")) is not null) throw new Exception("tradeskill card: a loose Run has no button");
                tsWin.Close();
            }

            // The crowd-control panels render the demo state.
            {
                var ccv = new CrowdControl(null, null);
                ccv.SeedDemo(DateTime.Now, broke: true);
                var cw = new Views.CharmWindow(ccv, new ConfigService(), 1.0) { Left = -9000, Top = -9000 };
                cw.SetLocked(false); cw.Show(); cw.Refresh();
                if (cw.LastState != "broke") throw new Exception("charm card: the demo should read broke, got " + cw.LastState);
                cw.Close();
                var mw = new Views.MezWindow(ccv, new ConfigService(), 1.0) { Left = -9000, Top = -9000 };
                mw.SetLocked(false); mw.Show(); mw.Refresh();
                if (mw.Rows.Count != 4 || !mw.Rows[0].Broke) throw new Exception("mez panel: the demo should list four rows, the broken one first");
                mw.Close();
            }

            // The con card renders verdicts and the honest empty line.
            {
                var cc = new Views.ConCardWindow { Left = -9000, Top = -9000 };
                cc.Show("a greater sphinx", 52, 21, new[]
                {
                    new ResistBook.Verdict("a greater sphinx", "Envenomed Breath", "poison", 0.62, 21, "immune"),
                    new ResistBook.Verdict("a greater sphinx", "Ignite", "fire", 0.35, 8, "resistant"),
                });
                cc.UpdateLayout();
                if (cc.RowCount != 2 || cc.Mob != "a greater sphinx") throw new Exception("con card: expected 2 verdict rows");
                cc.Show("a rat", 1, 6, Array.Empty<ResistBook.Verdict>());
                if (cc.RowCount != 0) throw new Exception("con card: the empty state renders no verdict rows");
                cc.Close();
            }

            // The level-up card renders and reports its rows.
            {
                var libC = new SpellLibrary(new ConfigService());
                var card = new Views.LevelUpWindow { Left = -9000, Top = -9000 };
                card.Show(46, new[] { "SHD", "SHM", "NEC" }, libC.UnlocksAt(46, new[] { "SHD", "SHM", "NEC" }));
                card.UpdateLayout();
                if (card.RowCount != 5) throw new Exception($"level-up card: expected 5 rows, got {card.RowCount}");
                card.Show(15, new[] { "DRU", "BRD", "WIZ" }, libC.UnlocksAt(15, new[] { "DRU", "BRD", "WIZ" }));
                if (!card.KindTexts.Contains("DIRECT DAMAGE") || !card.KindTexts.Contains("MEZ") || card.KindTexts.Contains("DEBUFF"))
                    throw new Exception("level-up card: level 15 DRU/BRD/WIZ must read DIRECT DAMAGE / MEZ, not DEBUFF: " + string.Join(",", card.KindTexts));
                card.Show(46, Array.Empty<string>(), Array.Empty<(SpellLibrary.Spell, string)>());
                if (card.RowCount != 0) throw new Exception("level-up card: the empty state must render zero rows");
                card.Close();
            }

            // Pin above game (7 Sep): the title-row pin flips Topmost and
            // BringToFront's bump must not knock it off again.
            var pinHost = new Window { Width = 300, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            int pinSaves = 0;
            var pin = new PagePin(pinHost, () => pinSaves++);
            if (pin.IsPinned || pinHost.Topmost) throw new Exception("pin: a fresh window is unpinned");
            pin.Toggle();
            if (!pin.IsPinned || !pinHost.Topmost || pinSaves != 1) throw new Exception("pin: toggle on");
            pin.Toggle();
            if (pin.IsPinned || pinHost.Topmost || pinSaves != 2) throw new Exception("pin: toggle off");
            pinHost.Close();
            // A bounds file written before the pin existed reads as unpinned;
            // one written since carries it.
            var oldBounds = System.Text.Json.JsonSerializer.Deserialize<ConfigService.DialogBounds>(
                "{\"Left\":10,\"Top\":20,\"Width\":800,\"Height\":600,\"Maximized\":false}");
            if (oldBounds is null || oldBounds.Pinned) throw new Exception("pin: legacy bounds read pinned");
            var newBounds = System.Text.Json.JsonSerializer.Deserialize<ConfigService.DialogBounds>(
                System.Text.Json.JsonSerializer.Serialize(oldBounds with { Pinned = true }));
            if (newBounds is null || !newBounds.Pinned) throw new Exception("pin: pinned bounds lost the pin");

            // The Sky helper panel renders its line list (temp progress file).
            string helperProg = Path.Combine(Path.GetTempPath(), "eql_test_helper_prog.json");
            try { File.Delete(helperProg); } catch { /* fresh */ }
            var helperSky = new SkyQuests(new ConfigService(), new LootTracker(new ConfigService()), helperProg);
            helperSky.SetTracked(helperSky.Quests[0], true);
            var helperWin = new Views.SkyHelperWindow(
                new SkyHelper(helperSky), helperSky, new ConfigService(), 1.0);
            helperWin.Show();
            helperWin.UpdateLayout();
            if (helperWin.LineTexts.Count == 0)
                throw new Exception("Sky helper panel rendered no lines for a tracked quest");
            helperWin.Close();

            // Fight History embeds the timeline view — constructing it proves
            // the UserControl resolves its theme resources on its own (a
            // parse-time StaticResource crash once hid exactly here).
            var histWin = new Views.HistoryWindow(cp, cs, new LootTracker(cs));
            histWin.Show();
            histWin.UpdateLayout();
            histWin.Close();

            // The cursor ring follows the mouse — construction + one frame.
            var ring = new Views.CursorRingWindow();
            ring.Show();
            ring.UpdateLayout();
            ring.Close();

            // The mote ticker's stint ends the moment you zone (16 Sep): a tier
            // 4 stint must not keep showing in tier 3 before the first drop there.
            {
                var t0 = DateTime.Now;
                var motes = new List<LootTracker.LootEntry>
                {
                    new(t0.AddMinutes(-2), "Mote of Major Potential", "a scorn banshee", "The Plane of Hate - Solo 4 (Refined)", LootTracker.LootKind.Kept),
                    new(t0.AddMinutes(-9), "Mote of Potential", "a scorn banshee", "The Plane of Hate - Solo 4 (Refined)", LootTracker.LootKind.Kept),
                    new(t0.AddMinutes(-40), "Mote of Potential", "a scorn banshee", "The Plane of Hate - Solo 4 (Refined)", LootTracker.LootKind.Kept),
                };
                var same = Views.MoteTickerWindow.LiveStint(motes, "The Plane of Hate - Solo 4 (Refined)", t0);
                var moved = Views.MoteTickerWindow.LiveStint(motes, "The Plane of Hate - Solo 3 (Ascended)", t0);
                var unknown = Views.MoteTickerWindow.LiveStint(motes, "", t0);
                if (same is not { ByGrade: var bg } || bg.Sum() != 2 || same.Value.Zone != "The Plane of Hate - Solo 4 (Refined)")
                    throw new Exception("mote ticker: the same-zone stint should chain the two recent motes and stop at the 31-minute gap");
                if (moved is not null) throw new Exception("mote ticker: zoning to another tier must end the stint");
                if (unknown is null) throw new Exception("mote ticker: an unknown current zone (fresh start) keeps the stint");
            }

            // The mote ticker builds and stays hidden with no live stint.
            var ticker = new Views.MoteTickerWindow(new LootTracker(cs), cs, 1.0);
            ticker.Show();
            ticker.UpdateLayout();
            ticker.Close();
            try { File.Delete(helperProg); } catch { /* temp */ }

            // The Sheet tab renders the doll + detail pane from a real-format
            // dump (worn item with a socket) inside the same window.
            File.WriteAllText(Path.Combine(invDir, "Sheetchar_paineel-Inventory.txt"),
                "Location\tName\tID\tCount\tSlots\r\n"
                + "Head\tWicked Sallet +5\t4301\t1\t10\r\n"
                + "Head-Slot7\tWicked Sallet (Exaltation)\t4301\t1\t10\r\n"
                + "Primary\tThe Baron's Blade +5\t5407\t1\t10\r\n"
                + "Ear\tEmpty\t0\t0\t0\r\n");
            var sheetWin = new Views.InventoryWindow(invDir, "Sheetchar", "paineel");
            sheetWin.Show();
            sheetWin.ShowTab("sheet"); // instantiate the doll + pane
            sheetWin.UpdateLayout();
            sheetWin.Close();
            var invEmpty = new Views.InventoryWindow(Path.Combine(invDir, "no_such_dir"), "X", "y");
            invEmpty.Show();
            invEmpty.ShowTab("sheet"); // the empty state must hold on every tab
            invEmpty.Close();

            // Segmented control (7 Sep): a click moves the pick and fires once;
            // Select() moves it silently; the thumb lands on the picked cell.
            var seg = new Segmented(new[]
            {
                new Segmented.Option("a", "This week"),
                new Segmented.Option("b", "All time"),
            }, "a");
            var segHost = new Window { Content = seg, Width = 300, Height = 100, Left = -9000, Top = -9000, ShowInTaskbar = false, ShowActivated = false };
            segHost.Show();
            segHost.UpdateLayout();
            int fired = 0;
            seg.Changed += _ => fired++;
            seg.Select("b");
            if (seg.Selected != "b" || fired != 0) throw new Exception("segmented: Select must be silent");
            seg.Select("a");
            segHost.UpdateLayout();
            segHost.Close();

            // Cursor ring card (7 Sep): settings reach the ring window in place.
            var ringDefaults = new Models.AppConfig().Overlay;
            if (ringDefaults.CursorRingSize != 44 || ringDefaults.CursorRingThickness != 3 || ringDefaults.CursorRingColor != "#E8C15A")
                throw new Exception("ring: defaults drifted");
            var ringWin = new CursorRingWindow();
            ringWin.ApplySettings(60, 4, "#4FD1FF", null);
            if (ringWin.RingSize != 60 || ringWin.RingThickness != 4 || ringWin.RingColor.B != 0xFF || ringWin.RingColor.R != 0x4F)
                throw new Exception("ring: ApplySettings did not reach the ellipses");
            if (ringWin.Width <= 60 + 8) throw new Exception("ring: the window must have room for the halo");
            ringWin.ApplySettings(30, 2, "not a color", null); // junk color falls back to gold, never throws
            if (ringWin.RingColor.R != 0xE8) throw new Exception("ring: junk color should fall back to gold");
            ringWin.Close();
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

            // ---- pet buffs: third-person landings gated to YOUR pet, anchored
            // to your begin-cast or the pet's own; the anonymous wear-off obeys
            // self-first then oldest; your death spares them, the pet's doesn't.
            var alacSpell = new SpellLibrary.Spell
            {
                Name = "Alacrity", Bucket = "Buff",
                CastOnYou = "You feel much faster.",
                CastOnOther = "Someone feels much faster.",
                WearsOff = "You feel yourself slow down.",
                DurationSec = 900,
            };
            var vortexSpell = new SpellLibrary.Spell
            {
                Name = "Shadow Vortex", Bucket = "Buff",
                CastOnOther = "Someone is protected by a vortex of shadows.",
                WearsOff = "The vortex of shadows fades.",
                DurationSec = 90,
            };
            // The scrape's placeholder subject varies — Spirit of the Puma
            // says "Target growls…" (the bug Johan caught 1 Sep 2026).
            var pumaSpell = new SpellLibrary.Spell
            {
                Name = "Spirit of the Puma", Bucket = "Buff",
                CastOnYou = "You begin to snarl as your features become feline.",
                CastOnOther = "Target growls with the spirit of the puma.",
                WearsOff = "The spirit of the puma departs.",
                DurationSec = 0,
            };
            var petAlac = SpellLibrary.PetBarTrigger(alacSpell, spokenWarning: true);
            var petVortex = SpellLibrary.PetBarTrigger(vortexSpell, spokenWarning: false);
            var petPuma = SpellLibrary.PetBarTrigger(pumaSpell, spokenWarning: false);
            Check("pet trigger: generated with OnPet, Pet category and the pet phrase",
                petAlac is { OnPet: true, Category: "Pet" }
                && petAlac.Alert?.Speak == "Your pet's Alacrity is about to fall");
            Check("pet trigger: every pet bar fades on the NAMED pet wear-off line",
                petAlac!.EndPattern == @"^Your pet's Alacrity(?: [IVX]{1,7})? spell has worn off\."
                && petVortex!.EndPattern is not null);
            Check("pet trigger: a 'Target …' scrape placeholder works too (Spirit of the Puma)",
                petPuma is not null
                && petPuma.StartRegex!.IsMatch("Vekn growls with the spirit of the puma."));

            var pcfg = new Models.AppConfig();
            pcfg.Triggers.Add(petAlac);
            pcfg.Triggers.Add(petVortex!);
            pcfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "selfvortex", Name = "Shadow Vortex", Category = "Buffs",
                StartPattern = @"You are protected by a vortex of shadows\.",
                EndPattern = @"The\ vortex\ of\ shadows\ fades\.",
                DurationSeconds = 90,
            });
            foreach (var t in pcfg.Triggers) ConfigService.CompileOne(t);
            var pe = new TriggerEngine(pcfg, new AlertService());
            pe.IsPetName = n => n is "Lonaner" or "Kibarn";

            pe.ProcessLine($"[{now}] Lonaner feels much faster.");
            Check("pet buff: an unanchored landing starts nothing", pe.Bars.Count == 0);
            pe.ProcessLine($"[{now}] You begin casting Alacrity.");
            pe.ProcessLine($"[{now}] Caladar feels much faster.");
            Check("pet buff: a groupmate's pet landing starts nothing", pe.Bars.Count == 0);
            pe.ProcessLine($"[{now}] Lonaner feels much faster.");
            Check("pet buff: your cast + the pet's landing = a named bar",
                pe.Bars.Count == 1 && pe.Bars[0].Name == "Alacrity — Lonaner"
                && pe.Bars[0].Category == "Pet");
            pe.ProcessLine($"[{now}] Lonaner begins casting Shadow Vortex.");
            pe.ProcessLine($"[{now}] Lonaner is protected by a vortex of shadows.");
            Check("pet buff: the pet's own cast anchors its self-buff",
                pe.Bars.Any(b => b.Name == "Shadow Vortex — Lonaner"));
            pe.ProcessLine($"[{now}] You are protected by a vortex of shadows.");
            Check("pet buff: your own vortex bar runs beside the pet's",
                pe.Bars.Count(b => b.Name.StartsWith("Shadow Vortex", StringComparison.Ordinal)) == 2);
            pe.ProcessLine($"[{now}] The vortex of shadows fades.");
            Check("pet buff: the impersonal fade takes YOUR bar only, the pet's stays",
                pe.Bars.Any(b => b.Name == "Shadow Vortex — Lonaner")
                && !pe.Bars.Any(b => b.Name == "Shadow Vortex"));
            pe.ProcessLine($"[{now}] Your pet's Shadow Vortex spell has worn off.");
            Check("pet buff: the NAMED pet wear-off closes the pet's bar",
                !pe.Bars.Any(b => b.Name.StartsWith("Shadow Vortex", StringComparison.Ordinal)));
            pe.ProcessLine($"[{now}] You are protected by a vortex of shadows.");
            pe.ProcessLine($"[{now}] You have been slain by a gnoll!");
            Check("pet buff: your death strips your bars but spares the pet's",
                pe.Bars.Count == 1 && pe.Bars[0].Name == "Alacrity — Lonaner");
            pe.ProcessLine($"[{now}] Lonaner has been slain by a gnoll!");
            Check("pet buff: the pet's death takes its buffs with it", pe.Bars.Count == 0);
            // Reminders: a pet trigger's REBUFF row says "Pet ·" (never a twin
            // of yours), and a dead pet has nothing to rebuff.
            petAlac.RemindWhenMissing = true;
            pe.CheckMissing(DateTime.Now);
            Check("pet buff: a dead pet raises no rebuff reminder", pe.Reminders.Count == 0);
            pe.ProcessLine($"[{now}] Kibarn begins casting Shadow Vortex.");
            pe.CheckMissing(DateTime.Now);
            Check("pet buff: the next summon's first cast wakes the reminder, labeled as the pet's",
                pe.Reminders.Count == 1 && pe.Reminders[0].Name == "Pet · Alacrity");

            // ---- fight details capture: damage schools from the log's own
            // words, debuff landings as timeline spans, and the analysis rules.
            var fd = new CombatParser { SelfName = "Johan", PetName = "Gobber" };
            fd.OtherLandingLookup = s => s.StartsWith("Drowsy", StringComparison.OrdinalIgnoreCase)
                ? (" looks drowsy.", true) : null;
            fd.DotDurationLookup = s => s == "Drowsy" ? 48 : null;
            fd.LoadoutLookup = () => "Raid SHD";
            fd.ActiveBuffsLookup = () => new[] { "Spirit of Wolf", "Vampiric Embrace" };
            fd.CurrentStance = "defensive"; // the persisted seed
            var fb = new DateTime(2026, 8, 20, 21, 0, 0);
            string FT(int s) => fb.AddSeconds(s).ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);
            fd.Replay($"[{FT(-40)}] [50 SHD/NEC] Johan (Ogre) <The Chosen Alliance> ZONE: Permafrost Keep (permafrost)  ");
            fd.Replay($"[{FT(-30)}] Lady Vox scowls at you, ready to attack -- looks like it would wipe the floor with you! (Lvl: 55)");
            fd.Replay($"[{FT(0)}] Lady Vox hit Johan for 250 points of cold damage by Frost Breath.");
            fd.NoteCondition("STUNNED", true, fb.AddSeconds(2));
            fd.Replay($"[{FT(3)}] You begin casting Drowsy.");
            fd.Replay($"[{FT(5)}] A dragon looks drowsy.");
            fd.Replay($"[{FT(6)}] You assume an offensive stance.");
            fd.NoteCondition("STUNNED", false, fb.AddSeconds(7));
            fd.Replay($"[{FT(4)}] Gobber hits Lady Vox for 50 points of damage.");
            fd.Replay($"[{FT(6)}] Lady Vox has taken 30 damage from Envenomed Bolt by Johan.");
            fd.Replay($"[{FT(9)}] Gobber has been slain by Lady Vox!");
            fd.Replay($"[{FT(7)}] Your Siphon Life spell is interrupted.");
            fd.Replay($"[{FT(8)}] You resist Lady Vox's Frost Breath!");
            fd.Replay($"[{FT(10)}] Lady Vox hit Johan for 250 points of cold damage by Frost Breath.");
            fd.Tick(fb.AddSeconds(60)); // idle -> archive
            var fdRec = fd.History[0];
            Check("fight: character, loadout, stance and buffs frozen at the pull",
                fdRec.Character == "Johan" && fdRec.Loadout == "Raid SHD"
                && fdRec.StanceAtStart == "defensive"
                && fdRec.BuffsAtStart.Contains("Spirit of Wolf"));
            Check("fight: the earlier /con stamps the enemy's level",
                fdRec.EnemyLevels.TryGetValue("Lady Vox", out int lvVox) && lvVox == 55);
            Check("fight: the /who line stamps YOUR level and class combo at the pull",
                fdRec.Classes == "SHD/NEC" && fdRec.Level == 50);

            // ---- class-combo capture: /who teaches, level-ups track, a
            // loadout swap (grant burst, no level line) honestly forgets.
            var wc = new CombatParser { SelfName = "Thorrak" };
            int swaps = 0;
            wc.SwapDetected += () => swaps++;
            wc.Replay($"[{FT(600)}] [26 SHD/ROG/SHM] Thorrak (Ogre) <The Chosen Alliance> ZONE: Najena (najena)  ");
            Check("classes: your own /who line teaches level + combo",
                wc.CurrentClasses == "SHD/ROG/SHM" && wc.CurrentLevel == 26);
            wc.Replay($"[{FT(601)}] [50 CLR/DRU/MAG] Retlon (Gnome)  ZONE: Najena (najena)  ");
            Check("classes: someone else's /who line changes nothing",
                wc.CurrentClasses == "SHD/ROG/SHM" && wc.CurrentLevel == 26);
            for (int i = 0; i < 5; i++)
                wc.Replay($"[{FT(620)}] You have been granted the following spell: Spell {i}.");
            wc.Replay($"[{FT(620)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(620)}] You have gained a level! Welcome to level 27!");
            wc.Replay($"[{FT(640)}] You are as quiet as a cat stalking its prey.");
            Check("classes: a level-up's grant burst tracks the level, keeps the combo",
                wc.CurrentClasses == "SHD/ROG/SHM" && wc.CurrentLevel == 27);
            for (int i = 0; i < 5; i++)
                wc.Replay($"[{FT(700)}] You have been granted the following spell: Other {i}.");
            wc.Replay($"[{FT(710)}] You are as quiet as a cat stalking its prey.");
            Check("classes: a grant burst with no level line = a swap = honestly unknown",
                wc.CurrentClasses == "" && wc.CurrentLevel == 0);
            wc.Replay($"[{FT(800)}] [27 SHD/ROG/SHM] Thorrak (Ogre)  ZONE: Najena (najena)  ");
            Check("classes: the next /who relabels after a swap",
                wc.CurrentClasses == "SHD/ROG/SHM" && wc.CurrentLevel == 27);
            wc.Replay($"[{FT(900)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(910)}] You are as quiet as a cat stalking its prey.");
            Check("classes: a lone spellbook refresh (pure-melee swap) also forgets",
                wc.CurrentClasses == "" && wc.CurrentLevel == 0);
            wc.Replay($"[{FT(1000)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(1002)}] [30 SHD/BER] Thorrak (Ogre)  ZONE: Najena (najena)  ");
            wc.Replay($"[{FT(1010)}] You are as quiet as a cat stalking its prey.");
            Check("classes: a /who inside the suspicion window is not wiped by it",
                wc.CurrentClasses == "SHD/BER" && wc.CurrentLevel == 30);
            Check("classes: the /who nag fired once per convicted swap", swaps == 2);
            // A spell upgrade rewrites the spellbook too (21 Sep): the merge line
            // explains the refresh that follows; an item merge does not.
            wc.Replay($"[{FT(1100)}] You have successfully merged two items together to create a new item: Drain Spirit V");
            wc.Replay($"[{FT(1102)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(1103)}] You have successfully merged two items together to create a new item: Drain Spirit VI");
            wc.Replay($"[{FT(1104)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(1112)}] You are as quiet as a cat stalking its prey.");
            Check("classes: spell upgrades never read as a swap — the combo stays",
                wc.CurrentClasses == "SHD/BER" && wc.CurrentLevel == 30 && swaps == 2);
            wc.Replay($"[{FT(1200)}] You have finished scribing Lich.");
            wc.Replay($"[{FT(1201)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(1210)}] You are as quiet as a cat stalking its prey.");
            Check("classes: a fresh scribe explains its refresh too", wc.CurrentClasses == "SHD/BER" && swaps == 2);
            wc.Replay($"[{FT(1300)}] You have successfully merged two items together to create a new item: Efreeti War Maul +2");
            wc.Replay($"[{FT(1301)}] Your spellbook has been updated!");
            wc.Replay($"[{FT(1310)}] You are as quiet as a cat stalking its prey.");
            Check("classes: an ITEM merge explains nothing — a refresh after it is still a swap", wc.CurrentClasses == "" && swaps == 3);

            // ---- instance tiers ride the zone name (Johan, 1 Sep 2026):
            // "Nagafen's Lair - Solo 4 (Refined)" = tier 4; no suffix = base.
            Check("tiers: the zone name's digit+mode is the tier",
                CombatParser.ZoneTier("Nagafen's Lair - Solo 4 (Refined)") == 4
                && CombatParser.ZoneTier("Befallen 3 (Fused)") == 3
                && CombatParser.ZoneTierLabel("The Ruins of Old Guk 1 (Awakened)") == "T1");
            Check("tiers: base zones, solo instances and blanks are tier 0",
                CombatParser.ZoneTier("Nagafen's Lair") == 0
                && CombatParser.ZoneTier("Nagafen's Lair - Solo") == 0
                && CombatParser.ZoneTierLabel("") == "");

            // ---- mote farm board: stints, floors, the grade lens and the
            // tier-lever verdict, over synthetic ledger entries.
            Check("motes: the ladder is the wiki's — Major BELOW Greater, ten ranks",
                MoteFarm.GradeOf("Mote of Minor Potential") == 1
                && MoteFarm.GradeOf("Mote of Potential") == 3
                && MoteFarm.GradeOf("Mote of Major Potential") == 4
                && MoteFarm.GradeOf("Mote of Greater Potential") == 5
                && MoteFarm.GradeOf("Mote of Ascendant Potential") == 8
                && MoteFarm.GradeOf("Mote of Infinite Potential") == 9
                && MoteFarm.GradeOf("Wind Rune Meda") == -1
                && MoteFarm.SpellPoints[9] == 512);
            var mb = new DateTime(2026, 9, 1, 20, 0, 0);
            var mEntries = new List<LootTracker.LootEntry>();
            // Old Paineel T4: 10 Greaters over 54 minutes — a PROVEN farm.
            for (int i = 0; i < 10; i++)
                mEntries.Add(new LootTracker.LootEntry(mb.AddMinutes(i * 6),
                    "Mote of Greater Potential", "Master Yael",
                    "The Ruins of Old Paineel - Solo 4 (Refined)", LootTracker.LootKind.Currency));
            // Plane of Hate: 8 motes in a hot ~32 minutes — rated, but too
            // young for the crown (Johan's 12-minute-wonder objection).
            for (int i = 0; i < 8; i++)
                mEntries.Add(new LootTracker.LootEntry(mb.AddMinutes(500 + i * 4.5),
                    "Mote of Greater Potential", "an ashenbone drake",
                    "The Plane of Hate - Solo 4 (Refined)", LootTracker.LootKind.Currency));
            // Old Paineel T3: 4 Greaters over 30 minutes — under the mote
            // floor but over the minutes floor.
            for (int i = 0; i < 4; i++)
                mEntries.Add(new LootTracker.LootEntry(mb.AddMinutes(120 + i * 10),
                    "Mote of Major Potential", "a burynai cleric",
                    "The Ruins of Old Paineel - Solo 3 (Fused)", LootTracker.LootKind.Currency));
            // Najena: two drops 40 minutes apart = TWO stints, 0 minutes on
            // the clock — small sample, no rate.
            mEntries.Add(new LootTracker.LootEntry(mb.AddMinutes(300), "Mote of Potential", "a ghoul",
                "Najena 2 (Adaptive)", LootTracker.LootKind.Currency));
            mEntries.Add(new LootTracker.LootEntry(mb.AddMinutes(340), "Mote of Potential", "a ghoul",
                "Najena 2 (Adaptive)", LootTracker.LootKind.Currency));
            var board = MoteFarm.Build(mEntries);
            var t4 = board.First(r => r.Zone.Contains("4 (Refined)"));
            var naj = board.First(r => r.Zone.StartsWith("Najena", StringComparison.Ordinal));
            Check("motes: a farmed zone rates in motes/hour",
                t4.RateFor() is { } t4r && Math.Abs(t4r - 11.1) < 0.6 && t4.Tier == 4);
            Check("motes: a gap over 15 minutes splits the stint and the clock stays honest",
                naj.Stints.Count == 2 && naj.RateFor() is null);
            Check("motes: the grade lens ranks by that grade alone",
                MoteFarm.Ranked(board, 4).All(r => r.ByGrade[4] > 0)
                && MoteFarm.Ranked(board, 4)[0].Zone.Contains("3 (Fused)"));
            Check("motes: droppers name who actually paid",
                t4.Droppers.Count == 1 && t4.Droppers[0] == ("Master Yael", 10));
            var mv = MoteFarm.Verdicts(board);
            Check("motes: the crown goes to the proven farm, and the tier lever speaks",
                mv.Any(x => x.Text.StartsWith("Your best farm", StringComparison.Ordinal)
                    && x.Text.Contains("The Ruins of Old Paineel - Solo 4"))
                && mv.Any(x => x.Text.Contains("Tier is the lever")
                    && x.Text.Contains("Greater") && x.Text.Contains("Major")));
            Check("motes: a hotter but younger zone gets the lucky-window caveat, not the crown",
                mv.Any(x => x.Caveat && x.Text.Contains("The Plane of Hate")
                    && x.Text.Contains("lucky window")));
            Check("motes: value/h weighs each mote by the wiki's spell points",
                t4.Points == 10 * 32 // ten Greaters
                && t4.ValueRate() is { } t4v && Math.Abs(t4v - 320 * 60.0 / 54) < 2
                && naj.ValueRate() is null); // floors gate value like rate
            // ---- best-in-slot finder: class rule, tier-scaled scoring, slots.
            Check("bis: the wiki's class spellings all parse (ANY class in the combo)",
                BisFinder.ClassAllowed("ALL", new[] { "SHD" })
                && !BisFinder.ClassAllowed("NONE", new[] { "SHD" })
                && BisFinder.ClassAllowed("ALL except NEC WIZ MAG ENC", new[] { "SHD", "NEC" })
                && !BisFinder.ClassAllowed("ALL except NEC WIZ MAG ENC", new[] { "NEC", "WIZ" })
                && BisFinder.ClassAllowed("WAR CLR PAL SHD BRD", new[] { "ROG", "SHD" })
                && !BisFinder.ClassAllowed("NEC WIZ MAG ENC", new[] { "SHD", "ROG", "SHM" }));
            var bisStats = new ItemStats();
            var bisRows = new List<InventoryStore.CarryRow>
            {
                new("Wicked Sallet +5", "wicked sallet +5", "Head", 1, "worn", 1),
                new("Woven Skull Cap", "woven skull cap", "General 3-Slot2", 1, "bags", 2),
                new("Golden Efreeti Boots +3", "golden efreeti boots +3", "Bank5-Slot8", 1, "bank", 3),
                new("Lustrous Russet Boots", "lustrous russet boots", "Bank5-Slot9", 1, "bank", 6), // +0 — the "would it be BiS upgraded?" case
                new("Grimy Black Silk Robe +5", "grimy black silk robe +5", "Bank5-Slot6", 1, "bank", 4),
                new("Coin Purse of Nowhere", "coin purse of nowhere", "General 1-Slot1", 1, "bags", 5),
            };
            var bis = BisFinder.Build(bisRows, bisStats, new[] { "SHD", "ROG", "SHM" },
                new[] { "AC", "STA", "INT" });
            var head = bis.Slots.First(s => s.Key == "HEAD");
            var chest = bis.Slots.First(s => s.Key == "CHEST");
            var feet = bis.Slots.First(s => s.Key == "FEET");
            // Wicked Sallet +5: AC 10→15, STA 3→8, INT 2→7 ⇒ 2·15 + 1.5·8 + 7 = 49 by default.
            Check("bis: stats scale by the item's +N tier and score 2 · 1.5 · 1 by default",
                head.Ranked.Count > 0 && head.Ranked[0].BaseName == "Wicked Sallet"
                && Math.Abs(head.Ranked[0].Score - 49) < 0.01 && head.Ranked[0].Worn);
            // The weights are the player's (3 Oct): the old 3 · 2 · 1 gives 45 + 16 + 7; the pills cycle 1 → 1.5 → 2 → 3.
            var steep = BisFinder.Build(bisRows, bisStats, new[] { "SHD" }, new[] { "AC", "STA", "INT" }, weights: new double[] { 3, 2, 1 });
            Check("bis: the three weights are the player's — 3 · 2 · 1 scores the sallet 68; a pill cycles 1 → 1.5 → 2 → 3 → 1",
                Math.Abs(steep.Slots.First(s => s.Key == "HEAD").Ranked[0].Score - 68) < 0.01
                && BisFinder.NextWeight(2) == 3 && BisFinder.NextWeight(3) == 1 && BisFinder.NextWeight(1) == 1.5 && BisFinder.NextWeight(1.5) == 2
                && BisFinder.WeightText(1.5) == "1.5" && BisFinder.WeightText(2) == "2");
            // The tail: Wicked Sallet +5 also carries STR 3→8 — at ×0.5 the score
            // grows by exactly 4; the three picks never count twice.
            var tailed = BisFinder.Build(bisRows, bisStats, new[] { "SHD" }, new[] { "AC", "STA", "INT" },
                tailWeight: 0.5);
            Check("bis: the other-stats tail rewards the well-rounded piece without double-counting",
                Math.Abs(tailed.Slots.First(s => s.Key == "HEAD").Ranked[0].Score - 53) < 0.01);
            Check("bis: the worn winner is no upgrade; a foreign-class robe stays visible but unranked",
                !head.Upgrades.Any()
                && chest.Ranked.Count == 0 && chest.Foreign.Count == 1
                && chest.Foreign[0].BaseName == "Grimy Black Silk Robe");
            Check("bis: an ALL-class item in the bank is an upgrade for an empty slot",
                feet.Upgrades.Any(u => u.Lane == "bank") && feet.Ranked.Count == 2);
            // Would be BiS if upgraded (5 Oct): the feet runner-up catches the pick at the LOWEST tier that beats it —
            // one tier under, it still loses; the head has one allowed item, so nothing to project.
            {
                var pick = feet.Picks.First(); var other = feet.Ranked[1];
                var pj = feet.Projected.ToList();
                Check("bis: the runner-up is projected at the lowest tier that beats the pick, with the score it would have there",
                    pj.Count == 1 && pj[0].Item.Name == other.Name && pj[0].Tier > other.Tier && pj[0].Tier <= ItemUpgrade.MaxTier
                    && pj[0].Score > pick.Score && other.ScoreByTier![pj[0].Tier - 1] <= pick.Score
                    && Math.Abs(pj[0].Score - BisFinder.Score(BisFinder.ScaledStats(other.Rec, pj[0].Tier), new[] { "AC", "STA", "INT" })) < 0.01
                    && Math.Abs(other.ScoreByTier[other.Tier] - other.Score) < 0.01
                    && !head.Projected.Any());
            }
            Check("bis: items the wiki doesn't know are named, never silently dropped",
                bis.Unknown.Contains("Coin Purse of Nowhere"));
            // Weapons by fighting style: a 1H stick, a 2H reaver, a shield.
            var wRows = new List<InventoryStore.CarryRow>
            {
                new("Carved Walking Stick", "carved walking stick", "Primary", 1, "worn", 10), // WORN in Primary
                new("A Dark Reaver +2", "a dark reaver +2", "Bank1-Slot1", 1, "bank", 11),
                new("Buckler of Doom", "buckler of doom", "Bank1-Slot2", 1, "bank", 12),
                new("Soldier's Brooch of the Spirited +2", "soldier's brooch", "Range", 1, "worn", 13), // stat range item
                new("Rib-bone Stiletto +1", "rib-bone stiletto +1", "General 3-Slot4", 1, "bags", 14), // backstab piercer
            };
            var wAll = BisFinder.Build(wRows, bisStats, new[] { "SHD", "SHM" }, new[] { "DMG_DLY", "STR", "STA" });
            var twoH = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.TwoHanded);
            var shield = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.ShieldAndOne);
            var dual = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.DualWield);
            Check("bis: two-handed style keeps only 2H weapons and drops the secondary slot",
                twoH.Slots.First(s => s.Key == "PRIMARY").Ranked.Select(c => c.BaseName).SequenceEqual(new[] { "A Dark Reaver" })
                && twoH.Slots.All(s => s.Key != "SECONDARY"));
            Check("bis: 1H + shield pairs a one-hander with an off-hand",
                shield.Slots.First(s => s.Key == "PRIMARY").Ranked[0].BaseName == "Carved Walking Stick"
                && shield.Slots.First(s => s.Key == "SECONDARY").Ranked.Single().BaseName == "Buckler of Doom");
            Check("bis: dual wield never offers the weapon worn in Primary for the off-hand",
                dual.Slots.First(s => s.Key == "SECONDARY").Ranked.All(c => c.BaseName != "Carved Walking Stick")
                && dual.Slots.First(s => s.Key == "SECONDARY").Ranked.Any(c => c.BaseName == "Rib-bone Stiletto"));
            var bsOnly = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.DualWield, backstabOnly: true);
            Check("bis: the backstab filter keeps only main-hand weapons with a backstab number, tier-scaled",
                bsOnly.Slots.First(s => s.Key == "PRIMARY").Ranked.Select(c => c.BaseName).SequenceEqual(new[] { "Rib-bone Stiletto" })
                && bsOnly.Slots.First(s => s.Key == "PRIMARY").Ranked[0].Stats["BACKSTAB"] == 7);
            var rangeStat = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.DualWield, BisFinder.RangeMode.Stat);
            Check("bis: Range lives in the weapons view, toggling DPS (bows) or stat (brooches)",
                BisFinder.ArmorView(wAll).Slots.All(s => !BisFinder.WeaponSlotKeys.Contains(s.Key))
                && dual.Slots.First(s => s.Key == "RANGE").Ranked.Count == 0
                && rangeStat.Slots.First(s => s.Key == "RANGE").Ranked.Any(c => c.BaseName.StartsWith("Soldier's Brooch")));
            var bisBank = BisFinder.Build(bisRows, bisStats, new[] { "SHD" }, new[] { "AC", "STA", "INT" },
                new[] { "bank" });
            Check("bis: the search-in lanes narrow the field",
                bisBank.Slots.First(s => s.Key == "HEAD").Ranked.Count == 0
                && bisBank.Slots.First(s => s.Key == "FEET").Ranked.Count == 2); // both pairs of boots sit in the bank

            var (msShown, msThin) = MoteFarm.SplitByFarmed(board, 45);
            Check("motes: the strictness dial splits farms from hints",
                msShown.Count == 1 && msShown[0].Zone.Contains("Old Paineel")
                && msThin.Count == 3 // Hate (32m), Old Paineel T3 (30m), Najena (0m)
                && MoteFarm.SplitByFarmed(board, 0).Thin.Count == 0);
            Check("fight: stance change, cast and interrupt ride the timeline",
                fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.Stance, Ability: "offensive" })
                && fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.Cast, Ability: "Drowsy", Miss: false })
                && fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.Cast, Miss: true }));
            Check("fight: the stun becomes a condition span (landing -> release)",
                fdRec.Events.Any(e => e.Stream == CombatParser.FightStream.Condition
                    && e.Ability == "Stunned" && Math.Abs(e.Amount - 5) < 0.01));
            Check("fight: a tick-shaped damage line flags its spell as a DoT",
                fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.SelfOut, Ability: "Envenomed Bolt", Dot: true }));
            Check("fight: the pet's name and its death ride the record",
                fdRec.Pet == "Gobber"
                && fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.PetDeath, Ability: "Gobber died" }));
            Check("fight: the pet lands in the record's pet list, not as a stranger",
                fdRec.Pets.Contains("Gobber") && fd.IsKnownPet("Gobber"));
            Check("fight: the DD line's school is kept, verbatim from the log",
                fdRec.Schools.TryGetValue("Frost Breath", out string? fs) && fs == "cold");
            Check("fight: a debuff landing becomes a timeline span with its duration",
                fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.Debuff, Ability: "Drowsy", Amount: 48 }));
            Check("fight: the incoming resist rides the timeline",
                fdRec.Events.Any(e => e is { Stream: CombatParser.FightStream.SelfIn, Resist: true }));
            // Bystander filter: history is YOUR story, the meter shows the rest.
            var by = new CombatParser { SelfName = "Johan" };
            by.Replay($"[{FT(100)}] A fire giant warrior hits Vana for 468 points of damage.");
            by.Tick(fb.AddSeconds(160));
            Check("fight: a mob beating on a passer-by never archives", by.History.Count == 0);
            by.Replay($"[{FT(200)}] Johan healed Johan for 300 hit points by Chloroplast.");
            by.Tick(fb.AddSeconds(260));
            Check("fight: your regen ticking between pulls never archives", by.History.Count == 0);
            by.Replay($"[{FT(300)}] A fire giant warrior hits Bruvos for 100 points of damage.");
            by.Replay($"[{FT(301)}] Johan healed Bruvos for 300 hit points by Superior Heal.");
            by.Tick(fb.AddSeconds(400));
            Check("fight: pure healing with a real enemy present still archives",
                by.History.Count == 1);
            Check("fight: your heal spells get their own drill-down rows",
                by.History[0].SelfHealAbilities.Any(a => a.Name == "Superior Heal"));

            // Allies vs bystanders: "group" means someone joined YOUR fight —
            // a neighbour farming their own camp in logging range is scenery.
            var al = new CombatParser { SelfName = "Johan" };
            al.Replay($"[{FT(500)}] Tolo hit Lady Vox for 40 points of magic damage by Odium."); // tags her FIRST
            al.Replay($"[{FT(501)}] Lady Vox hit Johan for 100 points of cold damage by Frost Breath.");
            al.Replay($"[{FT(503)}] Kettel hit a haunted chest for 90 points of magic damage by Blaze.");
            // Johan damages the chest too — but it never HIT him, and mob
            // instances share names, so Kettel stays a neighbour (owner's rule).
            al.Replay($"[{FT(505)}] Johan hit a haunted chest for 10 points of magic damage by Odium.");
            // A mob "healing" you (mitigated drains print 0-heals) is no friend.
            al.Replay($"[{FT(506)}] Lady Vox healed Johan for 0 hit points by Drain Essence.");
            al.Tick(fb.AddSeconds(560));
            Check("fight: an ally hit a mob that was ON me; a same-named neighbour never counts",
                al.History[0].Allies.Contains("Tolo") && !al.History[0].Allies.Contains("Kettel")
                && !al.History[0].Allies.Contains("Lady Vox"));
            Check("fight: an old pet's speech is harvested retroactively",
                al.TryParsePetSpeech($"[{FT(0)}] Lonaner told you, 'Attacking a will sapper Master.'",
                    out string oldPet) && oldPet == "Lonaner" && al.IsKnownPet("Lonaner"));

            Check("durations: percentile math (median + quartiles, interpolated)",
                Math.Abs(SpellDurations.Percentile(new[] { 1.0, 2, 3, 4 }, 0.50) - 2.5) < 0.001
                && Math.Abs(SpellDurations.Percentile(new[] { 1.0, 2, 3, 4 }, 0.25) - 1.75) < 0.001
                && Math.Abs(SpellDurations.Percentile(new[] { 7.0 }, 0.75) - 7) < 0.001);

            string fdTxt = Views.TimelineView.BuildAnalysis(fdRec);
            Check("analysis: names the dominant school and the resist advice",
                fdTxt.Contains("COLD") && fdTxt.Contains("More Cold resist")
                && fdTxt.Contains("resisted 1 of 3"));
            Check("analysis: reports the debuff coverage",
                fdTxt.Contains("Drowsy was up"));
            Check("analysis: CC, interrupted cast and stance each get a line",
                fdTxt.Contains("CC held you for 0:05")
                && fdTxt.Contains("Siphon Life was interrupted")
                && fdTxt.Contains("Mostly defensive stance") && fdTxt.Contains("switched 1×"));
            Check("analysis: the pet death speaks, the tile-covered share stays quiet",
                fdTxt.Contains("Gobber died at 0:09") && !fdTxt.Contains("Gobber dealt")
                && !fdTxt.Contains("Biggest hit on you:"));

            // The playbook rules (eql-fight-analyst): clipping, refresh nudge,
            // melee hit rate vs con level, and the danger window — on a
            // synthetic record built to trip each threshold.
            var ar = new CombatParser.FightRecord
            {
                Label = "audit fight",
                DurationSeconds = 100,
                IncomingSelfTotal = 1000,
                Damage = { new CombatParser.Row("A test mob", 1000, 10, 100, true) },
                SelfAbilities =
                {
                    new CombatParser.Row("slash", 500, 5, 50, false, Hits: 8, Misses: 12),
                    new CombatParser.Row("kick", 100, 1, 10, false, Hits: 2, Misses: 3),
                },
                EnemyLevels = { ["A test mob"] = 56 },
            };
            ar.Events.AddRange(new[]
            {
                new CombatParser.FightEvent(0, "Venom", 40, CombatParser.FightStream.Debuff),
                new CombatParser.FightEvent(20, "Venom", 40, CombatParser.FightStream.Debuff),
                new CombatParser.FightEvent(5, "Venom", 30, CombatParser.FightStream.SelfOut, Dot: true),
                new CombatParser.FightEvent(50, "smash", 400, CombatParser.FightStream.SelfIn),
                new CombatParser.FightEvent(55, "smash", 350, CombatParser.FightStream.SelfIn),
                new CombatParser.FightEvent(10, "Heal", 200, CombatParser.FightStream.HealOut),
            });
            string aud = Views.TimelineView.BuildAnalysis(ar);
            Check("analysis: clipping, melee hit rate and the danger window fire",
                aud.Contains("clipped Venom 1×") && aud.Contains("~20s")
                && aud.Contains("melee landed only 40%") && aud.Contains("Lvl 56")
                && aud.Contains("Danger window"));
            Check("analysis: a sloppy DoT gets the refresh nudge",
                aud.Contains("Refresh sooner"));
            Check("analysis: coverage math merges overlaps and clips",
                Math.Abs(Views.TimelineView.CoverageSeconds(
                    new[] { (0.0, 10.0), (5.0, 10.0) }, 30) - 15) < 0.01
                && Math.Abs(Views.TimelineView.CoverageSeconds(
                    new[] { (25.0, 10.0) }, 30) - 5) < 0.01);

            // ---- permanent buffs (Vampiric Embrace): ∞ until death, a
            // wear-off line, or a loadout switch — never a countdown.
            var permCfg = new Models.AppConfig();
            permCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "ve", Name = "Vampiric Embrace", Category = "Buffs",
                StartPattern = @"Your hand begins to glow\.",
                Permanent = true, RemindWhenMissing = true, DurationSeconds = 0,
            });
            foreach (var t in permCfg.Triggers) ConfigService.CompileOne(t);
            var perm = new TriggerEngine(permCfg, new AlertService { Muted = true });
            perm.ProcessLine($"[{now}] Your hand begins to glow.");
            Check("permanent: the bar lands as ∞, never expiring",
                perm.Bars.Count == 1
                && perm.Bars[0] is { IsPermanent: true, RemainingText: "∞", IsExpired: false });
            perm.CheckMissing(DateTime.Now.AddHours(6));
            Check("permanent: hours later it is still not 'missing'",
                perm.Reminders.Count == 0 && perm.Bars.Count == 1);
            perm.ProcessLine($"[{now}] You have been slain by a gnoll reaver!");
            perm.CheckMissing(DateTime.Now.AddSeconds(1));
            Check("permanent: death strips it and the rebuff reminder takes over",
                perm.Bars.Count == 0 && perm.Reminders.Count == 1);

            // ---- rebuff reminders: repeat at the interval; after 5 spoken
            // warnings the interval DOUBLES (ignored nagging earns quieter
            // nagging), snapping back when the buff is reapplied.
            var remCfg = new Models.AppConfig { Overlay = { RemindIntervalSeconds = 10 } };
            remCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "sow", Name = "Spirit of Wolf", Category = "Buffs",
                StartPattern = @"You feel the spirit of wolf enter you\.",
                EndPattern = @"Your Spirit of Wolf spell has worn off\.",
                DurationSeconds = 1800, RemindWhenMissing = true,
            });
            foreach (var t in remCfg.Triggers) ConfigService.CompileOne(t);
            var rem = new TriggerEngine(remCfg, new AlertService { Muted = true });
            var t0 = DateTime.Now;
            string RT(double s) => t0.AddSeconds(s).ToString("ddd MMM dd HH:mm:ss yyyy",
                System.Globalization.CultureInfo.InvariantCulture);
            rem.ProcessLine($"[{RT(0)}] You feel the spirit of wolf enter you.");
            rem.ProcessLine($"[{RT(5)}] Your Spirit of Wolf spell has worn off.");
            rem.CheckMissing(t0.AddSeconds(6)); // missing appears: warning #1
            for (int i = 1; i <= 4; i++) rem.CheckMissing(t0.AddSeconds(6 + i * 10));
            Check("reminders: five warnings at the configured cadence",
                rem.RemindCountFor("sow") == 5 && rem.Reminders.Count == 1);
            rem.CheckMissing(t0.AddSeconds(6 + 5 * 10)); // +10s: inside the doubled interval
            Check("reminders: after five, the interval doubles",
                rem.RemindCountFor("sow") == 5);
            rem.CheckMissing(t0.AddSeconds(6 + 6 * 10)); // +20s since #5: speaks
            Check("reminders: the doubled cadence still speaks",
                rem.RemindCountFor("sow") == 6);
            rem.ProcessLine($"[{RT(70)}] You feel the spirit of wolf enter you.");
            rem.CheckMissing(t0.AddSeconds(71));
            Check("reminders: rebuffing clears the bar and resets the backoff",
                rem.RemindCountFor("sow") == 0 && rem.Reminders.Count == 0);
            rem.ProcessLine($"[{RT(80)}] Your Spirit of Wolf spell has worn off.");
            rem.CheckMissing(t0.AddSeconds(81));
            rem.CheckMissing(t0.AddSeconds(91)); // 10s again — the configured interval rules
            Check("reminders: the next outage starts at the configured interval",
                rem.RemindCountFor("sow") == 2);

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
            Check("loot: currency form (motes, wind runes)", LootTracker.TryParseLoot(
                "You looted a Mote of Minor Potential from a shin ghoul knight's corpse and stored it in your currency",
                out lk, out li, out lm, out lr, out _, out _)
                && lk == LootTracker.LootKind.Currency && li == "Mote of Minor Potential"
                && lm == "a shin ghoul knight");
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
            cp.ProcessLine("[Sat Aug 08 23:21:30 2026] You assume a defensive stance.");
            cp.ProcessLine("[Sat Aug 08 23:21:34 2026] A zol ghoul knight hits YOU for 42 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:34 2026] A zol ghoul knight tries to hit YOU, but misses!");
            cp.ProcessLine("[Sat Aug 08 23:21:35 2026] Nurse heals you for 50 hit points by Minor Healing.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] A bok ghoul knight hits YOU for 24 points of damage.");
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] You have been slain by a bok ghoul knight!");
            Check("recap: slain line fires with killer",
                deaths.Count == 1 && deaths[0].Killer == "a bok ghoul knight");
            Check("recap: the stance rides the death (8 Sep) and melee hits carry the Melee flavor",
                deaths.Count == 1 && deaths[0].Stance == "defensive" && cp.CurrentStance == "defensive"
                && deaths[0].Events[0].Flavor == CombatParser.SctFlavor.Melee
                && deaths[0].Events[2].Flavor == CombatParser.SctFlavor.Heal);
            cp.ProcessLine("[Sat Aug 08 23:21:37 2026] You assume a mage hunter stance.");
            Check("recap: a two-word stance parses", cp.CurrentStance == "mage hunter");
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

            // Recap presentation (the C+A rebuild): repeats merge into ×N
            // rows, misses never make rows, and the story names the burst.
            DateTime RD(int s) => new DateTime(2026, 8, 15, 22, 0, 0).AddSeconds(s);
            var rev = new List<CombatParser.RecapEntry>
            {
                new(RD(-14), "A spite golem", "hit", 87, Heal: false, Crit: false),
                new(RD(-12), "a loathling lich", "Specter Lifetap", 49, Heal: false, Crit: false),
                new(RD(-10), "a loathling lich", "Specter Lifetap", 49, Heal: false, Crit: false),
                new(RD(-9), "Thorrak", "Siphon Life", 163, Heal: true, Crit: false),
                new(RD(-8), "a loathling lich", "slice", 0, Heal: false, Crit: false, Miss: true),
                new(RD(-1), "an ire ghast", "Harm Touch", 453, Heal: false, Crit: false),
                new(RD(0), "A spite golem", "hit", 142, Heal: false, Crit: false),
            };
            var dev2 = new CombatParser.DeathEvent(RD(0), "a loathling lich", rev);
            var biggestHit = rev.Where(e => !e.Heal && !e.Miss).MaxBy(e => e.Amount);
            var grp = Views.DeathRecapWindow.GroupEvents(rev, biggestHit);
            Check("recap: repeats merge into ×N rows and misses are excluded",
                grp.Count == 4
                && grp.First(x => x.Ability == "Specter Lifetap") is { Count: 2, Total: 98 }
                && grp.First(x => x.Ability == "hit") is { Count: 2, Total: 229 }
                && grp.All(x => x.Ability != "slice"));
            Check("recap: the killing-blow group is flagged",
                grp.Single(x => x.HasBiggestHit).Ability == "Harm Touch");
            string story = Views.DeathRecapWindow.BuildStory(dev2, rev,
                taken: 780, healed: 163, span: 14);
            Check("recap: the story names the killing burst",
                story.Contains("595") && story.Contains("Harm Touch"));
            // Melee vs spell + the stance verdict (owner, 8 Sep).
            Check("recap: the split line carries both numbers and shares",
                Views.DeathRecapWindow.SplitLine(1200, 2800) is { } sl && sl.StartsWith("Melee −") && sl.Contains("(30%)") && sl.Contains("Spells −") && sl.Contains("(70%)"));
            Check("recap: defensive stance under spell damage points at mage hunter",
                Views.DeathRecapWindow.StanceVerdict("defensive", 1235, 2685).Contains("mage hunter stance would have halved"));
            Check("recap: mage hunter under melee damage points at defensive",
                Views.DeathRecapWindow.StanceVerdict("mage hunter", 3000, 500).Contains("defensive stance would have halved"));
            Check("recap: the right stance reads as a numbers problem",
                Views.DeathRecapWindow.StanceVerdict("defensive", 3000, 500).Contains("numbers problem")
                && Views.DeathRecapWindow.StanceVerdict("mage hunter", 100, 900).Contains("numbers problem"));
            Check("recap: an unknown stance still names the halving stance",
                Views.DeathRecapWindow.StanceVerdict("", 100, 900).Contains("No stance change seen")
                && Views.DeathRecapWindow.StanceVerdict("", 100, 900).Contains("mage hunter"));
            Check("recap: mixed damage says no stance halves both",
                Views.DeathRecapWindow.StanceVerdict("striker", 1000, 1100).Contains("no stance halves both"));
            var slow = rev.Where(e => (RD(0) - e.When).TotalSeconds > 2).ToList();
            Check("recap: no burst reads as worn down",
                Views.DeathRecapWindow.BuildStory(new CombatParser.DeathEvent(RD(0), "x", slow),
                    slow, taken: 185, healed: 163, span: 14).StartsWith("Worn down"));
            // The raid puzzle: +304 healing over −285 taken and dead anyway —
            // the window doesn't explain it, and the story must say so instead
            // of claiming "worn down".
            Check("recap: healing that covered the damage admits it doesn't add up",
                Views.DeathRecapWindow.BuildStory(new CombatParser.DeathEvent(RD(0), "x", slow),
                    slow, taken: 285, healed: 304, span: 15).Contains("don't add up"));

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
            // duration). Auto anchors EVERY library (lib-*) trigger: EQL is
            // solo-first, so a groupmate's buff landing on you starts nothing
            // by default; untick per trigger to opt into group play.
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
            var anc = new TriggerEngine(ancCfg, new AlertService());
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

            // Solo-first: even a UNIQUE landing sentence anchors on auto for a
            // library trigger — a groupmate's buff landing on you would
            // otherwise start a bar for a spell you never cast.
            var soloCfg = new Models.AppConfig();
            soloCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-strengthen", Name = "Strengthen", DurationAuto = false,
                StartPattern = Esc("You feel stronger."), DurationSeconds = 1620,
            });
            ConfigService.CompileOne(soloCfg.Triggers[0]);
            var solo = new TriggerEngine(soloCfg, new AlertService());
            solo.ProcessLine($"[{AT(0)}] You feel stronger.");
            Check("anchor: solo-first — an unshared library landing still needs your cast",
                solo.Bars.Count == 0);
            solo.ProcessLine($"[{AT(10)}] You begin casting Strengthen.");
            solo.ProcessLine($"[{AT(12)}] You feel stronger.");
            Check("anchor: solo-first — your own cast starts it", solo.Bars.Count == 1);

            // Quick Buff (the Companion's case 3): the AA lands the whole
            // spellbar at once with no cast lines. During the window an
            // anchored landing is admitted only when the spell is plausibly
            // yours — never-cast spells stay silent, ever-cast ones start,
            // learner knowledge counts as proof, others' activations don't.
            var qbCfg = new Models.AppConfig();
            qbCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-quickness", Name = "Quickness", DurationAuto = false,
                StartPattern = Esc("You feel much faster."), DurationSeconds = 660,
            });
            ConfigService.CompileOne(qbCfg.Triggers[0]);
            var qb = new TriggerEngine(qbCfg, new AlertService());
            qb.ProcessLine($"[{AT(0)}] You activate Quick Buff.");
            qb.ProcessLine($"[{AT(3)}] You feel much faster.");
            Check("quick buff: a never-cast spell stays silent", qb.Bars.Count == 0);
            qb.ProcessLine($"[{AT(100)}] You begin casting Quickness II.");
            qb.ProcessLine($"[{AT(103)}] You feel much faster.");
            qb.ProcessLine($"[{AT(200)}] You activate Quick Buff.");
            qb.ProcessLine($"[{AT(203)}] You feel much faster.");
            Check("quick buff: an ever-cast spell refreshes from the burst",
                qb.Bars.Count == 1
                && qb.Bars[0].EndTimeLocal > new DateTime(2026, 8, 10, 23, 0, 0).AddSeconds(850));
            qb.ProcessLine($"[{AT(300)}] You feel much faster.");
            double afterStray = (qb.Bars[0].EndTimeLocal
                - new DateTime(2026, 8, 10, 23, 0, 0)).TotalSeconds;
            Check("quick buff: outside the window the anchor still guards",
                Math.Abs(afterStray - 863) < 0.01); // unchanged since the 203 burst
            var qb2 = new TriggerEngine(qbCfg, new AlertService())
            {
                LearnedDuration = n => n == "Quickness" ? 555 : null,
            };
            qb2.ProcessLine($"[{AT(0)}] Caladar activates Quick Buff.");
            qb2.ProcessLine($"[{AT(3)}] You feel much faster.");
            Check("quick buff: someone else's activation opens no window", qb2.Bars.Count == 0);
            qb2.ProcessLine($"[{AT(50)}] You activate Quick Buff.");
            qb2.ProcessLine($"[{AT(53)}] You feel much faster.");
            Check("quick buff: learner knowledge admits a cold-start burst",
                qb2.Bars.Count == 1 && qb2.Bars[0].Name == "Quickness");

            var offCfg = new Models.AppConfig();
            offCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "lib-quickness", Name = "Quickness", DurationSeconds = 660,
                StartPattern = Esc("You feel much faster."), CastAnchored = false,
            });
            ConfigService.CompileOne(offCfg.Triggers[0]);
            var offEng = new TriggerEngine(offCfg, new AlertService());
            offEng.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: explicit untick beats auto", offEng.Bars.Count == 1);

            var freeCfg = new Models.AppConfig();
            freeCfg.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "custom-haste", Name = "AnyHaste", DurationSeconds = 60,
                StartPattern = Esc("You feel much faster."),
            });
            ConfigService.CompileOne(freeCfg.Triggers[0]);
            var freeEng = new TriggerEngine(freeCfg, new AlertService());
            freeEng.ProcessLine($"[{AT(0)}] You feel much faster.");
            Check("anchor: custom triggers stay unanchored on auto", freeEng.Bars.Count == 1);
            freeCfg.Triggers[0].CastAnchored = true;
            var freeEng2 = new TriggerEngine(freeCfg, new AlertService());
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
            // The regen line is maintenance, not rotational healing (2.12).
            Check("typing: Regeneration/Chloroplast are regen BUFFS, not HoTs",
                CatOf("Regeneration") == "Buffs" && CatOf("Chloroplast") == "Buffs");
            Check("typing: a long 'Regen'-typed spell is a buff too",
                CatOf("Spiritual Light") == "Buffs");
            Check("typing: Quickness stays a buff", CatOf("Quickness") == "Buffs");
            // Effect-first (25 Sep): the wiki's effect slots over landing-word guesses.
            Check("typing: effects decide — Vampiric Curse and Stinging Swarm are DoTs, Celestial Healing a HoT",
                CatOf("Vampiric Curse") == "DoTs" && CatOf("Stinging Swarm") == "DoTs" && CatOf("Celestial Healing") == "HoTs");
            Check("typing: effects decide — Calm is a debuff, Voice of Shadows your own buff, Tashani a debuff, Auspice a DoT",
                CatOf("Calm") == "Debuffs" && CatOf("Voice of Shadows") == "Buffs" && CatOf("Tashani") == "Debuffs" && CatOf("Auspice") == "DoTs");
            Check("typing: heals / travel keep the old rules (Minor Healing HoT, a detrimental port a debuff)",
                CatOf("Minor Healing") == "HoTs" && CatOf("Trakanon's Touch") == "Debuffs");
            var vc = new[] { new Models.TriggerDefinition { Id = "lib-vampiric-curse", Name = "Vampiric Curse", Category = "Debuffs" } };
            lib2.HealLibraryTriggers(vc);
            Check("typing: an old library trigger heals to its effect's type on load (Vampiric Curse Debuffs → DoTs)",
                vc[0].Category == "DoTs");
            Check("typing: the library search reaches effects and the words players type",
                lib2.Search("dot").Any(x => x.Name == "Vampiric Curse") && lib2.Search("mez").Any(x => x.Name == "Kelin's Lucid Lullaby")
                && lib2.Search("nuke").Any(x => x.Name == "Flame Shock") && !lib2.Search("nuke").Any(x => x.Name == "Vampiric Curse"));
            var retype = new[]
            {
                new Models.TriggerDefinition { Id = "lib-envenomed-bolt", Name = "Envenomed Bolt", Category = "Debuffs" },
                new Models.TriggerDefinition { Id = "lib-quickness", Name = "Quickness", Category = "Buffs" },
                // Custom type AND custom pattern — the heal must not touch either.
                new Models.TriggerDefinition { Id = "lib-snails-healing", Name = "Snails Healing", Category = "MyOwn", StartPattern = "my custom pattern" },
                new Models.TriggerDefinition { Id = "custom-1", Name = "Envenomed Bolt", Category = "Debuffs" },
            };
            Check("typing: heal fixes lib defaults, spares custom types and ids",
                lib2.HealLibraryTriggers(retype) == 2 // bolt retyped; both empty patterns filled
                && retype[0].Category == "DoTs" && retype[1].Category == "Buffs"
                && retype[2].Category == "MyOwn" && retype[2].StartPattern == "my custom pattern"
                && retype[3].Category == "Debuffs" && retype[3].StartPattern.Length == 0
                && retype[1].StartPattern == System.Text.RegularExpressions.Regex
                    .Escape("You feel much faster."));
            // Pre-2.12 files carry the regen line as HoTs — heals back to Buffs.
            var regen = new Models.TriggerDefinition
                { Id = "lib-chloroplast", Name = "Chloroplast", Category = "HoTs" };
            lib2.HealLibraryTriggers(new[] { regen });
            Check("typing: an existing HoT-typed Chloroplast heals to Buffs",
                regen.Category == "Buffs");

            // HoT bars carry extra height (the stay-alive bars).
            Check("bars: HoT bars render 1.4x tall, others 1x",
                ViewModels.TimerBarViewModel.CreateTimer("h1", "Slugs Healing", "HoTs", 24,
                    DateTime.Now.AddSeconds(24), Brushes.Green, 0, false, null, null)
                    .HeightScale == 1.4
                && ViewModels.TimerBarViewModel.CreateTimer("h2", "Quickness", "Buffs", 660,
                    DateTime.Now.AddSeconds(660), Brushes.Blue, 0, false, null, null)
                    .HeightScale == 1.0);

            // Junk landing text ("You .") falls back to the begin-cast line —
            // and already-added broken triggers heal to it on load.
            Check("junk: detector accepts real text, rejects the stubs",
                SpellLibrary.JunkMessage("You .") && SpellLibrary.JunkMessage("")
                && SpellLibrary.JunkMessage("Someone .")
                && !SpellLibrary.JunkMessage("You feel much faster."));
            Check("junk: a junk-text spell's bar anchors on its begin-cast line, rank-tolerant",
                lib2.FindByName("Befriend Animal") is { } befriend
                && SpellLibrary.BarTrigger(befriend, spokenWarning: true) is { } befriendBar
                && new System.Text.RegularExpressions.Regex(befriendBar.StartPattern)
                    .IsMatch("You begin casting Befriend Animal.")
                && new System.Text.RegularExpressions.Regex(befriendBar.StartPattern)
                    .IsMatch("You begin casting Befriend Animal V.")
                && new System.Text.RegularExpressions.Regex(befriendBar.StartPattern)
                    .IsMatch("You begin casting Befriend Animal VIII.")
                && new System.Text.RegularExpressions.Regex(befriendBar.StartPattern)
                    .IsMatch("You begin casting Befriend Animal X.")
                && !new System.Text.RegularExpressions.Regex(befriendBar.StartPattern)
                    .IsMatch("You begin casting Befriend Animal Ward."));
            // Ghost entries are gone; the scrape's own Tortoises entry carries
            // the family template and types as a HoT via its landing text.
            Check("junk: Sloths Healing is a ghost (not on the wiki) and is removed",
                lib2.FindByName("Sloths Healing") is null
                && lib2.FindByName("Tortoises Healing") is { } tortoise
                && SpellLibrary.TriggerCategory(tortoise) == "HoTs");
            Check("junk: rank pooling covers base through X",
                SpellDurations.BaseKey("Sloths Healing") == SpellDurations.BaseKey("Sloths Healing X")
                && SpellDurations.BaseKey("Sloths Healing VIII") == "sloths healing"
                && SpellDurations.BaseKey("Sloths Healing IX") == "sloths healing");
            var broken = new Models.TriggerDefinition
            {
                Id = "lib-befriend-animal", Name = "Befriend Animal", Category = "MyOwn",
                StartPattern = System.Text.RegularExpressions.Regex.Escape("You ."),
            };
            var legacy = new Models.TriggerDefinition
            {
                Id = "lib-slugs-healing", Name = "Slugs Healing", Category = "HoTs",
                StartPattern = @"^You begin casting Slugs\ Healing\.", // 2.9.0 fallback, no rank
            };
            Check("junk: heal repairs broken patterns; corrected spells graduate to landing text",
                lib2.HealLibraryTriggers(new[] { broken, legacy }) == 2
                && broken.StartRegex is not null
                && new System.Text.RegularExpressions.Regex(broken.StartPattern)
                    .IsMatch("You begin casting Befriend Animal II.")
                && new System.Text.RegularExpressions.Regex(legacy.StartPattern)
                    .IsMatch("You being to feel healed by the slug.")
                && legacy.EndPattern is not null
                && new System.Text.RegularExpressions.Regex(legacy.EndPattern)
                    .IsMatch("You feel the slug spirit depart."));

            // Observed message corrections (real-log sentences, game typo intact).
            Check("corrections: Slugs Healing carries its observed landing + fade",
                lib2.FindByName("Slugs Healing") is
                {
                    CastOnYou: "You being to feel healed by the slug.",
                    WearsOff: "You feel the slug spirit depart.",
                }
                && SpellLibrary.BarTrigger(lib2.FindByName("Slugs Healing")!, spokenWarning: false) is
                { } slugsBar
                && slugsBar.StartPattern == System.Text.RegularExpressions.Regex
                    .Escape("You being to feel healed by the slug.")
                && SpellLibrary.TriggerCategory(lib2.FindByName("Slugs Healing")!) == "HoTs");

            Check("anchor: library flags the shared haste landing as ambiguous",
                lib2.IsSharedLanding(Esc("You feel much faster."))
                && !lib2.IsSharedLanding("not a spell line at all"));

            // A zero-duration detrimental is an instant nuke/lifetap — its
            // landing must never open an enemy-DoT bar (Siphon Life field
            // report); real duration-carrying debuffs still arm.
            Check("dots: a zero-duration detrimental (Siphon Life) never arms a bar",
                lib2.OtherLanding("Siphon Life") is null
                && lib2.OtherLanding("Togor's Insects") is { Detrimental: true });

            // Condition badges: fear/charm/mez landings derive from the
            // library's wear-off families; STUN rides the game's own state
            // pair alone — "You are stunned!" / "You are no longer stunned."
            // (measured 488/488 across the real logs; spell-flavor landings
            // like "sudden force" also fire for stunless knockbacks).
            var cw = new ConditionWatcher(lib2);
            Check("conditions: stun is the state pair alone, the rest derive from the library",
                cw.LandingCount(ConditionWatcher.Stunned) == 1
                && cw.LandingCount(ConditionWatcher.Feared) > 3
                && cw.LandingCount(ConditionWatcher.Charmed) > 0
                && cw.LandingCount(ConditionWatcher.Mezzed) > 3);
            cw.ProcessLine($"[{AT(0)}] You are struck by a sudden force.");
            Check("conditions: a stunless knockback raises NOTHING",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 5)).Count == 0);
            cw.ProcessLine($"[{AT(3)}] You are stunned!"); // the state line, spell and melee alike
            Check("conditions: the stun state line raises the badge",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 4)) is [{ Kind: ConditionWatcher.Stunned }]);
            cw.ProcessLine($"[{AT(6)}] You are no longer stunned.");
            Check("conditions: the wear-off line clears it",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 7)).Count == 0);
            cw.ProcessLine($"[{AT(10)}] You freeze in terror.");
            cw.ProcessLine($"[{AT(11)}] You have been charmed.");
            Check("conditions: fear + charm stack, oldest first",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 12)) is
                    [{ Kind: ConditionWatcher.Feared }, { Kind: ConditionWatcher.Charmed }]);
            cw.ProcessLine($"[{AT(20)}] You have been slain by a gnoll reaver!");
            Check("conditions: death clears everything",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 21)).Count == 0);
            cw.ProcessLine($"[{AT(30)}] Your muscles scream with strength.");
            cw.ProcessLine($"[{AT(30)}] Your body screams with the power of an Avatar.");
            Check("conditions: scream-flavored BUFFS never raise a badge",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 31)).Count == 0);
            cw.ProcessLine($"[{AT(40)}] You are stunned!");
            Check("conditions: hygiene cap culls an eaten stun wear-off (30s)",
                cw.Active(new DateTime(2026, 8, 10, 23, 0, 45)).Count == 1
                && cw.Active(new DateTime(2026, 8, 10, 23, 1, 20)).Count == 0);
            cw.ProcessLine($"[{AT(90)}] Your mind fills with fear.");
            cw.ProcessLine($"[{AT(95)}] You have entered The Plane of Hate.");
            Check("conditions: zoning clears the badges",
                cw.Active(new DateTime(2026, 8, 10, 23, 1, 36)).Count == 0);
            // Moment flashes: YOUR broken/bounced casts only — pets' and
            // groupmates' spells ("Xarer's", "Gonartik's") stay silent.
            cw.ProcessLine($"[{AT(100)}] Your Siphon Life spell is interrupted.");
            cw.ProcessLine($"[{AT(100)}] Xarer's Frost Dagger spell is interrupted.");
            Check("conditions: YOUR interrupt flashes, then expires (a pet's never shows)",
                cw.Active(new DateTime(2026, 8, 10, 23, 1, 41)) is
                    [{ Kind: ConditionWatcher.Interrupted, Detail: "Siphon Life" }]
                && cw.Active(new DateTime(2026, 8, 10, 23, 1, 45)).Count == 0);
            cw.ProcessLine($"[{AT(110)}] A froglok shin knight resisted your Ignite!");
            cw.ProcessLine($"[{AT(110)}] A ghoul assassin resisted Gonartik's Drowsy!");
            Check("conditions: YOUR resist flashes with the spell (a pet's never)",
                cw.Active(new DateTime(2026, 8, 10, 23, 1, 51)) is
                    [{ Kind: ConditionWatcher.Resisted, Detail: "Ignite" }]
                && cw.Active(new DateTime(2026, 8, 10, 23, 1, 55)).Count == 0);
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
            // Ranked cast of a corrected spell: "Slugs Healing V" resolves to
            // the library's base entry, so the observed landing/fade pair mints.
            dur.ProcessLine($"[{T(9000)}] You begin casting Slugs Healing V.");
            dur.ProcessLine($"[{T(9006)}] You being to feel healed by the slug.");
            dur.ProcessLine($"[{T(9047)}] You feel the slug spirit depart.");
            Check("durations: ranked cast of a corrected spell mints a sample",
                dur.LearnedMaxSeconds("Slugs Healing") is double slugSec && Math.Abs(slugSec - 41) < 0.01);
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
            // Owner's Puma (22 Sep): Spirit of the Puma on himself, then on the
            // pet 6 s later — the second cast of the same spell used to read as
            // a re-cast and discard his own cycle, so Puma never learned.
            dur.ProcessLine($"[{T(13000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(13002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(13006)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(13008)}] Jobtik growls with the spirit of the puma.");
            dur.ProcessLine($"[{T(13212)}] The spirit of the puma departs.");
            Check("durations: the pet cast right after your own leaves your cycle alone (210 s learned)",
                dur.LearnedMaxSeconds("Spirit of the Puma") is double pumaSec && Math.Abs(pumaSec - 210) < 0.01);
            dur.ProcessLine($"[{T(13218)}] Your pet's Spirit of the Puma spell has worn off.");
            Check("durations: the pet's own wear-off mints a sample into the same pool",
                dur.SampleCount("Spirit of the Puma") == 2);
            dur.ProcessLine($"[{T(14000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(14002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(14100)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(14102)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(14302)}] The spirit of the puma departs.");
            Check("durations: a re-cast that LANDS on you still refreshes — one 200 s sample, never a 300 s one",
                dur.SampleCount("Spirit of the Puma") == 3
                && dur.ObservedMaxSeconds("Spirit of the Puma") is double pumaMax && Math.Abs(pumaMax - 210) < 0.01);
            dur.ProcessLine($"[{T(15000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(15002)}] Jobtik growls with the spirit of the puma.");
            dur.ProcessLine($"[{T(15100)}] Jobtik growls with the spirit of the puma."); // a groupmate re-buffed the pet
            dur.ProcessLine($"[{T(15400)}] Your pet's Spirit of the Puma spell has worn off.");
            Check("durations: another caster refreshing the pet contaminates the pet cycle",
                dur.SampleCount("Spirit of the Puma") == 3);
            dur.ProcessLine($"[{T(16000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(16002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(16010)}] You begin casting Spirit of the Puma X."); // interrupted — nothing lands
            dur.ProcessLine($"[{T(16212)}] The spirit of the puma departs.");
            Check("durations: an interrupted re-cast leaves your cycle alone too",
                dur.SampleCount("Spirit of the Puma") == 4);

            // The estimate CHANGE (22 Sep): announced once, remembered, worn once
            // by the next bar — and never for whole-second jitter.
            var pumaChange = dur.LastChange("Spirit of the Puma");
            Check("durations: the first Puma sample is remembered as a change from nothing",
                pumaChange is { From: null, To: 210 } && dur.ConsumeFresh("Spirit of the Puma") && !dur.ConsumeFresh("Spirit of the Puma"));
            int moves = 0;
            dur.EstimateChanged += (_, _, _, _) => moves++;
            dur.ProcessLine($"[{T(17000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(17002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(17213)}] The spirit of the puma departs.");   // 211 s
            dur.ProcessLine($"[{T(18000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(18002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(18211)}] The spirit of the puma departs.");   // 209 s
            Check("durations: ±1–2 s of log jitter moves nothing (tolerance max(3 s, 2 %))",
                moves == 0 && dur.LastChange("Spirit of the Puma") is { To: 210 } && !dur.ConsumeFresh("Spirit of the Puma")
                && Math.Abs(SpellDurations.ChangeTolerance(210) - 4.2) < 0.01 && SpellDurations.ChangeTolerance(60) == 3);
            dur.ProcessLine($"[{T(19000)}] You begin casting Spirit of the Puma X.");
            dur.ProcessLine($"[{T(19002)}] You begin to snarl as your features become feline.");
            dur.ProcessLine($"[{T(19262)}] The spirit of the puma departs.");   // 260 s — the rank X duration
            Check("durations: a real jump announces once, remembers from→to, and the next bar is fresh once",
                moves == 1 && dur.LastChange("Spirit of the Puma") is { From: 211, To: 260 }
                && dur.ConsumeFresh("Spirit of the Puma") && !dur.ConsumeFresh("Spirit of the Puma")
                && dur.SamplesFor("Spirit of the Puma").Count == 7);
            Check("durations: the bar carries the fresh flag",
                ViewModels.TimerBarViewModel.CreateTimer("k", "n", "Buff", 10, DateTime.Now.AddSeconds(10), System.Windows.Media.Brushes.SteelBlue, 0, false, null, null, learnedFresh: true).IsFresh
                && !ViewModels.TimerBarViewModel.CreateTimer("k", "n", "Buff", 10, DateTime.Now.AddSeconds(10), System.Windows.Media.Brushes.SteelBlue, 0, false, null, null).IsFresh);

            // The library floor (owner ruling, Chloroplast): the regen family
            // shares its landing/wear-off sentences, so cycles can close SHORT
            // — a learned figure below the library's stated duration is
            // pollution and stays silent. The evidence remains visible.
            dur.ProcessLine($"[{T(20000)}] You begin casting Chloroplast.");
            dur.ProcessLine($"[{T(20005)}] You begin to regenerate.");
            dur.ProcessLine($"[{T(20261)}] You have stopped regenerating.");
            Check("durations: a sample below the library's duration is ignored",
                dur.LearnedMaxSeconds("Chloroplast") is null
                && dur.ObservedMaxSeconds("Chloroplast") is double chloroRaw
                && Math.Abs(chloroRaw - 256) < 0.01
                && dur.LibraryFloorSeconds("Chloroplast") == 960);
            dur.ProcessLine($"[{T(30000)}] You begin casting Chloroplast.");
            dur.ProcessLine($"[{T(30005)}] You begin to regenerate.");
            dur.ProcessLine($"[{T(31085)}] You have stopped regenerating.");
            Check("durations: a genuine extension past the library still teaches",
                dur.LearnedMaxSeconds("Chloroplast") is double chloroSec
                && Math.Abs(chloroSec - 1080) < 0.01);
            dur.Forget("Chloroplast");
            Check("durations: Forget wipes one spell and only that spell",
                dur.ObservedMaxSeconds("Chloroplast") is null
                && dur.SampleCount("Spirit of Wolf") == 2);

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

            // Alert migration: pre-2.11 configs carried ONE speak/sound payload
            // gated by SpeakEnabled + AtSeconds/OnExpire — they map onto the
            // two-notice model (warn before fade / notify at fade).
            var vt = new Models.AppConfig();
            vt.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "v1", Name = "Voiced", StartPattern = @"^A voice\.", DurationSeconds = 30,
                Alert = new Models.AlertConfig { Speak = "hello", AtSeconds = 5 },
            });
            vt.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "v2", Name = "Muted", StartPattern = @"^A silence\.", DurationSeconds = 30,
                Alert = new Models.AlertConfig { Speak = "hello", AtSeconds = 5, SpeakEnabled = false },
            });
            vt.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "v3", Name = "Chimed", StartPattern = @"^A chime\.", DurationSeconds = 30,
                Alert = new Models.AlertConfig
                    { Sound = @"C:\Windows\Media\chimes.wav", AtSeconds = 5, SpeakEnabled = false },
            });
            foreach (var t in vt.Triggers) ConfigService.CompileOne(t);
            var vtEng = new TriggerEngine(vt, new AlertService());
            vtEng.ProcessLine($"[{AT(0)}] A voice.");
            vtEng.ProcessLine($"[{AT(0)}] A silence.");
            vtEng.ProcessLine($"[{AT(0)}] A chime.");
            Check("alerts: legacy timed speak migrates to the pre-fade notice",
                vtEng.Bars.First(b => b.Name == "Voiced").AlertSpeak == "hello"
                && vtEng.Bars.First(b => b.Name == "Voiced").AlertAtSeconds == 5);
            Check("alerts: legacy voice-off keeps the phrase but disables the notice",
                vtEng.Bars.First(b => b.Name == "Muted").AlertSpeak is null
                && vtEng.Bars.First(b => b.Name == "Muted").AlertAtSeconds == 0
                && vt.Triggers[1].Alert!.Speak == "hello");
            Check("alerts: legacy sound-only migrates to a sound-mode notice",
                vt.Triggers[2].Alert is { WarnEnabled: true, WarnMode: Models.AlertConfig.ModeSound }
                && vtEng.Bars.First(b => b.Name == "Chimed").AlertSound == @"C:\Windows\Media\chimes.wav"
                && vtEng.Bars.First(b => b.Name == "Chimed").AlertSpeak is null);

            // The two notices carry independent payloads to the bar.
            var two = new Models.AppConfig();
            two.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "t2", Name = "Twofold", StartPattern = @"^Twofold lands\.", DurationSeconds = 30,
                Alert = new Models.AlertConfig
                {
                    WarnEnabled = true, AtSeconds = 10,
                    WarnMode = Models.AlertConfig.ModeSpeak, Speak = "twofold ending",
                    FadedEnabled = true,
                    FadedMode = Models.AlertConfig.ModeSpeak, FadedSpeak = "twofold gone",
                },
            });
            foreach (var t in two.Triggers) ConfigService.CompileOne(t);
            var twoEng = new TriggerEngine(two, new AlertService());
            twoEng.ProcessLine($"[{AT(0)}] Twofold lands.");
            var twoBar = twoEng.Bars.First(b => b.Name == "Twofold");
            Check("alerts: warn and faded notices carry separate payloads",
                twoBar.AlertSpeak == "twofold ending" && twoBar.AlertAtSeconds == 10
                && twoBar.AlertOnExpire && twoBar.AlertFadedSpeak == "twofold gone");
            Check("voice: library adds arrive with a default pre-fade phrase at 15s",
                SpellLibrary.BarTrigger(lib2.FindByName("Quickness")!, spokenWarning: true) is
                    { Alert: { Speak: "Quickness is about to end", WarnEnabled: true, AtSeconds: 15,
                               WarnMode: Models.AlertConfig.ModeSpeak, FadedEnabled: false } });

            // Merged-log copies: timestamped name keeps base + extension.
            Check("merge copies: timestamped copy name",
                ConfigService.MergedCopyName("eqlog_Thorrak_paineel.txt",
                    new DateTime(2026, 8, 12, 20, 30, 15))
                    == "eqlog_Thorrak_paineel-20260812-203015.txt");

            // Overrun state: a bar with an end pattern grays out at 0 and
            // counts UP until the fade line — "still there, still learning".
            var ov = ViewModels.TimerBarViewModel.CreateTimer("k", "n", "Buffs", 10,
                new DateTime(2026, 8, 11, 12, 0, 10), Brushes.Blue, 0, false, null, null,
                waitsForFade: true);
            ov.Refresh(new DateTime(2026, 8, 11, 12, 0, 11), 5);
            Check("overrun: expired-but-waiting bar reports expired once", ov.IsExpired);
            ov.EnterOverrun();
            ov.Refresh(new DateTime(2026, 8, 11, 12, 0, 24), 5);
            Check("overrun: gray bar counts up and is no longer 'expired'",
                ov.IsOverrun && !ov.IsExpired && ov.RemainingText == "+14s"
                && Math.Abs(ov.OverrunSeconds - 14) < 0.01 && !ov.IsWarning);
            ov.Restart(10, new DateTime(2026, 8, 11, 12, 0, 40));
            Check("overrun: a retrigger returns the bar to a live countdown", !ov.IsOverrun);
            var nf = ViewModels.TimerBarViewModel.CreateTimer("k2", "n2", "Buffs", 10,
                new DateTime(2026, 8, 11, 12, 0, 10), Brushes.Blue, 0, false, null, null);
            nf.Refresh(new DateTime(2026, 8, 11, 12, 0, 11), 5);
            Check("overrun: bars without an end pattern still just expire",
                nf.IsExpired && !nf.WaitsForFade);

            // Learning mode: the gray bar SAYS it's learning, and the cull cap
            // scales with the estimate (a short library value must not vanish
            // the bar while the buff is demonstrably still up).
            var lv = ViewModels.TimerBarViewModel.CreateTimer("k3", "Learner", "Buffs", 600,
                new DateTime(2026, 8, 11, 12, 0, 0), Brushes.Blue, 0, false, null, null,
                waitsForFade: true, learnsDuration: true);
            lv.EnterOverrun();
            lv.Refresh(new DateTime(2026, 8, 11, 12, 1, 30), 5);
            Check("overrun: learning bar labels the count-up",
                lv.RemainingText == "learning +90s");
            Check("overrun: cull cap scales to the bar's own duration",
                Math.Abs(TriggerEngine.OverrunCapFor(lv) - 600) < 0.01
                && Math.Abs(TriggerEngine.OverrunCapFor(ov) - 60) < 0.01);

            // Own death strips buffs (that's also what eats fade lines) — but
            // cooldown bars keep ticking through it.
            var dth = new Models.AppConfig();
            dth.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "b1", Name = "Skin", Category = "Buffs", DurationSeconds = 600,
                StartPattern = @"^Your skin hardens\.",
            });
            dth.Triggers.Add(new Models.TriggerDefinition
            {
                Id = "c1", Name = "Harm Touch", Category = "Cooldowns", DurationSeconds = 1200,
                StartPattern = @"^You begin casting Harm Touch\.", CastAnchored = false,
            });
            foreach (var t in dth.Triggers) ConfigService.CompileOne(t);
            var dthEng = new TriggerEngine(dth, new AlertService());
            dthEng.ProcessLine($"[{AT(0)}] Your skin hardens.");
            dthEng.ProcessLine($"[{AT(1)}] You begin casting Harm Touch.");
            dthEng.ProcessLine($"[{AT(20)}] You have been slain by a gnoll reaver!");
            Check("death: buff bars strip, cooldown bars survive",
                dthEng.Bars.Count == 1 && dthEng.Bars[0].Name == "Harm Touch");

            // A legacy speak phrase with no timing meant "say it when the bar
            // runs out" — it migrates to the faded notice, phrase intact.
            var mute = new Models.TriggerDefinition
            {
                Id = "qk", Name = "Quickness", StartPattern = "x",
                Alert = new Models.AlertConfig { Speak = "Quickness faded" },
            };
            ConfigService.CompileOne(mute);
            Check("alert: legacy speak with no timing becomes the faded notice",
                mute.Alert is { FadedEnabled: true, FadedSpeak: "Quickness faded", WarnEnabled: false });
            var timed = new Models.TriggerDefinition
            {
                Id = "qk2", Name = "Quickness", StartPattern = "x",
                Alert = new Models.AlertConfig { Speak = "fading", AtSeconds = 20 },
            };
            ConfigService.CompileOne(timed);
            Check("alert: legacy timed speak stays a pre-fade notice only",
                timed.Alert is { WarnEnabled: true, AtSeconds: 20, FadedEnabled: false });
            ConfigService.CompileOne(timed); // normalize must be idempotent
            ConfigService.CompileOne(timed);
            Check("alert: normalization is idempotent across recompiles",
                timed.Alert is { WarnEnabled: true, AtSeconds: 20, FadedEnabled: false, Speak: "fading" });

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

            // ---- session stats (XP/AA/motes per hour, Companion design) -------
            {
                var ss = new SessionStats { SelfName = "Thorrak" };
                var s0 = new DateTime(2026, 8, 14, 20, 0, 0);
                string S(int sec) => s0.AddSeconds(sec)
                    .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
                DateTime N(int sec) => s0.AddSeconds(sec);

                Check("stats: mote tier extraction (incl. the tierless member)",
                    SessionStats.MoteTier("Mote of Minor Potential") == "Minor"
                    && SessionStats.MoteTier("Mote of Potential") == "Potential"
                    && SessionStats.MoteTier("Mote of Infinite Potential") == "Infinite");
                Check("stats: zone name folds place, tier + instance away",
                    SessionStats.ZoneKey("Befallen 3 (Fused)") == "befallen"
                    && SessionStats.ZoneKey("Nagafen's Lair - Solo 4 (Refined)") == "nagafen's lair"
                    && SessionStats.ZoneKey("The Ruins of Old Guk") == "ruins of old guk");

                ss.ProcessLine($"[{S(0)}] Welcome to EverQuest Legends!");
                ss.ProcessLine($"[{S(5)}] You have entered Befallen 3 (Fused).");
                // 30 min of steady killing: 1.5%/kill, one every 100s = 18 kills = 27%.
                for (int i = 0; i < 18; i++)
                    ss.ProcessLine($"[{S(60 + i * 100)}] You gain experience! (1.500%)");
                ss.ProcessLine($"[{S(200)}] You have gained an ability point!  You now have 1 ability point.");
                ss.ProcessLine($"[{S(900)}] You have gained 2 ability point(s)!  You now have 3 ability point(s).");
                ss.ProcessLine($"[{S(300)}] You looted a Mote of Minor Potential from a zol ghoul knight's corpse and stored it in your currency");
                ss.ProcessLine($"[{S(600)}] You looted a Mote of Minor Potential from a wan ghoul knight's corpse and stored it in your currency");
                ss.ProcessLine($"[{S(650)}] You looted a Mote of Lesser Potential from a ghoul cavalier's corpse and stored it in your currency");
                ss.ProcessLine($"[{S(700)}] this is a retro experience"); // chat must not count
                ss.ProcessLine($"[{S(710)}] You gain experience?");       // near-miss must not count

                var v = ss.Snapshot(N(1800), SessionStats.Slice.ZoneSession, exactTier: true,
                    SessionStats.Basis.Elapsed);
                // 27% over 30 min elapsed = 0.54 lvl/hr.
                Check("stats: lvl/hr = Σ stated % / 100 per elapsed hour",
                    v.Rows.FirstOrDefault(r => r.Label == "XP") is { Value: "0.54", Unit: "lvl/hr" });
                // 2 gain lines (1 + 2 points) over 1795s elapsed ≈ 4.01 AA/hr, 6.02 pts/hr.
                Check("stats: AA counts gain lines, points ride as the detail",
                    v.Rows.FirstOrDefault(r => r.Label == "AA") is { Value: "4.01" } aa
                    && aa.Detail == "6.02 pts/hr");
                Check("stats: mote rows per tier, most drops first, N× counts",
                    v.Rows.Where(r => r.Unit == "drops/hr").Select(r => (r.Label, r.Detail)).SequenceEqual(
                        new[] { ("MINOR", "2×"), ("LESSER", "1×") }));
                // The zone was entered 5s into the session, so 1795s ≈ 29m.
                Check("stats: caption carries zone, session and tier scoping",
                    v.Caption == "Befallen 3 (Fused) this session, this tier only"
                    && v.Span == "over 29m elapsed");
                Check("stats: no ding yet -> the ETA refuses, never guesses",
                    v.Rows.First(r => r.Label == "NEXT LEVEL").Value == "–");

                // A ding resets the bar; later percentages feed the ETA.
                ss.ProcessLine($"[{S(1810)}] You have gained a level! Welcome to level 35!");
                for (int i = 0; i < 6; i++)
                    ss.ProcessLine($"[{S(1900 + i * 100)}] You gain experience! (1.500%)");
                var v2 = ss.Snapshot(N(2700), SessionStats.Slice.ZoneSession, true, SessionStats.Basis.Elapsed);
                var eta = v2.Rows.First(r => r.Label == "NEXT LEVEL");
                // 9% into the bar; 36% over 45m elapsed = 0.48 lvl/hr -> 91%/0.48 ≈ 1h53m.
                Check("stats: ETA = bar remainder over the elapsed pace, no target claim",
                    eta.Value == "~1h 53m" && eta.Detail == "");
                Check("stats: the header level follows the ding",
                    v2.LevelText == "lvl 35");

                // /who states the level between dings — and must be YOUR row.
                ss.ProcessLine($"[{S(2800)}] [47 WAR/SHM/NEC] Humlesnurr (Gnome) <Petrichor> ZONE: Befallen (befallen)  ");
                ss.ProcessLine($"[{S(2810)}] [36 SHD/ROG/SHM] Thorrak (Ogre) <The Chosen Alliance> ZONE: Befallen (befallen)  ");
                Check("stats: own /who row updates the level, a stranger's never",
                    ss.Snapshot(N(2820), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed)
                        .LevelText == "lvl 36 /who");
                // A /who that CONTRADICTS the last ding = a loadout swap the
                // log never announces — the ETA refuses instead of asserting
                // another loadout's bar.
                Check("stats: a contradicting /who blocks the ETA (loadout swap)",
                    ss.Snapshot(N(2820), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed)
                        .Rows.First(r => r.Label == "NEXT LEVEL") is { Value: "–" } swapEta
                    && swapEta.Tip.Contains("loadout swap"));

                // Percent-less exp (the cap) is UNKNOWN, never zero.
                var capSs = new SessionStats();
                capSs.ProcessLine($"[{S(0)}] You have entered Befallen 3 (Fused).");
                for (int i = 0; i < 5; i++)
                    capSs.ProcessLine($"[{S(60 + i * 100)}] You gain experience!");
                var capV = capSs.Snapshot(N(600), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed);
                Check("stats: all-unstated exp -> no XP rate, not 0.00",
                    capV.Rows.First(r => r.Label == "XP").Value == "–");

                // Tier scoping: only the exact spelling counts under THIS TIER —
                // and the admitted time is the denominator too.
                var tz = new SessionStats();
                tz.ProcessLine($"[{S(0)}] You have entered Befallen 2 (Adaptive).");
                for (int i = 0; i < 6; i++)
                    tz.ProcessLine($"[{S(10 + i * 100)}] You gain experience! (1.000%)");
                tz.ProcessLine($"[{S(600)}] You have entered Befallen 3 (Fused).");
                for (int i = 0; i < 6; i++)
                    tz.ProcessLine($"[{S(610 + i * 100)}] You gain experience! (2.000%)");
                var exact = tz.Snapshot(N(1200), SessionStats.Slice.Zone, true, SessionStats.Basis.Elapsed);
                var folded = tz.Snapshot(N(1200), SessionStats.Slice.Zone, false, SessionStats.Basis.Elapsed);
                Check("stats: exact tier narrows both the events and the clock",
                    exact.Rows.First(r => r.Label == "XP").Value == "0.72"   // 12% / 10min
                    && exact.Span == "over 10m elapsed"
                    && folded.Rows.First(r => r.Label == "XP").Value == "0.54" // 18% / 20min
                    && folded.Span == "over 20m elapsed");

                // Offline: a ≥60s silence ending in a Welcome is absence, and a
                // second Welcome restarts the session slice.
                var off = new SessionStats();
                off.ProcessLine($"[{S(0)}] Welcome to EverQuest Legends!");
                for (int i = 0; i < 6; i++)
                    off.ProcessLine($"[{S(10 + i * 100)}] You gain experience! (1.000%)");
                off.ProcessLine($"[{S(4000)}] Welcome to EverQuest Legends!");
                for (int i = 0; i < 6; i++)
                    off.ProcessLine($"[{S(4010 + i * 100)}] You gain experience! (1.000%)");
                var offAll = off.Snapshot(N(4610), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed);
                var offSes = off.Snapshot(N(4610), SessionStats.Slice.Session, true, SessionStats.Basis.Elapsed);
                // All: the 4610s span minus the 3490s logout = 1120s ≈ 18m.
                Check("stats: the logout gap leaves the elapsed denominator",
                    offAll.Span == "over 18m elapsed"
                    && offSes.Caption == "this session" && offSes.Span == "over 10m elapsed");

                // Active basis: a mid-camp 10-minute silence is idle — it leaves
                // ACTIVE but stays in ELAPSED (medding is time you spent).
                var idle = new SessionStats();
                idle.ProcessLine($"[{S(0)}] You have entered Befallen 3 (Fused).");
                for (int i = 0; i < 6; i++)
                    idle.ProcessLine($"[{S(i * 60)}] You gain experience! (1.000%)");
                for (int i = 0; i < 6; i++)
                    idle.ProcessLine($"[{S(900 + i * 60)}] You gain experience! (1.000%)");
                var idleEl = idle.Snapshot(N(1260), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed);
                var idleAc = idle.Snapshot(N(1260), SessionStats.Slice.All, true, SessionStats.Basis.Active);
                Check("stats: idle leaves ACTIVE but stays in ELAPSED",
                    idleEl.Span == "over 21m elapsed" && idleAc.Span == "over 11m active");

                // Under 5 minutes nothing is stated as a rate — but counts stay.
                var young = new SessionStats();
                young.ProcessLine($"[{S(0)}] You gain experience! (1.000%)");
                young.ProcessLine($"[{S(30)}] You looted a Mote of Minor Potential from a ghoul's corpse and stored it in your currency");
                var youngV = young.Snapshot(N(90), SessionStats.Slice.All, true, SessionStats.Basis.Elapsed);
                Check("stats: under 5 minutes rates refuse, the mote count stays",
                    !youngV.Measurable
                    && youngV.Rows.First(r => r.Label == "XP").Value == "–"
                    && youngV.Rows.First(r => r.Unit == "drops/hr") is { Value: "–", Detail: "1×" });

                // Reset + refeed (the catch-up path) lands on the same numbers.
                var again = new SessionStats { SelfName = "Thorrak" };
                foreach (var line in new[]
                {
                    $"[{S(0)}] Welcome to EverQuest Legends!",
                    $"[{S(5)}] You have entered Befallen 3 (Fused).",
                    $"[{S(60)}] You gain experience! (1.500%)",
                })
                    again.ProcessLine(line);
                again.Reset();
                Check("stats: reset wipes the record", !again.HasData);
            }

            // ---- inventory dump parser (Companion's measured grammar) ---------
            {
                // A verbatim slice of the real fixture dump (tab-separated;
                // the KeyRing header really ends in a bare tab).
                string dumpText = string.Join("\r\n", new[]
                {
                    "Location\tName\tID\tCount\tSlots",
                    "Ear\tDrop of Crystallized Flame +7\t177839\t1\t10",
                    "Ear-Slot7\tEmpty\t0\t0\t0",
                    "Ear\tEarring of Disease Reflection +4\t10302\t1\t10",
                    "Wrist\tValorium Bracers +2\t4854\t1\t10",
                    "Wrist\tLustrous Russet Bracer +1\t4834\t1\t10",
                    "Primary\tThelvorn, Blade of Light +5\t27709\t1\t10",
                    "Primary-Slot10\tThelvorn, Blade of Light (Exaltation)\t27709\t1\t10",
                    "Ammo\tEmpty\t0\t0\t0",
                    "General 1\tSpacious Rucksack\t177751\t1\t24",
                    "General 1-Slot1\tTiny Dagger\t13080\t86\t10",
                    "General 1-Slot5\tBandages*\t21779\t20\t10",
                    "General 1-Slot9\tKelin`s Seven Stringed Lute +1\t11573\t1\t10",
                    "General 1-Slot9-Slot7\tKelin`s Seven Stringed Lute (Exaltation)\t11573\t1\t10",
                    "General 1-Slot24\tEmpty\t0\t0\t0",
                    "Bank1\tEmpty\t0\t0\t0",
                    "SharedBank1\tEmpty\t0\t0\t0",
                    "Personal-Depot1\tGriffenne Blood\t22526\t2\t10",
                    // The Dragon's Hoard rides the primary table, spaced like
                    // General and nestable (observed: Thorrak 2026-08-18).
                    "Hoard 1\tFine Steel Scimitar\t5353\t1\t10",
                    "Hoard 1-Slot2\tEmpty\t0\t0\t0",
                    "Held\tEmpty\t0\t0\t0",
                    "",
                    "KeyRing\tName\tID\t",
                    "Activated\tGuise of the Deceiver\t2469",
                    // Collected exaltations live on the key ring too (observed).
                    "Augmentation\tDamask Robe (Exaltation)\t1334",
                    "Equipment\tBoots of the Long Road\t177708",
                    "Equipment\tBoots of the Long Road +1\t177708",
                });
                var dump = InventoryStore.Parse(dumpText);

                Check("inventory: the -Slot chain nests, Personal-Depot1 keeps its hyphen",
                    InventoryStore.SplitBase("General 1-Slot9-Slot7") == "General 1"
                    && InventoryStore.SplitBase("Personal-Depot1") == "Personal-Depot1"
                    && InventoryStore.SplitBase("Any Slot-Slot2") == "Any Slot");
                Check("inventory: duplicate slots are real, children attach to the LAST seen",
                    dump.Items.Count(e => e.Base == "Ear") == 2
                    // Ear-Slot7 sits under the FIRST Ear (it came before the second).
                    && dump.Items.First(e => e.Base == "Ear").Children is [{ Empty: true }]);
                Check("inventory: nesting reaches the exaltation socket in the bag",
                    dump.Items.First(e => e.Location == "General 1").Children
                        .First(c => c.Location == "General 1-Slot9").Children
                        is [{ Name: "Kelin`s Seven Stringed Lute (Exaltation)" }]);
                Check("inventory: the keyring table parses through its bare-tab header",
                    dump.KeyRing.Count == 4 && dump.Sections.SequenceEqual(new[] { "Location", "KeyRing" })
                    && dump.MalformedCount == 0);

                var (rows, lanes) = InventoryStore.CarryAll(dump);
                Check("inventory: empty rows leave the ledger, real ones keep file order",
                    rows.All(r => r.Name != "Empty")
                    && rows.Select(r => r.Line).SequenceEqual(rows.Select(r => r.Line).OrderBy(n => n)));
                // The keyring is several in-game things: Equipment = Storage,
                // Activated = Activated items, the rest (Augmentation) stays
                // generic. Chips order carry-group first, stash-group after.
                Check("inventory: lanes split the keyring and order carry before stash",
                    lanes.Select(l => l.Id).SequenceEqual(new[]
                        { "worn", "bags", "storage", "activated", "keyring", "depot", "hoard" }));
                Check("inventory: stack counts survive, keyring categories land in their lanes",
                    rows.First(r => r.Name == "Tiny Dagger").Count == 86
                    && rows.First(r => r.Name == "Griffenne Blood") is { Count: 2, Lane: "depot" }
                    && rows.First(r => r.Name == "Fine Steel Scimitar").Lane == "hoard"
                    && rows.Count(r => r.Lane == "storage") == 2      // Equipment rows
                    && rows.Count(r => r.Lane == "activated") == 1    // Guise of the Deceiver
                    && rows.Count(r => r.Lane == "keyring") == 1);    // the Augmentation exaltation
                Check("inventory: lane groups tell carry from stash",
                    InventoryStore.LaneGroup("storage") == "carry"
                    && InventoryStore.LaneGroup("hoard") == "stash"
                    && InventoryStore.LaneGroup("elsewhere") == "");

                var held = InventoryStore.HeldCounts(dump);
                Check("inventory: held counts sum stacks; Activated is a look, not an item",
                    held["tiny dagger"] == 86
                    && held["boots of the long road"] == 1 && held["boots of the long road +1"] == 1
                    && !held.ContainsKey("guise of the deceiver"));

                // Tabs partition the rows: "(Exaltation)" copies get their own
                // tab, everything else (keyring included) is an item, and an
                // exaltation knows the item wearing it. The Focus effects tab
                // is not row-backed — it audits the dump (checked below).
                Check("inventory: tabs split items / exaltations (keyring Augmentation included)",
                    rows.Count(r => InventoryStore.TabOf(r) == "exalt") == 3
                    && rows.Count(r => InventoryStore.TabOf(r) == "items") == rows.Count - 3);
                Check("inventory: an exaltation names its host item",
                    rows.First(r => r.Name == "Thelvorn, Blade of Light (Exaltation)").Host
                        == "Thelvorn, Blade of Light +5"
                    && rows.First(r => r.Name == "Kelin`s Seven Stringed Lute (Exaltation)").Host
                        == "Kelin`s Seven Stringed Lute +1"
                    && rows.First(r => r.Name == "Spacious Rucksack").Host == "");

                // Coverage: the row is the evidence (an Empty bank slot still
                // proves the bank was dumped); missing = "the dump does not
                // say". Hoard rows are hoard evidence.
                Check("inventory: full coverage leaves nothing unsaid",
                    dump.Covered.SetEquals(new[] { "worn", "bags", "bank", "sharedBank", "depot", "hoard", "keyring" })
                    && InventoryStore.MissingStorages(dump).Count == 0);
                var partial = InventoryStore.Parse(string.Join("\r\n", new[]
                {
                    "Location\tName\tID\tCount\tSlots",
                    "Head\tValorium Helmet +1\t4851\t1\t10",
                    "General 1\tBackpack\t17005\t1\t8",
                }));
                // Slot types correlated from the in-game item window against
                // observed ladders (Aldryn's five typed rows = 1|2,7,8,9,10).
                Check("inventory: worn display order runs armor, jewelry, weapons",
                    InventoryStore.WornRank("Head") < InventoryStore.WornRank("Feet")
                    && InventoryStore.WornRank("Feet") < InventoryStore.WornRank("Ear")
                    && InventoryStore.WornRank("Fingers") < InventoryStore.WornRank("Primary")
                    && InventoryStore.WornRank("Primary") < InventoryStore.WornRank("Any Slot")
                    && InventoryStore.WornRank("SomethingNew") > InventoryStore.WornRank("Any Slot"));
                Check("inventory: slot numbers speak their game types",
                    InventoryStore.SlotType(7) == ("F", "Focus Exaltation")
                    && InventoryStore.SlotType(8) == ("C", "Click Exaltation")
                    && InventoryStore.SlotType(9) == ("W", "Worn Exaltation")
                    && InventoryStore.SlotType(10) == ("P", "Proc Exaltation")
                    && InventoryStore.SlotType(1) == ("O", "Ornamentation")
                    && InventoryStore.SlotType(2) == ("O", "Ornamentation")
                    && InventoryStore.SlotType(3) == ("3", "Slot 3"));
                Check("inventory: bags are containers, socketed items are not",
                    InventoryStore.IsContainer(dump.Items.First(e => e.Name == "Spacious Rucksack"))
                    && !InventoryStore.IsContainer(dump.Items.First(e => e.Base == "Ear" && e.Children.Count > 0))
                    && !InventoryStore.IsContainer(dump.Items.First(e => e.Location == "Hoard 1")));
                Check("inventory: an old dump names everything it left unsaid",
                    InventoryStore.MissingStorages(partial).SequenceEqual(
                        new[] { "bank", "tradeskill depot", "Dragon's Hoard", "exaltations & storage" }));
                var hoardish = InventoryStore.Parse(string.Join("\r\n", new[]
                {
                    "Location\tName\tID\tCount\tSlots",
                    "Head\tValorium Helmet +1\t4851\t1\t10",
                    "",
                    "Hoard\tName\tID\tCount\tSlots",
                    "Hoard1\tShiny Thing\t99\t1\t10",
                }));
                Check("inventory: an extra item table reads as the hoard, own lane chip",
                    !InventoryStore.MissingStorages(hoardish).Contains("Dragon's Hoard")
                    && InventoryStore.CarryAll(hoardish).Rows
                        .Any(r => r.Lane == "section:Hoard" && r.Name == "Shiny Thing"));

                // The duplicate finder: ≥2 physical rows per tier-stripped
                // name; a lone stack is one place and never counts.
                var dupRows = new List<InventoryStore.CarryRow>
                {
                    new("Pearl", "pearl", "General 1-Slot 1", 3, "bags", 1),
                    new("Pearl", "pearl", "Bank 2", 1, "bank", 2),
                    new("Barons Blade +3", "barons blade +3", "Primary", 1, "worn", 3),
                    new("Barons Blade", "barons blade", "Bank 4", 1, "bank", 4),
                    new("One Of A Kind", "one of a kind", "General 2", 5, "bags", 5),
                    new("Backpack", "backpack", "General 3", 1, "bags", 6, IsContainer: true),
                    new("Backpack", "backpack", "Bank 5", 1, "bank", 7, IsContainer: true),
                };
                var dk = InventoryStore.DuplicateKeys(dupRows);
                Check("inventory: duplicates fold +N tiers; stacks and bags never count",
                    dk.Count == 2 && dk.Contains(FocusEffects.ItemKey("Pearl"))
                    && dk.Contains(FocusEffects.ItemKey("Barons Blade"))
                    && !dk.Contains(FocusEffects.ItemKey("One Of A Kind"))
                    && !dk.Contains(FocusEffects.ItemKey("Backpack")));

                // ---- planar armor sets (data\armor-sets.json, eqlwiki scrape).
                var asets = new ArmorSets();
                Check("armor sets: the library loads — classic + Iksar + multiclass",
                    asets.Sets.Count >= 20
                    && asets.Sets.FirstOrDefault(s => s.Name == "Umbral Platemail")
                        is { Pieces.Count: 7, Multiclass: false, Classes: ["SHD"] }
                    && asets.Sets.FirstOrDefault(s => s.Name == "Greenmist Armor")
                        is { RaceNote: "Iksar only" });
                var lrset = asets.Sets.FirstOrDefault(s => s.Name == "Lustrous Russet Armor");
                Check("armor sets: multiclass sets carry per-piece class lists",
                    lrset is { Multiclass: true, Pieces.Count: 7 }
                    && lrset.Classes.Contains("SHD") && lrset.Classes.Contains("BER")
                    && lrset.Pieces.First(p => p.Slot == "CHEST").Classes.Contains("BER")
                    && !lrset.Pieces.First(p => p.Slot == "HEAD").Classes.Contains("BER"));
                var thorrak = new[] { "SHD", "ROG", "SHM" };
                Check("armor sets: relevance follows /who classes (unknown = all)",
                    ArmorSets.Relevant(asets.Sets.First(s => s.Name == "Umbral Platemail"), thorrak)
                    && ArmorSets.Relevant(lrset!, thorrak)
                    && !ArmorSets.Relevant(asets.Sets.First(s => s.Name == "Indicolite Armor"), thorrak)
                    && ArmorSets.Relevant(asets.Sets.First(s => s.Name == "Indicolite Armor"),
                        Array.Empty<string>()));
                Check("armor sets: pieces-unknown pages load as note-only sets",
                    asets.Sets.FirstOrDefault(s => s.Name == "Righteous Armor")
                        is { Pieces.Count: 0 } ra && ra.Note.Length > 0);

                // ---- Sky quest helper: full dropper names + sighting matcher.
                string skyProg = Path.Combine(Path.GetTempPath(),
                    $"eql_test_skyprog_{Guid.NewGuid():N}.json");
                var skyCfg = new ConfigService();
                var skyLoot = new LootTracker(skyCfg, Path.Combine(Path.GetTempPath(),
                    $"eql_test_loot_{Guid.NewGuid():N}.json"));
                var sq = new SkyQuests(skyCfg, skyLoot, skyProg);
                var voice = sq.Quests.First(q => q.Name == "Bard Test of Voice");
                Check("sky helper: the library carries FULL dropper names",
                    voice.Items.First(i => i.Name == "Light Woolen Mantle").Mobs
                        .Contains("Keeper of Souls")
                    && voice.Items.First(i => i.Name.StartsWith("Wind Rune")).Mobs.Count == 0
                    && sq.Quests.All(q => q.Items.Count > 0));

                // Wind runes are CURRENCIES in EQL — their pickup line (real
                // form, 29 Aug log: word order is "Wind Rune Jaka", whatever
                // the currency TAB displays) must count toward the quest.
                // Delta-based: the tracker rides the REAL loot history here,
                // so absolute counts belong to Johan, not the test. The
                // synthetic line's mob/date never collide with real entries.
                var jaka = sq.Quests.First(q => q.Name == "Magician Test of Gesticulation")
                    .Items.First(i => i.Name == "Wind Rune Jaka");
                int jakaBefore = sq.HeldCount(jaka);
                skyLoot.ProcessLine("[Mon Jan 06 12:00:00 2020] You looted a Wind Rune Jaka from a selftest zephyr's corpse and stored it in your currency");
                Check("sky: a currency rune pickup ticks the quest's have-count",
                    sq.HeldCount(jaka) == jakaBefore + 1);

                // Runes recorded BEFORE their quest key matched reconcile in
                // on the next start — counts lift from history, never drop.
                var sqAgain = new SkyQuests(skyCfg, skyLoot, skyProg);
                Check("sky: startup reconciles missed loot into the counts",
                    sqAgain.HeldCount(sqAgain.Quests.First(q => q.Name == "Magician Test of Gesticulation")
                        .Items.First(i => i.Name == "Wind Rune Jaka")) >= jakaBefore + 1);

                // Turn-ins ARE logged even though rewards aren't: offering all
                // of a quest's items to ITS OWN NPC completes it, drops the
                // held counts — and a catch-up replay of the same lines never
                // double-counts.
                var cleric = sqAgain.Quests.First(q => q.Name == "Cleric Test of Skill");
                var shaman = sqAgain.Quests.First(q => q.Name == "Shaman Test of Might");
                var clericMeda = cleric.Items.First(i => i.Name == "Wind Rune Meda");
                var shamanMeda = shaman.Items.First(i => i.Name == "Wind Rune Meda");

                // Every rune serves 6-7 quests: ONE looted Meda lights them
                // all up (you hold one, any could spend it)…
                skyLoot.ProcessLine("[Sat Aug 29 00:29:00 2026] You looted a Wind Rune Meda from a selftest zephyr's corpse and stored it in your currency");
                Check("sky: one looted rune counts for every quest that wants it",
                    sqAgain.HeldCount(clericMeda) == 1 && sqAgain.HeldCount(shamanMeda) == 1);
                // One rune, two quests: it is CREDITED to one of them only.
                // (Meda serves 6-7 quests; whichever comes first in the order
                // gets the rune, everyone else reads 0 - the total credit is ONE.)
                int medaCredited = sqAgain.Quests.Where(q => !sqAgain.IsCompleted(q))
                    .Sum(q => q.Items.Where(i => i.Name == "Wind Rune Meda").Sum(i => sqAgain.AllocatedHeld(q, i)));
                Check("sky: a shared item is allocated to one quest, never counted twice",
                    medaCredited == 1
                    && sqAgain.AllocatedHeld(cleric, clericMeda) + sqAgain.AllocatedHeld(shaman, shamanMeda) <= 1);

                // Game-focus watch (7 Sep): the game is any exe under the log's
                // install root; our own pid keeps the overlay; every doubt keeps it.
                const string eqRoot = @"C:\Users\Public\Daybreak Game Company\Installed Games\EverQuest Legends";
                Check("game focus: the game's exe under the install root keeps the overlay",
                    GameFocus.Keep(eqRoot + @"\eqgame.exe", 4242, 100, eqRoot)
                    && GameFocus.Keep(eqRoot.ToUpperInvariant() + @"\EQGAME.EXE", 4242, 100, eqRoot));
                Check("game focus: a browser in front hides it",
                    !GameFocus.Keep(@"C:\Program Files\Mozilla Firefox\firefox.exe", 777, 100, eqRoot));
                Check("game focus: a sibling folder with the root as a prefix is NOT the game",
                    !GameFocus.Keep(eqRoot + @" Beta\eqgame.exe", 777, 100, eqRoot));
                Check("game focus: our own windows keep the overlay",
                    GameFocus.Keep(@"C:\Tools\EQL_Assistant.exe", 100, 100, eqRoot));
                Check("game focus: no install root or an unreadable process never hides",
                    GameFocus.Keep(@"C:\Program Files\Mozilla Firefox\firefox.exe", 777, 100, "")
                    && GameFocus.Keep(null, 777, 100, eqRoot));
                var awayTracker = new GameAwayTracker();
                var ga0 = new DateTime(2026, 9, 7, 20, 0, 0);
                Check("game focus: a blink of the alt-tab switcher does not hide the overlay",
                    !awayTracker.Update(false, ga0) && !awayTracker.Update(false, ga0.AddMilliseconds(500))
                    && !awayTracker.Update(true, ga0.AddMilliseconds(1000)));
                Check("game focus: away past the grace hides, the game's return unhides at once",
                    !awayTracker.Update(false, ga0.AddSeconds(5)) && awayTracker.Update(false, ga0.AddSeconds(6.5))
                    && awayTracker.Away && !awayTracker.Update(true, ga0.AddSeconds(7)));

                Check("alerts: headless runs are gagged — nothing speaks from a selftest", AlertService.Silenced);
            Check("log: the tailer's default poll is 100 ms", new Models.AppConfig().Log.PollIntervalMs == 100);

            // Reparse progress card (14 Sep): bytes done over total, culture-proof percent, file N of M.
            {
                var rp = new ReparseProgress("eqlog_Thorrak_paineel.txt", 2, 3, 61_300_000, 142_000_000, 213_400);
                Check("reparse: progress is bytes done over total, percent rounded, title says file N of M",
                    Math.Abs(rp.Fraction - 0.4317) < 0.001 && rp.Percent == "43%"
                    && rp.Title == "Replaying eqlog_Thorrak_paineel.txt — file 2 of 3");
                var one = new ReparseProgress("a.txt", 1, 1, 0, 0, 0);
                Check("reparse: an empty file reads 0% until done, then 100%; one file has no 'of'",
                    one.Fraction == 0 && one.Percent == "0%" && (one with { Done = true }).Fraction == 1
                    && one.Title == "Replaying a.txt" && new ReparseProgress("a", 1, 1, 9, 4, 1).Fraction == 1);
                CrowdControlChecks(Check);
                TradeskillChecks(Check);
                RaceChecks(Check);
                EfficiencyChecks(Check);
                BisPoolChecks(Check);
                InvocationChecks(Check);
                ToolsChecks(Check);
                Check("reparse: the catch-up card says so, and the toolbar fill is the track times the fraction",
                    new ReparseProgress("a.txt", 1, 1, 0, 0, 0, Verb: "Catching up").Title == "Catching up a.txt"
                    && Math.Abs(new ViewModels.OverlayViewModel(new TriggerEngine(new Models.AppConfig(), new AlertService()), new Models.AppConfig())
                        { Progress = new ReparseProgress("a", 1, 1, 1, 2, 0) }.ProgressFill - ViewModels.OverlayViewModel.ProgressTrack / 2) < 0.01);
            }

            // Quest chips say where a held copy sits (11 Sep).
            {
                var chipRows = new List<InventoryStore.CarryRow>
                {
                    new("Gorgon Head", "gorgon head", "General 11-Slot 9", 1, "bags", 1),
                    new("Golden Coffer +1", "golden coffer", "Bank 3-Slot 2", 2, "bank", 2),
                    new("Golden Coffer", "golden coffer", "General 2-Slot 1", 1, "bags", 3),
                    new("Golden Coffer", "golden coffer", "General 2-Slot 4", 1, "bags", 4),
                    new("Golden Coffer (Exaltation)", "golden coffer", "General 2-Slot 4", 1, "bags", 5),
                };
                Check("chips: a held item names its slot; tiers fold; two spots then +N; exaltation rows skipped",
                    Views.SkyWindow.WhereText(chipRows, "Gorgon Head", 1) == "in General 11-Slot 9"
                    && Views.SkyWindow.WhereText(chipRows, "Golden Coffer", 3) == "in Bank 3-Slot 2 ×2, General 2-Slot 1 +1 more");
                Check("chips: nothing held, no dump, or not in the dump reads empty",
                    Views.SkyWindow.WhereText(chipRows, "Gorgon Head", 0) == ""
                    && Views.SkyWindow.WhereText(null, "Gorgon Head", 1) == ""
                    && Views.SkyWindow.WhereText(chipRows, "Wind Rune Meda", 1) == "");
            }

            // Incoming damage watch (11 Sep): per-second buckets and the in-fight verdict.
            {
                var iw = new IncomingWatch { WindowSec = 15 };
                var iw0 = new DateTime(2026, 9, 11, 21, 0, 0);
                iw.Add(iw0.AddSeconds(-14), 300, spell: false);
                iw.Add(iw0.AddSeconds(-14), 100, spell: true);
                iw.Add(iw0.AddSeconds(-3), 900, spell: true);
                iw.Add(iw0.AddSeconds(-20), 5000, spell: true); // outside the window
                var snap = iw.Take(iw0, "defensive");
                Check("incoming: buckets land in their second, the window drops older hits",
                    snap.WindowSec == 15 && snap.MeleeCols[0] == 300 && snap.SpellCols[0] == 100 && snap.SpellCols[11] == 900
                    && Math.Abs(snap.Total - 1300) < 0.01 && Math.Abs(snap.SpellShare - 1000.0 / 1300) < 0.001);
                Check("incoming: spells dominant in defensive says switch to mage hunter",
                    snap.VerdictKind == "switch" && snap.VerdictText.Contains("Mage hunter would halve"));
                Check("incoming: the right stance reads ok, a mix reads mixed, nothing reads nothing",
                    IncomingWatch.Verdict("mage hunter", 100, 900).Kind == "ok"
                    && IncomingWatch.Verdict("defensive", 900, 100).Kind == "ok"
                    && IncomingWatch.Verdict("mage hunter", 900, 100).Kind == "switch"
                    && IncomingWatch.Verdict("striker", 500, 500).Kind == "mixed"
                    && IncomingWatch.Verdict("", 900, 100).Text.Contains("defensive would halve")
                    && IncomingWatch.Verdict("defensive", 0, 0).Kind == "");
                Check("incoming: the config defaults — shown, 15 s window",
                    new Models.AppConfig().Overlay.IncomingVisible && new Models.AppConfig().Overlay.IncomingWindowSec == 15);

                // The wrong-stance notice (14 Sep): share over a short window, the
                // other stance as the target, quiet when already there or too few hits.
                var sw = new IncomingWatch();
                var sw0 = new DateTime(2026, 9, 14, 21, 0, 0);
                sw.Add(sw0.AddSeconds(-8), 300, spell: true);
                sw.Add(sw0.AddSeconds(-4), 300, spell: true);
                Check("incoming: two hits are not a pattern — no advice yet", sw.SwitchAdvice(sw0, "defensive", 10, 0.75).Target == "");
                sw.Add(sw0.AddSeconds(-2), 200, spell: true);
                sw.Add(sw0.AddSeconds(-1), 100, spell: false);
                var a1 = sw.SwitchAdvice(sw0, "defensive", 10, 0.75);
                Check("incoming: spells at 89% in defensive says mage hunter, with the share and the hit count",
                    a1.Target == "mage hunter" && Math.Abs(a1.Share - 800.0 / 900) < 0.001 && a1.Hits == 4);
                Check("incoming: already in mage hunter stays quiet; a 90% bar is not met; a 5 s window drops the oldest hit and still advises on three",
                    sw.SwitchAdvice(sw0, "mage hunter", 10, 0.75).Target == ""
                    && sw.SwitchAdvice(sw0, "defensive", 10, 0.90).Target == ""
                    && sw.SwitchAdvice(sw0, "defensive", 5, 0.75) is { Hits: 3, Target: "mage hunter" });
                sw.Add(sw0.AddSeconds(-1), 2000, spell: false);
                Check("incoming: melee taking over says defensive from an unknown stance",
                    sw.SwitchAdvice(sw0, "", 10, 0.6).Target == "defensive");
                Check("incoming: the notice phrase fills the target, empty falls back",
                    EQLOverlay.MainWindow.StancePhrase("Go {stance} now", "defensive") == "Go defensive now"
                    && EQLOverlay.MainWindow.StancePhrase("", "mage hunter") == "Switch to mage hunter");
                Check("incoming: the notice defaults — on, 75% over 10 s, spoken",
                    new Models.AppConfig().Overlay is { StanceNoticeEnabled: true, StanceNoticeShare: 75, StanceNoticeWindowSec: 10, StanceNoticeMode: "speak" });
            }

            // Attack rounds (8 Sep): the annotation rides every melee event; the
            // avoid word rides every miss; RoundStats reads them honestly.
            {
                var ap = new CombatParser { SelfName = "Thorrak" };
                string RoundTs(int s) => $"[Tue Sep 08 22:00:{s:00} 2026]";
                ap.ProcessLine($"{RoundTs(0)} Thorrak slashes a wan ghoul knight for 120 points of damage.");
                ap.ProcessLine($"{RoundTs(0)} Thorrak slashes a wan ghoul knight for 130 points of damage. (Critical)");
                ap.ProcessLine($"{RoundTs(1)} Thorrak slashes a wan ghoul knight for 110 points of damage. (Riposte)");
                ap.ProcessLine($"{RoundTs(2)} Thorrak slashes a wan ghoul knight for 115 points of damage. (Flurry)");
                ap.ProcessLine($"{RoundTs(2)} Thorrak slashes a wan ghoul knight for 100 points of damage.");
                ap.ProcessLine($"{RoundTs(3)} A wan ghoul knight tries to hit YOU, but YOU riposte!");
                ap.ProcessLine($"{RoundTs(3)} A wan ghoul knight tries to hit YOU, but YOU dodge!");
                ap.ProcessLine($"{RoundTs(4)} A wan ghoul knight tries to hit YOU, but misses!");
                ap.ProcessLine($"{RoundTs(4)} A wan ghoul knight hits YOU for 90 points of damage. (Riposte)");
                ap.ProcessLine($"{RoundTs(5)} A wan ghoul knight hits YOU for 70 points of damage. (Rampage)");
                for (int i = 6; i < 14; i++) ap.ProcessLine($"{RoundTs(i)} Thorrak slashes a wan ghoul knight for 100 points of damage.");
                ap.ProcessLine($"{RoundTs(14)} Thorrak slashes a wan ghoul knight for 300 points of damage. (Slay Undead)");
                ap.ProcessLine($"{RoundTs(15)} You have slain a wan ghoul knight!");
                ap.Tick(new DateTime(2026, 9, 8, 22, 5, 0));
                var arec = ap.History.FirstOrDefault();
                Check("rounds: the fight recorded with tagged events",
                    arec is not null && arec.Events.Any(e => e.Tag == "riposte" && e.Stream == CombatParser.FightStream.SelfOut)
                    && arec.Events.Any(e => e.Tag == "riposte" && e.Miss && e.Stream == CombatParser.FightStream.SelfIn)
                    && arec.Events.Any(e => e.Tag == "dodge" && e.Miss)
                    && arec.Events.Any(e => e.Tag == "miss" && e.Miss)
                    && arec.Events.Any(e => e.Tag == "rampage" && !e.Miss && e.Stream == CombatParser.FightStream.SelfIn)
                    && arec.Events.Any(e => e.Tag == "slay undead")
                    && arec.Events.First(e => e.Crit && e.Stream == CombatParser.FightStream.SelfOut).Tag == "");
                if (arec is not null)
                {
                    var rs = RoundStats.Compute(arec);
                    Check("rounds: defence counts — 5 swings at you: 1 riposted, 1 dodged, 1 missed, 1 mob riposte, 1 rampage",
                        rs.SwingsOnYou == 5 && rs.YouRiposted == 1 && rs.YouDodged == 1 && rs.MissedYou == 1
                        && rs.MobRipostesTaken == 1 && rs.MobRiposteDamage == 90 && rs.RampagesTaken == 1);
                    Check("rounds: offence counts — 1 riposte swing for 110, 1 flurry, 1 Slay Undead",
                        rs.YourRipostes == 1 && rs.YourRiposteHits == 1 && rs.YourRiposteDamage == 110 && rs.Flurries == 1 && rs.SlayUndead == 1);
                    var slash = rs.Skills.FirstOrDefault(s => s.Ability == "slash");
                    Check("rounds: multi-swing rounds — plain slashes cluster by second; ripostes and flurries don't count",
                        slash is not null && slash.Rounds == 11 && slash.Swings == 12 && slash.Multi2 == 1 && slash.Multi3 == 0);
                    Check("rounds: the card's rows carry denominators and the dual-wield caveat",
                        Views.TimelineView.RoundsOffenceRows(rs).Any(r => r.Tail.Contains("11 rounds"))
                        && Views.TimelineView.RoundsOffenceRows(rs).Any(r => r.Tail.Contains("dual wield"))
                        && Views.TimelineView.RoundsDefenceRows(rs).First().Val == "5");
                }
                // A kept fight from before the tag reads as untagged, never as a crash.
                var legacyEv = System.Text.Json.JsonSerializer.Deserialize<CombatParser.FightEvent>("{\"t\":1,\"a\":\"slash\",\"v\":10,\"s\":0}");
                Check("rounds: pre-tag fight events load with an empty tag", legacyEv is not null && legacyEv.Tag == "");
            }

            // Per-mob resist table (8 Sep): landings and resists from the parser's
            // own lines, keyed so replay never double-counts; verdicts past 5 casts.
            {
                string rbPath = Path.Combine(Path.GetTempPath(), "eql_test_resists.json");
                try { File.Delete(rbPath); } catch { /* fresh */ }
                var rp = new CombatParser { SelfName = "Thorrak" };
                var rb = new ResistBook(new ConfigService(), rp, rbPath);
                rp.ProcessLine("[Tue Sep 08 20:00:00 2026] You have entered Eye of Veeshan.");
                rp.ProcessLine("[Tue Sep 08 20:00:01 2026] A greater sphinx scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 52)");
                rb.ProcessLine("[Tue Sep 08 20:00:01 2026] A greater sphinx scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 52)");
                rp.ProcessLine("[Tue Sep 08 20:00:02 2026] A greater sphinx resisted your Envenomed Breath!");
                rp.ProcessLine("[Tue Sep 08 20:00:05 2026] A greater sphinx resisted your Envenomed Breath!");
                rp.ProcessLine("[Tue Sep 08 20:00:08 2026] Thorrak hit a greater sphinx for 178 points of poison damage by Envenomed Breath.");
                rp.ProcessLine("[Tue Sep 08 20:00:12 2026] A greater sphinx resisted your Envenomed Breath!");
                rp.ProcessLine("[Tue Sep 08 20:00:15 2026] A greater sphinx resisted your Envenomed Breath!");
                rp.ProcessLine("[Tue Sep 08 20:00:20 2026] Thorrak hit a greater sphinx for 310 points of magic damage by Siphon Life.");
                var sphinx = rb.ForMob("a greater sphinx");
                var breath = sphinx.FirstOrDefault(c => c.Spell == "Envenomed Breath");
                Check("resists: resists and a landing count per mob per spell, article-insensitive",
                    breath is { Resisted: 4, Landed: 1 } && breath.School == "poison" && breath.Level == 52 && breath.Zone == "Eye of Veeshan");
                Check("resists: past 5 casts, 80% resisted reads nearly immune; 1 cast is no verdict",
                    rb.Notable("A greater sphinx") is { Count: 1 } nv && nv[0].Spell == "Envenomed Breath" && nv[0].Severity == "immune"
                    && ResistBook.Severity(0, 1) == "" && ResistBook.Severity(0.4, 5) == "resistant" && ResistBook.Severity(0.1, 5) == "fine");
                // Replay of the same lines changes nothing.
                rp.ProcessLine("[Tue Sep 08 20:00:12 2026] A greater sphinx resisted your Envenomed Breath!");
                rp.ProcessLine("[Tue Sep 08 20:00:08 2026] Thorrak hit a greater sphinx for 178 points of poison damage by Envenomed Breath.");
                Check("resists: replay is a no-op", rb.ForMob("a greater sphinx").First(c => c.Spell == "Envenomed Breath") is { Resisted: 4, Landed: 1 });
                // Your own damage on yourself or your pet never counts; a stranger's spell never counts.
                rp.PetName = "Jobaner";
                rp.ProcessLine("[Tue Sep 08 20:01:00 2026] Cognitive hit a greater sphinx for 90 points of fire damage by Ignite.");
                rp.ProcessLine("[Tue Sep 08 20:01:01 2026] Thorrak hit Jobaner for 5 points of magic damage by Siphon Life.");
                Check("resists: only YOUR casts on mobs are counted",
                    rb.ForMob("a greater sphinx").All(c => c.Spell != "Ignite") && rb.ForMob("Jobaner").Count == 0);
                string connedMob = ""; int connedLvl = 0;
                rb.Conned += (mm, ll) => { connedMob = mm; connedLvl = ll; };
                rb.ProcessLine("[Tue Sep 08 20:02:00 2026] A greater sphinx scowls at you, ready to attack -- it appears to be quite formidable. (Lvl: 52)");
                Check("con card: every /con announces the mob and level, even one already known",
                    connedMob == "a greater sphinx" && connedLvl == 52 && rb.CastsOn("A greater sphinx") == 6);
                var rb2 = new ResistBook(new ConfigService(), null, rbPath);
                Check("resists: the book survives a reload", rb2.ForMob("a greater sphinx").First(c => c.Spell == "Envenomed Breath").Resisted == 4);
                try { File.Delete(rbPath); } catch { /* temp */ }
            }

            // New at this level (8 Sep): unlocks by combo from the library's class levels.
            {
                var libL = new SpellLibrary(new ConfigService());
                var shd46 = libL.UnlocksAt(46, new[] { "SHD" });
                var combo46 = libL.UnlocksAt(46, new[] { "SHD", "SHM", "NEC" });
                var all46 = libL.UnlocksAt(46, Array.Empty<string>());
                Check("levelup: SHD unlocks Voice of Shadows at 46 and nothing at 47 reads as 46",
                    shd46.Count == 1 && shd46[0].Spell.Name == "Voice of Shadows" && shd46[0].Cls == "SHD"
                    && libL.UnlocksAt(47, new[] { "SHD" }).All(u => u.Spell.Name != "Voice of Shadows"));
                Check("levelup: a three-class combo pools every class's unlocks",
                    combo46.Count == 5 && combo46.Any(u => u.Spell.Name == "Paralyzing Earth" && u.Cls == "NEC")
                    && combo46.Any(u => u.Spell.Name == "Strength" && u.Cls == "SHM"));
                Check("levelup: no combo = every class, never nothing", all46.Count > combo46.Count);
                // Effects (owner, 25 Sep): the card said DEBUFF for a nuke and BUFF for a heal.
                SpellLibrary.Spell? Sp(string n) => libL.Spells.FirstOrDefault(x => x.Name == n);
                Check("levelup: effects from eqlwiki — nuke, AE nuke, DoT, mez, heal, lifetap, calm, snare",
                    Sp("Flame Shock")?.Effect == "Direct damage" && Sp("Pillar of Fire")?.Effect == "AE damage"
                    && Sp("Stinging Swarm")?.Effect == "Damage over time" && Sp("Kelin's Lucid Lullaby")?.Effect == "Mez"
                    && Sp("Minor Healing")?.Effect == "Heal" && Sp("Drain Spirit")?.Effect == "Lifetap"
                    && Sp("Calm Animal")?.Effect == "Calm" && Sp("Ensnare")?.Effect == "Snare");
                Check("levelup: HP buffs stay buffs, per-tick heals are HoT / regen by length",
                    Sp("Courage")?.Effect == "Buff" && Sp("Aegolism")?.Effect == "Buff"
                    && Sp("Celestial Healing")?.Effect == "Heal over time" && Sp("Chloroplast")?.Effect == "Regen"
                    && Sp("Stoicism")?.Effect == "Heal over time");
                Check("levelup: effect colours follow the trigger types",
                    SpellLibrary.EffectColor("Damage over time") == TriggerColors.Dot && SpellLibrary.EffectColor("Heal") == TriggerColors.Heal
                    && SpellLibrary.EffectColor("Mez") == TriggerColors.Debuff && SpellLibrary.EffectColor("Haste") == TriggerColors.Buff);
                int withEffect = libL.Spells.Count(x => x.Effect.Length > 0);
                Check($"levelup: nearly every library spell carries an effect ({withEffect})", withEffect > 1380);
                var lp = new CombatParser();
                int dinged = 0;
                lp.LeveledUp += l => dinged = l;
                lp.ProcessLine("[Tue Sep 08 21:00:00 2026] You have gained a level! Welcome to level 46!");
                Check("levelup: the ding fires its own event even with the combo unknown", dinged == 46 && lp.CurrentClasses.Length == 0);
            }

            // Diagnostics bundle (8 Sep): chat and tells go, everything the
            // parser reads stays; the slice is the last N minutes of the FILE.
            Check("diag: tells, channels, says and shouts between players are chat",
                Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Gorby tells General:1, 'why when i swapp loadout'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Cognitive tells you, 'inc'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] You told Cognitive, 'ok'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] You tell General:1, 'hi'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Sycopata tells the group, 'pull'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] You say, 'I still seek guidance'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Bob says, 'lol'")
                && Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Bob shouts, 'train'"));
            Check("diag: NPC speech, combat, loot and casts are not chat",
                !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] Klok Lagnoz says, 'Welcome to my shop, Baskit.'")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] The Kerran Sha`rr says, 'Something is wrrrong.'")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] a rat says, 'squeak'")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] A zol ghoul knight hits YOU for 42 points of damage.")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] --You have looted Dark Reaver from a ghoul cavalier's corpse.--")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] You begin casting Drain Soul VI.")
                && !Diagnostics.IsChat("[Tue Sep 08 20:00:00 2026] You assume a defensive stance."));
            {
                var diagLines = new List<string>
                {
                    "[Tue Sep 08 19:00:00 2026] You have entered Lower Guk.",
                    "[Tue Sep 08 19:40:00 2026] Bob tells you, 'old chat'",
                    "[Tue Sep 08 19:50:00 2026] A froglok hits YOU for 10 points of damage.",
                    "[Tue Sep 08 19:55:00 2026] Bob tells you, 'recent chat'",
                    "[Tue Sep 08 20:00:00 2026] You have slain a froglok!",
                };
                var slice = Diagnostics.Slice(diagLines, 15);
                Check("diag: the slice keeps the last 15 minutes of the file minus chat, and says so",
                    slice.Count == 3 && slice[0].StartsWith("# last 15 min") && slice[0].Contains("1 chat lines removed")
                    && slice[1].Contains("hits YOU") && slice[2].Contains("slain"));
                string zipPath = Path.Combine(Path.GetTempPath(), "eql_test_diag.zip");
                string fakeLog = Path.Combine(Path.GetTempPath(), "eqlog_Test_paineel.txt");
                File.WriteAllLines(fakeLog, diagLines);
                var dcs = new ConfigService();
                string summary = Diagnostics.BuildBundle(zipPath, dcs, fakeLog, 15, Diagnostics.About(dcs, new Models.AppConfig(), fakeLog, "selftest"));
                using (var z = System.IO.Compression.ZipFile.OpenRead(zipPath))
                {
                    var names = z.Entries.Select(x => x.FullName).ToList();
                    Check("diag: the bundle carries about.txt and the scrubbed game-log slice",
                        names.Contains("about.txt") && names.Contains("game-log-last-15min.txt") && summary.Contains("chat removed"));
                }
                try { File.Delete(zipPath); File.Delete(fakeLog); } catch { /* temp */ }
            }
            // Offline voice packs (9 Sep): catalog, unzip, and the adapter pointer.
            {
                Check("voice packs: the catalog names distinct https packs with distinct folders",
                    VoicePacks.Catalog.Count >= 4
                    && VoicePacks.Catalog.All(p => p.Url.StartsWith("https://", StringComparison.Ordinal) && p.Url.EndsWith(".Msix", StringComparison.Ordinal))
                    && VoicePacks.Catalog.Select(p => p.Folder).Distinct().Count() == VoicePacks.Catalog.Count);
                Check("voice packs: the expected picker name drops 'Online'",
                    VoicePacks.ExpectedVoiceName(VoicePacks.Catalog[0]) == "Microsoft Sonia (Natural) - English (United Kingdom)");
                string vpTmp = Path.Combine(Path.GetTempPath(), "eql_test_voicepack");
                try { Directory.Delete(vpTmp, true); } catch { /* fresh */ }
                Directory.CreateDirectory(vpTmp);
                string fakeMsix = Path.Combine(vpTmp, "fake.Msix");
                using (var z = System.IO.Compression.ZipFile.Open(fakeMsix, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var en = z.CreateEntry("AppxManifest.xml");
                    using var w = new StreamWriter(en.Open()); w.Write("<Package/>");
                }
                string outDir = Path.Combine(vpTmp, "MicrosoftWindows.Voice.en-GB.Sonia.1");
                VoicePacks.Extract(fakeMsix, outDir);
                Check("voice packs: an MSIX unzips like a zip into the pack folder", File.Exists(Path.Combine(outDir, "AppxManifest.xml")));
                string realKey = VoicePacks.EnumeratorKey;
                VoicePacks.EnumeratorKey = @"Software\EQL_Assistant_Selftest\Enumerator";
                try
                {
                    VoicePacks.PointAdapterAt(vpTmp);
                    Check("voice packs: the adapter pointer lands in the user hive with local voices enabled",
                        VoicePacks.CurrentPath() == vpTmp);
                }
                finally
                {
                    VoicePacks.EnumeratorKey = realKey;
                    try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\EQL_Assistant_Selftest", false); } catch { /* test key */ }
                }
                try { Directory.Delete(vpTmp, true); } catch { /* temp */ }
            }
            Check("voices: an Online natural voice is flagged, an offline one is not",
                TriggerManagerWindow.IsOnlineVoice("Microsoft Jenny Online (Natural) - English (United States)")
                && !TriggerManagerWindow.IsOnlineVoice("Microsoft Jenny (Natural) - English (United States)")
                && !TriggerManagerWindow.IsOnlineVoice(""));

            // Notable quest lines (7 Sep): the Torrid Corruptor walked
                // through the log — kills, loot, hand-ins, a said keyword;
                // right-clicks are ticks; later steps imply earlier ones;
                // replay never double-counts.
                {
                    string qlPath = Path.Combine(Path.GetTempPath(), "eql_test_questlines.json");
                    try { File.Delete(qlPath); } catch { /* fresh */ }
                    var ql = new QuestLines(new ConfigService(), null, qlPath);
                    var torrid = ql.Quests.First(q => q.Key == "torrid-corruptor");
                    var hayle = torrid.Steps[0]; var reaver = torrid.Steps[1]; var grimIn = torrid.Steps[2];
                    var grimKill = torrid.Steps[3]; var dason = torrid.Steps[4]; var luxio = torrid.Steps[6];
                    Check("lines: three lines load, Torrid Corruptor has seven steps",
                        ql.Quests.Count == 3 && torrid.Steps.Count == 7 && ql.NextStep(torrid) == hayle);
                    int fired = 0;
                    ql.StepDone += (_, _) => fired++;
                    ql.ProcessLine("[Tue Sep 01 22:40:00 2026] You have slain Brother Hayle!");
                    ql.ProcessLine("[Tue Sep 01 22:40:10 2026] --You have looted Burning Soul of the Pious from Brother Hayle's corpse.--");
                    Check("lines: kill + loot proven, the right-click still waits on the tick",
                        !ql.IsDone(torrid, hayle) && ql.AwaitsClick(torrid, hayle) && fired == 0);
                    ql.Tick(torrid, hayle, new DateTime(2026, 9, 1, 22, 41, 0));
                    Check("lines: the tick finishes a click step (how = you)",
                        ql.IsDone(torrid, hayle) && ql.MarkOf(torrid, hayle)!.How == "you" && ql.NextStep(torrid) == reaver);
                    ql.ProcessLine("[Wed Sep 02 23:05:00 2026] --You have looted Dark Reaver from a ghoul cavalier's corpse.--");
                    Check("lines: a plain loot step proves itself from the log", ql.IsDone(torrid, reaver) && ql.MarkOf(torrid, reaver)!.How == "auto" && fired == 1);
                    ql.ProcessLine("[Thu Sep 03 21:13:50 2026] You offered 1 Burning Soul of the Pious to Lord Grimrot.");
                    Check("lines: an offer alone proves nothing", !ql.IsDone(torrid, grimIn));
                    ql.ProcessLine("[Thu Sep 03 21:13:55 2026] You offered 1 Dark Reaver to Lord Grimrot.");
                    ql.ProcessLine("[Thu Sep 03 21:14:00 2026] You complete the trade with Lord Grimrot.");
                    Check("lines: the completed trade seals the hand-in", ql.IsDone(torrid, grimIn) && ql.NextStep(torrid) == grimKill);
                    // Replay of the same lines: nothing changes, nothing re-fires.
                    int before = fired;
                    ql.ProcessLine("[Thu Sep 03 21:14:00 2026] You complete the trade with Lord Grimrot.");
                    ql.ProcessLine("[Wed Sep 02 23:05:00 2026] --You have looted Dark Reaver from a ghoul cavalier's corpse.--");
                    Check("lines: replay is a no-op", fired == before && ql.DoneCount(torrid) == 3);
                    // Coins-only step: the trade itself is the proof.
                    ql.ProcessLine("[Fri Sep 04 20:00:00 2026] You complete the trade with Dason Goldblade.");
                    Check("lines: a coins-only hand-in proves on the trade line of a STARTED line, and vouches for nothing before it",
                        ql.IsDone(torrid, dason) && !ql.IsDone(torrid, grimKill) && ql.NextStep(torrid) == grimKill);
                    // The keyword + the final hand-in.
                    ql.ProcessLine("[Sat Sep 05 21:00:00 2026] You say, 'I still seek guidance'");
                    Check("lines: the said keyword is a partial proof", !ql.IsDone(torrid, luxio) && ql.PartialOf(torrid, luxio).Contains("say"));
                    foreach (var it in new[] { "Burning Soul of the Pious", "Burning Soul of the Pestilent", "Burning Soul of the Virtuous", "SoulFire" })
                        ql.ProcessLine($"[Sat Sep 05 21:01:00 2026] You offered 1 {it} to Luxio Nulsis.");
                    ql.ProcessLine("[Sat Sep 05 21:01:30 2026] You complete the trade with Luxio Nulsis.");
                    Check("lines: the line completes; every step done", ql.IsComplete(torrid) && ql.NextStep(torrid) is null);
                    // Persistence round-trip.
                    var ql2 = new QuestLines(new ConfigService(), null, qlPath);
                    var torrid2 = ql2.Quests.First(q => q.Key == "torrid-corruptor");
                    Check("lines: progress survives a reload", ql2.IsComplete(torrid2) && ql2.MarkOf(torrid2, torrid2.Steps[0])!.How == "you");
                    // Untick: only the owner's marks come back off.
                    Check("lines: the log's own marks can't be unticked, the owner's can",
                        !ql2.Untick(torrid2, torrid2.Steps[1]) && ql2.Untick(torrid2, torrid2.Steps[0]) && !ql2.IsDone(torrid2, torrid2.Steps[0]));
                    // Fiery Avenger: either NPC seals the books step; "has been slain by" counts a group kill.
                    var fiery = ql.Quests.First(q => q.Key == "fiery-avenger");
                    ql.ProcessLine("[Sun Sep 06 20:00:00 2026] You offered 1 Torn, burnt book to Rineval Talyas.");
                    ql.ProcessLine("[Sun Sep 06 20:00:01 2026] You offered 1 Torn, Frost covered book to Rineval Talyas.");
                    ql.ProcessLine("[Sun Sep 06 20:00:05 2026] You complete the trade with Rineval Talyas.");
                    Check("lines: an alternative NPC seals the step", ql.IsDone(fiery, fiery.Steps[1]));
                    ql.ProcessLine("[Sun Sep 06 21:00:00 2026] Miragul has been slain by Thorrak!");
                    Check("lines: a group kill line counts", ql.PartialOf(fiery, fiery.Steps[3]).Contains("kill:miragul"));
                    // The Torrid line's Dason trade (Sep 4) came before this line had any
                    // progress — a coin hand-in never proves a quest you haven't started.
                    Check("lines: a shared coins-only NPC can't prove an unstarted line", !ql.IsDone(fiery, fiery.Steps[4]));
                    try { File.Delete(qlPath); } catch { /* temp */ }

                    // Kills and drops alone never start a line (17 Sep: Xicotl + the
                    // hilt in Hate, Dark Reavers in Guk had two lines "in progress"
                    // the owner never began). The evidence waits; a sealed hand-in
                    // starts the line and the waiting step lights up — nothing implied.
                    string qlPath2 = Path.Combine(Path.GetTempPath(), "eql_test_questlines2.json");
                    try { File.Delete(qlPath2); } catch { /* fresh */ }
                    var qi = new QuestLines(new ConfigService(), null, qlPath2);
                    var zimel = qi.Quests.First(q => q.Key == "zimels-blades");
                    var xicotl = zimel.Steps.First(s => s.Id == "xicotl");
                    qi.ProcessLine("[Sun Sep 13 22:40:00 2026] You have slain Xicotl!");
                    qi.ProcessLine("[Sun Sep 13 22:40:20 2026] --You have looted a Glowing Sword Hilt from Xicotl's corpse.--");
                    qi.ProcessLine("[Sun Sep 13 23:00:00 2026] --You have looted a Dark Reaver +2 from a ghoul cavalier's corpse.--");
                    Check("lines: a kill and a drop on an unstarted line mark nothing, the evidence waits",
                        qi.DoneCount(zimel) == 0 && !qi.Started(zimel) && qi.PartialOf(zimel, xicotl).Contains("kill:xicotl")
                        && qi.Quests.All(q => qi.DoneCount(q) == 0));
                    for (int i = 0; i < 4; i++) qi.ProcessLine("[Mon Sep 14 20:00:00 2026] You offered 1 Drom's Champagne to Tykar Renlin.");
                    qi.ProcessLine("[Mon Sep 14 20:00:05 2026] You complete the trade with Tykar Renlin.");
                    Check("lines: the sealed hand-in starts the line and the hilt already in hand counts — steps between stay open",
                        qi.Started(zimel) && qi.IsDone(zimel, zimel.Steps[0]) && qi.IsDone(zimel, xicotl) && qi.MarkOf(zimel, xicotl)!.How == "auto"
                        && qi.DoneCount(zimel) == 2 && qi.NextStep(zimel) == zimel.Steps[1]);
                    // Old progress is re-judged on load: the chain a Hate evening implied is withdrawn.
                    string bad = Path.Combine(Path.GetTempPath(), "eql_test_questlines_bad.json");
                    File.WriteAllText(bad, "{\"Steps\":{"
                        + "\"zimels-blades/xicotl\":{\"When\":\"2026-09-13T22:40:00\",\"How\":\"auto\",\"Evidence\":\"You have slain Xicotl!\"},"
                        + "\"zimels-blades/champagne\":{\"When\":\"2026-09-13T22:40:00\",\"How\":\"implied\",\"Evidence\":\"implied by step 8: Kill Xicotl for the Glowing Sword Hilt\"},"
                        + "\"zimels-blades/prisoner\":{\"When\":\"2026-09-13T22:40:00\",\"How\":\"implied\",\"Evidence\":\"implied by step 8: Kill Xicotl for the Glowing Sword Hilt\"}}}");
                    var qs = new QuestLines(new ConfigService(), null, bad);
                    var zimelS = qs.Quests.First(q => q.Key == "zimels-blades");
                    Check("lines: on load, marks a kill-and-drop step implied are withdrawn, and so is that step on a never-started line",
                        qs.DoneCount(zimelS) == 0 && !qs.Started(zimelS));
                    try { File.Delete(qlPath2); File.Delete(bad); } catch { /* temp */ }

                    // Johan's night (6 Oct 2026): a second SoulFire and the Fiery Avenger, every line as the
                    // game printed it — starred quest NPCs, backtick apostrophes, no articles, "+4" tiers,
                    // "1,000 Platinum" — the tracker lost most of it. Both lines must read complete.
                    string qlPath3 = Path.Combine(Path.GetTempPath(), "eql_test_questlines3.json");
                    try { File.Delete(qlPath3); } catch { /* fresh */ }
                    var qj = new QuestLines(new ConfigService(), null, qlPath3);
                    var zb = qj.Quests.First(q => q.Key == "zimels-blades"); var fa = qj.Quests.First(q => q.Key == "fiery-avenger");
                    string[] night =
                    {
                        "[Tue Oct 06 15:42:42 2026] You have slain Xicotl!",
                        "[Tue Oct 06 15:42:44 2026] --You have looted a Glowing Sword Hilt from Xicotl's corpse.--",
                        "[Tue Oct 06 16:52:23 2026] You offered 4 Drom's Champagne to Tykar Renlin.",
                        "[Tue Oct 06 16:52:25 2026] You complete the trade with Tykar Renlin.",
                        "[Tue Oct 06 17:13:34 2026] You offered 1 Bunker Cell #1 to a prisoner.",
                        "[Tue Oct 06 17:13:36 2026] You offered 1 Edible Goo to a prisoner.",
                        "[Tue Oct 06 17:13:37 2026] You offered 1 Bog Juice to a prisoner.",
                        "[Tue Oct 06 17:13:38 2026] You complete the trade with a prisoner.",
                        "[Tue Oct 06 17:43:29 2026] You offered 1 Cloth Shirt to Altunic Jartin.",
                        "[Tue Oct 06 17:43:31 2026] You complete the trade with Altunic Jartin.",
                        "[Tue Oct 06 19:28:40 2026] --You have looted a Spider Venom Sac from a giant spider's corpse.--",
                        "[Tue Oct 06 19:33:48 2026] You offered 1 H. K. 102 to Assistant Kiolna.",
                        "[Tue Oct 06 19:33:50 2026] You complete the trade with Assistant Kiolna.",
                        "[Tue Oct 06 19:40:26 2026] You offered 1 Spider Venom Sac to Merko Quetalis.",
                        "[Tue Oct 06 19:40:28 2026] You complete the trade with Merko Quetalis.",
                        "[Tue Oct 06 19:40:37 2026] You offered 1 Token of Generosity to Merko Quetalis.",
                        "[Tue Oct 06 19:40:38 2026] You offered 1 Token of Bravery to Merko Quetalis.",
                        "[Tue Oct 06 19:40:40 2026] You complete the trade with Merko Quetalis.",
                        "[Tue Oct 06 19:40:52 2026] You have slain *Guard Willia!",
                        "[Tue Oct 06 19:40:53 2026] --You have looted a Token of Truth from *Guard Willia's corpse.--",
                        "[Tue Oct 06 19:42:13 2026] You offered 1 Token of Truth to Merko Quetalis.",
                        "[Tue Oct 06 19:42:14 2026] You complete the trade with Merko Quetalis.",
                        "[Tue Oct 06 19:49:13 2026] You have slain Sir Lucan D`Lere!",
                        "[Tue Oct 06 19:49:22 2026] You have slain Sir Lucan D`Lere!",
                        "[Tue Oct 06 19:49:25 2026] --You have looted a Testimony of Truth from Sir Lucan D`Lere's corpse.--",
                        "[Tue Oct 06 19:55:50 2026] You offered 1 Testimony of Truth to Valeron Dushire.",
                        "[Tue Oct 06 19:55:52 2026] You complete the trade with Valeron Dushire.",
                        "[Tue Oct 06 20:04:46 2026] You offered 1 Sealed Note to Brother Hayle.",
                        "[Tue Oct 06 20:04:49 2026] You complete the trade with Brother Hayle.",
                        "[Tue Oct 06 20:04:53 2026] You offered 1 Note to Brother Hayle.",
                        "[Tue Oct 06 20:04:55 2026] You offered 1 Testimony to Brother Hayle.",
                        "[Tue Oct 06 20:04:58 2026] You offered 1 Glowing Sword Hilt to Brother Hayle.",
                        "[Tue Oct 06 20:05:02 2026] You offered 1 Brilliant Sword of Faith to Brother Hayle.",
                        "[Tue Oct 06 20:05:03 2026] You complete the trade with Brother Hayle.",
                        "[Tue Oct 06 20:37:15 2026] --You have looted a Torn, Frost-Covered Book from Lady Vox's corpse.--",
                        "[Tue Oct 06 22:15:56 2026] --You have looted a Ghoulbane +4 from the froglok shin lord's corpse.--",
                        "[Tue Oct 06 22:21:29 2026] You offered 1 Torn, Frost-Covered Book to Rysva To`Biath.",
                        "[Tue Oct 06 22:21:31 2026] You offered 1 Torn, Burnt Book to Rysva To`Biath.",
                        "[Tue Oct 06 22:21:37 2026] You offered 1,000 Platinum to Rysva To`Biath.",
                        "[Tue Oct 06 22:21:43 2026] You complete the trade with Rysva To`Biath.",
                        "[Tue Oct 06 22:25:52 2026] You offered 1 Book of Scale to Oracle of K`Arnon.",
                        "[Tue Oct 06 22:25:54 2026] You complete the trade with Oracle of K`Arnon.",
                        "[Tue Oct 06 22:37:09 2026] You offered 1 Miragul's Phylactery to Lich of Miragul.",
                        "[Tue Oct 06 22:37:10 2026] You complete the trade with Lich of Miragul.",
                        "[Tue Oct 06 22:37:45 2026] You have slain Miragul pet!",
                        "[Tue Oct 06 22:44:27 2026] You have slain *Miragul!",
                        "[Tue Oct 06 22:44:33 2026] --You have looted a Miragul's Head from *Miragul's corpse.--",
                        "[Tue Oct 06 22:44:34 2026] --You have looted a Miragul's Robe +1 from *Miragul's corpse.--",
                        "[Tue Oct 06 23:07:17 2026] You offered 500 Platinum to Dason Goldblade.",
                        "[Tue Oct 06 23:07:21 2026] You complete the trade with Dason Goldblade.",
                        "[Tue Oct 06 23:08:28 2026] You offered 1 SoulFire to *Inte Akera.",
                        "[Tue Oct 06 23:08:33 2026] You complete the trade with *Inte Akera.",
                        "[Tue Oct 06 23:08:35 2026] You offered 1 Ghoulbane +4 to *Inte Akera.",
                        "[Tue Oct 06 23:08:42 2026] You complete the trade with *Inte Akera.",
                        "[Tue Oct 06 23:08:55 2026] You offered 1 Inte's First Blessing to *Inte Akera.",
                        "[Tue Oct 06 23:08:56 2026] You offered 1 Inte's Second Blessing to *Inte Akera.",
                        "[Tue Oct 06 23:08:58 2026] You offered 1 Miragul's Head to *Inte Akera.",
                        "[Tue Oct 06 23:08:59 2026] You offered 1 Miragul's Robe +1 to *Inte Akera.",
                        "[Tue Oct 06 23:09:01 2026] You complete the trade with *Inte Akera.",
                    };
                    foreach (var ln in night) qj.ProcessLine(ln);
                    QuestLines.Step S(QuestLines.Quest q, string id) => q.Steps.First(x => x.Id == id);
                    Check("lines: Johan's night — the names fold: no article, backtick as apostrophe, hyphen as space, the quest NPC's * and the +N tier",
                        QuestLines.Key("A Spider Venom Sac") == "spider venom sac" && QuestLines.Key("Torn, Frost covered book") == QuestLines.Key("Torn, Frost-Covered Book")
                        && QuestLines.Key("Ghoulbane +4") == "ghoulbane" && QuestLines.Key("Miragul's Robe +1") == QuestLines.Key("Miragul\u2019s Robe")
                        && QuestLines.Who("*Inte Akera") == "inte akera" && QuestLines.Who("Sir Lucan D`Lere") == QuestLines.Who("Sir Lucan D'Lere") && QuestLines.Who("Rysva To`Biath") == QuestLines.Who("Rysva To'Biath"));
                    Check("lines: Johan's night — the second SoulFire reads complete: the venom sac, the sealed note, *Guard Willia and Sir Lucan D`Lere all proven from the log",
                        qj.IsComplete(zb) && qj.MarkOf(zb, S(zb, "venom"))!.How == "auto" && qj.MarkOf(zb, S(zb, "note"))!.How == "auto"
                        && qj.MarkOf(zb, S(zb, "willia"))!.How == "auto" && qj.MarkOf(zb, S(zb, "lucan"))!.How == "auto" && qj.MarkOf(zb, S(zb, "xicotl"))!.How == "auto");
                    Check("lines: Johan's night — the Fiery Avenger reads complete: the books and 1,000 platinum to Rysva To`Biath, *Miragul's head and robe, both blessings to *Inte Akera",
                        qj.IsComplete(fa) && qj.MarkOf(fa, S(fa, "books"))!.How == "auto" && qj.MarkOf(fa, S(fa, "miragul"))!.How == "auto"
                        && qj.MarkOf(fa, S(fa, "blessings"))!.How == "auto" && qj.MarkOf(fa, S(fa, "avenger"))!.How == "auto");
                    try { File.Delete(qlPath3); } catch { /* temp */ }
                }

                // Destroyed copies leave the ledger (11 Sep); the snapshot caps it.
                {
                    string dPath = Path.Combine(Path.GetTempPath(), "eql_test_sky_destroy.json");
                    try { File.Delete(dPath); } catch { /* fresh */ }
                    string dLoot = Path.Combine(Path.GetTempPath(), "eql_test_sky_destroy_loot.json");
                    try { File.Delete(dLoot); } catch { /* fresh */ }
                    var dl = new LootTracker(new ConfigService(), dLoot);
                    var ds = new SkyQuests(new ConfigService(), dl, dPath);
                    var coffer = ds.Quests.SelectMany(q => q.Items).First(i => i.Name == "Golden Coffer");
                    for (int i = 0; i < 4; i++)
                        dl.ProcessLine($"[Thu Sep 10 22:0{i}:00 2026] --You have looted Golden Coffer from a windrider drake's corpse.--");
                    int before = ds.HeldCount(coffer);
                    ds.ProcessLine("[Thu Sep 10 22:10:00 2026] You successfully destroyed 3 Golden Coffer.");
                    Check("sky: destroyed copies leave the ledger", before == 4 && ds.HeldCount(coffer) == 1);
                    ds.ProcessLine("[Thu Sep 10 22:10:00 2026] You successfully destroyed 3 Golden Coffer.");
                    Check("sky: a replayed destroy line counts once", ds.HeldCount(coffer) == 1);
                    var ds2 = new SkyQuests(new ConfigService(), dl, dPath);
                    Check("sky: destroyed copies survive a reload", ds2.HeldCount(coffer) == 1);
                    // The snapshot caps the ledger: ledger 1 held, dump says 0 -> ledger's word; dump says 5 -> ledger's word.
                    var dsAll = new SkyQuests(new ConfigService(), dl, dPath);
                    foreach (var q in dsAll.Quests) dsAll.SetCompleted(q, true); // nothing needs a coffer -> all held are spare
                    int ledgerSpare = dsAll.Surplus().First(s => s.Item == "Golden Coffer").Surplus;
                    int dumpNone = dsAll.Surplus(_ => -1).First(s => s.Item == "Golden Coffer").Surplus;
                    int dumpMore = dsAll.Surplus(_ => 5).First(s => s.Item == "Golden Coffer").Surplus;
                    for (int i = 0; i < 6; i++)
                        dl.ProcessLine($"[Thu Sep 10 22:2{i}:00 2026] --You have looted Golden Coffer from a windrider drake's corpse.--");
                    int dumpFewer = dsAll.Surplus(name => name == "Golden Coffer" ? 2 : -1).First(s => s.Item == "Golden Coffer").Surplus;
                    Check("sky: the snapshot caps the spare count when it holds fewer than the ledger, never raises it",
                        ledgerSpare == 1 && dumpNone == 1 && dumpMore == 1 && dumpFewer == 2);
                    // A destroy AFTER the dump comes off the dump's count too (owner,
                    // 25 Sep: PoS clean-out, the panel stuck on a stale "×1 spare").
                    // Ledger 7 (1 + 6 looted), bags 2 at 22:30 → held 2; destroying
                    // one at 22:31 must read 1, not stay pinned at the dump's 2.
                    dsAll.SnapshotCopies = name => name == "Golden Coffer" ? 2 : -1;
                    dsAll.SnapshotAt = new DateTime(2026, 9, 10, 22, 30, 0);
                    int cappedHeld = dsAll.HeldCount(coffer);
                    dsAll.ProcessLine("[Thu Sep 10 22:31:00 2026] You successfully destroyed 1 Golden Coffer.");
                    int afterDestroy = dsAll.HeldCount(coffer);
                    var dsReload = new SkyQuests(new ConfigService(), dl, dPath)
                    { SnapshotCopies = name => name == "Golden Coffer" ? 2 : -1, SnapshotAt = new DateTime(2026, 9, 10, 22, 30, 0) };
                    Check("sky: a destroy after the dump lowers the capped count, and still does after a restart",
                        cappedHeld == 2 && afterDestroy == 1 && dsReload.HeldCount(coffer) == 1);
                    dsAll.SnapshotAt = new DateTime(2026, 9, 10, 22, 32, 0); // a fresh dump already lists the loss
                    Check("sky: a dump written after the destroy is not docked twice", dsAll.HeldCount(coffer) == 2);
                    // Housekeeping icons (25 Sep): the turn-in items carry their own icon.
                    var skyItems = dsAll.Quests.SelectMany(q => q.Items.Select(i => i.Name)).Where(n => !n.StartsWith("Wind Rune", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    int withIcon = skyItems.Count(n => Views.SkyWindow.IconFor(n) is not null);
                    Check($"sky: housekeeping rows find an icon for nearly every turn-in item ({withIcon}/{skyItems.Count})",
                        skyItems.Count > 0 && withIcon >= skyItems.Count * 0.9 && Views.SkyWindow.IconFor("Silken Strands") is not null && Views.SkyWindow.IconFor("Woven Skull Cap") is not null);
                    dsAll.SnapshotCopies = null; dsAll.SnapshotAt = null;
                    try { File.Delete(dPath); File.Delete(dLoot); } catch { /* temp */ }
                }

                string offer1 = "[Sat Aug 29 00:30:00 2026] You offered 1 Small Shield to Josin Faithbringer.";
                string offer2 = "[Sat Aug 29 00:30:01 2026] You offered 1 Wind Rune Meda to Josin Faithbringer.";
                string trade = "[Sat Aug 29 00:30:05 2026] You complete the trade with Josin Faithbringer.";
                sqAgain.ProcessLine(offer1);
                sqAgain.ProcessLine(offer2);
                // A trade can be CANCELLED (window closed, death) — offers
                // alone must neither complete the quest nor spend the rune.
                Check("sky: offers alone seal nothing — the trade can still cancel",
                    !sqAgain.IsCompleted(cleric) && sqAgain.HeldCount(clericMeda) == 1);
                sqAgain.ProcessLine(trade);
                Check("sky: the completed trade seals the turn-in",
                    sqAgain.IsCompleted(cleric) && sqAgain.HeldCount(clericMeda) == 0);

                // …and spending it on the Cleric leaves the Shaman honestly
                // short again — the rune left your bags.
                Check("sky: a turned-in rune stops counting for the OTHER quests",
                    sqAgain.HeldCount(shamanMeda) == 0 && !sqAgain.IsCompleted(shaman));

                sqAgain.ProcessLine(offer2); // catch-up replays the whole trade —
                sqAgain.ProcessLine(trade);  // committed lines must dedupe
                Check("sky: a replayed trade never double-counts",
                    sqAgain.HeldCount(clericMeda) == 0
                    && sqAgain.MissingByIsle().First(r => r.Item == "Wind Rune Meda").Missing == 5);
                // The bags win (15 Sep): a dump newer than the pickup that lacks a
                // PHYSICAL item counts it as gone; an older dump doesn't; currency
                // is never capped; the ledger's own number stays readable.
                {
                    var gorgon = sqAgain.Quests.SelectMany(q => q.Items).First(i => i.Name == "Gorgon Head");
                    int gorgonBefore = sqAgain.HeldCount(gorgon);
                    skyLoot.ProcessLine("[Mon Jan 06 12:05:00 2020] --You have looted a Gorgon Head from a selftest gorgon's corpse.--");
                    Check("sky: a kept quest item ticks the ledger", sqAgain.HeldCount(gorgon) == gorgonBefore + 1);
                    var jakaItem = sqAgain.Quests.First(q => q.Name == "Magician Test of Gesticulation").Items.First(i => i.Name == "Wind Rune Jaka");
                    int jakaHeld = sqAgain.HeldCount(jakaItem);
                    sqAgain.SnapshotCopies = _ => 0;
                    sqAgain.SnapshotAt = new DateTime(2026, 9, 15, 8, 0, 0);
                    Check("sky: a fresh dump that lacks the item counts it as gone; the ledger still says what it said; currency is untouched",
                        sqAgain.HeldCount(gorgon) == 0 && sqAgain.LedgerHeld("Gorgon Head") == gorgonBefore + 1
                        && sqAgain.DumpDecided("Gorgon Head") && sqAgain.HeldCount(jakaItem) == jakaHeld && !sqAgain.DumpDecided("Wind Rune Jaka"));
                    sqAgain.SnapshotAt = new DateTime(2019, 1, 1);
                    Check("sky: a dump older than the pickup is stale for that item — no cap",
                        sqAgain.HeldCount(gorgon) == gorgonBefore + 1 && !sqAgain.DumpDecided("Gorgon Head"));
                    sqAgain.SnapshotCopies = null; sqAgain.SnapshotAt = null;

                    // Housekeeping spreads one spare count over the lanes that hold
                    // copies (16 Sep: a belt in the bags and one in the bank read
                    // "x2 spare" in BOTH sections) — bags first, never twice.
                    var split = Views.SkyWindow.AllocateSpares(2, new[] { ("bank", 1), ("bags", 1) });
                    var one = Views.SkyWindow.AllocateSpares(1, new[] { ("bank", 1), ("bags", 1) });
                    var many = Views.SkyWindow.AllocateSpares(3, new[] { ("bank", 2), ("bags", 1) });
                    Check("housekeeping: spares are split across lanes, bags first, each lane at most its own copies",
                        split.SequenceEqual(new[] { ("bags", 1), ("bank", 1) })
                        && one.SequenceEqual(new[] { ("bags", 1) })
                        && many.SequenceEqual(new[] { ("bags", 1), ("bank", 2) })
                        && Views.SkyWindow.AllocateSpares(5, new[] { ("bank", 2), ("bags", 1) }).Sum(x => x.Spare) == 3);

                    // The audit (15 Sep): a fresh replay of a log file, the drift
                    // against the live ledger, and the realign that adopts it.
                    string auditLog = Path.Combine(Path.GetTempPath(), "eql_selftest_audit_log.txt");
                    File.WriteAllLines(auditLog, new[]
                    {
                        "[Mon Jan 06 12:05:00 2020] --You have looted a Gorgon Head from a selftest gorgon's corpse.--",
                        "[Mon Jan 06 12:06:00 2020] You offered 1 Gorgon Head to Selftest Keeper.",
                        "[Mon Jan 06 12:06:02 2020] You complete the trade with Selftest Keeper.",
                    });
                    var skyAuditRes = SkyAudit.ReplayAsync(skyCfg, new[] { auditLog }, "", null, null).GetAwaiter().GetResult();
                    Check("audit: the replay reads the file alone — looted 1, offered 1, held 0, in the report",
                        skyAuditRes.Lines == 3 && skyAuditRes.Sky.Audit("Gorgon Head") is { Looted: 1, Offered: 1, Held: 0 }
                        && skyAuditRes.Report.Contains("Gorgon Head: looted 1 · offered 1 · destroyed 0 → held 0"));
                    // A separate "live" ledger (same loot history, its own progress
                    // file) takes the realign — the shared one keeps its state for the
                    // checks that follow.
                    string liveProg = Path.Combine(Path.GetTempPath(), "eql_selftest_sky_live.json");
                    try { File.Delete(liveProg); } catch { /* fresh */ }
                    var sqLive = new SkyQuests(skyCfg, skyLoot, liveProg);
                    var drift = sqLive.DriftAgainst(skyAuditRes.Sky);
                    Check("audit: the live ledger's extra Gorgon Head shows as drift, live → log",
                        drift.Any(d => d.StartsWith("Gorgon Head: live ") && d.EndsWith(" → log 0")));
                    var gorgonQuest = sqLive.Quests.First(q => q.Items.Any(i => i.Name == "Gorgon Head"));
                    sqLive.SetTracked(gorgonQuest, true);
                    sqLive.AdoptFrom(skyAuditRes.Sky);
                    Check("audit: realigning adopts the log's ledger and keeps tracking",
                        sqLive.HeldCount(gorgon) == 0 && sqLive.DriftAgainst(skyAuditRes.Sky).Count == 0 && sqLive.IsTracked(gorgonQuest));
                    try { File.Delete(auditLog); File.Delete(liveProg); } catch { /* temp */ }
                }
                // The isle CHECKLIST carries held items at Missing 0 (with the held
                // count); the plain shopping list still hides them.
                Check("sky: a class filter may name several classes (the MINE badge)",
                    SkyQuests.ClassMatches("Shaman", "Shadow Knight|Shaman|Necromancer")
                    && !SkyQuests.ClassMatches("Wizard", "Shadow Knight|Shaman|Necromancer")
                    && SkyQuests.ClassMatches("Wizard", "")
                    && sqAgain.MissingByIsle("Cleric|Shaman").All(r =>
                        r.Quests.Any(q => q.StartsWith("Cleric") || q.StartsWith("Shaman"))));
                var checklist = sqAgain.MissingByIsle(includeHeld: true);
                Check("sky: the isle checklist keeps held items at zero missing, the shopping list hides them",
                    checklist.Count >= sqAgain.MissingByIsle().Count
                    && checklist.All(r => r.Missing == Math.Max(0, r.Needed - r.Held))
                    && sqAgain.MissingByIsle().All(r => r.Missing > 0));

                // The shopping list: Meda is spent, five quests still want one
                // each — "need 5", aggregated, filed under the random-drop isle.
                var medaNeed = sqAgain.MissingByIsle()
                    .FirstOrDefault(r => r.Item == "Wind Rune Meda");
                Check("sky: the isle list aggregates a shared rune across its open quests",
                    medaNeed is { Missing: 5, Quests.Count: 5 });

                // Housekeeping: complete every Ozah quest, then loot an Ozah —
                // nothing wants it, yet it is CURRENCY (stacks in the tab, no
                // bag space), so housekeeping never lists it (owner ruling, 3 Sep).
                foreach (var q in sqAgain.Quests.Where(q =>
                             q.Items.Any(i => i.Name == "Wind Rune Ozah")))
                    sqAgain.SetCompleted(q, true);
                skyLoot.ProcessLine("[Sat Aug 29 00:40:00 2026] You looted a Wind Rune Ozah from a selftest zephyr's corpse and stored it in your currency");
                Check("sky: a spare rune is tracked but never listed as housekeeping (currency takes no space)",
                    sqAgain.Surplus().All(s => s.Item != "Wind Rune Ozah")
                    && sqAgain.MissingByIsle().All(r => r.Item != "Wind Rune Ozah"));
                // Group headers say what the group IS (14 Sep): wind runes are the
                // any-isle random drops, the Efreeti weapons come from the named.
                var isleRows = sqAgain.MissingByIsle(includeHeld: true);
                Check("sky: wind runes file under the any-isle random-drop header, Efreeti weapons under the named bosses",
                    isleRows.First(r => r.Item == "Wind Rune Meda").Isle == SkyQuests.AnyIsleLabel
                    && isleRows.Where(r => r.Item.StartsWith("Efreeti ")).All(r => r.Isle.StartsWith(SkyQuests.NamedLabelPrefix) && r.Isle.Contains("Noble Dojorn") && !r.Isle.Contains("Unknown"))
                    && isleRows.All(r => r.Isle != "Plane of Sky")
                    && isleRows.Select(r => r.Isle).Distinct().ToList() is var order
                    && order.IndexOf(SkyQuests.AnyIsleLabel) < order.FindIndex(i => i.StartsWith(SkyQuests.NamedLabelPrefix))
                    && order.IndexOf("Island 8") < order.IndexOf(SkyQuests.AnyIsleLabel));

                var helper = new SkyHelper(sq); // never class-locked (owner ruling, 6 Sep)
                var sighted = new List<string>();
                helper.Sighted += m => sighted.Add(m);
                helper.ProcessLine("[x] Keeper of Souls glowers at you dubiously -- it appears to be quite formidable. (Lvl: 60)");
                Check("sky helper: a /con line sights the dropper, for EVERY class's quest",
                    sighted is ["Keeper of Souls"]
                    && helper.ItemsFor("Keeper of Souls") is { Count: > 1 } keeperItems
                    && keeperItems.Any(i => i is { Item: "Light Woolen Mantle", Class: "BRD", Held: 0, Need: 1 })
                    && keeperItems.Any(i => i.Class == "NEC"));
                helper.ProcessLine("[x] You slash Gorgalosk for 50 points of damage.");
                Check("sky helper: a damage line sights the dropper",
                    sighted.Count == 2 && sighted[1] == "Gorgalosk");
                helper.ProcessLine("[x] You have slain a bixie drone!");
                Check("sky helper: an unrelated mob sights nothing", sighted.Count == 2);
                sq.SetCompleted(voice, true);
                sighted.Clear();
                helper.ProcessLine("[x] Keeper of Souls hits YOU for 120 points of damage.");
                Check("sky helper: a completed quest's drop leaves the card by default",
                    helper.ItemsFor("Keeper of Souls").All(i => i.Class != "BRD"));
                helper.ShowCompleted = true;
                Check("sky helper: ...and returns, marked done, when configured to",
                    helper.ItemsFor("Keeper of Souls").Any(i => i is { Class: "BRD", QuestDone: true }));

                // The notable quest lines' droppers ride the same helper (17 Sep):
                // Xicotl carries the Glowing Sword Hilt for Zimel's Blades; the drop
                // and the destroy of a wanted item both shout, started line or not.
                string qlHelperPath = Path.Combine(Path.GetTempPath(), "eql_test_ql_helper.json");
                try { File.Delete(qlHelperPath); } catch { /* fresh */ }
                var qlh = new QuestLines(new ConfigService(), null, qlHelperPath);
                var helper2 = new SkyHelper(sq, qlh);
                var sighted2 = new List<string>();
                helper2.Sighted += m => sighted2.Add(m);
                helper2.ProcessLine("[x] Xicotl hits YOU for 88 points of damage.");
                Check("quest droppers: a quest line's kill-and-loot mob sights, and the card names the item, the line and the step",
                    sighted2 is ["Xicotl"]
                    && helper2.ItemsFor("Xicotl") is [{ Item: "Glowing Sword Hilt", Quest: "Zimel's Blades · step 8", Class: "any", Held: 0, Need: 1, QuestDone: false }]
                    && helper2.ItemsFor("Lord Grimrot").Any(i => i.Item == "Burning Soul of the Pestilent" && i.Class == "PAL/SHD"));
                var shouts = new List<string>();
                qlh.ItemLooted += (q, s, item) => shouts.Add($"loot:{item}:{q.Key}:{q.Steps.IndexOf(s) + 1}");
                qlh.ItemDestroyed += (q, s, item) => shouts.Add($"gone:{item}:{q.Key}");
                qlh.ProcessLine("[Sun Sep 13 22:40:20 2026] --You have looted a Glowing Sword Hilt from Xicotl's corpse.--");
                qlh.ProcessLine("[Sun Sep 13 22:50:00 2026] You successfully destroyed 1 Glowing Sword Hilt.");
                qlh.ProcessLine("[Sun Sep 13 22:50:00 2026] You successfully destroyed 1 Glowing Sword Hilt.");
                qlh.ProcessLine("[Sun Sep 13 22:51:00 2026] You successfully destroyed 3 Bone Chips.");
                Check("quest droppers: the drop and the destroy of a wanted item shout once each, junk stays quiet",
                    shouts.SequenceEqual(new[] { "loot:Glowing Sword Hilt:zimels-blades:8", "gone:Glowing Sword Hilt:zimels-blades" }));
                try { File.Delete(qlHelperPath); } catch { /* temp */ }

                // Tracking: ★ persists, completion un-tracks.
                sq.SetCompleted(voice, false);
                sq.SetTracked(voice, true);
                var sq2 = new SkyQuests(skyCfg, skyLoot, skyProg);
                Check("sky helper: a tracked hunt survives a relaunch",
                    sq2.IsTracked(sq2.Quests.First(q => q.Name == "Bard Test of Voice")));
                sq.SetCompleted(voice, true);
                Check("sky helper: a finished hunt un-tracks itself",
                    !sq.IsTracked(voice) && sq.TrackedQuests().Count == 0);
                try { File.Delete(skyProg); } catch { /* temp */ }

                // A malformed row is counted, never thrown on; an unknown-shaped
                // section is carried as uninterpreted rows.
                var odd = InventoryStore.Parse("Location\tName\tID\tCount\tSlots\r\nJunkRowWithoutTabs\r\n"
                    + "Hoard\tName\tMystery\t\r\nHoardSlot1\tShiny Thing\t1\t1");
                Check("inventory: malformed and unknown rows are counted, not fatal",
                    odd.MalformedCount == 1 && odd.UnknownSectionRows == 1);

                Check("inventory: log name yields char + server for the preferred dump name",
                    InventoryStore.ParseLogName(@"C:\x\Logs\eqlog_Thorrak_paineel.txt") == ("Thorrak", "paineel"));

                // ---- focus-effect audit --------------------------------------
                var focus = new FocusEffects();
                Check("focus: 24 families / 68 tiers load from the embedded table",
                    focus.Families.Count == 24
                    && focus.Families.Sum(f => f.Tiers.Count) == 68);
                // Minor Improved Damage (10% ≤20, one robe) is dropped by
                // Johan's call — a twink curiosity that broke the columns.
                Check("focus: Minor Improved Damage stays off the board",
                    focus.Families.First(f => f.Name == "Improved Damage").Tiers
                        .Select(t => t.Effect).SequenceEqual(new[]
                        {
                            "Improved Damage I", "Improved Damage II", "Improved Damage III",
                        })
                    && focus.Families.All(f => f.Name != "Minor Improved Damage"));
                var jolum = focus.Families.First(f => f.Name == "Jolum's Abatement");
                Check("focus: named tiers order by the observed level caps",
                    jolum.Tiers.Select(t => t.Effect).SequenceEqual(new[]
                    {
                        "Jolum's Minor Abatement", "Jolum's Abatement",
                        "Jolum's Major Abatement", "Jolum's Superior Abatement",
                    }));
                // The JSON's field names must actually reach the model — an
                // unmapped "tier" once deserialized as 0 everywhere and the
                // audit read "none owned" forever (0/25 in the field).
                Check("focus: tier numbers, groups and kinds survive the JSON round trip",
                    focus.Families.All(f => f.Tiers.Select(t => t.TierNum)
                        .SequenceEqual(Enumerable.Range(1, f.Tiers.Count)))
                    && focus.Families.Count(f => f.Group == "song") == 4
                    && focus.Families.Count(f => f.Group == "summoned") == 5
                    && focus.Families.All(f => f.Kind.Length > 0));
                // Burning Affliction has summoned CARRIERS but real items too —
                // only all-summoned families leave the main sections.
                Check("focus: mixed families stay spells; all-summoned families fold away",
                    focus.Families.First(f => f.Name == "Burning Affliction").Group == "spell"
                    && focus.Families.First(f => f.Name == "Jolum's Abatement").Group == "summoned");
                var realAudit = focus.Audit(new[]
                {
                    new InventoryStore.CarryRow("White Dragonscale Cloak", "white dragonscale cloak", "Back", 1, "worn", 1),
                });
                Check("focus: a real carrier joins the loaded table end to end",
                    realAudit.First(a => a.Family.Name == "Improved Damage")
                        is { BestTier: 3, Status: 2, BestPlace: "worn" });
                Check("focus: the category page's missing tier and empty page carried honestly",
                    focus.Families.First(f => f.Name == "Reanimation Efficiency").Tiers.Count == 3
                    && focus.Families.First(f => f.Name == "Improved Healing")
                        .Tiers.First(t => t.Effect == "Improved Healing II").Items.Count == 0);
                Check("focus: item join folds +N, the star, and drops the apostrophes",
                    FocusEffects.ItemKey("Kelin`s Seven Stringed Lute +3") == "kelins seven stringed lute"
                    && FocusEffects.ItemKey("Bandages*") == "bandages"
                    // The game says "Djarn's", the wiki page says "Djarns" —
                    // the audit once missed a WORN Spell Haste II over it.
                    && FocusEffects.ItemKey("Djarn's Amethyst Ring +2") == FocusEffects.ItemKey("Djarns Amethyst Ring"));
                Check("focus: EffectsOf answers per item for the socket fold-outs",
                    focus.EffectsOf("Wicked Sallet +5").Any(e => e.Tier.Effect == "Mana Preservation I")
                    && focus.EffectsOf("Wicked Sallet (Exaltation)").Any(e => e.Tier.Effect == "Mana Preservation I")
                    && focus.EffectsOf("A Perfectly Ordinary Rock").Count == 0);
                // ---- item stats (the character sheet's wiki table) ----------
                var istats = new ItemStats();
                Check("item stats: ~11k wiki items load with pairs + icon ids",
                    istats.Count > 11000
                    && istats.Lookup("Wicked Sallet +5") is { Ac: 10, Classes: "SHD", Icon: 628 } ws
                    && ws.Stats.Any(p => p is ["STR", "+3"])
                    && istats.Lookup("Djarn's Amethyst Ring +2") is { Name: "Djarns Amethyst Ring", Icon: 612 }
                    && istats.Lookup("The Baron's Blade +5") is { Dmg: 10, Delay: 30, Skill: "1H Slashing" }
                    && istats.Lookup("A Perfectly Ordinary Rock") is null);
                // The wiki's "HP Regen: 2 Mana Regen: 2 End Regen: 2" line once
                // shattered in the scrape (stray "HP", a "2 End" value) — the
                // build repairs it from the raw block: 2/2/2 base, 7/7/7 at +5.
                Check("item stats: droppers ride the record (mob + zone)",
                    istats.Lookup("Wicked Sallet +5") is { } wsd
                    && wsd.Drops.Any(d => d is ["Lord Elgnub", "Blackburrow"]));
                // The all-empty 10-slot bag the dump can't tell from sockets —
                // the wiki's Capacity flag names it a container anyway.
                Check("item stats: the wiki knows a bag when the dump cannot",
                    istats.IsContainer("Kavruul`s Mystic Pouch")
                    && !istats.IsContainer("Wicked Sallet +5"));
                Check("item stats: the three-regen line is whole (7/7/7 at +5)",
                    istats.Lookup("Talisman of Kejaar Kerrath +5") is { } tkk
                    && tkk.Stats.Any(p => p is ["HP Regen", "2"])
                    && tkk.Stats.Any(p => p is ["Mana Regen", "2"])
                    && tkk.Stats.Any(p => p is ["End Regen", "2"])
                    && tkk.Extras.Length == 0
                    && ItemUpgrade.ScaleValueText("End Regen", "2", 5) == "7"
                    && ItemUpgrade.ScaleValueText("HP Regen", "2", 5) == "7");
                // ---- the tier math (eqlwiki's own slider rules, via Companion) ----
                // Fixtures pinned by Companion's port: rounding spelling and the
                // IEEE754 weight artifact are load-bearing.
                Check("item upgrade: primary >10 rounds the increment BEFORE the add",
                    ItemUpgrade.ScalePrimary(15, 2, 3) == 19       // NOT 20 (one-step spelling)
                    && ItemUpgrade.ScalePrimary(10, 5) == 15       // ≤10: +1 per tier
                    && ItemUpgrade.ScalePrimary(0, 7) == 0         // absent stays absent
                    && ItemUpgrade.ScalePrimary(-5, 3) == -2       // penalties shrink toward 0
                    && ItemUpgrade.ScalePrimary(-5, 7) == 0);      // and never cross it
                Check("item upgrade: weapon DMG reads the fraction, flat + weight curves hold",
                    ItemUpgrade.ScaleDamage(30, 2, 3) == 38        // eff 2.75 → +floor(8.25)
                    && ItemUpgrade.ScaleFlat(36, 5) == 41          // Haste 36% +1/tier
                    && Math.Abs(ItemUpgrade.ScaleWeight(3.0, 2, 3) - 2.3) < 1e-9   // ceil, not round
                    && Math.Abs(ItemUpgrade.ScaleWeight(3.0, 10) - 0.4) < 1e-9     // the float artifact
                    && Math.Abs(ItemUpgrade.ScaleWeight(0.1, 10) - 0.1) < 1e-9);   // feather guard
                Check("item upgrade: SV VOID grant + key aliases",
                    ItemUpgrade.SynthesizesVoid(new[] { "STR", "STA" }, 3)
                    && !ItemUpgrade.SynthesizesVoid(new[] { "AC", "HP" }, 3)
                    && !ItemUpgrade.SynthesizesVoid(new[] { "STR", "STA", "SV VOID" }, 3)
                    && ItemUpgrade.NormalizeKey("Mana Regen") == "MANA_REGEN"
                    && ItemUpgrade.NormalizeKey("MANA") == "MP"
                    && ItemUpgrade.ClassOf("SV FIRE") == ItemUpgrade.StatClass.Primary
                    && ItemUpgrade.ClassOf("Haste") == ItemUpgrade.StatClass.Flat
                    && ItemUpgrade.ScaleValueText("STR", "+3", 5) == "+8"
                    && ItemUpgrade.ScaleValueText("Haste", "36%", 5) == "41%");

                var djarns = focus.Audit(new[]
                {
                    new InventoryStore.CarryRow("Djarn's Amethyst Ring +2", "djarn's amethyst ring +2", "Fingers", 1, "worn", 1),
                });
                Check("focus: the apostrophe never hides a worn focus again",
                    djarns.First(a => a.Family.Name == "Spell Haste") is { BestTier: 2, Status: 2, BestPlace: "worn" });

                var fams = new List<FocusEffects.Family>
                {
                    new()
                    {
                        Name = "Testing", Tiers = new List<FocusEffects.Tier>
                        {
                            new() { Effect = "Testing I", TierNum = 1, Items = new() { new() { Name = "Item A" } } },
                            new() { Effect = "Testing II", TierNum = 2, Items = new() { new() { Name = "Item B" } } },
                            new() { Effect = "Testing III", TierNum = 3, Items = new() { new() { Name = "Item C" } } },
                        },
                    },
                    new()
                    {
                        Name = "Empty", Tiers = new List<FocusEffects.Tier>
                        {
                            new() { Effect = "Empty I", TierNum = 1, Items = new() { new() { Name = "Item D" } } },
                        },
                    },
                };
                var mini = new FocusEffects(fams);
                // EQL delivers foci AS exaltations — the socketed copy in
                // worn gear counts, wearing its host's lane.
                var audit = mini.Audit(new[]
                {
                    new InventoryStore.CarryRow("Item A +2", "item a +2", "Head", 1, "worn", 1),
                    new InventoryStore.CarryRow("Item B", "item b", "Bank3", 1, "bank", 2),
                    new InventoryStore.CarryRow("Item C (Exaltation)", "item c (exaltation)", "Head-Slot7", 1, "worn", 3),
                });
                Check("focus: audit reads best owned tier; a worn exaltation socket counts",
                    audit[0] is { BestTier: 3, BestItem: "Item C (Exaltation)", BestPlace: "worn", Status: 2 }
                    && audit[0].OwnedTiers.SequenceEqual(new[] { true, true, true })
                    && audit[1] is { BestTier: 0, Status: 0 });
                var worn = mini.Audit(new[]
                {
                    new InventoryStore.CarryRow("Item C", "item c", "Bank1", 1, "bank", 1),
                    new InventoryStore.CarryRow("Item C", "item c", "Head", 1, "worn", 2),
                });
                Check("focus: top tier reads green and worn beats banked at the same tier",
                    worn[0] is { BestTier: 3, Status: 2, BestPlace: "worn" });
                // Green means WEARING the best — the top tier sitting in the
                // bank is an orange, not a trophy.
                var banked = mini.Audit(new[]
                {
                    new InventoryStore.CarryRow("Item C", "item c", "Bank1", 1, "bank", 1),
                });
                Check("focus: best tier in the bank reads orange, never green",
                    banked[0] is { BestTier: 3, WornTier: 0, Status: 1, BestPlace: "in bank" });

                // A summoned-only top tier can't be hunted — wearing the best
                // PERMANENT tier is green (Burning Affliction IV is only a
                // conjured Rallican bracelet).
                var capped = new FocusEffects(new List<FocusEffects.Family>
                {
                    new()
                    {
                        Name = "Capped", Tiers = new List<FocusEffects.Tier>
                        {
                            new() { Effect = "Capped I", TierNum = 1, Items = new() { new() { Name = "Item D" } } },
                            new() { Effect = "Capped II", TierNum = 2, Items = new() { new() { Name = "Item E" } } },
                            new() { Effect = "Capped III", TierNum = 3, SummonedOnly = true,
                                Items = new() { new() { Name = "Summoned: Item F" } } },
                        },
                    },
                });
                Check("focus: the green line stops at the best huntable tier",
                    capped.Audit(new[]
                    {
                        new InventoryStore.CarryRow("Item E", "item e", "Head", 1, "worn", 1),
                    })[0] is { WornTier: 2, HuntableMax: 2, Status: 2 }
                    && new FocusEffects().Families.First(f => f.Name == "Burning Affliction")
                        .Tiers.Single(t => t.Effect == "Burning Affliction IV").SummonedOnly);
            }
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

    /// <summary>The owner's two dumps, trimmed (22 Sep): three races and the factions they name.</summary>
    internal static RaceBook RaceDemo(DateTime dumpAt, string? path = null)
    {
        const string achievements = "Untapped Potential: Races\n"
            + "C\tRace Unlock - Barbarian\nC\t\tGet maximum faction with Rogues of the White Rose.\nC\t\tGet maximum faction with Wolves of the North.\nC\t\tGet maximum faction with Merchants of Halas.\nI\t\tThis achievement will autocomplete if your character was created as a Barbarian.\nI\t\tThis achievement can be bypassed using a Race Unlock Token.\n"
            + "I\tRace Unlock - High Elf\nC\t\tGet maximum faction with Clerics of Tunare.\nI\t\tGet maximum faction with Keepers of the Art.\nC\t\tGet maximum faction with Merchants of Felwithe.\nI\t\tThis achievement will autocomplete if your character was created as a High Elf.\n"
            + "I\tRace Unlock - Human (Qeynos)\nI\t\tGet maximum faction with Corrupt Qeynos Guard.\nC\t\tGet maximum faction with Guards of Qeynos.\nC\t\tGet maximum faction with Merchants of Qeynos.\nI\t\tThis achievement will autocomplete if your character was created as a Human.\n"
            + "C\tRace Unlock - Half Elf\nC\t\tThis achievement will autocomplete when you unlock Human or Wood Elf as a race.\nI\t\tThis achievement will autocomplete if your character was created as a Half Elf.\n"
            + "C\tRace Unlock - Ogre\nC\t\tGet maximum faction with Clurg.\nC\t\tGet maximum faction with Oggok Guards.\nC\t\tGet maximum faction with Merchants of Oggok.\nC\t\tThis achievement will autocomplete if your character was created as a Ogre.\n"
            + "I\tRace Unlock - Kerran\nI\t\tComplete the 'Aid the Kerrans of Kerra Isle' Task.\nI\t\tThis achievement will autocomplete if your character was created as a Kerran.\n"
            + "Untapped Potential: Classes\nI\tClass Unlock - Bard\nI\t\tGet maximum faction with League of Antonican Bards.\n";
        const string factions = "ID\tName\tStandingValue\tPointsToMax\n305\tRogues of the White Rose\t2000\t0\n320\tWolves of the North\t2000\t0\n328\tMerchants of Halas\t2000\t0\n"
            + "226\tClerics of Tunare\t2000\t0\n275\tKeepers of the Art\t-380\t2380\n325\tMerchants of Felwithe\t2000\t0\n"
            + "262\tGuards of Qeynos\t1994\t6\n291\tMerchants of Qeynos\t1970\t30\n228\tClurg\t2000\t0\n337\tOggok Guards\t2000\t0\n338\tMerchants of Oggok\t2000\t0\n";
        var book = new RaceBook(null, path);
        book.LoadDumpText(factions, achievements, dumpAt, dumpAt);
        return book;
    }

    /// <summary>A demo yield for the Efficiency tab's render and tests: real-log shapes.</summary>
    internal static SpellYield EfficiencyDemo()
    {
        var y = new SpellYield(null, null) { SelfName = "Thorrak" };
        var t0 = new DateTime(2026, 9, 24, 20, 0, 0);
        int sec = 0;
        string L(string body) => $"[{t0.AddSeconds(sec++).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
        for (int i = 0; i < 12; i++)
        {
            y.ProcessLine(L("You begin casting Boil Blood VI."));
            for (int k = 0; k < 4; k++) y.ProcessLine(L("A windrider drake has taken 81 damage from your Boil Blood."));
            y.ProcessLine(L("You begin casting Spear of Disease IX."));
            y.ProcessLine(L("You hit a windrider drake for 396 points of disease damage by Spear of Disease IX." + (i % 5 == 0 ? " (Critical)" : "")));
            y.ProcessLine(L("You begin casting Envenomed Bolt X."));
            for (int k = 0; k < 5; k++) y.ProcessLine(L("A windrider drake has taken 470 damage from your Envenomed Bolt."));
            y.ProcessLine(L("You begin casting Superior Healing IV."));
            y.ProcessLine(L("You healed Thorrak for 640 (702) hit points by Superior Healing IV."));
        }
        return y;
    }

    /// <summary>A synthetic stretch for the Invocations tab (render + tests): a short
    /// cheap fight, a long spam, an Over Channel stretch with fewer resists.</summary>
    internal static List<string> InvocationDemo(DateTime t0)
    {
        var lines = new List<string>();
        string L(double sec, string body) => $"[{t0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
        lines.Add(L(-120, "You begin reciting the recovery invocation."));
        // Fight 1 — short: 6 bolts in a minute.
        for (int i = 0; i < 6; i++)
        {
            lines.Add(L(i * 10, "You begin casting Envenomed Bolt X."));
            lines.Add(L(i * 10 + 3, "A gnoll has taken 470 damage from your Envenomed Bolt."));
            lines.Add(L(i * 10 + 4, "A gnoll hits YOU for 40 points of damage."));
        }
        // Fight 2 — long: a 5-minute spam, a cast every 5 s, one interrupted.
        for (int i = 0; i < 60; i++)
        {
            double t = 300 + i * 5;
            lines.Add(L(t, i % 2 == 0 ? "You begin casting Drain Soul VI." : "You begin casting Envenomed Bolt X."));
            if (i == 7) { lines.Add(L(t + 1, "Your Drain Soul spell is interrupted.")); continue; }
            lines.Add(L(t + 3, "You hit a windrider drake for 380 points of magic damage by Drain Soul VI."));
            lines.Add(L(t + 4, "A windrider drake hits YOU for 120 points of damage."));
            if (i % 12 == 5) lines.Add(L(t + 3, "A windrider drake resisted your Envenomed Bolt!"));
        }
        // Fight 3 — Over Channel: fewer resists.
        lines.Add(L(700, "You begin reciting the overchannel invocation."));
        for (int i = 0; i < 30; i++)
        {
            double t = 720 + i * 5;
            lines.Add(L(t, "You begin casting Envenomed Bolt X."));
            lines.Add(L(t + 3, "A sphinx has taken 470 damage from your Envenomed Bolt."));
            lines.Add(L(t + 4, "A sphinx hits YOU for 60 points of damage."));
            if (i % 30 == 7) lines.Add(L(t + 3, "A sphinx resisted your Envenomed Bolt!"));
        }
        return lines;
    }

    /// <summary>Invocations (29 Sep): the rules per combo, the stretch reader, the replay, the log's tail.</summary>
    private static void InvocationChecks(Action<string, bool> Check)
    {
        var sk = InvocationPlanner.RulesFor(new[] { "SHD", "SHM", "ENC" });
        var wiz = InvocationPlanner.RulesFor(new[] { "WIZ", "ENC", "NEC" });
        Check("inv: the rules follow the combo — SHD/SHM/ENC 20% faster / −10%, Empower +30%, resist −180; WIZ/ENC/NEC 40% / −20%, +40%, −195",
            sk is { IntCount: 1, PureCount: 2, AmSpeed: 0.2, AmSave: 0.1, OcAdjust: 180 } && Math.Abs(sk.EmpDamage - 0.3) < 1e-9
            && Math.Abs(wiz.AmSpeed - 0.4) < 1e-9 && Math.Abs(wiz.AmSave - 0.2) < 1e-9 && Math.Abs(wiz.EmpDamage - 0.4) < 1e-9 && wiz.OcAdjust == 195);
        Check("inv: who recites what — a SHD alone has no Empower, a WAR none at all, SHD/SHM/ENC all five",
            !InvocationPlanner.Available(new[] { "SHD" }).Contains("Empower") && InvocationPlanner.Available(new[] { "SHD" }).Contains("Arcane Mastery")
            && InvocationPlanner.Available(new[] { "WAR" }).Count == 0 && InvocationPlanner.Available(new[] { "SHD", "SHM", "ENC" }).Count == 5);

        var lib = new SpellLibrary(new ConfigService());
        var t0 = new DateTime(2026, 9, 21, 22, 30, 0);
        var lines = InvocationDemo(t0);
        var st = InvocationPlanner.Parse(lines, t0.AddMinutes(-1), t0.AddMinutes(20), lib);
        Check("inv: the stretch — 96 casts (one interrupted), three fights, the damage, resists by the invocation that was up",
            st.Casts.Count == 96 && st.Casts.Count(c => !c.Landed) == 1 && st.Fights.Count == 3
            && st.Damage.Sum(d => d.Amount) == 6 * 470 + 59 * 380 + 30 * 470
            && st.Casts.First().Rank == 10 && st.Casts.First().Invocation == "Recovery"
            && st.Casts.Last().Invocation == "Over Channel" && st.ResistsBy["Recovery"].Resists == 5 && st.ResistsBy["Over Channel"].Resists == 1
            && lib.FindByName("Odium")?.Mana > 0);
        // Resists (30 Sep, rig log): per mob a damage cast reached; songs and snares stay out.
        {
            string L(double sec, string body) => $"[{t0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
            var rl = new List<string> { L(-60, "You begin reciting the recovery invocation.") };
            rl.Add(L(0, "You begin casting Frost Storm."));
            rl.Add(L(3, "You hit a zol ghoul knight for 522 points of cold damage by Frost Storm."));
            rl.Add(L(3, "You hit a zol ghoul knight for 522 points of cold damage by Frost Storm."));
            rl.Add(L(3, "You hit a dar ghoul knight for 522 points of cold damage by Frost Storm."));
            rl.Add(L(3, "A wan ghoul knight resisted your Frost Storm!"));
            rl.Add(L(4, "You begin singing Largo's Melodic Binding."));
            for (int i = 0; i < 6; i++) rl.Add(L(5 + i * 6, "A zol ghoul knight resisted your Largo's Melodic Binding!"));
            rl.Add(L(40, "A dar ghoul knight resisted your Frost Storm!")); // no cast of it this close: not a try
            var rs = InvocationPlanner.Parse(rl, t0.AddMinutes(-1), t0.AddMinutes(5), lib);
            Check("inv: resists count per mob a damage cast reached — an AE's four lines are four tries, one resisted; song pulses stay out",
                rs.ResistsBy.GetValueOrDefault("Recovery") == (4, 1));
        }
        var poor = InvocationPlanner.Replay(st, lib, new[] { "SHD", "SHM", "ENC" }, 0, 2600);
        var rich = InvocationPlanner.Replay(st, lib, new[] { "SHD", "SHM", "ENC" }, 400, 20000);
        Check("inv: the log proves a regen floor when none is typed, and uses it",
            poor.ProvenRegen > 0 && poor.RegenUsed == poor.ProvenRegen && rich.RegenUsed == 400);
        Check("inv: a deep pool affords Empower everywhere, full into every fight; the long spam on a small pool runs dry",
            rich.Fights.All(f => f.Pick == "Empower" && f.Affordable) && rich.Fights[0].ManaIn == 20000
            && !poor.Fights[1].Affordable && poor.Fights[1].ManaIn <= 2600);

        // Carry-over (30 Sep): two pulls back to back on a pool that pays for one
        // Empower fight — the second starts on what the first left; ten minutes
        // apart, rested regen fills you up again.
        {
            string L(double sec, string body) => $"[{t0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
            List<string> Pulls(double gap, bool oom)
            {
                var pl = new List<string> { L(-60, "You begin reciting the empowering invocation.") };
                foreach (double at in new[] { 0.0, 50 + gap })
                    for (int i = 0; i < 10; i++)
                    {
                        pl.Add(L(at + i * 5, "You begin casting Envenomed Bolt X."));
                        pl.Add(L(at + i * 5 + 3, "A gnoll has taken 470 damage from your Envenomed Bolt."));
                        if (oom && at > 0 && i == 8) pl.Add(L(at + i * 5 + 4, "Insufficient Mana to cast this spell!"));
                    }
                return pl;
            }
            double m = (lib.FindByBaseName("Envenomed Bolt")?.Mana ?? 0) * 0.8;
            int cpool = (int)(10 * m * 1.3);
            var chain = InvocationPlanner.Replay(InvocationPlanner.Parse(Pulls(20, oom: true), t0.AddMinutes(-1), t0.AddMinutes(20), lib),
                lib, new[] { "SHD", "SHM", "ENC" }, 1, cpool);
            var emp = chain.Ledgers.First(l => l.Name == "Empower");
            Check("inv: chained, the second pull starts on what the first left; staying in Empower runs dry, the plan does no worse than any single invocation",
                m > 0 && chain.Fights.Count == 2 && chain.Fights[0].ManaIn == cpool && chain.Fights[1].ManaIn < 0.5 * cpool
                && emp.Dry >= 1 && chain.Fights.Sum(f => f.Damage) >= chain.Ledgers.Max(l => l.Damage) - 1);
            Check("inv: as you played (Empower both) the replay runs you dry once — and so does the log's Insufficient Mana",
                chain.PlayedDry == 1 && chain.LogDry == 1 && Views.SpellLibraryPanel.PlayedCheck(chain).Contains("shows 1 fight", StringComparison.Ordinal));
            var rested = InvocationPlanner.Replay(InvocationPlanner.Parse(Pulls(600, oom: false), t0.AddMinutes(-1), t0.AddMinutes(30), lib),
                lib, new[] { "SHD", "SHM", "ENC" }, 30, cpool);
            Check("inv: ten minutes between pulls, rested regen (Recovery's, whatever is up) fills the pool — Empower both times",
                rested.Fights.Count == 2 && rested.Fights[1].ManaIn == cpool && rested.Fights.All(f => f.Pick == "Empower" && f.Affordable));
        }
        Check("inv: Inversion keeps more mana than Recovery on a spam at a low regen; Empower costs the most",
            poor.Ledgers.First(l => l.Name == "Inversion").VsRecovery > poor.Ledgers.First(l => l.Name == "Empower").VsRecovery
            && poor.Ledgers.First(l => l.Name == "Empower").Damage > poor.Ledgers.First(l => l.Name == "Recovery").Damage);
        Check("inv: Over Channel measured from your resists here (5 of 66 → 1 of 30 in the demo), else scored as no change",
            poor.OcFactor > 1 && poor.ResistNote.Contains("Over Channel", StringComparison.Ordinal)
            && InvocationPlanner.Replay(InvocationPlanner.Parse(lines, t0.AddMinutes(-1), t0.AddMinutes(9), lib), lib, new[] { "SHD", "SHM", "ENC" }, 0, 2600).OcFactor == 1);

        // The tail of a big log: exactly the lines since the cutoff (plus 20 min for the invocation up).
        string tail = Path.Combine(Path.GetTempPath(), "eql_selftest_inv_tail.txt");
        using (var w = new StreamWriter(tail))
            for (int i = 0; i < 20000; i++) w.WriteLine($"[{t0.AddSeconds(i).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] You feel a line {i}.");
        var got = InvocationPlanner.ReadSince(tail, t0.AddSeconds(19000));
        Check("inv: the log's tail reads backwards — the last 1,000 lines plus the 20 minutes before",
            got.Count == 1000 + 1200 && got[0].Contains("line 17800", StringComparison.Ordinal) && got[^1].Contains("line 19999", StringComparison.Ordinal));
        try { File.Delete(tail); } catch { /* temp */ }

        var wv = new Views.SpellLibraryWindow(lib, _ => { }, null, null, () => "SHD/SHM/ENC", () => 50, null, () => "Level 50 SHD/SHM/ENC · stated by /who at 22:31")
        { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        wv.Show();
        wv.ShowTab("invocations");
        wv.InvUseLinesForTest(lines, t0.AddMinutes(20), 0, 2600);
        Check("inv: the tab paints the replay — three fights, a pick each, the ledger for all five",
            wv.InvResultForTest is { Fights.Count: 3, Ledgers.Count: 5 } r && r.Fights.All(f => f.Pick.Length > 0));
        var box = wv.InvRegenBoxForTest!;
        box.Text = "900";
        box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Check("inv: a typed regen redraws the results at once and the box stays the same box (keeps focus)",
            wv.InvResultForTest is { RegenUsed: 900 } && ReferenceEquals(box, wv.InvRegenBoxForTest));
        wv.Close();
    }

    /// <summary>The Tools window on demo data (render + tests): races, a Brewing
    /// value, a resist book with one notable mob, no inventory dump.</summary>
    internal static Views.ToolsWindow ToolsDemo(out bool[] tsShown)
    {
        var lib = new SpellLibrary(new ConfigService());
        var tsData = new TradeskillData();
        string tsPath = Path.Combine(Path.GetTempPath(), "eql_selftest_tools_ts.json");
        try { File.Delete(tsPath); } catch { /* fresh */ }
        var ts = new TradeskillWatch(tsData, null, tsPath);
        ts.SetValue("Brewing", 87);
        string rbPath = Path.Combine(Path.GetTempPath(), "eql_selftest_tools_resists.json");
        try { File.Delete(rbPath); } catch { /* fresh */ }
        // The resist book learns from the parser's own lines (as live).
        var rp = new CombatParser { SelfName = "Thorrak" };
        var resists = new ResistBook(new ConfigService(), rp, rbPath);
        for (int i = 0; i < 6; i++)
            rp.ProcessLine(i < 5
                ? $"[Tue Sep 29 20:00:{i * 5:00} 2026] A greater sphinx resisted your Envenomed Bolt!"
                : "[Tue Sep 29 20:00:30 2026] Thorrak hit a greater sphinx for 410 points of poison damage by Envenomed Bolt.");
        var shown = new[] { false };
        tsShown = shown;
        string skill = "Brewing";
        var races = RaceDemo(DateTime.Now.AddHours(-2));
        races.SetTracked("High Elf", true);
        // A dump shaped like Thorrak's (17 Aug): worn items with focus sockets,
        // exaltations socketed and loose on the key ring, wrist items for five classes.
        // A real dump in env EQL_TOOLS_DUMP stands in for the demo one (renders against your own gear).
        string dumpText = Environment.GetEnvironmentVariable("EQL_TOOLS_DUMP") is { Length: > 0 } dumpFile && File.Exists(dumpFile) ? File.ReadAllText(dumpFile) : ToolsDemoDump();
        var demoDump = InventoryStore.Parse(dumpText);
        var dumpRows = InventoryStore.CarryAll(demoDump).Rows;
        string prefsPath = Path.Combine(Path.GetTempPath(), "eql_selftest_tools_prefs.json");
        try { File.Delete(prefsPath); } catch { /* fresh */ }
        var prefs = new ToolPrefs(prefsPath);
        prefs.Set("slot:demo_paineel", "WRIST");
        // Statistics on a synthetic fortnight (or a real log in env EQL_STATS_LOG).
        string statsPath = Path.Combine(Path.GetTempPath(), "eql_selftest_tools_stats.json");
        try { File.Delete(statsPath); } catch { /* fresh */ }
        var stats = new Statistics(statsPath) { SelfName = "Thorrak" };
        if (Environment.GetEnvironmentVariable("EQL_STATS_LOG") is { Length: > 0 } statsLog && File.Exists(statsLog))
            foreach (var l in File.ReadLines(statsLog)) stats.ProcessLine(l);
        else foreach (var l in StatsDemoLines()) stats.ProcessLine(l);
        var raidsDemo = new RaidKills(new ConfigService(), Path.Combine(Path.GetTempPath(), "eql_selftest_tools_raidkills.json"));
        // Charmed pets and Focus effects (moved from the Character window, 3 Oct): a small
        // ledger, and the demo dump written where the audit board's panel looks for it.
        var charmsDemo = new CharmBook(null, null);
        {
            var t = DateTime.Now;
            charmsDemo.Add(new CharmBook.Episode("a wan ghoul knight", "Beguile", "The Plane of Hate", t.AddMinutes(-52), t.AddMinutes(-36), "broke", 9473, 41, 612, 4, 48, "Beguile", 3400, 50));
            charmsDemo.Add(new CharmBook.Episode("a wan ghoul knight", "Beguile", "The Plane of Hate", t.AddMinutes(-30), t.AddMinutes(-24), "died", 2210, 12, 380, 1, 48, "Beguile", 5100, 50));
            charmsDemo.Add(new CharmBook.Episode("a greater ice bones", "Beguile Undead", "Permafrost Caverns", t.AddDays(-3), t.AddDays(-3).AddSeconds(6), "broke", 40, 1, 40, 1, 44, "Beguile Undead", 0, 47));
            charmsDemo.AddAttempt(new CharmBook.Attempt("a greater ice bones", "Beguile Undead", "resisted", t.AddDays(-3).AddMinutes(-1), "Permafrost Caverns"));
        }
        string invDir = Path.Combine(Path.GetTempPath(), "eql_selftest_tools_inv");
        Directory.CreateDirectory(invDir);
        File.WriteAllText(Path.Combine(invDir, "Demo_paineel-Inventory.txt"), dumpText);
        return new Views.ToolsWindow(new Views.ToolsWindow.Context
        {
            Stats = stats,
            Raids = raidsDemo,
            Charms = charmsDemo,
            Character = () => (invDir, "Demo", "paineel"),
            Dump = () => (dumpRows, new DateTime(2026, 8, 17, 21, 36, 0), demoDump),
            CharKey = () => "demo_paineel",
            ToolPrefsPath = prefsPath,
            Library = lib,
            Yield = EfficiencyDemo(),
            Classes = () => "SHD/SHM/ENC",
            Level = () => 44,
            Snapshot = () => "Level 44 SHD/SHM/ENC · stated by /who at 22:31",
            Races = races,
            Resists = resists,
            TsData = tsData,
            Ts = ts,
            TsVisible = () => shown[0],
            TsSkill = () => skill,
            ToggleTs = () => shown[0] = !shown[0],
            PickTs = sk => skill = sk,
        });
    }

    /// <summary>A fortnight of play for the Statistics demo: two zones, kills, casts,
    /// loot, a death, a ding, a raid boss, a 5-day-old day and a 20-day-old day.</summary>
    internal static List<string> StatsDemoLines()
    {
        var lines = new List<string>();
        string L(DateTime t, string body) => $"[{t.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
        foreach (int back in new[] { 20, 5, 0 })
        {
            var t0 = DateTime.Today.AddDays(-back).AddHours(20);
            lines.Add(L(t0, "You have entered The Ruins of Old Guk 1 (Awakened)."));
            for (int i = 0; i < 30; i++)
            {
                var t = t0.AddSeconds(i * 40);
                lines.Add(L(t, "You begin casting Envenomed Bolt X."));
                lines.Add(L(t.AddSeconds(3), $"You hit a zol ghoul knight for {400 + i * 7} points of poison damage by Envenomed Bolt X.{(i % 9 == 0 ? " (Critical)" : "")}"));
                lines.Add(L(t.AddSeconds(5), "You slash a zol ghoul knight for 60 points of damage."));
                if (i % 3 == 0) lines.Add(L(t.AddSeconds(8), "You have slain a zol ghoul knight!"));
                if (i % 5 == 0) lines.Add(L(t.AddSeconds(9), i % 15 == 0 ? "You looted a Mote of Minor Potential from a zol ghoul knight's corpse and stored it in your currency" : "--You have looted a Undead Froglok Tongue from a zol ghoul knight's corpse.--"));
                if (i % 10 == 1) lines.Add(L(t.AddSeconds(10), "A zol ghoul knight resisted your Envenomed Bolt!"));
            }
            lines.Add(L(t0.AddMinutes(21), "You have been slain by a zol ghoul knight!"));
            lines.Add(L(t0.AddMinutes(25), "You have entered The Plane of Fear."));
            lines.Add(L(t0.AddMinutes(26), "You begin casting Lifedraw III."));
            lines.Add(L(t0.AddMinutes(26).AddSeconds(2), "You hit Dread for 900 points of magic damage by Lifedraw III."));
            lines.Add(L(t0.AddMinutes(27), "Dread has been slain by Genantik!"));
            lines.Add(L(t0.AddMinutes(28), "You have gained a level! Welcome to level 45!"));
            lines.Add(L(t0.AddMinutes(29), "You say, 'Hail, Genantik'"));
            lines.Add(L(t0.AddMinutes(30), "You have become better at Specialize Alteration! (120)"));
        }
        return lines;
    }

    /// <summary>The tab-separated inventory dump behind the Tools demo (real item names, so the wiki table knows them).</summary>
    internal static string ToolsDemoDump() => string.Join("\r\n", new[]
    {
        "Location\tName\tID\tCount\tSlots",
        "Head\tWicked Sallet +5\t177814\t1\t10",
        "Head-Slot7\tWicked Sallet (Exaltation)\t177814\t1\t10",
        "Face\tPolished Mithril Mask +1\t1\t1\t10",
        "Face-Slot7\tPolished Mithril Mask (Exaltation)\t1\t1\t10",
        "Neck\tTalisman of Kejaar Kerrath +5\t2\t1\t10",
        "Neck-Slot7\tWhite Gold Necklace (Exaltation)\t3\t1\t10",
        "Shoulders\tPauldrons of Power +1\t4\t1\t10",
        "Shoulders-Slot7\tEmpty\t0\t0\t0",
        "Wrist\tPristine Studded Leather Bracer +5\t5\t1\t10",
        "Wrist-Slot7\tSerpentine Bracer (Exaltation)\t6\t1\t10",
        "Wrist\tLustrous Russet Bracer +1\t7\t1\t10",
        "Wrist-Slot7\tRuned Mithril Bracer (Exaltation)\t8\t1\t10",
        "Secondary\tNisch Mas Ilkvel +4\t9\t1\t10",
        "Secondary-Slot7\tNisch Mas Ilkvel (Exaltation)\t9\t1\t10",
        "Feet\tLustrous Russet Boots +1\t10\t1\t10",
        "Feet-Slot7\tEmpty\t0\t0\t0",
        "Chest\tPristine Studded Leather Tunic +6\t11\t1\t10",
        "Chest-Slot7\tEmpty\t0\t0\t0",
        "Primary\tThe Baron's Blade +5\t12\t1\t10",
        "Primary-Slot7\tEmpty\t0\t0\t0",
        "Hands\tSlime Blood of Cazic-Thule +7\t19\t1\t10",
        "Hands-Slot7\tEmpty\t0\t0\t0",
        "Any Slot\tTalisman of Kejaar Kerrath +5\t2\t1\t10",
        "Any Slot-Slot7\tEmpty\t0\t0\t0",
        "General 1\tBackpack\t13\t1\t8",
        "General 1-Slot1\tRing of Pureblood +2\t14\t1\t10",
        "Bank1\tInsidious Manacle +2\t15\t1\t10",
        "Bank2\tVermiculated Bracelet +1\t16\t1\t10",
        "Bank3\tIndicolite Bracer +3\t17\t1\t10",
        "Bank4\tGilded Cloth +3\t18\t1\t10",
        "Bank4-Slot7\tGilded Cloth (Exaltation)\t18\t1\t10",
        "Bank5\tKelin`s Seven Stringed Lute +2\t11573\t1\t10",
        "Bank5-Slot7\tKelin`s Seven Stringed Lute (Exaltation)\t11573\t1\t10",
        "Held\tEmpty\t0\t0\t0",
        "",
        "KeyRing\tName\tID\t",
        "Augmentation\tGreen Silken Drape (Exaltation)\t1412",
        "Augmentation\tGolden Efreeti Boots (Exaltation)\t4407",
        "Augmentation\tDamask Robe (Exaltation)\t1334",
        "Augmentation\tEmissary Mask (Exaltation)\t177834",
        "Augmentation\tElven Charm Necklace (Exaltation)\t10701",
        "Equipment\tBoots of the Long Road +1\t177708",
    });

    /// <summary>The Tools window (29 Sep): every page builds, the home says something per tool.</summary>
    private static void ToolsChecks(Action<string, bool> Check)
    {
        var tw = ToolsDemo(out var shown);
        tw.Left = -9000; tw.Top = -9000; tw.ShowActivated = false; tw.ShowInTaskbar = false;
        tw.Show();
        Check("tools: home opens first, with a live line for each of the ten tools",
            tw.PageShown == "home" && tw.HomeLines.Count == 10 && tw.HomeLines.All(l => l.Length > 0)
            && tw.HomeLines[0].StartsWith("Best per mana at 41–50", StringComparison.Ordinal)
            && tw.HomeLines[3].StartsWith("Wrist is the fullest: 5 items, 3 your combo can wear", StringComparison.Ordinal)
            && tw.HomeLines[4].StartsWith("2 moves would socket 3 of 4 needs", StringComparison.Ordinal)
            && tw.HomeLines[5].Contains("★ High Elf", StringComparison.Ordinal)
            && tw.HomeLines[6].Contains("focus families worn at their best", StringComparison.Ordinal)
            && tw.HomeLines[7] == "3 charms on 2 mobs · last: a wan ghoul knight (Beguile)"
            && tw.HomeLines[8].Contains("sphinx", StringComparison.Ordinal)
            // Statistics sits last on the rail and the home grid (4 Oct).
            && tw.HomeLines[9].StartsWith("30 kills · 93 casts", StringComparison.Ordinal) && tw.HomeLines[9].Contains("most killed: a zol ghoul knight", StringComparison.Ordinal)
            && Views.ToolsWindow.Pages[^1].Id == "stats" && tw.HasPinForTest);
        // Focus effects and Charmed pets (3 Oct): the Character window's audit board hosted on its own, the charm ledger as a page.
        tw.ShowPage("charms");
        Check("tools: Charmed pets lists the ledger's mobs (moved from the Character window)", tw.CharmsForTest is { RowCount: 2 } && tw.PageShown == "charms");
        tw.ShowPage("fx");
        tw.UpdateLayout();
        Check("tools: Focus effects hosts the audit board on the demo dump — rows on the board, no header, no tab row",
            tw.FocusEffectsForTest is { } fxp && fxp.FocusRowsForTest > 0 && fxp.HeaderRow.Visibility == Visibility.Collapsed && fxp.TabPanel.Visibility == Visibility.Collapsed);

        // Statistics (1 Oct): counted per day, deduped per minute, folded zones, the ranges.
        {
            string sp2 = Path.Combine(Path.GetTempPath(), "eql_selftest_stats2.json");
            try { File.Delete(sp2); } catch { /* fresh */ }
            var st2 = new Statistics(sp2) { SelfName = "Thorrak" };
            var demo = StatsDemoLines();
            foreach (var l in demo) st2.ProcessLine(l);
            var all = st2.Summarize(null);
            Check("stats: three days of play — 30 kills, 93 casts, 18 loot (kept + currency), 3 deaths, 3 dings, the zone folded, Dread credited to a groupmate",
                all.DaysWithPlay == 3 && all.Kills == 30 && all.Casts == 93 && all.Loot == 18 && all.Deaths == 3 && all.Dings == 3
                && all.TopKills[0].Name == "a zol ghoul knight" && all.TopKills[0].Note == "The Ruins of Old Guk" && all.TopZones[0].Name == "The Ruins of Old Guk" && all.TopLoot[0].Name == "Undead Froglok Tongue" && all.TopLoot[0].N == 12
                && all.AllKills.GetValueOrDefault("Dread") == 3 && all.Resisted == 9 && all.Hails == 3 && all.TopSkills[0].Name == "Specialize Alteration"
                && all.BigHit is { Value: 900, What: "Lifedraw III", Target: "Dread" } && all.Crits == 12 && all.Hits == 183);
            foreach (var l in demo) st2.ProcessLine(l); // the same lines again: a reparse, a merged copy
            var again = st2.Summarize(null);
            Check("stats: the same lines fed again count nothing twice (the minute bitmap)", again.Kills == 30 && again.Casts == 93 && again.Deaths == 3);
            st2.Save();
            var reloaded = new Statistics(sp2) { SelfName = "Thorrak" };
            foreach (var l in demo) reloaded.ProcessLine(l);
            Check("stats: after a reload the bitmap still holds — a catch-up of known minutes adds nothing", reloaded.Summarize(null).Kills == 30 && reloaded.Days.Count == 3);
            Check("stats: the ranges — 7 days holds two of the three days, today one, 30 days all",
                st2.Summarize(DateTime.Today.AddDays(-6)).DaysWithPlay == 2 && st2.Summarize(DateTime.Today).DaysWithPlay == 1 && st2.Summarize(DateTime.Today.AddDays(-29)).DaysWithPlay == 3
                && Statistics.FoldZone("The Ruins of Old Guk 1 (Awakened)") == "The Ruins of Old Guk" && Statistics.FoldZone("Befallen 3 (Fused)") == "Befallen" && Statistics.FoldZone("The Plane of Fear") == "The Plane of Fear");
            tw.ShowPage("stats");
            var sv = tw.StatsForTest!;
            Check("stats: the page paints six tiles and ten cards, with Dread under raid targets",
                sv.TileLines.Count == 6 && sv.CardCountForTest == 10 && sv.TileLines[1].StartsWith("KILLS: 30", StringComparison.Ordinal) && sv.SummaryForTest!.AllKills.ContainsKey("Dread"));
        }

        // The slot finder (30 Sep): Wrist, worn + bank, three combos' worth of bracers.
        tw.ShowPage("slots");
        var sf = tw.SlotsForTest!;
        Check("slot finder: Wrist remembered; 3 wearable by SHD/SHM/ENC (2 worn + the Manacle), 2 for other classes folded away; socketed exaltations stay out",
            sf.SlotShown == "WRIST" && sf.GroupCountsForTest[SlotFinder.Fit.You] == 3 && sf.GroupCountsForTest[SlotFinder.Fit.Neither] == 2
            && !sf.RowNamesForTest.Any(n => n.Contains("Serpentine")) && sf.RowNamesForTest.Count == 3);
        sf.YouPickerForTest.ClickForTest("DRU"); // SHD/SHM/ENC + DRU → SHM/ENC/DRU: the druid bracelet joins your side
        Check("slot finder: a pick in the picker redraws the board and is remembered for the days /who hasn't said",
            sf.YouPickerForTest.Combo.SequenceEqual(new[] { "SHM", "ENC", "DRU" }) && sf.GroupCountsForTest[SlotFinder.Fit.You] == 4
            && new ToolPrefs(Path.Combine(Path.GetTempPath(), "eql_selftest_tools_prefs.json")).Get("you:demo_paineel") == "SHM/ENC/DRU");
        // Two +0 copies (2 Oct): no +N pill, and the "2 copies" note still has a place to sit.
        {
            string T = ((char)9).ToString(), NL = ((char)13).ToString() + (char)10; // no escapes: the heredoc ate them once
            string twoDump = string.Join(NL, new[] { "Location" + T + "Name" + T + "ID" + T + "Count" + T + "Slots", "Bank1" + T + "Serpentine Bracer" + T + "6" + T + "1" + T + "10", "Bank2" + T + "Serpentine Bracer" + T + "6" + T + "1" + T + "10", "Held" + T + "Empty" + T + "0" + T + "0" + T + "0" });
            var sv2 = new Views.SlotFinderView();
            sv2.Init(new ItemStats(), null, "two");
            string err = "";
            try { sv2.Update(InventoryStore.CarryAll(InventoryStore.Parse(twoDump)).Rows, "SHD/SHM/ENC", "now"); } catch (Exception ex) { err = ex.Message; }
            Check("slot finder: two +0 copies of one item draw as one row with a copies note, no pill, no crash",
                err.Length == 0 && sv2.RowNamesForTest.Count == 1 && sv2.GroupCountsForTest[SlotFinder.Fit.You] == 2);
        }
        // Storage (1 Oct): the key ring's Equipment rows are items you hold — the BiS finder searches them like the slot finder does.
        var bisAll = BisFinder.Build(InventoryStore.CarryAll(InventoryStore.Parse(ToolsDemoDump())).Rows, new ItemStats(), new[] { "SHD", "SHM", "ENC" }, new[] { "AC", "STA", "INT" });
        Check("bis: an item in the key ring's Storage is a candidate (Boots of the Long Road +1 under Feet, lane storage), placed by its spot in the list",
            bisAll.Slots.First(s => s.Key == "FEET").Ranked.Concat(bisAll.Slots.First(s => s.Key == "FEET").Foreign).Any(c => c.Name.StartsWith("Boots of the Long Road") && c.Lane == "storage" && c.Location == "#1")
            && SlotFinder.PrettyLocation("#1", "storage") == "Storage · #1");
        // The hyphen (1 Oct): the game's "Cazic-Thule" finds the wiki's "Cazic Thule" page, and the Fear gauntlets stand under Hands.
        var allSlots = SlotFinder.Build(InventoryStore.CarryAll(InventoryStore.Parse(ToolsDemoDump())).Rows, new ItemStats());
        Check("slot finder: a hyphen in the dump's name finds the wiki's hyphen-less page — Slime Blood of Cazic-Thule stands worn under Hands",
            new ItemStats().Lookup("Slime Blood of Cazic-Thule +7") is { Slot: "HANDS", Ac: 16 }
            && allSlots.First(s => s.Key == "HANDS").Items.Any(i => i.Name.StartsWith("Slime Blood") && i.WornIn("HANDS")));
        var wristItems = SlotFinder.Build(tw.SlotsForTest is not null ? InventoryStore.CarryAll(InventoryStore.Parse(ToolsDemoDump())).Rows : new(), new ItemStats()).First(s => s.Key == "WRIST").Items;
        Check("slot finder: the worn bracer reads worn in Wrist, the bank one reads 'Bank 1', the tally of the rest names WAR",
            wristItems.First(i => i.Name.StartsWith("Pristine")).WornIn("WRIST") && !wristItems.First(i => i.Name.StartsWith("Insidious")).Worn
            && SlotFinder.PrettyLocation("Bank1", "bank") == "Bank 1" && SlotFinder.PrettyLocation("SharedBank1-Slot3", "bank") == "Shared bank 1 · slot 3"
            && SlotFinder.PrettyLocation("General 8-Slot6", "bags") == "Bag 8 · slot 6"
            && SlotFinder.Tally(wristItems.Where(i => SlotFinder.FitOf(i, new[] { "SHD", "SHM", "ENC" }, new[] { "DRU", "BRD", "WIZ" }) == SlotFinder.Fit.Neither)) is [("WAR", 1)]);

        // The focus planner (30 Sep): the exact plan on the demo dump.
        tw.ShowPage("focus");
        var fp = tw.FocusForTest!;
        fp.OpenWantsForTest(); // the marks fold to a line by default; the checks below read the open rows too
        var plan = fp.PlanForTest!;
        string PlanIn(string sock) => plan.Sockets.First(p => p.Socket.Label == sock).Plan?.Name ?? "";
        Check("focus: 12 open sockets; Face keeps Improved Damage II over Emissary Mask's decayed Healing I; Secondary keeps Mana Preservation II; Shoulders and Feet get filled — 2 moves",
            plan.Exact && plan.Sockets.Count(p => !p.Socket.Fixed) == 12 && plan.Moves == 2
            && PlanIn("Face").StartsWith("Polished Mithril Mask") && PlanIn("Secondary").StartsWith("Nisch Mas Ilkvel")
            && PlanIn("Shoulders").StartsWith("Gilded Cloth") && PlanIn("Feet").StartsWith("Golden Efreeti Boots")
            && PlanIn("Primary") == "");
        // The class rule (2 Oct): the Green Silken Drape (NEC WIZ MAG ENC) fits the Chest slot, but inside the
        // Pristine Studded Leather Tunic (no caster but SHM) the tunic would be for nobody — so Chest stays empty.
        Check("focus: an exaltation whose classes don't overlap the item's for your combo is never planned — Chest stays empty, and the pair is named",
            PlanIn("Chest") == "" && plan.ClassBlocked.Any(b => b.E.Name.StartsWith("Green Silken Drape") && b.S.Label == "Chest" && b.Left == "")
            && !plan.ClassBlocked.Any(b => b.S.Label == "Shoulders"));
        Check("focus: the move reads as a sentence — what to take out, where it is, what it goes into",
            FocusPlanner.MoveText(plan.Sockets.First(p => p.Socket.Label == "Shoulders")) == "Pull the Gilded Cloth exaltation out of Gilded Cloth +3 (Bank 4) and socket it into Pauldrons of Power +1 (Shoulders)."
            && FocusPlanner.MoveText(plan.Sockets.First(p => p.Socket.Label == "Feet")) == "Take the Golden Efreeti Boots exaltation from the key ring and socket it into Lustrous Russet Boots +1 (Feet).");
        Check("focus: the head's Mana Preservation I is shadowed by the II in Secondary; Improved Healing (a Need) goes without and Face is the conflict; Spell Haste II is worth hunting",
            fp.MoveLinesForTest.Any(l => l.StartsWith("Head: shadowed by Mana Preservation II in Secondary", StringComparison.Ordinal))
            && plan.NeedsPlaced == 3 && plan.Needs == 4 && plan.Conflicts.Any(c => c.Socket.Label == "Face" && c.Wanting.Any(e => e.Family.Name == "Improved Healing"))
            && plan.Hunts.Any(h => h.Tier.Effect == "Spell Haste II") && plan.Hunts.Any(h => h.Tier.Effect == "Improved Damage III" && h.OpenSocket.Length == 0)
            && !plan.Families.First(f => f.Family.Name == "String Resonance").Shown);
        // An Any Slot item takes only its OWN slot's exaltations (2 Oct, the game's refusal): the
        // Talisman (Neck) in Any slot takes the Elven Charm Necklace, never the Face-only Emissary Mask.
        fp.SetWantForTest("Extended Range", FocusPlanner.Want.Need);
        var anyPlan = fp.PlanForTest!;
        var anySock = anyPlan.Sockets.First(p => p.Socket.Label == "Any slot");
        Check("focus: a Neck item worn in Any Slot takes a Neck exaltation and nothing else",
            anySock.Socket.Accepts.SequenceEqual(new[] { "NECK" }) && anySock.Plan is { } ap && ap.Name.StartsWith("Elven Charm Necklace")
            && !anyPlan.Sockets.Any(p => p.Socket.Label == "Any slot" && p.Plan?.Name.StartsWith("Emissary") == true));
        fp.SetWantForTest("Extended Range", FocusPlanner.Want.Off);
        fp.SetComboForTest("DRU", "BRD", "WIZ");
        var wi = fp.PlanForTest!;
        Check("focus: with a bard, String Resonance shows (Nice by default) and Mana Preservation II still holds Secondary",
            wi.Families.First(f => f.Family.Name == "String Resonance").Shown && wi.Families.First(f => f.Family.Name == "String Resonance").Want == FocusPlanner.Want.Nice
            && wi.Sockets.First(p => p.Socket.Label == "Secondary").Plan!.Name.StartsWith("Nisch"));
        fp.SetWantForTest("String Resonance", FocusPlanner.Want.Need);
        var wi2 = fp.PlanForTest!;
        Check("focus: two Needs that both fit Secondary — Mana Preservation stays, and the lute finds the other Secondary-capable item, the 1H in Primary",
            wi2.Sockets.First(p => p.Socket.Label == "Secondary").Plan!.Name.StartsWith("Nisch")
            && wi2.Sockets.First(p => p.Socket.Label == "Primary").Plan!.Name.StartsWith("Kelin") && wi2.Moves == 3);
        fp.SetWantForTest("Mana Preservation", FocusPlanner.Want.Nice);
        var wi3 = fp.PlanForTest!;
        Check("focus: Mana Preservation marked Nice still keeps Secondary (nothing else wants it); the SHD-only sallet is foreign to DRU/BRD/WIZ, so the head stays empty",
            wi3.Sockets.First(p => p.Socket.Label == "Secondary").Plan!.Name.StartsWith("Nisch") && wi3.Sockets.First(p => p.Socket.Label == "Head").Plan is null
            && wi3.Foreign.Any(e => e.Name.StartsWith("Wicked Sallet")) && wi3.Moves == 3);
        // Johan's own case (2 Oct): Rokyl's Channelling Crystal (BRD NEC WIZ MAG ENC) in a Bladestopper
        // (WAR CLR PAL RNG SHD BRD ROG SHM) leaves the shield BRD-only — fine for a bard, useless for SHD/SHM/ENC.
        {
            string tb = ((char)9).ToString(), nl = ((char)13).ToString() + (char)10;
            var rokDump = InventoryStore.Parse("Location" + tb + "Name" + tb + "ID" + tb + "Count" + tb + "Slots" + nl
                + "Secondary" + tb + "Bladestopper +6" + tb + "1" + tb + "1" + tb + "10" + nl
                + "Secondary-Slot7" + tb + "Empty" + tb + "0" + tb + "0" + tb + "0" + nl + nl
                + "KeyRing" + tb + "Name" + tb + "ID" + tb + nl
                + "Augmentation" + tb + "Rokyls Channelling Crystal (Exaltation)" + tb + "2" + nl);
            var rokRows = InventoryStore.CarryAll(rokDump).Rows;
            var fx = new FocusEffects(); var ist = new ItemStats();
            var shd = new[] { "SHD", "SHM", "ENC" }; var brd = new[] { "DRU", "BRD", "WIZ" };
            var rokShd = FocusPlanner.Build(rokRows, fx, ist, shd, 50, FocusPlanner.DefaultWants(shd));
            var rokBrd = FocusPlanner.Build(rokRows, fx, ist, brd, 50, FocusPlanner.DefaultWants(brd));
            Check("focus: Rokyl's Crystal fits the Bladestopper's slot but not its classes for SHD/SHM/ENC — left out and named as BRD-only; with a bard it goes in",
                FocusPlanner.ClassesWith("WAR CLR PAL RNG SHD BRD ROG SHM", "BRD NEC WIZ MAG ENC") is ["BRD"]
                && !FocusPlanner.Wearable("WAR CLR PAL RNG SHD BRD ROG SHM", "BRD NEC WIZ MAG ENC", shd) && FocusPlanner.Wearable("WAR CLR PAL RNG SHD BRD ROG SHM", "BRD NEC WIZ MAG ENC", brd)
                && FocusPlanner.Wearable("ALL", "", shd) && !FocusPlanner.Wearable("NONE", "ALL", shd) && FocusPlanner.Wearable("ALL except WAR", "ALL except SHM", shd)
                && rokShd.Sockets.Single().Plan is null && rokShd.ClassBlocked is [{ Left: "BRD" } rb] && rb.E.Name.StartsWith("Rokyls") && rb.S.Item == "Bladestopper +6"
                && rokBrd.Sockets.Single().Plan is { } rp && rp.Name.StartsWith("Rokyls") && rokBrd.ClassBlocked.Count == 0 && rokBrd.Moves == 1);
        }
        tw.ShowPage("eff");
        var libA = tw.LibraryForTest;
        tw.ShowPage("inv");
        Check("tools: Spell efficiency and Invocations share one spell library panel, no tab row of its own",
            libA is not null && ReferenceEquals(libA, tw.LibraryForTest) && !libA.TabRowShown);
        tw.ShowPage("races");
        Check("tools: Race unlocks shows the races (moved from the Character window)", tw.RacesForTest is { RowCount: 6 });
        tw.ShowPage("ts");
        Check("tools: the Tradeskills page lays out the whole ladder and marks where 87 Brewing stands (Skull Ale, 31–151)",
            tw.LadderLines.Count >= 5 && tw.LadderLines.Any(l => l.StartsWith("31-151 Skull Ale", StringComparison.Ordinal) && l.EndsWith(" NOW", StringComparison.Ordinal)) && tw.LadderLines.Count(l => l.EndsWith(" NOW", StringComparison.Ordinal)) == 1);
        bool before = shown[0];
        tw.ShowPage("res");
        tw.ShowPage("bis");
        Check("tools: Resists and BiS pages build on the demo dump", tw.BisForTest is { HasBoard: true } && tw.PageShown == "bis" && shown[0] == before);
        // Wiki links (3 Oct): the name in an item line opens its eqlwiki page; the +N and the tags don't.
        {
            var linked = ItemChips.Linkify(ItemChips.Name("Kelin`s Seven Stringed Lute", 2, null), "Kelin`s Seven Stringed Lute");
            var plain = ItemChips.Linkify(ItemChips.Name("Coin Purse of Nowhere", 0, null), null);
            Check("items: the name links to eqlwiki — spaces as underscores, the rest escaped; an item the wiki doesn't know stays plain",
                ItemStats.WikiUrl("Woven Shadow Bracer") == "https://eqlwiki.com/Woven_Shadow_Bracer"
                && ItemStats.WikiUrl("Kelin`s Seven Stringed Lute") == "https://eqlwiki.com/Kelin%60s_Seven_Stringed_Lute"
                && linked.Children[1] is TextBlock lt && lt.Cursor == System.Windows.Input.Cursors.Hand && lt.ToolTip is string ltt && ltt.Contains("eqlwiki.com/Kelin")
                && plain.Children[1] is TextBlock pt && pt.Cursor is null && pt.ToolTip is null
                && tw.BisForTest!.BoardForTest.Children.OfType<Border>().Select(b => b.Child).OfType<StackPanel>().Any(sp => sp.Children.Count > 1 && sp.Children[1] is TextBlock t && t.Cursor == System.Windows.Input.Cursors.Hand));
        }
        // Sticky column titles (3 Oct): the title row is its own grid above the scroller, eight cells over eight
        // columns, and the board's first row is a slot header, not the titles.
        {
            var bv = tw.BisForTest!;
            bv.UpdateLayout();
            Check("bis: the column titles sit above the scroller — eight titles over eight mirrored columns, pinned to the board's widths",
                bv.BoardHeadForTest.Children.Count == 8 && bv.BoardHeadForTest.ColumnDefinitions.Count == 8
                && bv.BoardHeadForTest.Children.OfType<Border>().Select(b => (b.Child as TextBlock)?.Text).SequenceEqual(new[] { "ITEM", "SCORE", "AC", "STA", "INT", "OTHER", "WHERE", "CLASSES" })
                && !bv.BoardForTest.Children.OfType<Border>().Any(b => Grid.GetRow(b) == 0 && (b.Child as TextBlock)?.Text == "ITEM")
                && (bv.BoardForTest.ActualWidth < 1 || bv.BoardHeadForTest.ColumnDefinitions.All(c => c.Width.IsAbsolute))
                && bv.BoardForTest.ColumnDefinitions[1].MinWidth > 30 && bv.BoardForTest.ColumnDefinitions[0].MinWidth == 320);
        }
        // Would be BiS if upgraded (5 Oct): the demo's Insidious Manacle +2 in the bank would take Wrist at +8 —
        // the folded header says so, the verdict counts it, and the unfolded slot draws the row with its chip.
        {
            var bv = tw.BisForTest!;
            // The header's badges are Borders inside InlineUIContainers — a TextRange skips them, so look at the badge texts.
            bool named = bv.WouldBeCount == 1 && bv.BoardForTest.Children.OfType<TextBlock>().Any(t => t.Inlines.OfType<InlineUIContainer>().Any(c => ((c.Child as Border)?.Child as TextBlock)?.Text == "INSIDIOUS MANACLE WOULD BEAT IT AT +8"));
            bv.OpenForTest("WRIST2");
            bool chip = bv.BoardForTest.Children.OfType<Border>().Select(b => b.Child).OfType<StackPanel>().SelectMany(sp => sp.Children.OfType<Border>()).Any(b => (b.Child as TextBlock)?.Text == "BiS AT +8");
            bool bill = bv.BoardForTest.Children.OfType<TextBlock>().Any(t => string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) == "+2 → +8 costs 252 XP.  Motes: 6 Major to +5, then 6 Greater, 10 Superior, 16 Grand.  Duplicates: 63 more +2 copies, or 16 copies filled to +4 (3 Majors each from +0).");
            Check("bis: an owned item that would be BiS upgraded — the folded header names it with its tier, the verdict counts it, the open slot draws it with a BiS AT +N chip and the bill",
                named && chip && bill);
            // The XP economy (eqlwiki Item Upgrade System + Mote Guide, 5 Oct): tier t → t+1 costs 2^t; a mote works up to its limit.
            Check("upgrade: the climb's bill — 2^to − 2^from XP, duplicates at the current tier, the one mote kind good for every step",
                ItemUpgrade.XpBetween(0, 10) == 1023 && ItemUpgrade.XpBetween(2, 8) == 252 && ItemUpgrade.XpBetween(6, 7) == 64 && ItemUpgrade.XpBetween(0, 4) == 15 && ItemUpgrade.XpBetween(5, 5) == 0
                && ItemUpgrade.CopiesBetween(2, 8) == 63 && ItemUpgrade.CopiesBetween(0, 4) == 15 && ItemUpgrade.CopiesBetween(9, 10) == 1
                && ItemUpgrade.MotesBetween(2, 8) == ("Grand", 32) && ItemUpgrade.MotesBetween(0, 4) == ("Potential", 4) && ItemUpgrade.MotesBetween(5, 6) == ("Greater", 6) && ItemUpgrade.MotesBetween(9, 10) == ("Infinite", 52)
                && ItemUpgrade.ClimbText(9, 10) == "+9 → +10 costs 512 XP");
            // The routes (owner, 5 Oct): Majors first — they pay every step up to +5 — then the lowest mote each higher
            // step allows; duplicates as they drop, or filled to +4 with three Majors each so the common mote dodges its ceiling.
            Check("upgrade: the mote route is Majors to +5 then step by step; the duplicate route names the +4 fill trick when the climb is worth a copy",
                ItemUpgrade.RouteMotes(2, 8) == "6 Major to +5, then 6 Greater, 10 Superior, 16 Grand" && ItemUpgrade.RouteMotes(0, 4) == "3 Major"
                && ItemUpgrade.RouteMotes(0, 5) == "7 Major" && ItemUpgrade.RouteMotes(6, 8) == "10 Superior, 16 Grand" && ItemUpgrade.RouteMotes(9, 10) == "52 Infinite"
                && ItemUpgrade.RouteCopies(2, 8) == "63 more +2 copies, or 16 copies filled to +4 (3 Majors each from +0)"
                && ItemUpgrade.RouteCopies(0, 3) == "7 more +0 copies" && ItemUpgrade.RouteCopies(6, 7) == "1 more +6 copy" && ItemUpgrade.RouteCopies(0, 10) == "1023 more +0 copies, or 64 copies filled to +4 (3 Majors each from +0)");
        }
        // The ×N pills (3 Oct): a click cycles the pick's weight, the title and the score line follow, four clicks come round.
        {
            var bv = tw.BisForTest!;
            bool start = bv.WeightsForTest.SequenceEqual(new double[] { 2, 1.5, 1 }) && bv.PrioTitleForTest == "PRIORITIES · WEIGHTED 2 · 1.5 · 1";
            bv.CycleWeightForTest(0);
            bool one = bv.WeightsForTest[0] == 3 && bv.ScoreNoteForTest.StartsWith("Score = 3·p1 + 1.5·p2 + 1·p3", StringComparison.Ordinal) && bv.PrioTitleForTest == "PRIORITIES · WEIGHTED 3 · 1.5 · 1";
            bv.CycleWeightForTest(0);
            bool two = bv.WeightsForTest[0] == 1;
            bv.CycleWeightForTest(0); bv.CycleWeightForTest(0);
            Check("bis: the ×N pill beside a pick cycles its weight 2 → 3 → 1 → 1.5 → 2, and the title and score line say so",
                start && one && two && bv.WeightsForTest[0] == 2 && bv.ScoreNoteForTest.StartsWith("Score = 2·p1", StringComparison.Ordinal));
        }

        // Resists, light on open (30 Sep): 60 mobs → one page of 30 heads, the newest 5 with their tables.
        {
            string rbPath2 = Path.Combine(Path.GetTempPath(), "eql_selftest_resists_light.json");
            try { File.Delete(rbPath2); } catch { /* fresh */ }
            var rp2 = new CombatParser { SelfName = "Thorrak" }; // the book learns resists through the parser's lines
            var big = new ResistBook(new ConfigService(), rp2, rbPath2);
            var r1 = new DateTime(2026, 9, 29, 20, 0, 0);
            for (int i = 0; i < 60; i++)
            {
                string at(int sec) => r1.AddSeconds(i * 30 + sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
                rp2.ProcessLine($"[{at(0)}] You begin casting Envenomed Bolt X.");
                rp2.ProcessLine($"[{at(3)}] A test mob {i:00} resisted your Envenomed Bolt!");
            }
            var rv = new Views.ResistsView();
            rv.Init(big, () => "");
            rv.Build();
            Check("resists: 60 mobs paint one page of 30 heads, the newest 5 open with their tables; This zone is the default and falls back to all zones until one is known",
                rv.MobCountForTest == Views.ResistsView.PageSize && rv.TableCountForTest == Views.ResistsView.AutoOpen);
        }
        // Every page twice, in and out of order (owner, 29 Sep: the second Resists visit threw
        // "Specified element is already the logical child of another element").
        bool twice = true;
        try { foreach (var pg in new[] { "res", "races", "res", "eff", "races", "inv", "eff", "home", "res", "ts", "races", "fx", "charms", "fx", "home", "charms", "fx" }) tw.ShowPage(pg); }
        catch (Exception ex) { twice = false; Log.Warn("tools revisit: " + ex.Message); }
        Check("tools: every page opens again after another — the cached tools move between frames", twice && tw.RacesForTest is { RowCount: 6 });
        tw.Close();
    }

    /// <summary>BiS finder pools (28 Sep): HP and mana scored as the stat points they equal.</summary>
    private static void BisPoolChecks(Action<string, bool> Check)
    {
        var sk = BisFinder.RatesFor(new[] { "SHD", "SHM", "ENC" }, 50);
        var war60 = BisFinder.RatesFor(new[] { "WAR" }, 60);
        var wiz25 = BisFinder.RatesFor(new[] { "WIZ" }, 25);
        var war = BisFinder.RatesFor(new[] { "WAR" }, 50);
        Check("bis: pool rates from eqlwiki — SHD/SHM/ENC at 50: 1 STA = 3.8 HP (SHD), 1 INT/WIS ≈ 9.4 mana; WAR at 60 = 6 HP; a level-25 WIZ = 1 HP",
            sk is { HpPerSta: 3.8, HpClass: "SHD", ManaStats: "INT/WIS" } && Math.Abs(sk.ManaPerPoint - 9.42) < 0.01
            && war60.HpPerSta == 6.0 && wiz25.HpPerSta == 1.0 && Math.Abs(BisFinder.RatesFor(new[] { "SHD" }, 55).HpPerSta - 4.5) < 0.01);
        Check("bis: a warrior has no mana class — mana scores nothing", war is { ManaStats: "", ManaPerPoint: 0 } && war.Points("MP", 100) == 0);
        var hpItem = new Dictionary<string, int> { ["HP"] = 50 };
        var staItem = new Dictionary<string, int> { ["STA"] = 10 };
        var prio = new[] { "HP", "STA", "INT" };
        double hp = BisFinder.Score(hpItem, prio, 0, sk), sta = BisFinder.Score(staItem, prio, 0, sk);
        Check("bis: +50 HP is ~13 STA points for a SHD at 50, not 50 — it no longer buries +10 STA five to one",
            Math.Abs(hp - 2 * 50 / 3.8) < 0.01 && sta == 15 && BisFinder.Score(hpItem, prio) == 100);
        Check("bis: the other-stats tail uses the same rates",
            Math.Abs(BisFinder.Score(new Dictionary<string, int> { ["MP"] = 94 }, new[] { "AC", "", "" }, 1, sk) - 94 / 9.42) < 0.01);
    }

    /// <summary>Spell efficiency (25 Sep): the rank rules, the chained cycle, the
    /// log learner's dedupe, the rows the tab ranks.</summary>
    private static void EfficiencyChecks(Action<string, bool> Check)
    {
        var lib = new SpellLibrary(new ConfigService());
        SpellLibrary.Spell S(string n) => lib.Spells.First(x => x.Name == n);
        var flame = S("Flame Shock"); var boil = S("Boil Blood"); var spear = S("Spear of Pain"); var light = S("Light Healing");
        Check("eff: the wiki numbers ride the library — Flame Shock 175 fire for 65 mana, 2.5 s; Boil Blood 67 × 7 ticks, fire −100",
            flame is { Mana: 65, Hit: 175, Resist: "Fire", CastSec: 2.5 } && boil is { Tick: 67, Ticks: 7, Resist: "Fire", ResistMod: -100 }
            && lib.Spells.Count(SpellEfficiency.Rankable) > 300);
        var fx = SpellEfficiency.AtRank(flame, 10);
        var bx = SpellEfficiency.AtRank(boil, 10);
        Check("eff: rank X — a nuke +60 % damage for −20 % mana (2× per mana); a DoT +30 % a tick over +50 % ticks (≈2.4×)",
            Math.Abs(fx.Total - 280) < 0.01 && Math.Abs(fx.Mana - 52) < 0.01 && Math.Abs(fx.Cast - 2.0) < 0.01
            && Math.Abs(bx.Total - 67 * 1.3 * 7 * 1.5) < 0.01 && Math.Abs(bx.Total / bx.Mana / (469.0 / 136) - 2.4375) < 0.01
            && Math.Abs(SpellEfficiency.AtRank(light, 5).Total - 65 * 1.15) < 0.01 && SpellEfficiency.AtRank(flame, 0).Total == 175);
        Check("eff: chained, a 45 s reuse sinks Spear of Pain to ~7 a second; a DoT is never re-cast inside its own run",
            Math.Abs(SpellEfficiency.CycleSec(spear) - 45.5) < 0.01 && SpellEfficiency.CycleSec(boil) >= 42);

        var y = EfficiencyDemo();
        Check("eff: the learner — casts and their rank, DoT ticks, a nuke's crit, the heal as landed (640 of 702)",
            y.Of("Boil Blood") is { Casts: 12, Damage: 12 * 4 * 81, Rank: 6 } && y.Of("Spear of Disease IX") is { Casts: 12, Crits: 3, Rank: 9 }
            && y.Of("Superior Healing") is { Healing: 12 * 640, Rank: 4 } && SpellYield.RankOf("Envenomed Bolt X") == 10 && SpellYield.RankOf("Harm Touch VIII") == 8);
        y.ProcessLine("[Thu Sep 24 20:00:05 2026] You begin casting Boil Blood VI.");
        y.ProcessLine("[Thu Sep 24 19:00:00 2026] You hit a gnoll for 999 points of fire damage by Boil Blood VI.");
        Check("eff: a replayed or older line counts nothing", y.Of("Boil Blood") is { Casts: 12, Damage: 12 * 4 * 81 });
        y.ProcessLine("[Thu Sep 24 21:00:00 2026] You begin casting Boil Blood VI.");
        y.ProcessLine("[Thu Sep 24 21:00:01 2026] Your Boil Blood spell is interrupted.");
        Check("eff: an interrupted cast is taken back", y.Of("Boil Blood")!.Casts == 12);

        var dmg = SpellEfficiency.Rows(lib, y, new[] { "NEC", "SHM" }, 21, 30, healing: false, targets: 1);
        var bb = dmg.First(r => r.Spell.Name == "Boil Blood");
        var at6 = SpellEfficiency.AtRank(boil, 6);
        Check("eff: a row carries the wiki, your rank and yours — Boil Blood VI: 324 a cast of its 666 at VI",
            bb.Rank == 6 && Math.Abs(bb.RankTotal!.Value - at6.Total) < 0.01 && Math.Abs(bb.Observed!.Value - 324) < 0.01
            && Math.Abs(bb.ObservedPct!.Value - 324 / at6.Total) < 0.001 && Math.Abs(bb.ObservedPerMana!.Value - 324 / at6.Mana) < 0.001
            && !dmg.Any(r => r.Spell.Name == "Flame Shock") && dmg.All(r => r.Level is >= 21 and <= 30) && bb.Level == 28);
        var cap = SpellEfficiency.Rows(lib, y, new[] { "SHD", "SHM", "NEC" }, 1, 60, healing: false, targets: 1);
        Check("eff: nothing above the level cap (50) — Asystole reads NEC 40, never SHD 60; Torpor (SHM 60) is gone",
            cap.All(r => r.Level <= SpellEfficiency.LevelCap) && cap.First(r => r.Spell.Name == "Asystole") is { Level: 40, ClassText: "NEC 40" }
            && !SpellEfficiency.Rows(lib, y, new[] { "SHM" }, 1, 60, healing: true, targets: 1).Any(r => r.Spell.Name == "Torpor")
            && SpellEfficiency.BandFor(50) == 4 && SpellEfficiency.BandFor(28) == 2 && SpellEfficiency.BandFor(0) == 0);
        var fire = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 1, resist: "Fire");
        var aoe3 = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 3).First(r => r.Spell.Name == "Pillar of Fire");
        Check("eff: the resist filter keeps one school; 3 targets triple an AE, never a single-target nuke",
            fire.Any(r => r.Spell.Name == "Flame Shock") && fire.All(r => r.Spell.Resist == "Fire") && !fire.Any(r => r.Spell.Name == "Envenomed Bolt")
            && Math.Abs(aoe3.Total - SpellEfficiency.BaseTotal(S("Pillar of Fire")) * 3) < 0.01
            && SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 3).First(r => r.Spell.Name == "Flame Shock").Total == 175);
        var heals = SpellEfficiency.Rows(lib, y, new[] { "SHM" }, 1, 50, healing: true, targets: 1);
        Check("eff: the healing list holds heals and HoTs only, Superior Healing with yours",
            heals.Count > 5 && heals.All(r => SpellEfficiency.IsHealing(r.Spell.Effect)) && heals.First(r => r.Spell.Name == "Superior Healing").Observed == 640);

        // Sub-filters (owner, 25 Sep): DD / DoT / AE; Direct / HoT / Group.
        var all50 = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 1);
        var dd = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 1, sub: "dd");
        var dot = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 1, sub: "dot");
        var ae = SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, false, 1, sub: "ae");
        Check("eff: DD / DoT / AE split the damage list — Flame Shock a DD, Boil Blood a DoT, Pillar of Fire an AE, a ticking drain a DoT",
            dd.Any(r => r.Spell.Name == "Flame Shock") && !dd.Any(r => r.Spell.Name == "Boil Blood") && dot.Any(r => r.Spell.Name == "Boil Blood")
            && ae.Any(r => r.Spell.Name == "Pillar of Fire") && !dd.Any(r => r.Spell.Name == "Pillar of Fire")
            && dot.Any(r => r.Spell.Name == "Auspice") && dd.Count + dot.Count + ae.Count == all50.Count);
        Check("eff: Direct / HoT / Group split the heals", SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, true, 1, sub: "hot").All(r => r.Spell.Effect == "Heal over time")
            && SpellEfficiency.Rows(lib, y, Array.Empty<string>(), 1, 50, true, 1, sub: "direct").Any(r => r.Spell.Name == "Light Healing"));
        Check("eff: the loadout name reads as your classes until a /who does",
            SpellEfficiency.ClassesFromName("Enc-Shm-SK") == "ENC/SHM/SHD" && SpellEfficiency.ClassesFromName("Default") == ""
            && SpellEfficiency.ClassesFromName("wiz mage cleric") == "WIZ/MAG/CLR");

        // A fresh /who beats a remembered band (owner, 28 Sep: level 21 and still 1–20).
        string whoPath = Path.Combine(Path.GetTempPath(), "eql_selftest_library_who.json");
        try { File.Delete(whoPath); } catch { /* fresh */ }
        int lvNow = 16; string combo = "DRU/BRD/WIZ";
        var ww = new Views.SpellLibraryWindow(lib, _ => { }, null, y, () => combo, () => lvNow, whoPath, () => $"Level {lvNow} {combo} · stated by /who at 14:37")
        { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        ww.Show();
        ww.ShowTab("efficiency");
        int bandAt16 = ww.BandForTest;
        ww.Close();
        lvNow = 21;
        var ww2 = new Views.SpellLibraryWindow(lib, _ => { }, null, y, () => combo, () => lvNow, whoPath, () => $"Level {lvNow} {combo} · stated by /who at 14:37")
        { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        ww2.Show();
        Check("eff: a new level moves the remembered band (16 → 1–20, 21 → 21–30) and the USING line names the /who",
            bandAt16 == 1 && ww2.BandForTest == 2 && ww2.WhoLine.Contains("Level 21 DRU/BRD/WIZ · stated by /who at 14:37", StringComparison.Ordinal));
        combo = "SHD/SHM/ENC";
        ww2.ShowTab("efficiency");
        Check("eff: a new combo relights the class chips", ww2.EffRowsForTest.All(r => r.ClassText.Split(" · ").All(p => p.StartsWith("SHD") || p.StartsWith("SHM") || p.StartsWith("ENC"))));
        ww2.Close();
        try { File.Delete(whoPath); } catch { /* temp */ }

        string viewPath = Path.Combine(Path.GetTempPath(), "eql_selftest_library_view.json");
        try { File.Delete(viewPath); } catch { /* fresh */ }
        var wv = new Views.SpellLibraryWindow(lib, _ => { }, null, y, () => "", () => 0, viewPath) { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        wv.Show();
        wv.ShowTab("efficiency");
        Check("eff: no /who and no level opens on the top band (41–50), never the heavy All",
            wv.EffRowsForTest.Count > 0 && wv.EffRowsForTest.All(r => r.Level is >= 41 and <= 50));
        wv.EffSetForTest(healing: true);
        wv.Close();
        var wv2 = new Views.SpellLibraryWindow(lib, _ => { }, null, y, () => "", () => 0, viewPath) { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        wv2.Show();
        Check("eff: the tab remembers itself — reopens on Efficiency, Healing",
            wv2.EffRowsForTest.Count > 0 && wv2.EffRowsForTest.All(r => SpellEfficiency.IsHealing(r.Spell.Effect)));
        wv2.Close();
        try { File.Delete(viewPath); } catch { /* temp */ }

        var w = new Views.SpellLibraryWindow(lib, _ => { }, null, y, () => "SHD/SHM/ENC", () => 50) { Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        w.Show();
        w.ShowTab("efficiency");
        var painted = w.EffRowsForTest;
        Check("eff: the tab paints your combo in your level's band (41–50 at level 50), best per mana first",
            painted.Count > 5 && painted.All(r => r.Level is >= 41 and <= 50)
            && painted.Zip(painted.Skip(1)).All(p => p.First.BestPerMana >= p.Second.BestPerMana)
            && painted.Any(r => r.Spell.Name == "Envenomed Bolt" && r.Observed is not null));
        Check("eff: the verdict cards name who gets the spell and when",
            w.VerdictTexts.Count == 3 && w.VerdictTexts[0].StartsWith("MOST PER MANA  ·  " + painted.OrderByDescending(r => r.BestPerMana).First().ClassText, StringComparison.Ordinal)
            && w.VerdictTexts.Take(2).All(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"·\s+[A-Z]{3} \d+")));
        Check("eff: rank 0 reads as the base spell, never a bare 0 (owner, 28 Sep)",
            SpellEfficiency.Roman(0) == "base" && SpellEfficiency.Roman(10) == "X" && !w.VerdictTexts.Any(t => t.Contains(" at 0", StringComparison.Ordinal)));
        w.Close();
    }

    /// <summary>Race unlocks (22 Sep): the dump parsers, the live standing math, the
    /// mob → faction learning, the tracked-race gate for the helper card.</summary>
    private static void RaceChecks(Action<string, bool> Check)
    {
        var d0 = new DateTime(2026, 9, 22, 22, 14, 0);
        string L(int sec, string body) => $"[{d0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
        var book = RaceDemo(d0);
        var races = book.Views();
        Check("races: six races parse — done first, then progress; classes stay out",
            races.Count == 6 && races.Take(3).All(r => r.Done) && races[3].Name == "Human (Qeynos)" && races[4].Name == "High Elf" && races[5].Name == "Kerran"
            && !races.Any(r => r.Name == "Bard"));
        var ogre = races.First(r => r.Name == "Ogre"); var half = races.First(r => r.Name == "Half Elf"); var kerran = races.First(r => r.Name == "Kerran");
        Check("races: notes — YOU for the born race, AUTO for Half Elf, TASK for Kerran",
            ogre.Note == "YOU" && ogre.CountText == "YOU" && half.Note == "AUTO" && half.DependsOn == "Human or Wood Elf" && kerran.Task == "Aid the Kerrans of Kerra Isle" && kerran.CountText == "TASK");
        var qey = races.First(r => r.Name == "Human (Qeynos)");
        Check("races: standings from the dump — 1,994/2,000 with 6 to go, done flags from the achievement, an unknown faction reads unknown",
            qey.DoneCount == 2 && qey.Factions.First(f => f.Name == "Guards of Qeynos") is { Standing: 1994, Max: 2000, ToGo: 6, Done: true }
            && qey.Factions.First(f => f.Name == "Corrupt Qeynos Guard") is { Known: false, Standing: 0, Max: 2000 });
        var hie = races.First(r => r.Name == "High Elf");
        Check("races: a negative standing — −380 with 2,380 to max, the bar reads negative",
            hie.Factions.First(f => f.Name == "Keepers of the Art") is { Standing: -380, ToGo: 2380, Negative: true, Done: false } && Math.Abs(hie.Progress - 2.0 / 3) < 0.01);

        // The log: a line BEFORE the dump is already inside it; lines after move the standing.
        book.ProcessLine(L(-3600, "You have slain a gnoll!"), live: false);
        book.ProcessLine(L(-3600, "Your faction standing with Guards of Qeynos has been adjusted by 5."), live: false);
        Check("races: a faction line older than the dump moves nothing but teaches the mob",
            book.Standing("Guards of Qeynos") == 1994 && book.SourcesOf("Guards of Qeynos") is [{ Mob: "a gnoll", Hit: 5, Count: 1 }]);
        string hits = ""; book.FactionHit += (f, n, m) => hits += $"{f}:{n}:{m};";
        string maxed = ""; book.FactionMaxed += f => maxed += f + ";";
        book.SetTracked("Human (Qeynos)", true);
        book.ProcessLine(L(60, "You have slain a gnoll elite!"));
        book.ProcessLine(L(60, "Your faction standing with Guards of Qeynos has been adjusted by 5."));
        book.ProcessLine(L(60, "Your faction standing with Sabertooths of Blackburrow has been adjusted by -5."));
        Check("races: a live hit after the dump moves the standing, names the mob, and only the race's factions reach the tracked gate",
            book.Standing("Guards of Qeynos") == 1999 && hits.StartsWith("Guards of Qeynos:5:a gnoll elite;") && book.TrackedRaceOf("Guards of Qeynos") == "Human (Qeynos)"
            && book.TrackedRaceOf("Sabertooths of Blackburrow") is null && book.TrackedRaceOf("Keepers of the Art") is null
            && book.EstimateKills("Guards of Qeynos") == 1);
        book.ProcessLine(L(60, "Your faction standing with Guards of Qeynos has been adjusted by 5."));
        Check("races: the same line replayed is deduped", book.Standing("Guards of Qeynos") == 1999);
        book.ProcessLine(L(120, "You have slain a gnoll elite!"));
        book.ProcessLine(L(120, "Your faction standing with Guards of Qeynos has been adjusted by 5."));
        book.ProcessLine(L(180, "You have slain a gnoll elite!"));
        book.ProcessLine(L(180, "Your faction standing with Guards of Qeynos could not possibly get any better."));
        Check("races: the cap line marks MAXED once, and the standing never exceeds the max",
            maxed == "Guards of Qeynos;" && book.IsMaxed("Guards of Qeynos") && book.ViewOf("Guards of Qeynos").Done && book.ViewOf("Guards of Qeynos").ToGo == 0);
        book.ProcessLine(L(200, "Your faction standing with Guards of Qeynos could not possibly get any better."));
        Check("races: …once", maxed == "Guards of Qeynos;");
        book.SetTracked("Human (Qeynos)", false);
        Check("races: untracking silences the gate", book.TrackedRaceOf("Guards of Qeynos") is null && book.RaceOf("Guards of Qeynos") == "Human (Qeynos)");
        // Johan's Dwarf night (5 Oct): hand-ins to an NPC moved three factions by +5 a time, then the game printed
        // "You have completed achievement: <faction>" ×3 and "… Race Unlock - Dwarf" — the panel still read 1/3.
        {
            string said = ""; book.FactionMaxed += f => said += f + ";";
            book.ProcessLine(L(300, "Trantor Everhot says, 'Great! I did not have the time to get down to Irontoe's today. Here. Like I said.'"));
            book.ProcessLine(L(300, "Your faction standing with Corrupt Qeynos Guard has been adjusted by 5."));
            Check("races: a hand-in's faction hit names the NPC who spoke as its source",
                book.SourcesOf("Corrupt Qeynos Guard") is [{ Mob: "Trantor Everhot (hand-in)", Hit: 5, Count: 1 }] && book.Standing("Corrupt Qeynos Guard") == 5);
            book.ProcessLine(L(301, "You have completed achievement: Corrupt Qeynos Guard"));
            book.ProcessLine(L(301, "You have completed achievement: Level 25"));
            Check("races: the faction achievement line maxes the faction — whatever the dump's 2,000 said — and says so once",
                book.IsMaxed("Corrupt Qeynos Guard") && book.ViewOf("Corrupt Qeynos Guard") is { Done: true, CappedBelowMax: true } && said == "Corrupt Qeynos Guard;" && !book.IsMaxed("Level 25"));
            book.ProcessLine(L(301, "You have completed achievement: Corrupt Qeynos Guard"));
            Check("races: …once", said == "Corrupt Qeynos Guard;");
            // (Its third faction just got achieved, so the race reads DONE by its factions already — the unlock line is the game's own word for it.)
            book.ProcessLine(L(302, "You have completed achievement: Race Unlock - Human (Qeynos)"));
            Check("races: the Race Unlock line marks the race DONE ahead of the next achievements dump, and it persists",
                book.View("Human (Qeynos)")!.Done && book.IsUnlocked("Human (Qeynos)") && !book.IsUnlocked("High Elf") && book.View("Human (Qeynos)")!.CountText == "DONE");
            string rbPath = Path.Combine(Path.GetTempPath(), "eql_selftest_races_ach.json");
            try { File.Delete(rbPath); } catch { /* fresh */ }
            var b2 = new RaceBook(null, rbPath);
            b2.ProcessLine(L(400, "You have completed achievement: Race Unlock - Dwarf"), live: false);
            b2.ProcessLine(L(400, "You have completed achievement: Kazon Stormhammer"), live: false); // no dump loaded: not a known race's faction yet — ignored, honestly
            var b3 = new RaceBook(null, rbPath);
            Check("races: an unlock learned on a replay survives a restart", b3.IsUnlocked("Dwarf") && !b3.IsMaxed("Kazon Stormhammer"));
            try { File.Delete(rbPath); } catch { /* temp */ }
        }
        var parsed = FactionDumps.ParseClasses("Untapped Potential: Classes\nI\tClass Unlock - Bard\nI\t\tGet maximum faction with League of Antonican Bards.\n");
        Check("races: the classes section parses with the same reader", parsed is [{ Name: "Bard", Factions: [{ Faction: "League of Antonican Bards" }] }]);
        Check("races: config default — the helper card on", new Models.AppConfig().Overlay.FactionHelperVisible);

        // Owner, 25 Sep (Dark Elf on an Ogre): the game caps Dark Bargainers at
        // −220, far under the dump's 2,000 — MAXED is YOUR ceiling, remembered.
        string capPath = Path.Combine(Path.GetTempPath(), "eql_selftest_races_caps.json");
        try { File.Delete(capPath); } catch { /* fresh */ }
        const string delAch = "Untapped Potential: Races\nI\tRace Unlock - Dark Elf\nI\t\tGet maximum faction with Dark Bargainers.\nI\t\tGet maximum faction with Dreadguard Outer.\nI\t\tGet maximum faction with Dreadguard Inner.\n";
        const string delFac = "ID\tName\tStandingValue\tPointsToMax\n236\tDark Bargainers\t-240\t2240\n334\tDreadguard Outer\t-1590\t3590\n370\tDreadguard Inner\t-400\t2400\n";
        var del = new RaceBook(null, capPath);
        del.LoadDumpText(delFac, delAch, d0, d0);
        del.SetTracked("Dark Elf", true);
        string delHits = ""; del.FactionHit += (f, n, m) => delHits += $"{f}:{n}:{m};";
        for (int i = 0; i < 4; i++)
        {
            del.ProcessLine(L(100 + i * 30, "A Dreadguard has been slain by Jobtik!"));
            del.ProcessLine(L(100 + i * 30, "Your faction standing with Dark Bargainers has been adjusted by 5."));
            del.ProcessLine(L(100 + i * 30, "Your faction standing with Dreadguard Outer has been adjusted by 5."));
        }
        del.ProcessLine(L(300, "a Dreadguard has been slain by Jobtik!"));
        del.ProcessLine(L(300, "Your faction standing with Dark Bargainers could not possibly get any better."));
        del.ProcessLine(L(300, "Your faction standing with Dreadguard Outer has been adjusted by 5."));
        var db = del.ViewOf("Dark Bargainers");
        Check("races: a pet's kill teaches the mob, the live hits move the standing and reach the card's gate",
            del.Standing("Dreadguard Outer") == -1565 && del.SourcesOf("Dreadguard Outer") is [{ Mob: "a Dreadguard", Hit: 5, Count: 5 }]
            && delHits.Contains("Dreadguard Outer:5:a Dreadguard;") && del.TrackedRaceOf("Dreadguard Outer") == "Dark Elf");
        Check("races: the cap line makes −220 YOUR ceiling — MAXED, capped under the dump's max",
            db is { Done: true, Standing: -220, Max: 2000, CappedBelowMax: true, Cap: -220 } && !del.ViewOf("Dreadguard Outer").Done);
        var del2 = new RaceBook(null, capPath);
        del2.LoadDumpText(delFac.Replace("-240\t2240", "-220\t2220"), delAch, d0.AddHours(1), d0.AddHours(1));
        Check("races: the learned cap survives a restart and a fresh dump — still MAXED",
            del2.ViewOf("Dark Bargainers") is { Done: true, CappedBelowMax: true } && del2.CapOf("Dark Bargainers") == -220);
        try { File.Delete(capPath); } catch { /* temp */ }
    }

    /// <summary>Tradeskill helper (21 Sep): the wiki data, the combine engine
    /// on the 13 Aug Blacksmithing lines, the sourcing verdicts.</summary>
    private static void TradeskillChecks(Action<string, bool> Check)
    {
        var data = new TradeskillData();
        Check("ts: nine skills load, each with a ladder, and a hundred-odd recipes",
            data.Skills.Count == 9 && data.Skills.All(s => s.Steps.Any()) && data.RecipeCount > 100);
        var skull = data.RecipeFor("Skull Ale");
        Check("ts: Skull Ale — trivial 151, a dropped skull that returns, bought spices",
            skull is { Trivial: 151 }
            && skull.Ingredients.Any(i => i.Item.Equals("Cyclops skull", StringComparison.OrdinalIgnoreCase) && i.Source == "drop" && i.Returned)
            && skull.Ingredients.Any(i => i.Item == "Spices" && i.Source == "vendor"));
        Check("ts: sourcing — Skull Ale is FARM 1, Short Beer is ALL BOUGHT",
            data.SourcingOf(skull!).Tag == "FARM 1" && data.SourcingOf(data.RecipeFor("Short Beer")!).AllBought);
        Check("ts: a made ingredient inherits its chain — Batwing Pie's dough needs an egg",
            !data.SourcingOf(data.RecipeFor("Batwing Pie")!).AllBought);
        Check("ts: Jewelry Making is the game's name for Jewelcrafting", data.Find("Jewelry Making")?.Name == "Jewelcrafting");
        var fl = data.Find("Fletching")!;
        Check("ts: the arrow steps share a product but keep their own recipes",
            data.StepFor(fl, "CLASS 1 Wood Point Arrow", 40) is { To: 56 } && data.StepFor(fl, "CLASS 1 Wood Point Arrow", 5) is { To: 16 }
            && data.RecipeFor(data.StepFor(fl, "CLASS 1 Wood Point Arrow", 5)!.Recipe)!.Ingredients.Any(i => i.Item == "Large Groove Nocks"));
        Check("ts: where a drop comes from", data.WhereLine("Cyclops skull").Contains("undead cyclops"));

        var t0 = new DateTime(2026, 8, 13, 9, 12, 0);
        string L(int sec, string body) => $"[{t0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
        var w = new TradeskillWatch(data, null);
        w.ProcessLine(L(0, "You have entered Paineel."), live: false);
        w.ProcessLine(L(1, "You have become better at Blacksmithing! (2)"), live: false);
        w.ProcessLine(L(2, "You have become better at Bash! (120)"), live: false);
        w.ProcessLine(L(3, "You purchased 40 Small Piece of Ore from Klok Lagnoz for  21 platinum 2 gold."), live: false);
        Check("ts: a replay learns the skill, ignores Bash, remembers the vendor and its zone",
            w.ValueOf("Blacksmithing") == 2 && w.ValueOf("Bash") is null && w.Skills.Count == 1
            && w.VendorFor("Small Piece of Ore") is { Npc: "Klok Lagnoz", Zone: "Paineel" });
        Check("ts: no session until a skill is opened", w.Take(t0) is null);
        w.StartSession("Blacksmithing", t0);
        const string trivialLine = "You can no longer advance your skill from making this item.";
        string said = "";
        w.WentTrivial += prod => said += prod + ";";
        for (int i = 0; i < 4; i++)
        {
            w.ProcessLine(L(10 + i * 3, "You have fashioned the items together to create something new: Metal Bits."));
            w.ProcessLine(L(11 + i * 3, $"You have become better at Blacksmithing! ({3 + i})"));
        }
        w.ProcessLine(L(30, "You lacked the skills to fashion Metal Bits."));
        var s1 = w.Take(t0.AddSeconds(31))!;
        Check("ts: the session counts combines, ok, fails and skill-ups", s1 is { Combines: 5, Ok: 4, Fail: 1, SkillUps: 4 } && w.ValueOf("Blacksmithing") == 6);
        Check("ts: the step you are on is the product you combined, in its tier, with the next lined up",
            s1.Current is { Recipe: "Metal Bits", To: 21 } && s1.CurrentTier?.Name.StartsWith("Getting Started") == true
            && s1.Next is { Recipe: "Sheet Metal" } && !s1.Trivial && s1.TierIndex == 1 && s1.TierCount == 3);
        Check("ts: pace — 5 combines for 4 points, ~19 more to 21", s1.CombinesPerPoint is > 1.2 and < 1.3 && s1.EstimateCombines == 19);
        w.ProcessLine(L(40, "You purchased 10 Water Flask from Klok Lagnoz for  1 gold 5 silver."));
        Check("ts: session purchases add up in copper; coins print as the two big denominations",
            w.Take(t0.AddSeconds(41))!.SpentCopper == 150 && TradeskillWatch.Coins(21200) == "21p 2g" && TradeskillWatch.Coins(150) == "1g 5s" && TradeskillWatch.Coins(0) == "—");
        w.ProcessLine(L(50, trivialLine));
        w.ProcessLine(L(50, "You have fashioned the items together to create something new: Metal Bits."));
        var s2 = w.Take(t0.AddSeconds(51))!;
        Check("ts: the game's trivial line turns the step amber and says so", s2.Trivial && said == "Metal Bits;");
        w.ProcessLine(L(53, trivialLine));
        w.ProcessLine(L(53, "You have fashioned the items together to create something new: Metal Bits."));
        Check("ts: …once per recipe", said == "Metal Bits;" && w.Take(t0.AddSeconds(54))!.Trivial);
        w.ProcessLine(L(60, "You have fashioned the items together to create an alternate product: Sheet Metal."));
        var s3 = w.Take(t0.AddSeconds(61))!;
        Check("ts: a different recipe moves the step and ends the amber state", s3.Current is { Recipe: "Sheet Metal" } && !s3.Trivial && s3.Ok == 7);
        w.ProcessLine(L(70, "Sorry, but you don't have everything you need for this recipe in your general inventory."));
        Check("ts: the missing-ingredient line flags for 30 s", w.Take(t0.AddSeconds(71))!.Missing && !w.Take(t0.AddSeconds(120))!.Missing);
        // Bought and used since (4 Oct): the ledgers behind the ingredient counts, live and replayed alike.
        var mbRecipe = data.RecipeFor("Metal Bits")!;
        var oreIng = mbRecipe.Ingredients.FirstOrDefault(i => !i.Returned && i.Item.Contains("Ore", StringComparison.OrdinalIgnoreCase));
        Check("ts: bought-since counts the vendor lines after a moment — the replayed ore and the live flasks",
            w.BoughtSince("Small Piece of Ore", t0.AddSeconds(-1)) == 40 && w.BoughtSince("Small Piece of Ore", t0.AddSeconds(5)) == 0
            && w.BoughtSince("Water Flask", t0) == 10 && w.BoughtSince("water flask", t0.AddSeconds(45)) == 0);
        Check("ts: used-since charges every combine, ok or fail, by the recipe's ingredient counts — 7 Metal Bits so far",
            oreIng is null || w.UsedSince(oreIng.Item, t0) == 7 * Math.Max(1, oreIng.Count));
        // The station: a /loc within three minutes before a combine in a stationary container places it.
        w.ProcessLine(L(80, "Your Location is 100.50, -200.25, 3.10"));
        w.ProcessLine(L(85, "You have fashioned the items together to create something new: Metal Bits."));
        Check("ts: a /loc before a forge combine teaches where the forge is, per kind and zone",
            w.StationFor("Forge", "Paineel") is { Y: 100.5, X: -200.25, Z: 3.1 } st0 && st0.Way == "/way -200 101 3" && w.StationFor("Oven", "Paineel") is null && w.StationFor("Forge", "Erudin") is null
            && w.Zone == "Paineel");
        Check("ts: the stations data — a recipe's container wording maps to a station kind or to nothing you carry; Erudin's forge at /loc -1223, -249; Paineel's kiln named without a /loc",
            Stations.KindOf("Brewing Barrel") == "Brew Barrel" && Stations.KindOf("Oven or Spit") == "Oven" && Stations.KindOf("Feir`Dal Forge") == "Forge" && Stations.KindOf("Loom or Large Sewing Kit") == "Loom"
            && Stations.KindOf("Jeweler's Kit") is null && Stations.KindOf("Fletching Table") is null && Stations.KindOf("") is null
            && Stations.In("Forge", "Erudin").Any(st => st is { Y: -1223, X: -249, Note: "53" }) && Stations.In("Kiln", "Paineel").Any(st => st is { Y: null, Note: "False Idols" })
            && Stations.In("Forge", "Nowhere").Count == 0 && Stations.FoldZone("The Ruins of Old Guk 1 (Awakened)") == "The Ruins of Old Guk"
            && Stations.Way(-1223, -249) == "/way -249 -1223" && Stations.Way(-960, 36, 5) == "/way 36 -960 5"
            && Stations.In("Kiln", "East Freeport").Any(st => st is { Y: -960, X: 36, Z: 5 }) && Stations.AllStations.Count > 150);
        {
            string tsPath2 = Path.Combine(Path.GetTempPath(), "eql_selftest_ts_ledger.json");
            try { File.Delete(tsPath2); } catch { /* fresh */ }
            var w5 = new TradeskillWatch(data, null, tsPath2);
            w5.ProcessLine(L(0, "You have entered Erudin."), live: false);
            w5.ProcessLine(L(1, "You purchased 100 Frosting from Innkeep Seke for  5 platinum."), live: false);
            w5.ProcessLine(L(1, "You purchased 100 Frosting from Innkeep Seke for  5 platinum."), live: false); // the same line twice (a merged log)
            w5.ProcessLine(L(2, "Your Location is -1223.4, -249.1, 53.0"), live: false);
            w5.ProcessLine(L(4, "You lacked the skills to fashion Metal Bits."), live: false);
            w5.SaveLearned();
            var w6 = new TradeskillWatch(data, null, tsPath2);
            Check("ts: the bought / used / station ledgers persist and a replayed line counts once",
                w6.BoughtSince("Frosting", t0.AddSeconds(-5)) == 100 && w6.StationFor("Forge", "Erudin") is { Y: -1223.4 } && w6.UsedSince(oreIng?.Item ?? "Small Piece of Ore", t0.AddSeconds(-5)) == (oreIng is null ? 0 : Math.Max(1, oreIng.Count)));
            try { File.Delete(tsPath2); } catch { /* temp */ }
        }

        var w2 = new TradeskillWatch(data, null);
        w2.SetValue("Brewing", 160);
        w2.StartSession("Brewing", t0);
        var b = w2.Take(t0)!;
        Check("ts: with no combine yet the step is the first your skill has not passed", b.Current is { Recipe: "Ginesh" } && !b.Trivial && b.Value == 160 && b.Remaining == 8);
        w2.SetValue("Brewing", 170);
        Check("ts: with no combine the step moves past what the skill has passed", w2.Take(t0)!.Current is { Recipe: "Faydwer Shaker" });
        w2.ProcessLine(L(5, "You have fashioned the items together to create something new: Ginesh."));
        Check("ts: the recipe you are combining reads trivial by skill value alone", w2.Take(t0.AddSeconds(6))! is { Current.Recipe: "Ginesh", Trivial: true });
        var w3 = new TradeskillWatch(data, null);
        w3.SetValue("Pottery", 40);
        bool set = w3.ValueOf("Pottery") == 40;
        w3.SetValue("Pottery", 0);
        Check("ts: the manual value can be set and cleared", set && w3.ValueOf("Pottery") is null);
        var w4 = new TradeskillWatch(data, null);
        w4.StartSession("Baking", t0);
        Check("ts: an unknown skill opens on the ladder's first step", w4.Take(t0) is { Value: null, Current: { Recipe: "Batwing Crunchies" } });
        Check("ts: config defaults — closed, step view, bag counts, notice spoken",
            new Models.AppConfig().Overlay is { TradeskillOpen: "", TradeskillVisible: false, TradeskillLadder: false, TradeskillBagCounts: true, TradeskillNoticeEnabled: true, TradeskillNoticeMode: "speak", TradeskillToolbarBtn: true });
        Check("ts: the phrase template", EQLOverlay.MainWindow.TradeskillPhrase("", "Metal Bits") == "Metal Bits is trivial" && EQLOverlay.MainWindow.TradeskillPhrase("Move on from {item}", "Skull Ale") == "Move on from Skull Ale");
    }

    /// <summary>Crowd control (14 Sep): the charm card and mez panel engine,
    /// replayed on the 24 Aug Beguile Undead lines and a synthetic mez chain.</summary>
    private static void CrowdControlChecks(Action<string, bool> Check)
    {
        var c0 = new DateTime(2026, 8, 24, 8, 42, 0);
        string L(int sec, string body) => $"[{c0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";

        // Charm, on the lines the log actually printed.
        var cc = new CrowdControl(null, null) { IsSelf = n => n == "Thorrak" };
        var episodes = new List<CharmBook.Episode>();
        cc.CharmEnded += episodes.Add;
        Check("cc: the hand-added necro charms exist, Beguile Undead with its observed landing",
            cc.Find("Beguile Undead") is { Kind: CrowdControl.Kind.Charm, LandingSuffix: "moans." }
            && cc.Find("Dominate Undead") is { Kind: CrowdControl.Kind.Charm, LandingSuffix: "" } && cc.Find("Boil Blood") is null);
        cc.ProcessLine(L(15, "You begin casting Beguile Undead."));
        Check("cc: an armed charm shows nothing until the landing", cc.Charm is null);
        cc.ProcessLine(L(20, "a greater ice bones moans."));
        Check("cc: the observed landing opens the card on the mob",
            cc.Charm is { Pet: "a greater ice bones", Spell: "Beguile Undead", Assumed: false });
        cc.NoteDamage("A greater ice bones", "a greater ice bones", 40, c0.AddSeconds(21));
        cc.NoteDamage("A greater ice bones", "Thorrak", 8, c0.AddSeconds(21));
        cc.ProcessLine(L(23, "A greater ice bones has been slain by a greater ice bones!"));
        Check("cc: same-name pet vs mob — the pet's hits and kill count, hits on you don't",
            cc.Charm is { PetDamage: 40, PetKills: 1 });
        int broke = 0; string brokePet = "";
        cc.CharmBroke += p => { broke++; brokePet = p; };
        cc.ProcessLine(L(26, "Your Beguile Undead spell has worn off of a greater ice bones."));
        cc.ProcessLine(L(26, "Your Beguile Undead spell has worn off of a greater ice bones."));
        Check("cc: the wear-off line breaks the charm once, naming the pet", broke == 1 && brokePet == "a greater ice bones");
        Check("cc: the episode went to the ledger — pet, spell, six seconds held, broke, 40 damage in 1 hit, 1 kill",
            episodes is [{ Mob: "a greater ice bones", Spell: "Beguile Undead", How: "broke", PetDamage: 40, PetHits: 1, MaxHit: 40, PetKills: 1 }]
            && Math.Abs(episodes[0].HeldSec - 6) < 0.01);
        var s1 = cc.Take(c0.AddSeconds(30));
        Check("cc: the card reads broke with the time it held — frozen at the break, not still counting", s1.Charm is { Broke: true, Held: 6, SinceBreak: 4 } && s1.Attempt is null);
        cc.ProcessLine(L(45, "You begin casting Beguile Undead."));
        cc.ProcessLine(L(50, "Your Beguile Undead spell is interrupted."));
        Check("cc: a failed recast waits behind the red card", cc.Take(c0.AddSeconds(51)).Charm is { Broke: true });
        var s2 = cc.Take(c0.AddSeconds(100));
        Check("cc: the red card leaves after a minute", s2.Charm is null && s2.Attempt is null);
        cc.ProcessLine(L(101, "You begin casting Beguile Undead."));
        cc.ProcessLine(L(106, "a greater ice bones resisted your Beguile Undead!"));
        Check("cc: a resist is the amber attempt, naming the mob, gone after 30 s",
            cc.Take(c0.AddSeconds(110)).Attempt is { How: "resisted", Target: "a greater ice bones", Spell: "Beguile Undead" }
            && cc.Take(c0.AddSeconds(140)).Attempt is null);
        cc.ProcessLine(L(150, "You begin casting Beguile Undead."));
        cc.ProcessLine(L(155, "a greater ice bones moans."));
        cc.ProcessLine(L(160, "You have entered Permafrost Caverns."));
        Check("cc: zoning clears the card", cc.Charm is null && !cc.Take(c0.AddSeconds(161)).Any);
        Check("cc: zoning closes the episode as 'zoned', five seconds held, and the break earlier was recorded once",
            episodes.Count == 2 && episodes[1] is { How: "zoned", PetHits: 0 } && Math.Abs(episodes[1].HeldSec - 5) < 0.01);

        // The charm ledger (21 Sep): per-mob averages, the card's countdown.
        string bookPath = Path.Combine(Path.GetTempPath(), "eql_selftest_charms.json");
        try { File.Delete(bookPath); } catch { /* fresh */ }
        var book = new CharmBook(null, bookPath);
        foreach (var ep in episodes) book.Add(ep);
        book.Add(episodes[0]); // replay dedupe
        book.Add(new CharmBook.Episode("a skeleton", "Dominate Undead", "Befallen", c0, c0.AddSeconds(120), "died", 2400, 20, 300, 3));
        var stats = book.Stats("a greater ice bones");
        Check("ledger: per mob — two charms, avg 5.5 s, longest 6 s, one broke, 40/hit, kills carry, replay dedupes",
            stats is { Charms: 2, Breaks: 1, Deaths: 0, Kills: 1, DamagePerHit: 40, MaxHit: 40 } && Math.Abs(stats.AvgHeldSec - 5.5) < 0.01 && stats.LongestSec == 6
            && book.ByMob()[0].Mob == "a greater ice bones" && book.ByMob("Dominate") is [{ Mob: "a skeleton", Dps: 20, Deaths: 1 }]);
        book.AddAttempt(new CharmBook.Attempt("a skeleton", "Dominate Undead", "resisted", c0.AddSeconds(-30), "Befallen"));
        book.AddAttempt(new CharmBook.Attempt("a skeleton", "Dominate Undead", "resisted", c0.AddSeconds(-30), "Befallen")); // replay
        book.AddAttempt(new CharmBook.Attempt("", "Dominate Undead", "interrupted", c0.AddSeconds(-20), "Befallen")); // no mob — nothing to learn
        var book2 = new CharmBook(null, bookPath);
        Check("ledger: episodes and attempts survive a restart; a resist counts once per mob, a nameless interrupt not at all",
            book2.Episodes.Count == 3 && book2.Stats("a skeleton") is { Zone: "Befallen", Resisted: 1 } && book2.Attempts.Count == 1
            && book2.Stats("a greater ice bones")!.Resisted == 0);
        // The five fields (21 Sep): mob level from /con, the cast's rank, damage the pet took, your level, resists.
        var cf5 = new CrowdControl(null, null) { IsSelf = n => n == "Thorrak", LevelLookup = m => m == "a greater ice bones" ? 44 : 0, OwnLevel = () => 47 };
        var eps5 = new List<CharmBook.Episode>(); var att5 = new List<CharmBook.Attempt>();
        cf5.CharmEnded += eps5.Add; cf5.CharmAttemptFailed += att5.Add;
        cf5.ProcessLine(L(0, "You have entered Permafrost Caverns."));
        cf5.ProcessLine(L(10, "You begin casting Beguile Undead."));
        cf5.ProcessLine(L(14, "a greater ice bones resisted your Beguile Undead!"));
        cf5.ProcessLine(L(20, "You begin casting Beguile Undead."));
        cf5.ProcessLine(L(25, "a greater ice bones moans."));
        cf5.NoteDamage("A greater ice bones", "an ice bones", 40, c0.AddSeconds(26));
        cf5.NoteDamage("an ice bones", "a greater ice bones", 15, c0.AddSeconds(27));
        cf5.NoteDamage("A greater ice bones", "a greater ice bones", 9, c0.AddSeconds(28)); // same-name twin: the pet's hit
        cf5.ProcessLine(L(40, "Your Beguile Undead spell has worn off of a greater ice bones."));
        Check("cc: the episode carries the /con level, the cast as printed, what the pet took and your level; the resist is an attempt",
            eps5 is [{ MobLevel: 44, Rank: "Beguile Undead", PetTaken: 15, YourLevel: 47, Zone: "Permafrost Caverns", PetHits: 2, PetDamage: 49 }]
            && att5 is [{ Mob: "a greater ice bones", How: "resisted", Zone: "Permafrost Caverns" }]);
        try { File.Delete(bookPath); } catch { /* temp */ }
        var holding = new CrowdControl.CharmView("a wan ghoul knight", "Beguile", 506, 960, false, false, 0, 9473, 4, 3, 41, 612);
        var past = holding with { Held = 1000 };
        var noCeil = holding with { Ceiling = 0 };
        Check("card: a known ceiling counts down and depletes; past it reads +m:ss grey; none counts up",
            Views.CharmWindow.ClockFor(holding, false) is { Clock: "7:34", Overrun: false } h && Math.Abs(h.Fill - 454.0 / 960) < 0.001
            && Views.CharmWindow.ClockFor(past, false) is { Clock: "+0:40", Overrun: true, Fill: 1 }
            && Views.CharmWindow.ClockFor(noCeil, false) is { Clock: "8:26↑", Overrun: false }
            && Views.CharmWindow.ClockFor(holding, true).Spell.EndsWith("· learned")
            && Views.CharmWindow.PaceLine(holding) == "19 DPS · 231/hit · max 612 · 41 hits"
            && Views.CharmWindow.HistoryLine(stats).StartsWith("before: 2× · avg 0:05 · best 0:06"));

        // An unknown landing: assumed on the cast, named by the wear-off, learned from the emote.
        string learnPath = Path.Combine(Path.GetTempPath(), "eql_selftest_cc_landings.json");
        try { File.Delete(learnPath); } catch { /* fresh */ }
        var cl = new CrowdControl(null, learnPath) { IsSelf = n => n == "Thorrak" };
        cl.ProcessLine(L(0, "You begin casting Dominate Undead."));
        Check("cc: an unknown landing opens an assumed card at once", cl.Charm is { Assumed: true, Pet: "your target" });
        cl.ProcessLine(L(3, "A skeleton hits Thorrak for 5 points of damage."));
        cl.ProcessLine(L(4, "a skeleton cackles."));
        cl.ProcessLine(L(5, "A skeleton tries to hit Thorrak, but misses!"));
        cl.ProcessLine(L(40, "Your Dominate Undead spell has worn off of a skeleton."));
        Check("cc: the wear-off names the assumed pet, breaks it, and the emote after the cast is learned",
            cl.Charm is { Pet: "a skeleton", BrokeAt: not null } && cl.Landing("Dominate Undead") == "cackles." && File.Exists(learnPath));
        var cl2 = new CrowdControl(null, learnPath);
        Check("cc: the learned landing survives a restart", cl2.Landing("Dominate Undead") == "cackles.");
        var cf = new CrowdControl(null, null);
        cf.ProcessLine(L(0, "You begin casting Cajole Undead."));
        cf.ProcessLine(L(4, "a ghoul resisted your Cajole Undead!"));
        Check("cc: an assumed card dies with the resist of its own cast", cf.Charm is null && cf.LastAttempt is { How: "resisted" });
        try { File.Delete(learnPath); } catch { /* temp */ }

        // Mez, from the library's own text.
        var lib = new SpellLibrary(new ConfigService());
        var mz = new CrowdControl(lib, null) { IsSelf = n => n == "Thorrak" };
        Check("cc: the library yields the mez family — Mesmerization lands as 'has been mesmerized.' for 24 s",
            mz.Find("Mesmerization") is { Kind: CrowdControl.Kind.Mez, LandingSuffix: "has been mesmerized.", DurationSec: 24 }
            && mz.Find("Beguile") is { Kind: CrowdControl.Kind.Charm } && mz.Find("Enthrall") is { Kind: CrowdControl.Kind.Mez });
        mz.ProcessLine(L(0, "You begin casting Mesmerization."));
        mz.ProcessLine(L(3, "a greater ice bones has been mesmerized."));
        mz.ProcessLine(L(4, "a greater ice bones has been mesmerized.")); // the same cast: an AE twin
        Check("cc: two landings on one name within the same cast are twins, numbered",
            mz.MezRows.Count == 2 && mz.MezRows[0].Label == "a greater ice bones 01" && mz.MezRows[1].Label == "a greater ice bones 02");
        string brokeLabel = "", brokeBy = "";
        mz.MezBroke += (l, w) => { brokeLabel = l; brokeBy = w; };
        mz.NoteDamage("Garn", "a greater ice bones", 58, c0.AddSeconds(10));
        var m1 = mz.Take(c0.AddSeconds(11));
        Check("cc: damage on a mezzed name breaks the OLDEST row and says who",
            brokeLabel == "a greater ice bones 01" && brokeBy == "Garn" && m1.Mez[0] is { Broke: true, BrokeBy: "Garn", BrokeAmount: 58 }
            && m1.Held == 1 && m1.Broken == 1 && m1.Next is { Label: "a greater ice bones 02", Left: 17 });
        var due = new List<string>();
        mz.MezDue += due.Add;
        var m2 = mz.Take(c0.AddSeconds(27));
        mz.Take(c0.AddSeconds(28));
        Check("cc: the last stretch (max 6 s, 20%) flags the row due once and drops the broken row after 8 s",
            due.Count == 1 && due[0] == "a greater ice bones 02" && m2.Mez.Count == 1 && m2.Mez[0].Due);
        mz.ProcessLine(L(29, "Your Mesmerization spell has worn off of a greater ice bones."));
        Check("cc: the wear-off closes the row", mz.MezRows.Count == 0);

        // AE mez (owner, 15 Sep: "when I AE mez only one mob is shown"): every
        // landing within a breath of the first opens a row; a landing later in
        // the window is a stranger's; a resist mid-AE keeps the cast armed.
        var ae = new CrowdControl(lib, null);
        ae.ProcessLine(L(100, "You begin casting Mesmerization VIII."));
        ae.ProcessLine(L(103, "a will sapper resisted your Mesmerization VIII!"));
        ae.ProcessLine(L(103, "a thought spoiler has been mesmerized."));
        ae.ProcessLine(L(103, "a will sapper has been mesmerized."));
        ae.ProcessLine(L(104, "a will sapper has been mesmerized."));
        ae.ProcessLine(L(109, "a mind eater has been mesmerized."));
        Check("cc: an AE opens a row per landing — twins numbered — and ignores a landing outside the spread",
            ae.MezRows.Count == 3 && ae.MezRows.Count(r => r.Mob == "a will sapper") == 2
            && ae.MezRows.Where(r => r.Mob == "a will sapper").Select(r => r.Label).OrderBy(x => x).SequenceEqual(new[] { "a will sapper 01", "a will sapper 02" })
            && ae.MezRows.All(r => r.Mob != "a mind eater"));
        // A re-mez mid-clock REFRESHES (the common case); rows past the clock
        // overrun instead of vanishing; the wear-off teaches the real clock.
        ae.ProcessLine(L(115, "You begin casting Mesmerization VIII."));
        ae.ProcessLine(L(118, "a thought spoiler has been mesmerized."));
        Check("cc: a re-mez on a name mid-clock refreshes its row instead of minting a twin",
            ae.MezRows.Count == 3 && ae.MezRows.First(r => r.Mob == "a thought spoiler").Since == c0.AddSeconds(118)
            && ae.MezRows.First(r => r.Mob == "a thought spoiler").Refreshed);
        var ov = ae.Take(c0.AddSeconds(135)); // will sappers landed at 103/104 on a 24 s clock: past it
        Check("cc: a row past its clock overruns — grey, counting up, still listed",
            ov.Mez.Count == 3 && ov.Mez.Where(v => v.Label.StartsWith("a will sapper")).All(v => v.Overrun && v.Left < 0 && !v.Due)
            && Views.MezWindow.Row(ov.Mez.First(v => v.Overrun), true) is { Overrun: true } orow && orow.TimeText.StartsWith("+0:0"));
        ae.ProcessLine(L(143, "Your Mesmerization spell has worn off of a will sapper.")); // 40 s after the 103 landing
        Check("cc: the wear-off of an unbroken row teaches the clock — 40 s now, not the library's 24",
            ae.LearnedDuration("Mesmerization VIII") == 40 && ae.MezRows.Count == 2
            && ae.DurationFor(ae.Find("Mesmerization")!) == 40);
        ae.ProcessLine(L(150, "You begin casting Mesmerization VIII."));
        ae.ProcessLine(L(153, "an ice bones has been mesmerized."));
        Check("cc: the next landing runs on the learned clock and says so",
            ae.MezRows.First(r => r.Mob == "an ice bones").Duration == 40
            && Views.MezWindow.Row(ae.Take(c0.AddSeconds(154)).Mez.First(v => v.Label == "an ice bones"), true).RightText == "of 0:40 · learned");
        ae.NoteDamage("Garn", "an ice bones", 10, c0.AddSeconds(160));
        ae.ProcessLine(L(161, "Your Mesmerization spell has worn off of an ice bones."));
        Check("cc: a broken row's wear-off teaches nothing (its span is a break, not a clock)",
            ae.LearnedDuration("Mesmerization VIII") == 40);
        // A loose add (owner, 15 Sep): a mezzed mob never acts, so a held name
        // attacking or casting proves an unmezzed one — no false break on it,
        // the next landing appends, its death spares the rows.
        var la = new CrowdControl(lib, null) { IsSelf = n => n == "Thorrak" };
        la.ProcessLine(L(300, "You begin casting Mesmerization."));
        la.ProcessLine(L(303, "a will sapper has been mesmerized."));
        la.ProcessLine(L(303, "a thought spoiler has been mesmerized."));
        var looseSeen = new List<string>();
        la.MezLoose += looseSeen.Add;
        la.NoteDamage("a will sapper", "Thorrak", 12, c0.AddSeconds(310), dot: true); // its DoT still ticks — not an act
        Check("cc: a held mob's DoT tick is not an act", la.LooseNames.Count == 0);
        la.NoteDamage("A will sapper", "Thorrak", 12, c0.AddSeconds(311));
        la.NoteDamage("A will sapper", "Thorrak", 9, c0.AddSeconds(312));
        Check("cc: a held name hitting you proves a loose add — flagged once, spoken once",
            la.LooseNames.Contains("a will sapper") && looseSeen.Count == 1 && la.Take(c0.AddSeconds(312)).Loose.SequenceEqual(new[] { "a will sapper" }));
        la.NoteDamage("Garn", "a will sapper", 58, c0.AddSeconds(313));
        Check("cc: damage on a name with a loose add is the add's — the held row is not broken",
            la.MezRows.All(r => r.BrokeAt is null));
        la.ProcessLine(L(314, "A thought spoiler begins casting Instill."));
        Check("cc: a held name casting is an act too", la.LooseNames.Contains("a thought spoiler"));
        la.ProcessLine(L(315, "A thought spoiler has been slain by Garn!"));
        Check("cc: the loose one dying spares the held row and clears the flag",
            la.MezRows.Count(r => r.Mob == "a thought spoiler") == 1 && !la.LooseNames.Contains("a thought spoiler"));
        la.ProcessLine(L(320, "You begin casting Mesmerization."));
        la.ProcessLine(L(323, "a will sapper has been mesmerized."));
        Check("cc: the next landing on a loose name is the add's own row, never a refresh, and the flag clears",
            la.MezRows.Count(r => r.Mob == "a will sapper") == 2 && la.LooseNames.Count == 0);

        // One break, one row (owner, 21 Sep): three twins held, the group beats
        // on the one that broke — the other two stay mezzed.
        var tw = new CrowdControl(lib, null) { IsSelf = n => n == "Thorrak" };
        tw.ProcessLine(L(400, "You begin casting Mesmerization."));
        tw.ProcessLine(L(403, "an ice bones has been mesmerized."));
        tw.ProcessLine(L(403, "an ice bones has been mesmerized."));
        tw.ProcessLine(L(404, "an ice bones has been mesmerized."));
        int breaks = 0; tw.MezBroke += (_, _) => breaks++;
        tw.NoteDamage("Garn", "an ice bones", 58, c0.AddSeconds(406));
        tw.NoteDamage("Thorrak", "an ice bones", 120, c0.AddSeconds(407));
        tw.NoteDamage("Garn", "an ice bones", 61, c0.AddSeconds(408));
        var tws = tw.Take(c0.AddSeconds(409));
        Check("cc: hits on a name with a broken twin land on the broken one — one break, two still held",
            breaks == 1 && tws.Broken == 1 && tws.Held == 2 && tws.Loose.SequenceEqual(new[] { "an ice bones" }));
        tw.ProcessLine(L(410, "You begin casting Mesmerization."));
        tw.ProcessLine(L(413, "an ice bones has been mesmerized."));
        Check("cc: the re-mez takes the broken row back, no fourth twin, the name is no longer loose",
            tw.MezRows.Count == 3 && tw.MezRows.All(r => r.BrokeAt is null) && tw.LooseNames.Count == 0);
        tw.NoteDamage("Garn", "an ice bones", 58, c0.AddSeconds(420));
        tw.ProcessLine(L(425, "An ice bones has been slain by Garn!"));
        Check("cc: the broken one dying clears the loose name and leaves the held twins",
            tw.MezRows.Count(r => r.BrokeAt is null) == 2 && tw.LooseNames.Count == 0);

        // One break, three lines (rig log, 21 Sep 22:36:04): the wear-off, then
        // "Bazzzazzt has been awakened by Thorrak.", then the reave — the app
        // spent the wear-off on one twin and the hit on another (owner, 23 Sep:
        // "same name mobs breaking at the same time").
        var bk = new CrowdControl(lib, null) { IsSelf = n => n == "Thorrak" };
        bk.ProcessLine(L(500, "You begin casting Mesmerization VIII."));
        bk.ProcessLine(L(502, "Bazzzazzt has been mesmerized."));
        bk.ProcessLine(L(502, "Bazzzazzt has been mesmerized."));
        bk.ProcessLine(L(502, "Bazzzazzt has been mesmerized."));
        string bkLabel = ""; int bkBreaks = 0;
        bk.MezBroke += (l, _) => { bkLabel = l; bkBreaks++; };
        bk.ProcessLine(L(510, "Your Mesmerization VIII spell has worn off of Bazzzazzt."));
        bk.ProcessLine(L(510, "Bazzzazzt has been awakened by Thorrak."));
        bk.NoteDamage("Thorrak", "Bazzzazzt", 51, c0.AddSeconds(510));
        var bks = bk.Take(c0.AddSeconds(511));
        Check("cc: wear-off → awakened-by → the hit is ONE break: one red row naming the hitter, two twins still held",
            bkBreaks == 1 && bks.Held == 2 && bks.Broken == 1 && bkLabel == "Bazzzazzt 01"
            && bks.Mez[0] is { Broke: true, BrokeBy: "Thorrak", BrokeAmount: 51 } && bks.Loose.SequenceEqual(new[] { "Bazzzazzt" }));
        // The other order: the hit first, the wear-off a second later.
        var bk2 = new CrowdControl(lib, null) { IsSelf = n => n == "Thorrak" };
        bk2.ProcessLine(L(600, "You begin casting Mesmerization VIII."));
        bk2.ProcessLine(L(602, "Bzzazzt has been mesmerized."));
        bk2.ProcessLine(L(602, "Bzzazzt has been mesmerized."));
        bk2.ProcessLine(L(602, "Bzzazzt has been mesmerized."));
        int bk2Breaks = 0; bk2.MezBroke += (_, _) => bk2Breaks++;
        bk2.NoteDamage("Jobtik", "Bzzazzt", 58, c0.AddSeconds(606));
        bk2.ProcessLine(L(607, "Your Mesmerization VIII spell has worn off of Bzzazzt."));
        bk2.ProcessLine(L(607, "Bzzazzt has been awakened by Jobtik."));
        var bk2s = bk2.Take(c0.AddSeconds(608));
        Check("cc: the hit first, then the wear-off and awakened-by — still one break, two held",
            bk2Breaks == 1 && bk2s.Held == 2 && bk2s.Broken == 1);
        // A wear-off with no hit after it is an expiry: one row leaves quietly.
        bk2.ProcessLine(L(640, "Your Mesmerization VIII spell has worn off of Bzzazzt."));
        var bk2e = bk2.Take(c0.AddSeconds(645));
        Check("cc: a wear-off nobody hits after is an expiry — one row leaves, no break, the broken one long culled",
            bk2Breaks == 1 && bk2e.Held == 1 && bk2e.Broken == 0 && bk2.MezRows.Count == 1);

        // The learned clock persists with the landings.
        string ccPath = Path.Combine(Path.GetTempPath(), "eql_selftest_cc_durations.json");
        try { File.Delete(ccPath); } catch { /* fresh */ }
        var pl = new CrowdControl(lib, ccPath);
        pl.ProcessLine(L(200, "You begin casting Mesmerization."));
        pl.ProcessLine(L(203, "an ice bones has been mesmerized."));
        pl.ProcessLine(L(238, "Your Mesmerization spell has worn off of an ice bones."));
        var pl2 = new CrowdControl(lib, ccPath);
        Check("cc: learned clocks survive a restart alongside learned landings",
            pl2.LearnedDuration("Mesmerization") == 35 && pl2.Find("Beguile Undead")!.LandingSuffix == "moans.");
        try { File.Delete(ccPath); } catch { /* temp */ }
        var row = Views.MezWindow.Row(new CrowdControl.MezView("an ice bones", "Mesmerization", 4, 24, 20, false, false, "", 0, 0, true), true);
        var rowB = Views.MezWindow.Row(new CrowdControl.MezView("a greater ice bones 02", "Mesmerization", 0, 24, 11, false, true, "Garn", 58, 2, false), false);
        Check("cc: the mez row texts — due reads re-mez now, broke names the hitter",
            row is { TimeText: "0:04", SubText: "re-mez now", RightText: "of 0:24", FillWidth: 49 }
            && rowB is { TimeText: "BROKE", RightText: "held 0:11" } && rowB.SubText.StartsWith("Garn hit it for 58"));
        Check("cc: the config defaults — both panels on, notices spoken",
            new Models.AppConfig().Overlay is { CharmCardVisible: true, MezPanelVisible: true, CcSpeak: true });
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
            // Solo HPS view: heals split per spell, bare heals under "heal",
            // other healers' spells never bleed into your lanes.
            var heals = p.GetHealAbilityRows("Johan");
            Check("solo heals: per-spell split with bare-heal fallback",
                heals.First(r => r.Name == "heal") is { Total: 25 }
                && heals.First(r => r.Name == "Sprouting Heal") is { Total: 30 }
                && heals.All(r => r.Name != "Light Healing"));
            Check("solo heals: the other healer keeps their own lane",
                // 65 on Snik (Ts10) + 40 on Johan (Ts17), same fight
                p.GetHealAbilityRows("Malahoja").First(r => r.Name == "Light Healing") is { Total: 105 });
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

            // ---- forms observed at the 2026-08-16 Gynok Moltor death ------------
            // The killing blow was a second-person DoT tick that no regex
            // caught; the raid also swung "strikes" and burned with a flame
            // damage shield.
            var pg = new CombatParser();
            var deaths = new List<CombatParser.DeathEvent>();
            pg.PlayerDied += d => deaths.Add(d);
            pg.ProcessLine("[Sun Aug 16 23:09:58 2026] A hardened skeleton strikes YOU for 8 points of damage.");
            pg.ProcessLine("[Sun Aug 16 23:09:58 2026] YOU are burned by a hardened skeleton's flames for 6 points of non-melee damage!");
            pg.ProcessLine("[Sun Aug 16 23:10:02 2026] You have taken 1 damage from Rabies by a greater mummy.");
            pg.ProcessLine("[Sun Aug 16 23:10:02 2026] You have taken 29 damage from Searing Arrow by Gynok Moltor pet.");
            pg.ProcessLine("[Sun Aug 16 23:10:02 2026] You died.");
            var ginc = pg.GetIncomingAbilityRows(pet: false);
            Check("incoming: 'strikes' melee verb tracked",
                ginc.First(r => r.Name == "strike") is { Total: 8 });
            Check("incoming: flame damage shield tracked",
                ginc.First(r => r.Name == "flames") is { Total: 6 });
            Check("incoming: second-person DoT tick attributed to its caster",
                ginc.First(r => r.Name == "Searing Arrow") is { Total: 29 }
                && ginc.First(r => r.Name == "Rabies") is { Total: 1 });
            Check("recap: the killing tick reaches the death event",
                deaths is [{ } gd]
                && gd.Events.Any(e => e.Ability == "Searing Arrow" && (int)e.Amount == 29));

            // Rank pooling: the DD line says "by Envenomed Bolt VI", the tick
            // says "from Envenomed Bolt" — one lane, pooled math, labeled
            // with the highest rank observed.
            var pr = new CombatParser();
            pr.ProcessLine("[Sun Aug 16 23:11:00 2026] Johan hit a shiverback grizzly for 100 points of poison damage by Envenomed Bolt VI.");
            pr.ProcessLine("[Sun Aug 16 23:11:06 2026] A shiverback grizzly has taken 40 damage from Envenomed Bolt by Johan.");
            var ranked = pr.GetAbilityRows("Johan");
            Check("spell lanes pool ranks and wear the highest rank as the label",
                ranked.Count(r => r.Name.StartsWith("Envenomed Bolt", StringComparison.Ordinal)) == 1
                && ranked.First(r => r.Name == "Envenomed Bolt VI") is { Total: 140 });

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

            // A charmed mob is the meter's pet while the charm holds (21 Sep).
            {
                var cpp = new CombatParser { SelfName = "Thorrak", PetName = "Garn" };
                var cpSct = new List<CombatParser.SctHit>();
                cpp.SctEvent += cpSct.Add;
                cpp.CharmedPet = "a wan ghoul knight";
                cpp.ProcessLine("[Mon Sep 21 20:00:00 2026] A wan ghoul knight slashes a scorn banshee for 210 points of damage.");
                cpp.ProcessLine("[Mon Sep 21 20:00:01 2026] A scorn banshee hits a wan ghoul knight for 90 points of damage.");
                Check("charmed pet: the meter follows the charmed mob, its hits are pet damage, hits on it are pet incoming",
                    cpp.ActivePet == "a wan ghoul knight" && cpp.IsPet("A wan ghoul knight")
                    && cpSct.Any(h => h is { Kind: CombatParser.SctKind.OutgoingPet, Amount: 210 })
                    && cpSct.Any(h => h is { Kind: CombatParser.SctKind.IncomingPet, Amount: 90 })
                    && cpp.GetAbilityRows("a wan ghoul knight").Any(r => r.Total == 210));
                cpp.CharmedPet = "";
                Check("charmed pet: the charm gone, the summoned pet is the pet again", cpp.ActivePet == "Garn" && !cpp.IsPet("a wan ghoul knight"));
            }

            // Raid-kill death-line parsing (level suffixes stripped).
            Check("raid kill: slain-by line",
                RaidKills.TryParseKill("Lady Vox has been slain by Johan!", out var mob1) && mob1 == "Lady Vox");
            Check("raid kill: you-have-slain line strips level",
                RaidKills.TryParseKill("You have slain a Sage of Innoruuk (17)!", out var mob2)
                && mob2 == "a Sage of Innoruuk");
            Check("raid kill: normal line no match",
                !RaidKills.TryParseKill("You slash a rat for 5 points of damage.", out _));
            // Per-tier counts on the row (22 Sep): "D2 ×7", and the single tier
            // with the most kills wears gold — ties and week-scope ×1s wear none.
            {
                var tv = new RaidKills.TargetView("Maestro of Rancor", 14, DateTime.Now, new HashSet<int> { 0, 2, 3 },
                    new Dictionary<int, int> { [0] = 4, [2] = 7, [3] = 3 });
                var tie = new RaidKills.TargetView("Lord of Ire", 3, DateTime.Now, new HashSet<int> { 0, 2, 3 },
                    new Dictionary<int, int> { [0] = 1, [2] = 1, [3] = 1 });
                Check("raid tiers: per-tier counts and the top tier",
                    tv.CountOn(2) == 7 && tv.CountOn(1) == 0 && tv.TopTier == 2 && tie.TopTier is null
                    && new RaidKills.TargetView("x", 0, null, new HashSet<int>()).TopTier is null);
            }

            // Target renames observed in real logs migrate old target files —
            // Innoruuk's actual death line names him in full.
            Check("raid targets: short Innoruuk migrates to the observed full name",
                RaidKills.MigrateTargetName("Innoruuk") == "Innoruuk, the Prince of Hate"
                && RaidKills.MigrateTargetName("Lady Vox") == "Lady Vox");
            // "You have slain Cazic-Thule!" (17 Aug) — the game hyphenates.
            Check("raid targets: Cazic Thule migrates to the hyphenated log spelling",
                RaidKills.MigrateTargetName("Cazic Thule") == "Cazic-Thule");
            // The Hate minis' Teir`Dal names use backticks (observed 18 Aug),
            // and R`tal runs a lowercase t.
            Check("raid targets: the Hate minis migrate to their backtick spellings",
                RaidKills.MigrateTargetName("Coercer T'vala") == "Coercer T`vala"
                && RaidKills.MigrateTargetName("Grandmaster R'Tal") == "Grandmaster R`tal"
                && RaidKills.MigrateTargetName("Magi P'tasa") == "Magi P`tasa"
                && RaidKills.MigrateTargetName("High Priest M'kari") == "High Priest M`kari");

            // The weekly loot lockout (the Companion's research): the window
            // starts on the most recent Tuesday 08:00 PACIFIC and runs 7 days.
            var (wkStart, wkNext) = RaidKills.WeekBoundsLocal(DateTime.Now);
            var startPac = TimeZoneInfo.ConvertTime(
                DateTime.SpecifyKind(wkStart, DateTimeKind.Unspecified),
                TimeZoneInfo.Local, TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"));
            Check("lockout: week starts Tuesday 08:00 Pacific and contains now",
                startPac.DayOfWeek == DayOfWeek.Tuesday && startPac.Hour == 8
                && wkStart <= DateTime.Now && DateTime.Now < wkNext
                && Math.Abs((wkNext - wkStart).TotalDays - 7) < 0.05); // DST edge ±1h
            string rkwPath = Path.Combine(Path.GetTempPath(), "eql_rkw_test.json");
            File.Delete(rkwPath);
            var rkw = new RaidKills(new ConfigService(), rkwPath);
            rkw.ProcessLine("[x] Lady Vox has been slain by Johan!", wkStart.AddDays(-1)); // last week
            rkw.ProcessLine("[x] Lady Vox has been slain by Johan!", wkStart.AddHours(5)); // this week
            Check("lockout: the This-week view counts only this week's kills",
                rkw.GetView(wkStart).Single(t => t.Name == "Open World")
                    .Targets.Single(x => x.Name == "Lady Vox").Count == 1
                && rkw.GetView().Single(t => t.Name == "Open World")
                    .Targets.Single(x => x.Name == "Lady Vox").Count == 2
                && rkw.KillsFor("Lady Vox", wkStart).Count == 1);
            File.Delete(rkwPath);

            // A difficulty ladder (D0→D4, ~5 min a clear) re-kills the same
            // boss inside the replay-dedupe window — every TIER must record,
            // and only a same-difficulty replay dedupes. Master Yael, 19 Aug:
            // the old any-difficulty window silently ate the D1 and D3 kills.
            string rkdPath = Path.Combine(Path.GetTempPath(), "eql_rkd_test.json");
            File.Delete(rkdPath);
            var rkd = new RaidKills(new ConfigService(), rkdPath);
            var lad = new DateTime(2026, 8, 19, 23, 9, 0);
            rkd.ProcessLine("[x] You have entered The Ruins of Old Paineel - Solo.", lad);
            rkd.ProcessLine("[x] Lady Vox has been slain by Johan!", lad.AddMinutes(5));
            rkd.ProcessLine("[x] You have entered The Ruins of Old Paineel - Solo 1 (Awakened).", lad.AddMinutes(6));
            rkd.ProcessLine("[x] Lady Vox has been slain by Johan!", lad.AddMinutes(10));
            rkd.ProcessLine("[x] Lady Vox has been slain by Johan!", lad.AddMinutes(10)); // replayed line
            Check("kills: a D0→D1 ladder keeps both, a same-D replay dedupes",
                rkd.KillsFor("Lady Vox").Count == 2
                && rkd.KillsFor("Lady Vox").Select(k => k.D).OrderBy(x => x).SequenceEqual(new[] { 0, 1 }));
            File.Delete(rkdPath);

            // Article-blind targets (15 Sep): "You have slain a thunder spirit
            // princess!" is Thunder Spirit Princess; "the Hand of Veeshan" is The
            // Hand of Veeshan. Five princess kills had recorded nothing.
            string rkaPath = Path.Combine(Path.GetTempPath(), "eql_rka_test.json");
            File.Delete(rkaPath);
            var rka = new RaidKills(new ConfigService(), rkaPath);
            var art = new DateTime(2026, 9, 10, 21, 0, 0);
            rka.ProcessLine("[x] You have entered The Plane of Sky.", art);
            rka.ProcessLine("[x] You have slain a thunder spirit princess!", art.AddMinutes(1));
            rka.ProcessLine("[x] A thunder spirit princess has been slain by Puggaard!", art.AddMinutes(20));
            rka.ProcessLine("[x] You have slain the Hand of Veeshan!", art.AddMinutes(30));
            Check("kills: a leading article never hides a named — princess and Hand of Veeshan record under their listed names",
                rka.KillsFor("Thunder Spirit Princess").Count == 2 && rka.KillsFor("The Hand of Veeshan").Count == 1
                && rka.IsTarget("a thunder spirit princess +1") && !rka.IsTarget("a rat")
                && RaidKills.ArticleBlind.Key("The Hand of Veeshan") == "Hand of Veeshan" && RaidKills.ArticleBlind.Key("Anashti Sul") == "Anashti Sul");
            File.Delete(rkaPath);

            // Global respawns: the auto-generated death pattern matches both
            // forms; the duration is the learned minimum (typed times retired).
            var respEntry = new Models.RespawnEntry { Name = "Lady Vox" };
            respEntry.AddGap(400, DateTime.Now);
            var resp = ConfigService.BuildRespawnTrigger(respEntry);
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
                && bar.Alert is { WarnEnabled: true, AtSeconds: 15 });
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

            // One-word named mobs (30 Sep, rig log — Plane of Fear: Dread's
            // fights read "a thought bleeder +3"): it hits you / takes your
            // hits, so it is a mob; the groupmate beside it stays a player.
            {
                var q = new CombatParser { SelfName = "Thorrak" };
                q.ProcessLine($"[{Ts(0)}] You have entered Plane of Fear.");
                q.ProcessLine($"[{Ts(1)}] You slash Dread for 46 points of damage.");
                q.ProcessLine($"[{Ts(2)}] Genantik slashes Dread for 135 points of damage.");
                q.ProcessLine($"[{Ts(3)}] Dread hits YOU for 86 points of damage.");
                q.ProcessLine($"[{Ts(4)}] A thought bleeder bashes YOU for 9 points of damage.");
                q.ProcessLine($"[{Ts(5)}] You slash a thought bleeder for 30 points of damage.");
                Check("named mob: a one-word boss that fights you is an enemy, the groupmate is not",
                    q.IsEnemyName("Dread") && !q.IsEnemyName("Genantik") && q.TargetLabel == "Dread +1"
                    && q.GetRows(false).First(r => r.Name == "Dread").Enemy);
                q.ProcessLine($"[{Ts(6)}] You have entered Plane of Hate.");
                Check("named mob: the learned name stays in its zone", !q.IsEnemyName("Dread"));
                double mine = q.GetRows(false).FirstOrDefault(r => r.Name == "Thorrak").Total, inBefore = q.IncomingSelfTotal;
                q.ProcessLine($"[{Ts(7)}] You hit yourself for 1540 points of unresistable damage by Cannibalization I.");
                Check("cannibalize: hitting yourself is not your damage, nor incoming",
                    q.GetRows(false).FirstOrDefault(r => r.Name == "Thorrak").Total == mine && q.IncomingSelfTotal == inBefore);
                var solo = new CombatParser { SelfName = "Thorrak" };
                solo.ProcessLine($"[{Ts(10)}] You hit yourself for 1540 points of unresistable damage by Cannibalization I.");
                solo.Tick(DateTime.MaxValue);
                Check("cannibalize: alone it opens no fight (the 1 s \"fight\" stubs)", !solo.HasData && solo.History.Count == 0);

                // A raid target is a mob even when it never touches you, and names the pull over its adds.
                var fq = new CombatParser { SelfName = "Thorrak", KnownEnemy = n => n.Equals("Fright", StringComparison.OrdinalIgnoreCase) };
                fq.ProcessLine($"[{Ts(1)}] Genantik slashes Fright for 135 points of damage.");
                fq.ProcessLine($"[{Ts(2)}] Fright hits Genantik for 80 points of damage.");
                fq.ProcessLine($"[{Ts(3)}] You slash a thought bleeder for 400 points of damage.");
                Check("named mob: a raid target names the fight over a harder-hit add",
                    fq.TargetLabel == "Fright +1" && !fq.IsEnemyName("Genantik"));
            }

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
            // HoT ticks: the spell was cast ONCE, ticks trickle in far outside
            // the 12s window — a heal you've ever cast is never a proc.
            pw.ProcessLine($"[{PTs(40)}] You begin casting Slugs Healing V.");
            pw.ProcessLine($"[{PTs(60)}] Johan healed himself for 12 hit points by Slugs Healing.");
            pw.ProcessLine($"[{PTs(75)}] Johan healed himself for 12 hit points by Slugs Healing.");
            Check("procs: HoT ticks of a cast heal never count",
                !pw.SessionProcs.ContainsKey("Slugs Healing"));

            Check("procs: swings = your melee hits + misses", pw.SessionSwings == 1);
            double liveActive = pw.SessionActiveSeconds;
            Check("procs: active time accrues while fighting", liveActive is > 70 and < 85); // last line at +75s
            pw.Tick(new DateTime(2026, 8, 10, 21, 2, 0)); // idle out -> Archive
            Check("procs: an archived fight keeps its active time exactly once",
                Math.Abs(pw.SessionActiveSeconds - liveActive) < 0.5);
            pw.ResetSessionSkills();
            Check("procs: the session reset clears lanes and active time",
                pw.SessionProcs.Count == 0 && pw.SessionActiveSeconds == 0 && pw.SessionSwings == 0);

            // The raid report: Leech Touch (an activated AA) and Harnessing of
            // Spirit (a buff) are not procs. Activations open the cast window;
            // known beneficial spells never count at all.
            pw.ProcessLine($"[{PTs(90)}] You activate Leech Touch.");
            pw.ProcessLine($"[{PTs(91)}] Johan hit a gnoll pup for 300 points of magic damage by Leech Touch I.");
            pw.ProcessLine($"[{PTs(92)}] Johan healed himself for 300 hit points by Leech Touch I.");
            Check("procs: an activated AA's damage and heal are not procs",
                !pw.SessionProcs.ContainsKey("Leech Touch I"));
            pw.BeneficialLookup = n => SpellDurations.BaseKey(n) == "harnessing of spirit";
            pw.ProcessLine($"[{PTs(95)}] Johan healed himself for 20 hit points by Harnessing of Spirit.");
            Check("procs: a known beneficial spell landing cast-less is a buff, not a proc",
                !pw.SessionProcs.ContainsKey("Harnessing of Spirit"));

            // Pet auto-detect: the summon prints nothing, but the pet names
            // itself on any order (lines observed 15 Aug 2026).
            var pd = new CombatParser { SelfName = "Thorrak" };
            var petBinds = new List<string>();
            pd.PetDetected += n => petBinds.Add(n);
            pd.ProcessLine($"[{PTs(0)}] Venarab says, 'Following you, Master.'");
            Check("pet: the follow response binds the pet", pd.PetName == "Venarab");
            pd.ProcessLine($"[{PTs(1)}] Venarab says, 'Following you, Master.'");
            Check("pet: re-ordering the same pet fires no rebind", petBinds.Count == 1);
            pd.ProcessLine($"[{PTs(2)}] Lober says, 'As you wish, oh great one.'");
            Check("pet: the dismiss response rebinds the newer name", pd.PetName == "Lober");
            pd.ProcessLine($"[{PTs(3)}] Guard says, 'Hail, Thorrak'");
            pd.ProcessLine($"[{PTs(3)}] Tindel says, 'so run a parser, and check the ppm'");
            Check("pet: ordinary NPC/player chatter never binds", pd.PetName == "Lober");
            pd.ProcessLine($"[{PTs(4)}] Xanuusaz told you, 'Attacking a gnoll reaver, Master.'");
            Check("pet: the private Master-tell binds (the unforgeable route)", pd.PetName == "Xanuusaz");
            pd.ProcessLine($"[{PTs(5)}] Jabantik says, 'My leader is Thorrak.'");
            Check("pet: the /pet leader answer binds by your own name", pd.PetName == "Jabantik");

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

            // Enemy DoT tracker: one bar per same-named mob. A tick belongs to
            // the instance DUE one (its own 6s heartbeat); nobody due = a new
            // mob = the next bar. Wear-off closes the oldest; death, zoning
            // and tick silence censor.
            var ep = new CombatParser { SelfName = "Johan" };
            ep.DotDurationLookup = s => s == "Curse" ? 30.0 : null;
            string ETs(int s) => new DateTime(2026, 8, 11, 22, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            DateTime ET(int s) => new DateTime(2026, 8, 11, 22, 0, 0).AddSeconds(s);
            ep.ProcessLine($"[{ETs(0)}] A froglok has taken 100 damage from your Curse.");
            var dots = ep.EnemyDots(ET(1));
            Check("dots: first tick opens bar 01 with the countdown",
                dots is [{ Spell: "Curse", Target: "A froglok", Ordinal: 1, RemainingSeconds: not null }]
                && Math.Abs(dots[0].RemainingSeconds!.Value - 29) < 0.01);
            ep.ProcessLine($"[{ETs(2)}] A froglok has taken 100 damage from your Curse.");
            Check("dots: a tick when nobody is due = a second mob = bar 02",
                ep.EnemyDots(ET(3)).Select(d => d.Ordinal).OrderBy(x => x).SequenceEqual(new[] { 1, 2 }));
            ep.ProcessLine($"[{ETs(6)}] A froglok has taken 100 damage from your Curse.");
            Check("dots: a due instance owns its heartbeat tick (no third bar)",
                ep.EnemyDots(ET(7)).Count == 2);
            ep.ProcessLine($"[{ETs(8)}] Your Curse spell has worn off of a froglok.");
            Check("dots: the wear-off closes the OLDEST bar (01 fades, 02 stays)",
                ep.EnemyDots(ET(9)) is [{ Ordinal: 2 }]);
            ep.ProcessLine($"[{ETs(10)}] A froglok has taken 100 damage from your Curse.");
            ep.ProcessLine($"[{ETs(11)}] A froglok has taken 100 damage from your Curse.");
            Check("dots: a freed number is reused (new mob becomes 01 again)",
                ep.EnemyDots(ET(12)).Select(d => d.Ordinal).OrderBy(x => x).SequenceEqual(new[] { 1, 2 }));
            ep.ProcessLine($"[{ETs(12)}] Zibantik has taken 50 damage from your Curse.");
            Check("dots: single-word (player-like) targets never get bars",
                ep.EnemyDots(ET(13)).Count == 2);
            ep.ProcessLine($"[{ETs(13)}] A froglok has taken 40 damage from your Venom of the Snake.");
            Check("dots: unknown duration counts UP instead of guessing",
                ep.EnemyDots(ET(14)).First(d => d.Spell == "Venom of the Snake").RemainingSeconds is null);
            ep.ProcessLine($"[{ETs(14)}] You have slain a froglok!");
            Check("dots: death clears single-instance groups, twins wait for silence",
                ep.EnemyDots(ET(15)).All(d => d.Spell == "Curse")
                && ep.EnemyDots(ET(15)).Count == 2);
            Check("dots: tick silence culls the leftovers", ep.EnemyDots(ET(30)).Count == 0);
            ep.ProcessLine($"[{ETs(40)}] A froglok has taken 100 damage from your Curse.");
            ep.ProcessLine($"[{ETs(41)}] You have entered The Feerrott.");
            Check("dots: zoning leaves hostiles behind", ep.EnemyDots(ET(42)).Count == 0);
            ep.ProcessLine($"[{ETs(50)}] A froglok has taken 100 damage from your Curse.");
            ep.ProcessLine($"[{ETs(85)}] A froglok has taken 100 damage from your Curse.");
            Check("dots: a tick past the duration goes gray-OVERRUN, never a guessed restart",
                ep.EnemyDots(ET(86)) is [{ Overrun: true } r2]
                && Math.Abs(r2.OverrunSeconds - 6) < 0.01);

            // Landing-based debuff bars: your cast arms the detector, the
            // third-person landing names the mob — one bar per mob, closed by
            // wear-off/death, culled by the unwitnessed-overrun cap (they
            // never tick, so silence proves nothing).
            var eb = new CombatParser { SelfName = "Johan" };
            eb.DotDurationLookup = s => SpellDurations.BaseKey(s) == "envenomed bolt" ? 36.0 : null;
            eb.OtherLandingLookup = s => SpellDurations.BaseKey(s) == "envenomed bolt"
                ? ("has been poisoned.", true) : ((string, bool)?)null;
            string BTs(int s) => new DateTime(2026, 8, 11, 23, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            DateTime BT(int s) => new DateTime(2026, 8, 11, 23, 0, 0).AddSeconds(s);
            eb.ProcessLine($"[{BTs(0)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(3)}] A froglok has been poisoned.");
            var dbars = eb.EnemyDots(BT(4));
            Check("debuffs: your cast's landing opens bar 01 with the countdown",
                dbars is [{ Ordinal: 1, RemainingSeconds: not null }]
                && Math.Abs(dbars[0].RemainingSeconds!.Value - 35) < 0.01); // clock starts at LANDING
            eb.ProcessLine($"[{BTs(5)}] A froglok has been poisoned.");
            Check("debuffs: a landing with no cast of yours is someone else's",
                eb.EnemyDots(BT(6)).Count == 1);
            eb.ProcessLine($"[{BTs(8)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(11)}] A froglok has been poisoned.");
            Check("debuffs: second cast+landing = bar 02", eb.EnemyDots(BT(12)).Count == 2);
            Check("debuffs: non-ticking bars are exempt from the silence cull",
                eb.EnemyDots(BT(30)).Count == 2);
            eb.ProcessLine($"[{BTs(31)}] Your Envenomed Bolt spell has worn off of a froglok.");
            Check("debuffs: the wear-off closes the oldest bar",
                eb.EnemyDots(BT(32)) is [{ Ordinal: 2 }]);
            Check("debuffs: the unwitnessed-overrun cap culls the leftovers",
                eb.EnemyDots(BT(11 + 36 + 61)).Count == 0);
            eb.ProcessLine($"[{BTs(120)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(123)}] A froglok has been poisoned.");
            eb.ProcessLine($"[{BTs(130)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(133)}] A froglok has been poisoned.");
            eb.ProcessLine($"[{BTs(161)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(164)}] A froglok has been poisoned.");
            var refreshed = eb.EnemyDots(BT(165));
            Check("debuffs: a re-cast refreshes the overrun bar, no phantom third",
                refreshed.Count == 2 && refreshed.All(r => !r.Overrun));
            eb.ProcessLine($"[{BTs(170)}] You begin casting Envenomed Bolt V.");
            eb.ProcessLine($"[{BTs(181)}] A froglok has been poisoned.");
            Check("debuffs: a landing outside the cast window is ignored",
                eb.EnemyDots(BT(182)).Count == 2);

            // Ghost-bar defence (the Companion's bounded reading, JOS-140):
            // one mob, re-dot around expiry — the bar refreshes in place, it
            // never grows a phantom "02".
            var rd = new CombatParser { SelfName = "Johan" };
            rd.OtherLandingLookup = s => SpellDurations.BaseKey(s) == "venom of the snake"
                ? ("has been poisoned.", true) : ((string, bool)?)null;
            string RTs(int s) => new DateTime(2026, 8, 12, 23, 0, 0).AddSeconds(s)
                .ToString("ddd MMM dd HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
            DateTime RT(int s) => new DateTime(2026, 8, 12, 23, 0, 0).AddSeconds(s);
            rd.ProcessLine($"[{RTs(0)}] You begin casting Venom of the Snake.");
            rd.ProcessLine($"[{RTs(2)}] A froglok has been poisoned.");
            rd.ProcessLine($"[{RTs(4)}] A froglok has taken 40 damage from your Venom of the Snake.");
            Check("dots: the first tick joins the fresh landing — no ghost 02",
                rd.EnemyDots(RT(5)) is [{ Ordinal: 1 }]);
            rd.ProcessLine($"[{RTs(10)}] A froglok has taken 40 damage from your Venom of the Snake.");
            rd.ProcessLine($"[{RTs(16)}] A froglok has taken 40 damage from your Venom of the Snake.");
            // The dot ends (ticks stop); the re-cast lands inside the cull window.
            rd.ProcessLine($"[{RTs(26)}] You begin casting Venom of the Snake.");
            rd.ProcessLine($"[{RTs(29)}] A froglok has been poisoned.");
            Check("dots: a re-dot after the ticks stop refreshes the SAME bar",
                rd.EnemyDots(RT(30)) is [{ Ordinal: 1 }]);
            rd.ProcessLine($"[{RTs(33)}] A froglok has taken 40 damage from your Venom of the Snake.");
            rd.ProcessLine($"[{RTs(36)}] You begin casting Venom of the Snake.");
            rd.ProcessLine($"[{RTs(39)}] A froglok has been poisoned.");
            Check("dots: unknown duration always refreshes — a bar never grows a ghost",
                rd.EnemyDots(RT(40)) is [{ Ordinal: 1 }]);

            // Known duration: a re-dot in the last stretch refreshes; a landing
            // while the clock runs comfortably is a SECOND MOB (tab spread).
            var tl = new CombatParser { SelfName = "Johan" };
            tl.DotDurationLookup = s => SpellDurations.BaseKey(s) == "envenomed bolt" ? 36.0 : null;
            tl.OtherLandingLookup = s => SpellDurations.BaseKey(s) == "envenomed bolt"
                ? ("has been poisoned.", true) : ((string, bool)?)null;
            tl.ProcessLine($"[{RTs(100)}] You begin casting Envenomed Bolt V.");
            tl.ProcessLine($"[{RTs(103)}] A froglok has been poisoned.");
            tl.ProcessLine($"[{RTs(130)}] You begin casting Envenomed Bolt V.");
            tl.ProcessLine($"[{RTs(133)}] A froglok has been poisoned."); // 6s left = tail
            var tail = tl.EnemyDots(RT(134));
            Check("debuffs: a re-dot in the last stretch refreshes in place",
                tail is [{ Ordinal: 1, RemainingSeconds: not null }]
                && Math.Abs(tail[0].RemainingSeconds!.Value - 35) < 0.01);

            // Orange vs red: a landing-only known debuff flags Debuff; a bar
            // that has TICKED is damage regardless of what the library says.
            var od = new CombatParser { SelfName = "Johan" };
            od.OtherLandingLookup = s => SpellDurations.BaseKey(s) == "malosini"
                ? ("looks very uncomfortable.", true) : ((string, bool)?)null;
            od.DebuffLookup = s => SpellDurations.BaseKey(s) == "malosini";
            od.ProcessLine($"[{RTs(200)}] You begin casting Malosini.");
            od.ProcessLine($"[{RTs(203)}] A froglok looks very uncomfortable.");
            od.ProcessLine($"[{RTs(205)}] A froglok has taken 50 damage from your Curse.");
            var tinted = od.EnemyDots(RT(206));
            Check("dots: landing-only debuffs flag orange, ticked bars stay red",
                tinted.First(r => r.Spell == "Malosini").Debuff
                && !tinted.First(r => r.Spell == "Curse").Debuff);

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

    /// <summary>Feed the log's last 60k lines through each live consumer on
    /// its own, timing per line; then all of them in the live order. Reports
    /// µs/line, the worst single line, and lines/s headroom against the log's
    /// own peak rate (65/s observed). Alerts are gagged.</summary>
    private static void RunSkyAudit(string path, string filter)
    {
        var r = SkyAudit.ReplayAsync(new ConfigService(), new[] { path }, filter, null, null).GetAwaiter().GetResult();
        File.WriteAllText(SkyAudit.ReportPath, r.Report);
    }

    private void RunBench(string path)
    {
        AlertService.Silenced = true;
        var report = new System.Text.StringBuilder();
        var all = File.ReadLines(path).ToList();
        var lines = all.Count > 60000 ? all.Skip(all.Count - 60000).ToList() : all;
        report.AppendLine($"bench: {Path.GetFileName(path)} — {lines.Count} lines (of {all.Count})");

        var cs = new ConfigService();
        var cfg = cs.LoadSettings();
        Models.Loadout? lo = null;
        try { lo = cs.LoadLoadout(cfg.ActiveLoadout); } catch { /* broken loadout */ }
        if (lo is not null) { cfg.Triggers = lo.Triggers; cfg.ActiveLoadout = lo.Name; }
        cfg.Triggers.RemoveAll(t => t.Panel == Models.Panels.TimerAuto);
        cfg.Triggers.AddRange(cs.BuildRespawnTriggers());
        report.AppendLine($"loadout: {cfg.ActiveLoadout} — {cfg.Triggers.Count} triggers ({cfg.Triggers.Count(t => t.Enabled)} enabled)");

        var alerts = new AlertService();
        var engine = new TriggerEngine(cfg, alerts);
        var combat = new CombatParser { SelfName = "Thorrak", PetName = "Jobaner" };
        var raids = new RaidKills(cs, Path.Combine(Path.GetTempPath(), "eql_bench_raids.json"));
        var loot = new LootTracker(cs, Path.Combine(Path.GetTempPath(), "eql_bench_loot.json"));
        var sky = new SkyQuests(cs, loot, Path.Combine(Path.GetTempPath(), "eql_bench_sky.json"));
        var quests = new QuestLines(cs, loot, Path.Combine(Path.GetTempPath(), "eql_bench_lines.json"));
        var lib = new SpellLibrary(cs);
        var dur = new SpellDurations(cs, lib, Path.Combine(Path.GetTempPath(), "eql_bench_dur.json"));
        var cond = new ConditionWatcher(lib);
        var learner = new Services.RespawnLearner();
        var helper = new SkyHelper(sky);
        var session = new SessionStats();

        var consumers = new (string Name, Action<string> Feed)[]
        {
            ("TriggerEngine", engine.ProcessLine),
            ("CombatParser", combat.ProcessLine),
            ("RaidKills", l => raids.ProcessLine(l)),
            ("LootTracker", loot.ProcessLine),
            ("SkyQuests", sky.ProcessLine),
            ("QuestLines", quests.ProcessLine),
            ("SpellLibrary.MarkSeen", lib.MarkSeenFromLine),
            ("SpellDurations", dur.ProcessLine),
            ("ConditionWatcher", cond.ProcessLine),
            ("RespawnLearner", learner.ProcessLine),
            ("SkyHelper", helper.ProcessLine),
            ("SessionStats", session.ProcessLine),
        };

        report.AppendLine();
        report.AppendLine($"{"consumer",-24} {"µs/line",10} {"worst ms",10} {"total ms",10}   worst line");
        double totalUs = 0;
        var sw = new System.Diagnostics.Stopwatch();
        foreach (var (name, feed) in consumers)
        {
            long worstTicks = 0; string worstLine = "";
            long sum = 0;
            foreach (var line in lines)
            {
                sw.Restart();
                try { feed(line); } catch (Exception ex) { report.AppendLine($"  !! {name} threw on: {line}\n     {ex.Message}"); }
                sw.Stop();
                sum += sw.ElapsedTicks;
                if (sw.ElapsedTicks > worstTicks) { worstTicks = sw.ElapsedTicks; worstLine = line; }
            }
            double us = sum * 1e6 / System.Diagnostics.Stopwatch.Frequency / lines.Count;
            double worstMs = worstTicks * 1e3 / System.Diagnostics.Stopwatch.Frequency;
            double totalMs = sum * 1e3 / System.Diagnostics.Stopwatch.Frequency;
            totalUs += us;
            report.AppendLine($"{name,-24} {us,10:0.0} {worstMs,10:0.00} {totalMs,10:0}   {Trunc(worstLine, 90)}");
        }
        report.AppendLine($"{"ALL (sum)",-24} {totalUs,10:0.0}");
        report.AppendLine();
        report.AppendLine($"per-line budget at the log's peak (65 lines/s): {1e6 / 65:0} µs — headroom ×{1e6 / 65 / Math.Max(1, totalUs):0}");
        report.AppendLine($"at p99 (26 lines/s): {1e6 / 26:0} µs — headroom ×{1e6 / 26 / Math.Max(1, totalUs):0}");

        // The engine's bar tick — what the UI thread pays 15× a second.
        sw.Restart();
        for (int i = 0; i < 200; i++) engine.TickForBench();
        sw.Stop();
        report.AppendLine($"TriggerEngine.Tick: {sw.Elapsed.TotalMilliseconds / 200:0.000} ms per tick with {engine.Bars.Count} bars live");

        foreach (var f in new[] { "eql_bench_raids.json", "eql_bench_loot.json", "eql_bench_sky.json", "eql_bench_lines.json", "eql_bench_dur.json" })
            try { File.Delete(Path.Combine(Path.GetTempPath(), f)); } catch { /* temp */ }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "eql_bench.txt"), report.ToString());
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

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
            report.AppendLine("--- last fights (newest first) ---");
            foreach (var f in p.History.Take(20))
                report.AppendLine($"  {f.EndedAt:dd MMM HH:mm:ss}  {f.Label}  {f.DurationSeconds:0}s");

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
        int clsCount = Views.ClassGlyphs.ClassNames.Count();
        int clsRows = (clsCount + cols - 1) / cols;
        int width = Math.Max(cols * cellW, stripCols * stripCell);
        int height = glyphRows * cellH + 40 + stripRows * stripRowH + 30 + 26 + clsRows * cellH + 20;

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

            // Class glyphs (Plane of Sky badge strip) — big for detail work,
            // small for the at-size read.
            var classes = Views.ClassGlyphs.ClassNames.ToList();
            double clsTop = stripTop + stripRows * stripRowH + 26;
            for (int i = 0; i < classes.Count; i++)
            {
                double cx = i % cols * cellW + cellW / 2.0;
                double cy = clsTop + i / cols * cellH + 52;
                DrawBadge(dc, Views.ClassGlyphs.For(classes[i]), Color.FromRgb(0x9F, 0xB6, 0xD4), null, cx, cy, 84);
                DrawBadge(dc, Views.ClassGlyphs.For(classes[i]), Color.FromRgb(0x9F, 0xB6, 0xD4), null,
                    cx + cellW / 2.0 - 22, cy - 30, 30);
                var ft = new FormattedText(classes[i], System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 12, Brushes.LightGray, 1.0);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, cy + 50));
            }
        }

        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(outPath);
        enc.Save(fs);
    }

    /// <summary>Open the Manager on <paramref name="page"/> off-screen and
    /// save its client area as a PNG.</summary>
    private static void RenderManagerPage(string page, string outPath, bool bottom)
    {
        Window mgr;
        // "character:<tab>" renders the Character window on a tab instead
        // (the selftest's inventory fixture in %TEMP% feeds it when present).
        if (page.Equals("toolbar", StringComparison.OrdinalIgnoreCase)
            || page.StartsWith("toolbar:", StringComparison.OrdinalIgnoreCase))
        {
            // The toolbar with a live view-model: "toolbar:hidden" shows the
            // eye in its panels-hidden state, "toolbar:catchup" the progress
            // card of a catch-up mid-run, "toolbar:working" locked + muted +
            // the tradeskill card open + badges, "toolbar:labels" the same
            // with a label under every key.
            var cs0 = new ConfigService();
            var cfg0 = cs0.LoadSettings();
            bool working = page.EndsWith(":working", StringComparison.OrdinalIgnoreCase) || page.EndsWith(":labels", StringComparison.OrdinalIgnoreCase);
            var vm = new ViewModels.OverlayViewModel(new TriggerEngine(cfg0, new AlertService()), cfg0)
            {
                PanelsHidden = page.EndsWith(":hidden", StringComparison.OrdinalIgnoreCase),
                Progress = page.EndsWith(":catchup", StringComparison.OrdinalIgnoreCase)
                    ? new ReparseProgress("eqlog_Thorrak_paineel.txt", 1, 1, 61_300_000, 142_000_000, 41_200, Verb: "Catching up")
                    : null,
                LogStatus = "Following eqlog_Thorrak_paineel.txt",
                LoadoutName = working ? "Charm ENC" : "Default",
                ToolbarLabels = page.EndsWith(":labels", StringComparison.OrdinalIgnoreCase),
                Locked = working,
                Muted = working,
                TradeskillOpen = working,
                QuestBadge = working ? 2 : 0,
                LootBadge = working ? 5 : 0,
            };
            var tb = new Views.ToolbarWindow(cs0)
            {
                DataContext = vm,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            tb.Show();
            mgr = tb;
        }
        else if (page.Equals("recap", StringComparison.OrdinalIgnoreCase))
        {
            // A synthetic death in a defensive stance, killed mostly by spells.
            DateTime at = new(2026, 9, 8, 16, 35, 5);
            var ev = new List<CombatParser.RecapEntry>
            {
                new(at.AddSeconds(-15), "a windrider drake", "flames", 77, false, false, Flavor: CombatParser.SctFlavor.Spell),
                new(at.AddSeconds(-15), "Sister of the Spire", "hit", 344, false, false),
                new(at.AddSeconds(-14), "A greater sphinx", "claw", 744, false, false),
                new(at.AddSeconds(-13), "a windrider drake", "Thunderbolt", 480, false, false, Flavor: CombatParser.SctFlavor.Spell),
                new(at.AddSeconds(-11), "Thorrak", "Slugs Healing", 518, true, false),
                new(at.AddSeconds(-10), "Sister of the Spire", "bash", 69, false, false),
                new(at.AddSeconds(-9), "a windrider drake", "Draught of Fire", 498, false, false, Flavor: CombatParser.SctFlavor.Spell),
                new(at.AddSeconds(-7), "Thorrak", "Drain Soul", 699, true, false),
                new(at.AddSeconds(-6), "A windrider drake", "bite", 228, false, false),
                new(at.AddSeconds(-5), "a windrider drake", "Whirlwind", 80, false, false, Flavor: CombatParser.SctFlavor.Spell),
                new(at.AddSeconds(-4), "Sister of the Spire", "kick", 0, false, false, Miss: true),
                new(at.AddSeconds(-2), "a windrider drake", "Mana Detonation", 640, false, false, Flavor: CombatParser.SctFlavor.Spell),
                new(at.AddSeconds(-1), "a windrider drake", "Mana Detonation", 640, false, false, Flavor: CombatParser.SctFlavor.Spell),
            };
            var recap = new Views.DeathRecapWindow(new CombatParser.DeathEvent(at, "a windrider drake", ev, "defensive"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            recap.Show();
            mgr = recap;
        }
        else if (page.Equals("meter:incoming", StringComparison.OrdinalIgnoreCase)
                 || page.Equals("meter:incoming:quiet", StringComparison.OrdinalIgnoreCase))
        {
            // The DPS meter with the incoming chart docked as its cap: a
            // spell-heavy window mid-fight, or folded to the header while quiet.
            var mp = new CombatParser { SelfName = "Thorrak", PetName = "Garn" };
            for (int i = 0; i < 6; i++)
            {
                mp.ProcessLine($"[Tue Sep 08 20:00:{i * 2:00} 2026] Thorrak slashes a thunder spirit princess for {280 + i * 15} points of damage.");
                mp.ProcessLine($"[Tue Sep 08 20:00:{i * 2 + 1:00} 2026] Garn bites a thunder spirit princess for {90 + i * 5} points of damage.");
                mp.ProcessLine($"[Tue Sep 08 20:00:{i * 2 + 1:00} 2026] Thorrak hit a thunder spirit princess for {160 + i * 10} points of disease damage by Spear of Disease.");
                mp.ProcessLine($"[Tue Sep 08 20:00:{i * 2 + 1:00} 2026] a thunder spirit princess hit YOU for {300 + i * 20} points of cold damage by Frost Spear.");
            }
            var iw = new IncomingWatch();
            if (!page.EndsWith(":quiet", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.Now;
                double[] m = { 80, 300, 500, 0, 0, 60, 0, 40, 180, 0, 0, 0, 0, 0, 100 };
                double[] sp = { 100, 0, 0, 400, 0, 0, 420, 0, 0, 80, 0, 0, 550, 550, 0 };
                for (int i = 0; i < 15; i++)
                {
                    if (m[i] > 0) iw.Add(now.AddSeconds(-(14 - i)), m[i], spell: false);
                    if (sp[i] > 0) iw.Add(now.AddSeconds(-(14 - i)), sp[i], spell: true);
                }
            }
            var meter = new Views.MeterWindow(new ConfigService(), mp, new LootTracker(new ConfigService()), 1.0,
                Array.Empty<string>(), false, false, soloMode: true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            meter.Show();
            meter.SetIncoming(iw, () => "defensive", 15, foldQuiet: true);
            mgr = meter;
        }
        else if (page.Equals("tradeskill", StringComparison.OrdinalIgnoreCase) || page.StartsWith("tradeskill:", StringComparison.OrdinalIgnoreCase))
        {
            // The tradeskill helper: Brewing mid-session on Skull Ale
            // (tradeskill), the whole ladder (tradeskill:ladder), or
            // Blacksmithing the moment Metal Bits went trivial (tradeskill:trivial).
            var tsd = new TradeskillData();
            var tsw = new TradeskillWatch(tsd, null);
            var t0 = DateTime.Now.AddMinutes(-25);
            string L(int sec, string body) => $"[{t0.AddSeconds(sec).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
            bool trivial = page.EndsWith(":trivial", StringComparison.OrdinalIgnoreCase);
            tsw.ProcessLine(L(0, "You have entered Paineel."), live: false);
            if (trivial)
            {
                tsw.SetValue("Blacksmithing", 38);
                tsw.StartSession("Blacksmithing");
                tsw.ProcessLine(L(1, "You purchased 40 Small Piece of Ore from Klok Lagnoz for  21 platinum 2 gold."));
                int sk = 38;
                for (int i = 0; i < 14; i++)
                {
                    tsw.ProcessLine(L(5 + i * 3, i % 4 == 3 ? "You lacked the skills to fashion Metal Bits." : "You have fashioned the items together to create something new: Metal Bits."));
                    if (i % 5 == 2) tsw.ProcessLine(L(6 + i * 3, $"You have become better at Blacksmithing! ({++sk})"));
                }
                tsw.ProcessLine(L(60, "You can no longer advance your skill from making this item."));
                tsw.ProcessLine(L(60, "You have fashioned the items together to create something new: Metal Bits."));
            }
            else
            {
                tsw.SetValue("Brewing", 68);
                tsw.StartSession("Brewing");
                tsw.ProcessLine(L(1, "You purchased 20 Vinegar from Innkeep Seke for  12 platinum 4 gold."));
                tsw.ProcessLine(L(2, "Your Location is 955.20, 788.70, -65.00")); // at Paineel's brew barrel — the card says so
                int sk = 68;
                for (int i = 0; i < 42; i++)
                {
                    tsw.ProcessLine(L(5 + i * 3, i % 4 == 1 ? "You lacked the skills to fashion Skull Ale." : "You have fashioned the items together to create something new: Skull Ale."));
                    if (sk < 87 && i % 2 == 0) tsw.ProcessLine(L(6 + i * 3, $"You have become better at Brewing! ({++sk})"));
                }
            }
            var win = new Views.TradeskillWindow(tsw, new ConfigService(), 1.0, ladder: page.EndsWith(":ladder", StringComparison.OrdinalIgnoreCase), bagCounts: false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
                BagCount = item => item switch { "Short Beer" => 18, "Spices" => 21, "Vinegar" => 3, "Cyclops skull" => 1, "Small Piece of Ore" => 26, "Water Flask" => 12, _ => -1 },
            };
            win.ApplySettings(1.0, win.ShowsLadder, bagCounts: true);
            win.Show();
            win.Refresh();
            mgr = win;
        }
        else if (page.Equals("faction", StringComparison.OrdinalIgnoreCase) || page.StartsWith("faction:", StringComparison.OrdinalIgnoreCase))
        {
            // The faction helper card: a hit (faction), the cap (faction:maxed), the wrong way (faction:bad).
            var rb = RaceDemo(DateTime.Now.AddHours(-2));
            rb.SetTracked("Human (Qeynos)", true); rb.SetTracked("High Elf", true);
            var f0 = DateTime.Now.AddMinutes(-30);
            string FL(int i, string body) => $"[{f0.AddSeconds(i).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
            rb.ProcessLine(FL(0, "You have slain a gnoll elite!"), live: false); rb.ProcessLine(FL(0, "Your faction standing with Guards of Qeynos has been adjusted by 5."), live: false);
            var win = new Views.FactionHelperWindow(rb, new ConfigService(), 1.0)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            win.SetLocked(true);
            win.Show();
            if (page.EndsWith(":standing", StringComparison.OrdinalIgnoreCase)) win.Refresh(); // no hit yet: the tracked races' open factions
            else if (page.EndsWith(":maxed", StringComparison.OrdinalIgnoreCase)) win.ShowDemo("maxed", "Human (Qeynos)", "Merchants of Qeynos", 0, null);
            else if (page.EndsWith(":bad", StringComparison.OrdinalIgnoreCase)) win.ShowDemo("hit", "High Elf", "Keepers of the Art", -190, "an ogre guard");
            else win.ShowDemo("hit", "Human (Qeynos)", "Guards of Qeynos", 5, "a gnoll elite");
            mgr = win;
        }
        else if (page.Equals("incoming", StringComparison.OrdinalIgnoreCase))
        {
            // A synthetic spell-heavy window in a defensive stance.
            var iw = new IncomingWatch();
            var now = DateTime.Now;
            double[] m = { 80, 300, 500, 0, 0, 60, 0, 40, 180, 0, 0, 0, 0, 0, 100 };
            double[] sp = { 100, 0, 0, 400, 0, 0, 420, 0, 0, 80, 0, 0, 550, 550, 0 };
            for (int i = 0; i < 15; i++)
            {
                if (m[i] > 0) iw.Add(now.AddSeconds(-(14 - i)), m[i], spell: false);
                if (sp[i] > 0) iw.Add(now.AddSeconds(-(14 - i)), sp[i], spell: true);
            }
            var win = new Views.IncomingWindow(iw, () => "defensive", new ConfigService(), 1.0, 15)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            win.SetLocked(true);
            win.Show();
            win.Refresh();
            mgr = win;
        }
        else if (page.Equals("charm", StringComparison.OrdinalIgnoreCase)
                 || page.Equals("charm:broke", StringComparison.OrdinalIgnoreCase)
                 || page.Equals("mez", StringComparison.OrdinalIgnoreCase))
        {
            // The crowd-control panels on the demo state: the charm card
            // holding / broken, the mez panel with a due, a broken and an
            // assumed row.
            var cc = new CrowdControl(null, null);
            cc.SeedDemo(DateTime.Now, broke: !page.Equals("charm", StringComparison.OrdinalIgnoreCase));
            Window win = page.StartsWith("charm", StringComparison.OrdinalIgnoreCase)
                ? new Views.CharmWindow(cc, new ConfigService(), 1.0)
                : new Views.MezWindow(cc, new ConfigService(), 1.0);
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -10000; win.Top = -10000; win.ShowInTaskbar = false; win.ShowActivated = false;
            if (win is Views.CharmWindow cw) { cw.SetLocked(true); cw.Show(); cw.Refresh(); }
            else if (win is Views.MezWindow mw) { mw.SetLocked(true); mw.Show(); mw.Refresh(); }
            mgr = win;
        }
        else if (page.Equals("sct", StringComparison.OrdinalIgnoreCase))
        {
            // One combat-text lane, unlocked, with a crit, a spell and a proc frozen mid-flight.
            var lane = new Views.SctLaneWindow(new ConfigService(), "sctRender", "Outgoing",
                new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F)), new SolidColorBrush(Color.FromRgb(0x9F, 0xA8, 0xDA)), new SolidColorBrush(Color.FromRgb(0x80, 0xCB, 0xC4)),
                1.0, 22, 500, 260, 220, -10000, -10000)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            lane.Show();
            lane.SetLocked(false);
            lane.FreezeForRender();
            mgr = lane;
        }
        else if (page.Equals("quests:house", StringComparison.OrdinalIgnoreCase))
        {
            // Quest item housekeeping on a scratch ledger: a few looted turn-in
            // items, every quest done so all of them read spare, the rows unfolded.
            string hp = Path.Combine(Path.GetTempPath(), "eql_render_house");
            Directory.CreateDirectory(hp);
            foreach (var f in Directory.GetFiles(hp)) try { File.Delete(f); } catch { /* scratch */ }
            var cs = new ConfigService();
            var loot = new LootTracker(cs, Path.Combine(hp, "loot.json"));
            var sky = new SkyQuests(cs, loot, Path.Combine(hp, "sky.json"));
            int sec = 0;
            foreach (var item in new[] { "Silken Strands", "Silvery Ring", "Silvery Ring", "Small Shield", "Sphinxian Ring", "Woven Skull Cap", "Woven Skull Cap" })
                loot.ProcessLine($"[Thu Sep 10 22:{sec / 60:00}:{sec++ % 60:00} 2026] --You have looted a {item} from a sphinx's corpse.--");
            foreach (var q in sky.Quests) sky.SetCompleted(q, true);
            var sw = new Views.SkyWindow(sky, null, () => "SHD/SHM/NEC")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            sw.Show();
            sw.ShowHousekeepingForTest(openAll: true);
            mgr = sw;
        }
        else if (page.Equals("library:eff", StringComparison.OrdinalIgnoreCase) || page.Equals("library:eff:heal", StringComparison.OrdinalIgnoreCase)
                 || page.Equals("library:eff:fire", StringComparison.OrdinalIgnoreCase))
        {
            // The Efficiency tab on the demo yield — SHD/SHM/ENC at level 50.
            var lw = new Views.SpellLibraryWindow(new SpellLibrary(new ConfigService()), _ => { }, null, EfficiencyDemo(), () => "SHD/SHM/ENC", () => 50,
                null, () => "Level 50 SHD/SHM/ENC · stated by /who at 14:37")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            lw.Show();
            lw.ShowTab("efficiency");
            if (page.EndsWith(":heal", StringComparison.OrdinalIgnoreCase)) lw.EffSetForTest(healing: true);
            if (page.EndsWith(":fire", StringComparison.OrdinalIgnoreCase)) lw.EffSetForTest(resist: "Fire", classes: "WIZ");
            mgr = lw;
        }
        else if (page.Equals("tools", StringComparison.OrdinalIgnoreCase) || page.StartsWith("tools:", StringComparison.OrdinalIgnoreCase))
        {
            // The Tools window on demo data: "tools" = home, "tools:<page>" = eff · inv · bis · slots · focus · stats · races · fx · charms · res · ts.
            var tw = ToolsDemo(out _);
            tw.WindowStartupLocation = WindowStartupLocation.Manual;
            tw.Left = -10000; tw.Top = -10000; tw.ShowInTaskbar = false; tw.ShowActivated = false;
            tw.Show();
            if (page.Contains(':')) tw.ShowPage(page[(page.IndexOf(':') + 1)..]);
            if (Environment.GetEnvironmentVariable("EQL_FOCUS_OPEN") is { Length: > 0 }) tw.FocusForTest?.OpenWantsForTest(); // the marks unfolded
            if (Environment.GetEnvironmentVariable("EQL_BIS_OPEN") is { Length: > 0 } bisOpen) tw.BisForTest?.OpenForTest(bisOpen); // a BiS slot unfolded ("WRIST2")
            mgr = tw;
        }
        else if (page.Equals("library:inv", StringComparison.OrdinalIgnoreCase) || page.Equals("library:invfile", StringComparison.OrdinalIgnoreCase))
        {
            // The Invocations tab: on the synthetic stretch, or (invfile) on the log
            // file in EQL_INV_LOG ending at EQL_INV_END ("yyyy-MM-dd HH:mm"), 60 min.
            string invClasses = Environment.GetEnvironmentVariable("EQL_INV_CLASSES") is { Length: > 0 } ic ? ic : "SHD/SHM/ENC";
            var lw = new Views.SpellLibraryWindow(new SpellLibrary(new ConfigService()), _ => { }, null, null, () => invClasses, () => 50,
                null, () => $"Level 50 {invClasses} · stated by /who at 22:31")
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            lw.Show();
            lw.ShowTab("invocations");
            if (page.EndsWith("invfile", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("EQL_INV_LOG") is { Length: > 0 } invLog)
            {
                var end = DateTime.ParseExact(Environment.GetEnvironmentVariable("EQL_INV_END") ?? "", "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                lw.InvUseLinesForTest(File.ReadAllLines(invLog).ToList(), end, int.TryParse(Environment.GetEnvironmentVariable("EQL_INV_REGEN"), out int rg) ? rg : 14,
                    int.TryParse(Environment.GetEnvironmentVariable("EQL_INV_POOL"), out int pl) ? pl : 2600);
            }
            else lw.InvUseLinesForTest(InvocationDemo(new DateTime(2026, 9, 21, 22, 30, 0)), new DateTime(2026, 9, 21, 22, 50, 0), 14, 2600);
            mgr = lw;
        }
        else if (page.StartsWith("library:", StringComparison.OrdinalIgnoreCase))
        {
            // The spell library window searched as typed ("library:dot") — the EFFECT column.
            var lw = new Views.SpellLibraryWindow(new SpellLibrary(new ConfigService()), _ => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            lw.Show();
            lw.SearchForTest(page["library:".Length..]);
            mgr = lw;
        }
        else if (page.Equals("levelup", StringComparison.OrdinalIgnoreCase) || page.Equals("levelup:15", StringComparison.OrdinalIgnoreCase))
        {
            // The level-up card for a three-class combo at 44 (dividers between
            // classes); "levelup:15" is the owner's DRU/BRD/WIZ ding (effects).
            var lib = new SpellLibrary(new ConfigService());
            bool at15 = page.EndsWith(":15", StringComparison.Ordinal);
            var classes = at15 ? new[] { "DRU", "BRD", "WIZ" } : new[] { "SHD", "SHM", "ENC" };
            var win = new Views.LevelUpWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            win.Show(at15 ? 15 : 44, classes, lib.UnlocksAt(at15 ? 15 : 44, classes));
            mgr = win;
        }
        else if (page.Equals("quests:lines", StringComparison.OrdinalIgnoreCase) || page.Equals("quests:sky", StringComparison.OrdinalIgnoreCase)
                 || page.Equals("quests:sky:all", StringComparison.OrdinalIgnoreCase))
        {
            var csq = new ConfigService();
            var sw = new Views.SkyWindow(new SkyQuests(csq, new LootTracker(csq)), null, () => "SHD/SHM/NEC", new QuestLines(csq, new LootTracker(csq)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            sw.Show();
            sw.ShowPack(page.Contains(":sky", StringComparison.OrdinalIgnoreCase) ? "sky" : "lines");
            if (page.EndsWith(":all", StringComparison.OrdinalIgnoreCase)) sw.ShowStatusForTest("all"); // every card, reward links visible
            mgr = sw;
        }
        else if (page.StartsWith("character:", StringComparison.OrdinalIgnoreCase))
        {
            // "character:charms" shows the ledger on demo rows.
            CharmBook? demoBook = null;
            if (page.Equals("character:charms", StringComparison.OrdinalIgnoreCase))
            {
                demoBook = new CharmBook(null, null);
                var t = DateTime.Now;
                demoBook.Add(new CharmBook.Episode("a wan ghoul knight", "Beguile", "The Plane of Hate", t.AddMinutes(-52), t.AddMinutes(-36), "broke", 9473, 41, 612, 4, 48, "Beguile", 3400, 50));
                demoBook.Add(new CharmBook.Episode("a wan ghoul knight", "Beguile", "The Plane of Hate", t.AddMinutes(-30), t.AddMinutes(-24), "died", 2210, 12, 380, 1, 48, "Beguile", 5100, 50));
                demoBook.Add(new CharmBook.Episode("a greater ice bones", "Beguile Undead", "Permafrost Caverns", t.AddDays(-3), t.AddDays(-3).AddSeconds(6), "broke", 40, 1, 40, 1, 44, "Beguile Undead", 0, 47));
                demoBook.AddAttempt(new CharmBook.Attempt("a greater ice bones", "Beguile Undead", "resisted", t.AddDays(-3).AddMinutes(-1), "Permafrost Caverns"));
            }
            RaceBook? demoRaces = page.Equals("character:races", StringComparison.OrdinalIgnoreCase) ? RaceDemo(DateTime.Now.AddHours(-2)) : null;
            if (demoRaces is not null)
            {
                var r0 = DateTime.Now.AddMinutes(-30);
                string RL(int i, string body) => $"[{r0.AddSeconds(i).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";
                for (int i = 0; i < 4; i++) { demoRaces.ProcessLine(RL(i * 60, "You have slain a gnoll elite!"), live: false); demoRaces.ProcessLine(RL(i * 60, "Your faction standing with Guards of Qeynos has been adjusted by 5."), live: false); }
                demoRaces.ProcessLine(RL(300, "You have slain an ogre guard!"), live: false);
                demoRaces.ProcessLine(RL(300, "Your faction standing with Keepers of the Art has been adjusted by -190."), live: false);
                demoRaces.SetTracked("High Elf", true);
            }
            var inv = new Views.InventoryWindow(Path.Combine(Path.GetTempPath(), "eql_selftest_inv"), "Testchar", "paineel", null, demoBook, demoRaces)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            inv.Show();
            inv.ShowTab(page.Substring("character:".Length));
            mgr = inv;
        }
        else
        {
            var cs = new ConfigService();
            var cfg = cs.LoadSettings();
            var m = new TriggerManagerWindow(cs, cfg, new LogBus(), new AlertService(),
                new RaidKills(cs), new SpellLibrary(cs), new CombatParser(), _ => { })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false,
            };
            m.Show();
            // "Data:reparse" shows the Data page with the progress card mid-run.
            bool reparseDemo = page.Equals("Data:reparse", StringComparison.OrdinalIgnoreCase);
            m.SelectPage(reparseDemo ? "Data" : page);
            if (reparseDemo)
                m.ShowReparseProgress(new ReparseProgress("eqlog_Thorrak_paineel.txt", 1, 1, 61_300_000, 142_000_000, 213_400));
            mgr = m;
        }
        mgr.UpdateLayout();
        if (mgr.Content is not FrameworkElement root) throw new Exception("window has no content");
        if (bottom)
        {
            foreach (var sv in Descendants(root).OfType<ScrollViewer>().Where(v => v.IsVisible))
                sv.ScrollToBottom();
            mgr.UpdateLayout();
        }
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(((SolidColorBrush)mgr.Background).Color), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(outPath)) enc.Save(fs);
        mgr.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
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

            // ---- voices: enumeration + switching never throw, and a real
            // Windows box always carries at least one SAPI voice.
            var voiceSvc = new AlertService { Muted = true };
            var voices = voiceSvc.InstalledVoices();
            Check("voices: at least one installed SAPI voice enumerates",
                voices.Count >= 1);
            voiceSvc.ApplyVoice(voices[0], 2);
            voiceSvc.ApplyVoice("No Such Voice", 0); // unknown name keeps default
            voiceSvc.ApplyVoice("", 0);
            Check("voices: switching (and an unknown name) never throws", true);

            // ---- the GLOBAL notices: one config for every mob, {mob} in a
            // phrase becomes the name, empty phrase = the default.
            Check("alerts: empty phrase speaks the default with the mob's name",
                Models.RespawnNotice.Payload(true, "speak", "", "", "Vox",
                    Models.RespawnNotice.DefaultSpawnPhrase) is { Speak: "Vox respawn", Sound: null });
            Check("alerts: {mob} substitutes into a custom phrase",
                Models.RespawnNotice.Payload(true, "speak", "{mob} is up, move!", "", "Vox",
                    Models.RespawnNotice.DefaultSpawnPhrase) is { Speak: "Vox is up, move!", Sound: null });
            Check("alerts: a disabled notice fires nothing",
                Models.RespawnNotice.Payload(false, "speak", "x", "", "Vox",
                    Models.RespawnNotice.DefaultSpawnPhrase) is null);
            Check("alerts: a sound notice with no file stays silent",
                Models.RespawnNotice.Payload(true, "sound", "", "", "Vox",
                    Models.RespawnNotice.DefaultWarnPhrase) is null);
            Check("alerts: sound mode carries the file, never a phrase",
                Models.RespawnNotice.Payload(true, "sound", "ignored", @"C:\Windows\Media\tada.wav",
                    "Vox", Models.RespawnNotice.DefaultWarnPhrase)
                    is { Speak: null, Sound: @"C:\Windows\Media\tada.wav" });

            // ---- the estimate: learned minimum only (typed times retired).
            var le = new Models.RespawnEntry { Name = "x" };
            Check("estimate: nothing before evidence", le.EffectiveSeconds is null);
            le.AddGap(400, DateTime.Now);
            le.AddGap(380, DateTime.Now);
            Check("estimate: the MINIMUM gap (upper bounds converge down)",
                le.EffectiveSeconds == 380 && le.LearnedSeconds == 380);
            for (int i = 0; i < 12; i++) le.AddGap(600 + i, DateTime.Now);
            Check("estimate: gap list capped at 8, newest first",
                le.Gaps.Count == Models.RespawnEntry.MaxGaps && le.Gaps[0].Seconds == 611);

            // A legacy typed time migrates into the FIRST gap sample and
            // clears; entries that already learned keep their evidence.
            var mig = new Models.RespawnEntry { Name = "y", Seconds = 600 };
            mig.MigrateTypedTime(DateTime.Now);
            Check("migration: a typed time becomes the seed gap",
                mig is { Seconds: 0, EffectiveSeconds: 600, Gaps.Count: 1 });
            mig.MigrateTypedTime(DateTime.Now);
            Check("migration: idempotent", mig.Gaps.Count == 1);
            var mig2 = new Models.RespawnEntry { Name = "z", Seconds = 900 };
            mig2.AddGap(300, DateTime.Now);
            mig2.MigrateTypedTime(DateTime.Now);
            Check("migration: learned evidence outlives the typed number",
                mig2 is { Seconds: 0, EffectiveSeconds: 300, Gaps.Count: 1 });

            Check("trigger: auto entry compiles with duration 0 (learning row)",
                ConfigService.BuildRespawnTrigger(new Models.RespawnEntry { Name = "Ghoul", Seconds = 0 })
                    is { DurationSeconds: 0 });
            var lrn = new Models.RespawnEntry { Name = "Ghoul", Seconds = 0 };
            lrn.AddGap(380, DateTime.Now);
            Check("trigger: learned minimum becomes the trigger duration",
                ConfigService.BuildRespawnTrigger(lrn) is { DurationSeconds: 380 });

            // ---- the learner: death → next-appearance, same zone stay only.
            var rl = new RespawnLearner();
            rl.UpdateEntries(new[] { new Models.RespawnEntry { Name = "Kurven the Cruel", Seconds = 0 } });
            var gaps = new List<(string Name, double Gap)>();
            int sightings = 0;
            rl.GapLearned += (n, g, _) => gaps.Add((n, g));
            rl.Sighted += _ => sightings++;
            var b0 = new DateTime(2026, 8, 20, 20, 0, 0);
            string L(int s, string body) => $"[{b0.AddSeconds(s).ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture)}] {body}";

            rl.ProcessLine(L(0, "You have entered Befallen 3 (Fused)."));
            rl.ProcessLine(L(10, "You have slain Kurven the Cruel!"));
            rl.ProcessLine(L(20, "Kurven the Cruel hits YOU for 30 points of damage."));
            Check("learner: the fight's tail is not a spawn", gaps.Count == 0 && sightings == 0);
            rl.ProcessLine(L(410, "Kurven the Cruel hits YOU for 30 points of damage."));
            Check("learner: reappearance closes the gap and marks a sighting",
                gaps is [("Kurven the Cruel", 400.0)] && sightings == 1);
            rl.ProcessLine(L(500, "You have slain Kurven the Cruel!"));
            rl.ProcessLine(L(520, "You have entered East Commonlands."));
            rl.ProcessLine(L(560, "You have entered Befallen 3 (Fused)."));
            rl.ProcessLine(L(900, "Kurven the Cruel begins casting a spell."));
            Check("learner: zoning between death and reappearance discards the sample",
                gaps.Count == 1 && sightings == 2);
            rl.ProcessLine(L(1000, "You have slain Kurven the Cruel!"));
            rl.ProcessLine(L(1500, "Kurven the Cruel has been slain by Caladar!"));
            Check("learner: death→death closes a gap too",
                gaps.Count == 2 && Math.Abs(gaps[1].Gap - 500) < 0.1);
            rl.ProcessLine(L(1600, "You looted a Rusty Sword from Kurven the Cruel's corpse."));
            Check("learner: a corpse is not a sighting", sightings == 2);

            // ---- the watch's row states: learning, UP, due machinery.
            var mutedAlerts = new AlertService { Muted = true };
            var tw2 = new TimerWindow(new ConfigService(), mutedAlerts, 400, 1.0, null);
            tw2.StartWith(0, "Ghost"); // no estimate: a learning row, never the pie
            Check("watch: learning kill takes a row, not the pie",
                tw2.BigState.Mode is null && tw2.RowStates is [("Ghost", "learning")]);
            tw2.NotifySighted("Ghost");
            Check("watch: a sighting flips the learning row UP",
                tw2.RowStates is [("Ghost", "up")]);
            tw2.StartWith(200, "Kurven");
            Check("watch: a countdown claims the pie past the UP row",
                tw2.BigState.Mode == "Kurven" && tw2.RowStates is [("Ghost", "up")]);
            tw2.NotifySighted("Kurven"); // named mid-countdown → alert now, row UP
            Check("watch: sighting the pie mob demotes it to an UP row",
                tw2.BigState.Mode is null && tw2.RowStates.Count(r => r.State == "up") == 2);
            tw2.Close();

            // ---- the auto/manual toggle: manual = the pie is YOUR egg timer.
            var tw3 = new TimerWindow(new ConfigService(), mutedAlerts, 300, 1.0, null);
            tw3.StartWith(200, "Kurven");
            tw3.SetManualMode(true);
            Check("manual: the pie's repop parks in the rows",
                tw3.BigState.Mode is null && tw3.RowStates is [("Kurven", "countdown")]);
            tw3.StartWith(100, "Vox");
            Check("manual: a death never steals the pie",
                tw3.BigState.Mode is null && tw3.SecondaryNames.Count == 2);
            tw3.SetManualMode(false);
            Check("auto again: the soonest respawn claims the pie back",
                tw3.BigState.Mode == "Vox" && tw3.SecondaryNames is ["Kurven"]);
            tw3.Close();

            // ---- watching rows + zone scoping: an added mob shows before its
            // first death, and zoning HIDES other zones' clocks (world state —
            // the mob keeps cooking while you bank; nothing is deleted).
            var zEntries = new List<Models.RespawnEntry>
            {
                new() { Name = "Kurven", Zone = "Befallen 3 (Fused)", Seconds = 200 },
                new() { Name = "Vox", Zone = "Permafrost", Seconds = 400 },
                new() { Name = "Wanderer", Seconds = 100 }, // zoneless: shows everywhere
            };
            var tw4 = new TimerWindow(new ConfigService(), mutedAlerts, 300, 1.0, null)
            {
                RespawnsProvider = () => zEntries,
                RespawnLookup = n => zEntries.FirstOrDefault(
                    r => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase)),
            };
            tw4.RefreshWatching();
            Check("watching: every enabled respawn takes a quiet row",
                tw4.BigState.Mode is null
                && tw4.RowStates.Count(r => r.State == "watching") == 3);
            tw4.SetZone("Befallen 3 (Fused)");
            Check("watching: rows follow the zone (zoneless shows everywhere)",
                tw4.HiddenNames is ["Vox"]);
            tw4.StartWith(200, "Kurven");
            Check("watching: a death turns the watcher into the clock",
                tw4.BigState.Mode == "Kurven"
                && tw4.RowStates.Count(r => r.State == "watching") == 2);
            tw4.SetZone("Permafrost");
            Check("zoning parks the other zone's clock, still counting",
                tw4.BigState.Mode is null
                && tw4.RowStates.Any(r => r is { Name: "Kurven", State: "countdown" })
                && tw4.HiddenNames.Contains("Kurven") && !tw4.HiddenNames.Contains("Vox"));
            tw4.SetZone("Befallen 3 (Fused)");
            Check("zoning back promotes the intact clock to the pie",
                tw4.BigState is { Mode: "Kurven", Remaining: > 150 and <= 200 });
            // Tiers are the same zone (21 Sep): a Befallen 3 mob shows in Befallen
            // and in Befallen 1 (Awakened) alike, and keeps the pie across tiers.
            tw4.SetZone("Befallen");
            Check("zones compare tier-blind: the clock stays on the pie in another tier of the same zone",
                tw4.BigState.Mode == "Kurven" && !tw4.HiddenNames.Contains("Kurven") && tw4.HiddenNames.Contains("Vox")
                && TimerWindow.SameZone("The Ruins of Old Guk 4 (Refined)", "The Ruins of Old Guk")
                && TimerWindow.SameZone("Nagafen's Lair - Solo 2 (Ascended)", "Nagafen's Lair")
                && !TimerWindow.SameZone("Befallen", "Permafrost"));
            tw4.Close();
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

    private string _lastFault = "";
    private DateTime _lastFaultAt = DateTime.MinValue;
    private int _faultRepeats;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception", e.Exception);
        e.Handled = true;
        // A fault on a timer re-fires while its dialog is still open, and
        // each firing stacked another dialog until the owner killed the
        // process (8 Sep). The same message within a minute is logged, not
        // shown — the first dialog already said it.
        string msg = e.Exception.Message;
        var now = DateTime.Now;
        if (msg == _lastFault && (now - _lastFaultAt).TotalSeconds < 60)
        {
            _faultRepeats++;
            if (_faultRepeats is 1 or 10 or 100 or 1000)
                Log.Warn($"Same fault repeated {_faultRepeats}x - dialog suppressed: {msg}");
            _lastFaultAt = now;
            return;
        }
        _lastFault = msg;
        _lastFaultAt = now;
        _faultRepeats = 0;
        MessageBox.Show(
            "EQL Assistant hit an unexpected error:\n\n" + e.Exception.Message +
            "\n\n(The overlay will keep running. Check your config.json if this repeats.)",
            "EQL Assistant",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
