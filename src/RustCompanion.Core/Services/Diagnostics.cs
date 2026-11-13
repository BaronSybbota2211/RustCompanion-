using Microsoft.Extensions.Logging;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;

namespace RustCompanion.Core.Services;

public sealed record DiagnosticEntry(DateTimeOffset AtUtc, string Source, SourceState State, string Message);

public sealed class DiagnosticsService
{
    private readonly object _lock = new();
    private readonly List<DiagnosticEntry> _entries = new();
    private readonly ILogger<DiagnosticsService>? _log;
    public DiagnosticsService(ILogger<DiagnosticsService>? log = null) { _log = log; }

    public void Record(string source, SourceState state, string message)
    {
        var safe = Redactor.Redact(message);
        lock (_lock)
        {
            _entries.Add(new DiagnosticEntry(DateTimeOffset.UtcNow, source, state, safe));
            if (_entries.Count > 200) _entries.RemoveRange(0, _entries.Count - 200);
        }
        _log?.LogInformation("{Source}: {State} {Message}", source, state, safe);
    }

    public IReadOnlyList<DiagnosticEntry> Snapshot() { lock (_lock) return _entries.ToArray(); }
}
