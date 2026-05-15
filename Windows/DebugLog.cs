using System.Collections.Concurrent;

namespace AutoFollow.Windows;

/// <summary>指令日志 — 记录每一步操作，用于调试（自动去重，同内容3秒内不重复记录）</summary>
public sealed class DebugLog
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private const int MaxEntries = 500;
    private string? _lastMsg;
    private DateTime _lastTime;

    public bool Enabled { get; set; } = true;

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public void Log(string category, string message)
    {
        if (!Enabled) return;

        // 同内容3秒内不重复记录（防刷屏）
        var now = DateTime.Now;
        if (message == _lastMsg && (now - _lastTime).TotalSeconds < 10)
            return;

        _lastMsg = message;
        _lastTime = now;

        var entry = new LogEntry
        {
            Timestamp = now,
            Category = category,
            Message = message,
        };
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    public void Clear() => _entries.Clear();
}

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";
}
