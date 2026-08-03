namespace EQLOverlay.Services;

/// <summary>
/// A tiny in-process pub/sub for raw log lines plus a rolling history buffer,
/// so the Trigger Manager can show/capture recent lines. Publish on the UI thread.
/// </summary>
public sealed class LogBus
{
    private readonly int _capacity;
    private readonly Queue<string> _buffer = new();

    public event Action<string>? LineReceived;

    public LogBus(int capacity = 300) => _capacity = capacity;

    public void Publish(string line)
    {
        _buffer.Enqueue(line);
        while (_buffer.Count > _capacity) _buffer.Dequeue();
        LineReceived?.Invoke(line);
    }

    public IReadOnlyList<string> Snapshot() => _buffer.ToArray();
}
