using System.IO;

namespace EQLOverlay.Services;

/// <summary>
/// Minimal thread-safe flat-file logger. Writes <c>eql_assistant.log</c> next to
/// the running exe (falls back to %APPDATA%\EQL_Assistant if that isn't writable).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _path = "";

    /// <summary>Full path of the active log file (empty if logging is disabled).</summary>
    public static string Path => _path;

    public static void Init()
    {
        // Preferred: same folder as the exe (keeps it simple/portable).
        string? exeDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppContext.BaseDirectory;
        if (TrySetTarget(exeDir)) return;

        // Fallback: the config folder, if the exe folder isn't writable
        // (e.g. installed under Program Files).
        string appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQL_Assistant");
        TrySetTarget(appData);
    }

    private static bool TrySetTarget(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            string candidate = System.IO.Path.Combine(dir, "eql_assistant.log");
            RotateIfLarge(candidate);
            // Prove we can write before committing to this path.
            File.AppendAllText(candidate, "");
            _path = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        if (string.IsNullOrEmpty(_path)) return;
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_path, line); }
            catch { /* never let logging throw */ }
        }
    }

    /// <summary>Roll the log over to eql_assistant.prev.log once it passes ~2 MB.</summary>
    private static void RotateIfLarge(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                string prev = System.IO.Path.ChangeExtension(path, ".prev.log");
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }
        }
        catch { /* ignore rotation failures */ }
    }
}
