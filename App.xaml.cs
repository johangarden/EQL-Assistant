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
            var invWin = new Views.InventoryWindow(invDir, "Testchar", "paineel");
            invWin.Show();
            invWin.ShowFocusTab(); // instantiate the audit-board template
            invWin.ShowTab("bis");  // and the best-in-slot board
            invWin.UpdateLayout();
            invWin.Close();

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
                new("Grimy Black Silk Robe +5", "grimy black silk robe +5", "Bank5-Slot6", 1, "bank", 4),
                new("Coin Purse of Nowhere", "coin purse of nowhere", "General 1-Slot1", 1, "bags", 5),
            };
            var bis = BisFinder.Build(bisRows, bisStats, new[] { "SHD", "ROG", "SHM" },
                new[] { "AC", "STA", "INT" });
            var head = bis.Slots.First(s => s.Key == "HEAD");
            var chest = bis.Slots.First(s => s.Key == "CHEST");
            var feet = bis.Slots.First(s => s.Key == "FEET");
            // Wicked Sallet +5: AC 10→15, STA 3→8, INT 2→7 ⇒ 45 + 16 + 7.
            Check("bis: stats scale by the item's +N tier and score 3·2·1",
                head.Ranked.Count > 0 && head.Ranked[0].BaseName == "Wicked Sallet"
                && Math.Abs(head.Ranked[0].Score - 68) < 0.01 && head.Ranked[0].Worn);
            Check("bis: the worn winner is no upgrade; a foreign-class robe stays visible but unranked",
                !head.Upgrades.Any()
                && chest.Ranked.Count == 0 && chest.Foreign.Count == 1
                && chest.Foreign[0].BaseName == "Grimy Black Silk Robe");
            Check("bis: an ALL-class item in the bank is an upgrade for an empty slot",
                feet.Upgrades.Any(u => u.BaseName == "Golden Efreeti Boots" && u.Lane == "bank"));
            Check("bis: items the wiki doesn't know are named, never silently dropped",
                bis.Unknown.Contains("Coin Purse of Nowhere"));
            // Weapons by fighting style: a 1H stick, a 2H reaver, a shield.
            var wRows = new List<InventoryStore.CarryRow>
            {
                new("Carved Walking Stick", "carved walking stick", "Primary", 1, "worn", 10), // WORN in Primary
                new("A Dark Reaver +2", "a dark reaver +2", "Bank1-Slot1", 1, "bank", 11),
                new("Buckler of Doom", "buckler of doom", "Bank1-Slot2", 1, "bank", 12),
                new("Soldier's Brooch of the Spirited +2", "soldier's brooch", "Range", 1, "worn", 13), // stat range item
            };
            var wAll = BisFinder.Build(wRows, bisStats, new[] { "SHD", "SHM" }, new[] { "DMG_DLY", "STR", "STA" });
            var twoH = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.TwoHanded);
            var shield = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.ShieldAndOne);
            var dual = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.DualWield);
            Check("bis: two-handed style keeps only 2H weapons and drops the secondary slot",
                twoH.Slots.First(s => s.Key == "PRIMARY").Ranked.Select(c => c.BaseName).SequenceEqual(new[] { "A Dark Reaver" })
                && twoH.Slots.All(s => s.Key != "SECONDARY"));
            Check("bis: 1H + shield pairs a one-hander with an off-hand",
                shield.Slots.First(s => s.Key == "PRIMARY").Ranked.Single().BaseName == "Carved Walking Stick"
                && shield.Slots.First(s => s.Key == "SECONDARY").Ranked.Single().BaseName == "Buckler of Doom");
            Check("bis: dual wield never offers the weapon worn in Primary for the off-hand",
                dual.Slots.First(s => s.Key == "SECONDARY").Ranked.Count == 0);
            var rangeStat = BisFinder.WeaponView(wAll, BisFinder.WeaponStyle.DualWield, BisFinder.RangeMode.Stat);
            Check("bis: Range lives in the weapons view, toggling DPS (bows) or stat (brooches)",
                BisFinder.ArmorView(wAll).Slots.All(s => !BisFinder.WeaponSlotKeys.Contains(s.Key))
                && dual.Slots.First(s => s.Key == "RANGE").Ranked.Count == 0
                && rangeStat.Slots.First(s => s.Key == "RANGE").Ranked.Any(c => c.BaseName.StartsWith("Soldier's Brooch")));
            var bisBank = BisFinder.Build(bisRows, bisStats, new[] { "SHD" }, new[] { "AC", "STA", "INT" },
                new[] { "bank" });
            Check("bis: the search-in lanes narrow the field",
                bisBank.Slots.First(s => s.Key == "HEAD").Ranked.Count == 0
                && bisBank.Slots.First(s => s.Key == "FEET").Ranked.Count == 1);

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

                // The shopping list: Meda is spent, five quests still want one
                // each — "need 5", aggregated, filed under the random-drop isle.
                var medaNeed = sqAgain.MissingByIsle()
                    .FirstOrDefault(r => r.Item == "Wind Rune Meda");
                Check("sky: the isle list aggregates a shared rune across its open quests",
                    medaNeed is { Missing: 5, Quests.Count: 5 });

                // Housekeeping: complete every Ozah quest, then loot an Ozah —
                // the ledger calls it spare.
                foreach (var q in sqAgain.Quests.Where(q =>
                             q.Items.Any(i => i.Name == "Wind Rune Ozah")))
                    sqAgain.SetCompleted(q, true);
                skyLoot.ProcessLine("[Sat Aug 29 00:40:00 2026] You looted a Wind Rune Ozah from a selftest zephyr's corpse and stored it in your currency");
                Check("sky: a rune nothing active wants shows as surplus",
                    sqAgain.Surplus().Any(s => s.Item == "Wind Rune Ozah" && s.Surplus == 1)
                    && sqAgain.MissingByIsle().All(r => r.Item != "Wind Rune Ozah"));

                var helper = new SkyHelper(sq) { ClassesProvider = () => new[] { "BRD" } };
                var sighted = new List<string>();
                helper.Sighted += m => sighted.Add(m);
                helper.ProcessLine("[x] Keeper of Souls glowers at you dubiously -- it appears to be quite formidable. (Lvl: 60)");
                Check("sky helper: a /con line sights the dropper",
                    sighted is ["Keeper of Souls"]
                    && helper.ItemsFor("Keeper of Souls")
                        is [{ Item: "Light Woolen Mantle", Class: "BRD", Held: 0, Need: 1 }]);
                helper.ProcessLine("[x] You slash Gorgalosk for 50 points of damage.");
                Check("sky helper: a damage line sights the dropper",
                    sighted.Count == 2 && sighted[1] == "Gorgalosk");
                helper.ProcessLine("[x] You have slain a bixie drone!");
                Check("sky helper: an unrelated mob sights nothing", sighted.Count == 2);
                sq.SetCompleted(voice, true);
                sighted.Clear();
                helper.ProcessLine("[x] Keeper of Souls hits YOU for 120 points of damage.");
                Check("sky helper: a completed quest's drop is silent by default",
                    sighted.Count == 0 && helper.ItemsFor("Keeper of Souls").Count == 0);
                helper.ShowCompleted = true;
                Check("sky helper: ...and speaks when configured to",
                    helper.ItemsFor("Keeper of Souls") is [{ QuestDone: true }]);

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

            // Raid-kill death-line parsing (level suffixes stripped).
            Check("raid kill: slain-by line",
                RaidKills.TryParseKill("Lady Vox has been slain by Johan!", out var mob1) && mob1 == "Lady Vox");
            Check("raid kill: you-have-slain line strips level",
                RaidKills.TryParseKill("You have slain a Sage of Innoruuk (17)!", out var mob2)
                && mob2 == "a Sage of Innoruuk");
            Check("raid kill: normal line no match",
                !RaidKills.TryParseKill("You slash a rat for 5 points of damage.", out _));

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
