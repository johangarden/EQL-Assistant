using System.IO;
using System.Text;
using EQLOverlay.Models;

namespace EQLOverlay.Services;

/// <summary>
/// Follows a log file like <c>tail -f</c>. Opens the file shared so the game
/// keeps writing to it, reads only newly-appended lines, and copes with the
/// file being truncated/rotated or a newer character log appearing.
/// </summary>
public sealed class LogWatcher : IDisposable
{
    private readonly LogConfig _config;
    private readonly Action<string> _onLine;
    private readonly Action<string> _onStatus;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private string? _currentPath;
    private long _position;
    private readonly StringBuilder _lineBuffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string? CurrentPath => _currentPath;

    public LogWatcher(LogConfig config, Action<string> onLine, Action<string> onStatus)
    {
        _config = config;
        _onLine = onLine;
        _onStatus = onStatus;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _loop?.Wait(1000); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _currentPath = null;
        _position = 0;
        _lineBuffer.Clear();
    }

    private async Task RunAsync(CancellationToken token)
    {
        int reselectTicks = 0;
        int delay = Math.Max(50, _config.PollIntervalMs);

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Re-evaluate which file is newest roughly every ~3s so we pick
                // up a character switch (a different eqlog_*.txt starts growing).
                if (_currentPath is null || reselectTicks++ >= Math.Max(1, 3000 / delay))
                {
                    reselectTicks = 0;
                    string? newest = ResolveTargetFile();
                    if (newest is not null && !string.Equals(newest, _currentPath, StringComparison.OrdinalIgnoreCase))
                        SwitchTo(newest);
                }

                if (_currentPath is not null)
                    ReadNewData();
                else
                    _onStatus("Waiting for a log file… set your log folder in Manage → Settings (Ctrl+Alt+M).");
            }
            catch (Exception ex)
            {
                _onStatus("Log read error: " + ex.Message);
                Log.Warn("Log read error: " + ex.Message);
            }

            try { await Task.Delay(delay, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    private string? ResolveTargetFile()
    {
        if (!string.IsNullOrWhiteSpace(_config.ExplicitFile))
            return File.Exists(_config.ExplicitFile) ? _config.ExplicitFile : null;

        if (string.IsNullOrWhiteSpace(_config.Directory) || !Directory.Exists(_config.Directory))
            return null;

        string pattern = string.IsNullOrWhiteSpace(_config.FilePattern) ? "*.txt" : _config.FilePattern;

        FileInfo? newest = null;
        foreach (var path in Directory.EnumerateFiles(_config.Directory, pattern))
        {
            var fi = new FileInfo(path);
            if (newest is null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                newest = fi;
        }
        return newest?.FullName;
    }

    private void SwitchTo(string path)
    {
        _currentPath = path;
        _lineBuffer.Clear();
        _decoder.Reset();

        long length = 0;
        try { length = new FileInfo(path).Length; } catch { /* ignore */ }

        // On first attach honor StartAtEndOfFile; on a later switch we also jump
        // to the end so we don't replay an entire existing log.
        _position = _config.StartAtEndOfFile ? length : 0;

        _onStatus($"Following {Path.GetFileName(path)}");
        Log.Info($"Following log file: {path}");
    }

    private void ReadNewData()
    {
        long length;
        try { length = new FileInfo(_currentPath!).Length; }
        catch { return; }

        if (length < _position)
        {
            // File shrank => truncated or rotated. Restart from the top.
            _position = 0;
            _lineBuffer.Clear();
            _decoder.Reset();
        }

        if (length == _position) return;

        using var fs = new FileStream(_currentPath!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(_position, SeekOrigin.Begin);

        byte[] buffer = new byte[8192];
        char[] chars = new char[8192];
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            int charCount = _decoder.GetChars(buffer, 0, read, chars, 0);
            for (int i = 0; i < charCount; i++)
            {
                char c = chars[i];
                if (c == '\n')
                {
                    EmitLine();
                }
                else if (c != '\r')
                {
                    _lineBuffer.Append(c);
                }
            }
        }

        _position = fs.Position;
    }

    private void EmitLine()
    {
        if (_lineBuffer.Length > 0)
        {
            string line = _lineBuffer.ToString();
            _lineBuffer.Clear();
            _onLine(line);
        }
    }

    public void Dispose() => Stop();
}
