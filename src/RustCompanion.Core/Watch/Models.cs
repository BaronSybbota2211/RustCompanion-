using RustCompanion.Core.Net;

namespace RustCompanion.Core.Watch;

public enum PresenceStatus { NoData = 0, OnServer = 1, NotDetected = 2, ExitConfirmed = 3, CheckError = 4 }
public enum PresenceKind { Present, Absent, Unknown }
public enum WatchEventKind { Join, Exit }

public static class PresenceText
{
    public static string Ru(this PresenceStatus s) => s switch
    {
        PresenceStatus.OnServer => "На сервере",
        PresenceStatus.NotDetected => "Не обнаружен",
        PresenceStatus.ExitConfirmed => "Выход подтверждён",
        PresenceStatus.CheckError => "Ошибка проверки",
        _ => "Нет данных"
    };
}

/// <summary>Результат одной проверки. Absent — только если источник ЯВНО сообщил, что игрока на сервере нет.</summary>
public sealed record PresenceObservation(PresenceKind Kind, DateTimeOffset ObservedAtUtc,
    DateTimeOffset? SourceTimestampUtc = null, DateTimeOffset? LastSeenUtc = null, bool IsError = false);

public sealed class MonitorState
{
    public long WatchId { get; set; }
    public PresenceStatus Status { get; set; } = PresenceStatus.NoData;
    public int AbsenceCount { get; set; }
    public DateTimeOffset? LastPresentUtc { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
    public DateTimeOffset? LastCountedSourceTs { get; set; }
    public bool ExitNotified { get; set; }
    public int Episode { get; set; }
    public string? LastError { get; set; }
}

public sealed record WatchEvent(WatchEventKind Kind, long WatchId, string EventKey, DateTimeOffset AtUtc,
    DateTimeOffset? LastPresentUtc, int Confirmations);

public sealed class WatchGroup
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#D9653B";
    public bool NotifyJoin { get; set; } = true;
    public bool NotifyLeave { get; set; } = true;
    public int Threshold { get; set; }
}

public sealed class WatchEntry
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public string PlayerKey { get; set; } = "";
    public string? SteamId { get; set; }
    /// <summary>Заданный пользователем уровень: 0 — по нику (не подтверждено), 1 — ник связан пользователем со SteamID64. Подтверждение Steam определяется на лету (<see cref="SteamMatch.Effective"/>) и не хранится.</summary>
    public IdentityLevel IdentityLevel { get; set; } = IdentityLevel.Unconfirmed;
    public string Name { get; set; } = "";
    public long? GroupId { get; set; }
    public string? Color { get; set; }
    public bool NotifyJoin { get; set; } = true;
    public bool NotifyLeave { get; set; } = true;
}

public sealed record ActivityEvent(long WatchId, DateTimeOffset AtUtc, string Kind, string Detail);
/// <summary>Сервер из поиска или опроса. Id — «хост:порт запросов A2S»; Port — игровой порт (если сервер или Steam его сообщил), он нужен для net.connect.</summary>
public sealed record FoundServer(string Id, string Name, string? Ip, int? Port, int? Players, int? MaxPlayers, string? Status, string? Map = null, string? Country = null);
/// <summary>Игрок из A2S_PLAYER. Steam Query не передаёт SteamID — идентификатор игрока совпадает с ником. SessionSeconds — время подключения, как его сообщил сервер (не проверяется).</summary>
public sealed record OnlinePlayer(string Id, string Name, double? SessionSeconds = null);
/// <summary>Снимок сервера с игроками. ListProvided=false — сервер не отдал список игроков (см. ListStatus и Note); пустой список в этом случае не выдаётся за «игроков нет».</summary>
public sealed record ServerSnapshot(FoundServer Server, IReadOnlyList<OnlinePlayer> Players, bool ListProvided,
    PlayerListStatus ListStatus = PlayerListStatus.Provided, string? Note = null);
public sealed record FoundPlayer(string Id, string Name);
/// <summary>Интервал присутствия по локальным наблюдениям: Start — первое обнаружение, Stop — последнее (null — игрок ещё виден). Это оценка по опросам, а не точное игровое время.</summary>
public sealed record PresenceSession(DateTimeOffset Start, DateTimeOffset? Stop, string? ServerId);

/// <summary>Правило «3 успешных подтверждения отсутствия». Чистая функция состояния — без сети и БД.</summary>
public static class PresenceMonitor
{
    public const int DefaultRequiredConfirmations = 3;

    public sealed record Outcome(MonitorState State, IReadOnlyList<WatchEvent> Events);

    public static Outcome Apply(MonitorState s, PresenceObservation o, int required = DefaultRequiredConfirmations, bool allowUntimestampedAbsence = true)
    {
        var events = new List<WatchEvent>();
        s.LastCheckedUtc = o.ObservedAtUtc;

        if (o.IsError || o.Kind == PresenceKind.Unknown)
        {
            // Ошибки и неопределённые ответы НЕ считаются отсутствием и счётчик не меняют.
            if (s.Status != PresenceStatus.ExitConfirmed)
                s.Status = o.IsError ? PresenceStatus.CheckError : PresenceStatus.NoData;
            return new Outcome(s, events);
        }
        s.LastError = null;

        if (o.Kind == PresenceKind.Present)
        {
            var wasAway = s.Status == PresenceStatus.ExitConfirmed; // «вернулся» только после подтверждённого выхода
            if (s.Status == PresenceStatus.ExitConfirmed) { s.Episode++; }
            s.Status = PresenceStatus.OnServer;
            s.AbsenceCount = 0;
            s.ExitNotified = false;
            s.LastCountedSourceTs = null;
            s.LastPresentUtc = o.ObservedAtUtc;
            if (wasAway)
                events.Add(new WatchEvent(WatchEventKind.Join, s.WatchId, $"{s.WatchId}:{s.Episode}:join", o.ObservedAtUtc, s.LastPresentUtc, 0));
            return new Outcome(s, events);
        }

        // Absent
        if (s.Status == PresenceStatus.ExitConfirmed) return new Outcome(s, events); // уже подтверждено, не накручиваем

        if (o.SourceTimestampUtc is { } ts)
        {
            if (s.LastCountedSourceTs is { } last && ts <= last)
            { s.Status = s.Status == PresenceStatus.OnServer ? PresenceStatus.NotDetected : s.Status; return new Outcome(s, events); } // устаревшие данные не считаются
        }
        else if (!allowUntimestampedAbsence)
        { if (s.Status != PresenceStatus.OnServer) s.Status = PresenceStatus.NoData; return new Outcome(s, events); }

        s.AbsenceCount++;
        s.LastCountedSourceTs = o.SourceTimestampUtc;
        s.Status = PresenceStatus.NotDetected;

        if (s.LastPresentUtc is not null && s.AbsenceCount >= required && !s.ExitNotified)
        {
            s.Status = PresenceStatus.ExitConfirmed;
            s.ExitNotified = true;
            events.Add(new WatchEvent(WatchEventKind.Exit, s.WatchId, $"{s.WatchId}:{s.Episode}:exit", o.ObservedAtUtc, s.LastPresentUtc, s.AbsenceCount));
        }
        return new Outcome(s, events);
    }
}

public static class ActivityStats
{
    public static TimeSpan? Duration(DateTimeOffset? start, DateTimeOffset? stop) =>
        start is null || stop is null || stop < start ? null : stop - start;

    public static TimeSpan? ActiveElapsed(DateTimeOffset? start, DateTimeOffset now) =>
        start is null || now < start ? null : now - start;

    public static TimeSpan Total(IEnumerable<PresenceSession> sessions, DateTimeOffset now) =>
        sessions.Aggregate(TimeSpan.Zero, (acc, s) => acc + (Duration(s.Start, s.Stop ?? now) ?? TimeSpan.Zero));

    /// <summary>Часы по календарным дням (локальная дата) за период; сессии, пересекающие полночь, делятся.</summary>
    public static SortedDictionary<DateOnly, double> DailyHours(IEnumerable<PresenceSession> sessions, DateTimeOffset now, int days, TimeZoneInfo tz)
    {
        var result = new SortedDictionary<DateOnly, double>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime);
        for (var i = days - 1; i >= 0; i--) result[today.AddDays(-i)] = 0;
        foreach (var s in sessions)
        {
            var start = TimeZoneInfo.ConvertTime(s.Start, tz).DateTime;
            var stop = TimeZoneInfo.ConvertTime(s.Stop ?? now, tz).DateTime;
            if (stop <= start) continue;
            for (var cur = start; cur < stop;)
            {
                var next = cur.Date.AddDays(1) < stop ? cur.Date.AddDays(1) : stop;
                var key = DateOnly.FromDateTime(cur);
                if (result.ContainsKey(key)) result[key] += (next - cur).TotalHours;
                cur = next;
            }
        }
        return result;
    }
}

public static class GroupThreshold
{
    public sealed record Result(bool Above, int Episode, bool Crossed);

    /// <summary>Пересечение порога «N игроков группы на одном сервере»: событие один раз на пересечение снизу вверх.</summary>
    public static Result Evaluate(int onlineInGroup, int threshold, bool wasAbove, int episode)
    {
        if (threshold <= 0) return new Result(false, episode, false);
        var above = onlineInGroup >= threshold;
        if (above && !wasAbove) return new Result(true, episode + 1, true);
        return new Result(above, episode, false);
    }
}
