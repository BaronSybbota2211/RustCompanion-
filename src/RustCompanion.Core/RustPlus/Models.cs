namespace RustCompanion.Core.RustPlus;

public enum RustPlusState { Disconnected, Connecting, Connected, Reconnecting, AuthFailed }
public enum RpDeviceKind { SmartSwitch = 0, SmartAlarm = 1, StorageMonitor = 2 }

public sealed record RpResult(bool Ok, string? Code = null, string? Message = null)
{
    public static readonly RpResult Success = new(true);
    public static RpResult Fail(string? code, string? message) => new(false, code, message);
}

public sealed record RpResult<T>(bool Ok, T? Value, string? Code = null, string? Message = null)
{
    public static RpResult<T> Success(T v) => new(true, v);
    public static RpResult<T> Fail(string? code, string? message) => new(false, default, code, message);
    public RpResult AsPlain() => new(Ok, Code, Message);
}

public static class RpErrors
{
    /// <summary>Эвристика: ответы, говорящие о недействительном токене/авторизации. Точные коды Rust+ в документации библиотеки не описаны — решение по подстрокам.</summary>
    public static bool IsAuth(string? code, string? message)
    {
        var t = ((code ?? "") + " " + (message ?? "")).ToLowerInvariant();
        return t.Contains("unauthor") || t.Contains("forbidden") || t.Contains("invalid token") || t.Contains("player token") || t.Contains("not authorized") || t.Contains("auth");
    }
    public static bool IsAuth<T>(RpResult<T> r) => !r.Ok && IsAuth(r.Code, r.Message);
    public static bool IsAuth(RpResult r) => !r.Ok && IsAuth(r.Code, r.Message);

    // ---- Facepunch закрыл прямое подключение к Rust+ (ноябрь 2026) ----
    // Коды и тексты ниже намеренно НЕ содержат подстрок, которые ловит IsAuth (auth, forbidden, unauthor, invalid token, player token):
    // это не «сопряжение устарело», останавливать повторы и просить повторное сопряжение не нужно.

    /// <summary>Старый прокси Facepunch (/game/{ip}/{port}) и прямой порт Rust+ больше не работают: сервер отвечает HTTP 418.</summary>
    public const string DirectRemoved = "facepunch_direct_removed";
    public const string DirectRemovedMessage = "Facepunch закрыл прямое подключение к Rust+ и старый прокси (ответ HTTP 418). Сопряжение и данные в порядке — нужен новый канал подключения.";
    /// <summary>Новый канал (/socket/{ServerId}) отклонил вход (HTTP 403): токен входа Facepunch не принят.</summary>
    public const string CompanionLoginRejected = "companion_login_rejected";
    /// <summary>Новый канал не знает такой сервер (HTTP 404): ID сервера неверный или устарел.</summary>
    public const string CompanionServerUnknown = "companion_server_unknown";
    /// <summary>Для нового канала не хватает данных (токен входа Facepunch и/или ID сервера из уведомления о сопряжении).</summary>
    public const string CompanionNotReady = "companion_not_ready";

    public static bool IsDirectRemoved(string? code, string? message) =>
        string.Equals(code, DirectRemoved, StringComparison.Ordinal) || (message ?? "").Contains("status code '418'", StringComparison.Ordinal);
    public static bool IsDirectRemoved(RpResult r) => !r.Ok && IsDirectRemoved(r.Code, r.Message);

    public static bool IsCompanionLoginRejected(RpResult r) => !r.Ok && r.Code == CompanionLoginRejected;
    public static bool IsCompanionServerUnknown(RpResult r) => !r.Ok && r.Code == CompanionServerUnknown;

    /// <summary>Ответ getInfo «not_found» на уже открытом соединении = сервер не принял токен игрока (для запросов к устройствам то же значение — «устройства нет», поэтому проверяется только на getInfo).</summary>
    public static bool IsPlayerTokenRejected(RpResult r) => !r.Ok && (string.Equals(r.Code?.Trim(), "not_found", StringComparison.OrdinalIgnoreCase) || string.Equals(r.Message?.Trim(), "not_found", StringComparison.OrdinalIgnoreCase));
}

public sealed record PairedServer(long Id, string Name, string Ip, int Port, ulong PlayerId, DateTimeOffset PairedAtUtc, DateTimeOffset? LastConnectedUtc, string? RustPlusId = null);
/// <summary>
/// Данные для подключения к серверу Rust+. <see cref="ServerId"/> — ID сервера из уведомления о сопряжении (PairedServer.RustPlusId),
/// <see cref="CompanionToken"/> — токен входа Facepunch (после входа через Steam); вместе они нужны для нового канала /socket/{ServerId}.
/// Токены не попадают в ToString (журналы, сообщения об ошибках).
/// </summary>
public sealed record RpCredentials(string Ip, int Port, ulong PlayerId, int PlayerToken, string? ServerId = null, string? CompanionToken = null)
{
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append("Ip = ").Append(Ip).Append(", Port = ").Append(Port).Append(", PlayerId = ").Append(PlayerId)
          .Append(", PlayerToken = ***, ServerId = ").Append(ServerId ?? "—").Append(", CompanionToken = ").Append(string.IsNullOrEmpty(CompanionToken) ? "—" : "***");
        return true;
    }
}

public sealed record RpServerInfo(string? Name, int? Players, int? MaxPlayers, int? Queued, int? MapSize, string? Map, DateTimeOffset? WipeUtc, long? Seed = null);
public sealed record RpTime(double? Time, double? Sunrise, double? Sunset, double? DayLengthMinutes, double? TimeScale);
public sealed record RpMember(ulong SteamId, string Name, bool? Online, bool? Alive, double? X, double? Y);
/// <summary>Метка команды из Rust+ (getTeamInfo): точка смерти или отметка игрока на карте. Спальники и кровати этим каналом не передаются.</summary>
public sealed record RpTeamNote(bool IsDeath, bool FromLeader, double X, double Y, string? Label, int Icon, int Color);
public sealed record RpTeam(ulong? LeaderSteamId, IReadOnlyList<RpMember> Members, IReadOnlyList<RpTeamNote>? Notes = null);
public sealed record RpChatMessage(ulong? SteamId, string Name, string Text, DateTimeOffset? AtUtc)
{
    /// <summary>Ключ для дедупликации: одно и то же сообщение, полученное событием и опросом, обрабатывается один раз.</summary>
    public string Key => $"{SteamId}|{AtUtc?.ToUnixTimeSeconds()}|{Text}";
}
public sealed record RpItem(int ItemId, int Quantity, string? Name = null);
public sealed record RpDeviceState(RpDeviceKind Kind, bool? IsOn, IReadOnlyList<RpItem>? Items = null, bool? HasProtection = null);
public sealed record RpDeviceChange(ulong EntityId, RpDeviceState State, DateTimeOffset AtUtc);

public sealed class RustPlusSnapshot
{
    public RpServerInfo? Info { get; set; }
    public RpTime? Time { get; set; }
    public RpTeam? Team { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public string? InfoError { get; set; }
}

/// <summary>Граница с библиотекой Rust+. Реальная реализация — в отдельном проекте; ядро и тесты работают с этим интерфейсом.</summary>
public interface IRustPlusGateway : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<RpResult> ConnectAsync(RpCredentials credentials, CancellationToken ct);
    Task<RpResult<RpServerInfo>> GetInfoAsync(CancellationToken ct);
    Task<RpResult<RpTime>> GetTimeAsync(CancellationToken ct);
    Task<RpResult<RpTeam>> GetTeamAsync(CancellationToken ct);
    Task<RpResult<IReadOnlyList<RpChatMessage>>> GetTeamChatAsync(CancellationToken ct);
    Task<RpResult> SendTeamMessageAsync(string text, CancellationToken ct);
    Task<RpResult<RpDeviceState>> GetDeviceAsync(ulong entityId, RpDeviceKind kind, CancellationToken ct);
    Task<RpResult> SetSwitchAsync(ulong entityId, bool on, CancellationToken ct);
    event Action<RpChatMessage>? ChatReceived;
    event Action<ulong, RpDeviceState>? DeviceChanged;
    event Action<string>? ConnectionLost;
    /// <summary>Диагностические сообщения шлюза (без секретов). По умолчанию не используются.</summary>
    event Action<string>? Trace { add { } remove { } }
    Task<RpResult<RpMap>> GetMapAsync(CancellationToken ct) => Task.FromResult(RpResult<RpMap>.Fail("not_supported", "Карта не поддерживается."));
    Task<RpResult<IReadOnlyList<RpMapMarker>>> GetMapMarkersAsync(CancellationToken ct) => Task.FromResult(RpResult<IReadOnlyList<RpMapMarker>>.Fail("not_supported", "Маркеры не поддерживаются."));
}
