using Serilog.Core;
using Serilog.Events;

namespace Poseidon.Desktop.Diagnostics;

public sealed class LiveLogBuffer
{
    private readonly object _lock = new();
    private readonly List<LiveLogEntry> _entries = new();
    private readonly int _capacity;

    public LiveLogBuffer(int capacity = 2_000)
    {
        _capacity = Math.Max(100, capacity);
    }

    public event EventHandler<LiveLogEntry>? EntryAdded;

    public IReadOnlyList<LiveLogEntry> Snapshot
    {
        get
        {
            lock (_lock)
                return _entries.ToList();
        }
    }

    public void Add(LiveLogEntry entry)
    {
        LiveLogEntry visibleEntry;
        lock (_lock)
        {
            var lastIndex = _entries.Count - 1;
            if (lastIndex >= 0 && IsDuplicate(_entries[lastIndex], entry))
            {
                visibleEntry = entry with
                {
                    RepeatCount = _entries[lastIndex].RepeatCount + 1
                };
                _entries[lastIndex] = visibleEntry;
            }
            else
            {
                visibleEntry = entry;
                _entries.Add(entry);
            }

            while (_entries.Count > _capacity)
                _entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(this, visibleEntry);
    }

    public IReadOnlyList<LiveLogEntry> Query(
        LogEventLevel minimumLevel,
        string? searchText = null,
        int take = 500)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(searchText)
            ? null
            : searchText.Trim();

        lock (_lock)
        {
            return _entries
                .Where(e => e.Level >= minimumLevel)
                .Where(e => normalizedSearch is null
                    || e.Message.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || e.Component.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || (e.Exception?.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ?? false))
                .Reverse()
                .Take(Math.Max(1, take))
                .Reverse()
                .ToList();
        }
    }

    public void ClearView()
    {
        lock (_lock)
            _entries.Clear();
    }

    private static bool IsDuplicate(LiveLogEntry previous, LiveLogEntry current)
    {
        return previous.Level == current.Level
            && string.Equals(previous.Component, current.Component, StringComparison.Ordinal)
            && string.Equals(previous.Message, current.Message, StringComparison.Ordinal)
            && string.Equals(previous.Exception, current.Exception, StringComparison.Ordinal);
    }
}

public sealed class LiveLogSink : ILogEventSink
{
    private readonly LiveLogBuffer _buffer;

    public LiveLogSink(LiveLogBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Emit(LogEvent logEvent)
    {
        var component = logEvent.Properties.TryGetValue("SourceContext", out var source)
            ? source.ToString().Trim('"')
            : "Poseidon";

        _buffer.Add(new LiveLogEntry(
            logEvent.Timestamp,
            logEvent.Level,
            component,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString()));
    }
}
