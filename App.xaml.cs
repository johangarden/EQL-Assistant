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

        Log.Init();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        Log.Info($"===== EQL Overlay v{ver} starting =====");
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
            var mgr = new TriggerManagerWindow(cs, cfg, new LogBus(), new AlertService(), _ => { });
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception", e.Exception);
        MessageBox.Show(
            "EQL Overlay hit an unexpected error:\n\n" + e.Exception.Message +
            "\n\n(The overlay will keep running. Check your config.json if this repeats.)",
            "EQL Overlay",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
