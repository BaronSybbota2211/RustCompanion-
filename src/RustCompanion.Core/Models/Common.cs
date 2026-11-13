namespace RustCompanion.Core.Models;

public enum DataStatus { Confirmed, Calculated, Estimate, Manual, Unavailable }

public static class DataStatusText
{
    public static string Ru(this DataStatus s) => s switch
    {
        DataStatus.Confirmed => "Подтверждено источником",
        DataStatus.Calculated => "Рассчитано",
        DataStatus.Estimate => "Оценка",
        DataStatus.Manual => "Введено вручную",
        _ => "Недоступно"
    };
}

/// <summary>Причина результата внешнего источника. NoResponse НЕ означает, что сервер выключен.</summary>
public enum SourceState { Ok, NoResponse, AccessDenied, RateLimited, NoData, NotConfigured, Error }

public sealed record SourceResult<T>(SourceState State, T? Value, string Message)
{
    public bool IsOk => State == SourceState.Ok;
    public static SourceResult<T> Ok(T value) => new(SourceState.Ok, value, "");
    public static SourceResult<T> Fail(SourceState state, string message) => new(state, default, message);
}

public enum TrackerKind { Monument = 0, DeepSea = 1, PatrolHelicopter = 2, Bradley = 3 }

public sealed class TimerEntry
{
    public long Id { get; set; }
    public TrackerKind Kind { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset? StartedAtUtc { get; set; }
    public int IntervalSeconds { get; set; }
    public int LeadSeconds { get; set; }
    public string Note { get; set; } = "";
    public bool NotifyEnabled { get; set; } = true;
    public bool LeadNotified { get; set; }
    public bool DueNotified { get; set; }
}

public sealed record HistoryEntry(DateTimeOffset AtUtc, string Action, string Note);
public sealed record NotificationEvent(long TimerId, string Title, string Message, bool IsDue);

public sealed class ServerEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int QueryPort { get; set; }
    public bool Favorite { get; set; }
}
public sealed record ServerInfo(string Name, string Map, int Players, int MaxPlayers, int Bots);
public sealed record ServerObservation(DateTimeOffset AtUtc, int Players, int MaxPlayers);

public sealed class PlayerEntry
{
    public long Id { get; set; }
    public string SteamId { get; set; } = "";
    public string Label { get; set; } = "";
}
public sealed record SteamPlayerStatus(
    string SteamId, string? PersonaName, int PersonaState, bool ProfilePublic,
    bool? InRust, string? ReportedServer, DateTimeOffset FetchedAtUtc);
