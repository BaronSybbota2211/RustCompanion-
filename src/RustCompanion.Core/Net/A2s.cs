using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using RustCompanion.Core.Models;

namespace RustCompanion.Core.Net;

// Протокол Steam Server Queries (Valve Developer Community, «Server queries»): A2S_INFO (0x54), A2S_PLAYER (0x55), challenge (0x41),
// «простые» пакеты (заголовок FF FF FF FF) и ответы, разбитые на части (заголовок FE FF FF FF).

public sealed class A2sProtocolException : Exception
{
    public A2sProtocolException(string message) : base(message) { }
}

/// <summary>Ответ A2S_INFO. Для Rust: сервер передаёт точные «cp»/«mp» в тегах (keywords), потому что поля Players/MaxPlayers — один байт (до 255).</summary>
public sealed record A2sInfoResponse(byte Protocol, string Name, string Map, string Folder, string Game, int AppId, int Players, int MaxPlayers, int Bots,
    char ServerType, char Environment, bool PasswordProtected, bool Vac, string Version, int? GamePort, ulong? SteamId, string? Keywords, ulong? GameId)
{
    private static readonly Regex TagRx = new(@"^(mp|cp|qp)(\d{1,6})$", RegexOptions.Compiled);

    /// <summary>Значение тега Rust (mp — максимум, cp — онлайн, qp — очередь) или null, если тега нет.</summary>
    public int? Tag(string prefix)
    {
        if (string.IsNullOrEmpty(Keywords)) return null;
        foreach (var raw in Keywords.Split(','))
        {
            var m = TagRx.Match(raw.Trim());
            if (m.Success && m.Groups[1].Value == prefix && int.TryParse(m.Groups[2].Value, out var v)) return v;
        }
        return null;
    }

    /// <summary>Онлайн: тег cp, если есть (не ограничен 255), иначе байт из A2S_INFO.</summary>
    public int ReportedPlayers => Tag("cp") ?? Players;
    public int ReportedMaxPlayers => Tag("mp") ?? MaxPlayers;
    public int? Queue => Tag("qp");
}

/// <summary>Игрок из A2S_PLAYER. DurationSeconds — время подключения, как его сообщил сервер (может быть NaN/отрицательным у «странных» серверов).</summary>
public sealed record A2sPlayerEntry(int Index, string Name, int Score, float DurationSeconds)
{
    public bool HasValidDuration => float.IsFinite(DurationSeconds) && DurationSeconds >= 0 && DurationSeconds < 60f * 60 * 24 * 365;
}

public sealed record A2sPlayersResponse(IReadOnlyList<A2sPlayerEntry> Players, int DeclaredCount, bool Truncated);

internal sealed class A2sReader
{
    private readonly byte[] _b;
    private int _pos;
    public A2sReader(byte[] data, int position) { _b = data; _pos = position; }
    public int Remaining => _b.Length - _pos;

    private void Need(int n) { if (n < 0 || _b.Length - _pos < n) throw new A2sProtocolException("Пакет оборван: данных меньше, чем требует формат."); }
    public byte Byte() { Need(1); return _b[_pos++]; }
    public int UInt16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_b.AsSpan(_pos, 2)); _pos += 2; return v; }
    public int Int32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(_pos, 4)); _pos += 4; return v; }
    public ulong UInt64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_b.AsSpan(_pos, 8)); _pos += 8; return v; }
    public float Single() => BitConverter.Int32BitsToSingle(Int32());

    public string CString()
    {
        var start = _pos;
        while (_pos < _b.Length && _b[_pos] != 0) _pos++;
        if (_pos >= _b.Length) throw new A2sProtocolException("Строка без завершающего нуля.");
        var s = Encoding.UTF8.GetString(_b, start, _pos - start);   // некорректный UTF-8 даёт символ замены, а не исключение
        _pos++;
        return s;
    }
}

public static class A2sProtocol
{
    public const byte InfoRequest = 0x54;
    public const byte PlayerRequest = 0x55;
    public const byte ChallengeResponse = 0x41;
    public const byte InfoResponse = 0x49;
    public const byte GoldSrcInfoResponse = 0x6D;
    public const byte PlayerResponse = 0x44;
    /// <summary>Начальное значение challenge для A2S_PLAYER (0xFFFFFFFF).</summary>
    public const int InitialChallenge = -1;

    private static readonly byte[] SimpleHeader = { 0xFF, 0xFF, 0xFF, 0xFF };
    private static readonly byte[] InfoPayload = Encoding.ASCII.GetBytes("Source Engine Query\0");

    public static bool IsSimplePacket(byte[] b) => b.Length >= 5 && b.AsSpan(0, 4).SequenceEqual(SimpleHeader);

    public static byte[] BuildInfoRequest(int? challenge)
    {
        var buf = new byte[5 + InfoPayload.Length + (challenge.HasValue ? 4 : 0)];
        SimpleHeader.CopyTo(buf, 0);
        buf[4] = InfoRequest;
        InfoPayload.CopyTo(buf, 5);
        if (challenge.HasValue) BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5 + InfoPayload.Length, 4), challenge.Value);
        return buf;
    }

    public static byte[] BuildPlayerRequest(int challenge)
    {
        var buf = new byte[9];
        SimpleHeader.CopyTo(buf, 0);
        buf[4] = PlayerRequest;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(5, 4), challenge);
        return buf;
    }

    public static bool TryParseChallenge(byte[] packet, out int challenge)
    {
        challenge = 0;
        if (!IsSimplePacket(packet) || packet[4] != ChallengeResponse || packet.Length < 9) return false;
        challenge = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(5, 4));
        return true;
    }

    public static A2sInfoResponse ParseInfo(byte[] p)
    {
        if (!IsSimplePacket(p)) throw new A2sProtocolException("Ответ без заголовка 0xFFFFFFFF.");
        if (p[4] == GoldSrcInfoResponse) throw new A2sProtocolException("Устаревший формат ответа GoldSrc (0x6D) не поддерживается.");
        if (p[4] != InfoResponse) throw new A2sProtocolException($"Ожидался ответ A2S_INFO (0x49), получен 0x{p[4]:X2}.");

        var r = new A2sReader(p, 5);
        var protocol = r.Byte();
        var name = r.CString(); var map = r.CString(); var folder = r.CString(); var game = r.CString();
        var appId = r.UInt16();
        int players = r.Byte(), max = r.Byte(), bots = r.Byte();
        var type = (char)r.Byte(); var env = (char)r.Byte();
        var passworded = r.Byte() == 1; var vac = r.Byte() == 1;
        var version = r.CString();

        int? port = null; ulong? steamId = null; string? keywords = null; ulong? gameId = null;
        if (r.Remaining > 0)
        {
            try
            {
                var edf = r.Byte();
                if ((edf & 0x80) != 0) port = r.UInt16();
                if ((edf & 0x10) != 0) steamId = r.UInt64();
                if ((edf & 0x40) != 0) { r.UInt16(); r.CString(); }       // порт и имя SourceTV — не используются
                if ((edf & 0x20) != 0) keywords = r.CString();
                if ((edf & 0x01) != 0) gameId = r.UInt64();
            }
            catch (A2sProtocolException) { /* необязательный блок оборван: основные поля уже получены */ }
        }
        return new A2sInfoResponse(protocol, name, map, folder, game, appId, players, max, bots, type, env, passworded, vac, version, port, steamId, keywords, gameId);
    }

    public static A2sPlayersResponse ParsePlayers(byte[] p)
    {
        if (!IsSimplePacket(p)) throw new A2sProtocolException("Ответ без заголовка 0xFFFFFFFF.");
        if (p[4] != PlayerResponse) throw new A2sProtocolException($"Ожидался ответ A2S_PLAYER (0x44), получен 0x{p[4]:X2}.");
        var r = new A2sReader(p, 5);
        var declared = r.Byte();
        var list = new List<A2sPlayerEntry>(declared);
        var truncated = false;
        for (var i = 0; i < declared; i++)
        {
            if (r.Remaining < 1 + 1 + 4 + 4) { truncated = true; break; }      // индекс + имя (минимум нуль) + score + duration
            try
            {
                var index = r.Byte(); var name = r.CString(); var score = r.Int32(); var duration = r.Single();
                list.Add(new A2sPlayerEntry(index, name, score, duration));
            }
            catch (A2sProtocolException) { truncated = true; break; }
        }
        return new A2sPlayersResponse(list, declared, truncated);
    }
}

/// <summary>Сборка ответа из частей (заголовок FE FF FF FF, id, всего, номер, размер). Поддерживаются несжатые ответы; сжатие bzip2 (старший бит id) не поддерживается.</summary>
public sealed class A2sSplitAssembler
{
    public const int MaxParts = 32;
    public const int MaxTotalBytes = 64 * 1024;
    private static readonly byte[] SplitHeader = { 0xFE, 0xFF, 0xFF, 0xFF };
    private const int HeaderSize = 12;
    private int? _id;
    private int _total;
    private readonly SortedDictionary<int, byte[]> _parts = new();

    public static bool IsSplit(byte[] d) => d.Length >= 4 && d.AsSpan(0, 4).SequenceEqual(SplitHeader);

    /// <summary>Добавляет датаграмму. Возвращает полный ответ, когда получены все части; иначе null. Части чужого запроса (другой id) и повторы игнорируются.</summary>
    public byte[]? Add(byte[] d)
    {
        if (!IsSplit(d) || d.Length < HeaderSize) throw new A2sProtocolException("Некорректный заголовок разбитого пакета.");
        var id = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4, 4));
        int total = d[8], number = d[9];
        if (id < 0) throw new A2sProtocolException("Ответ разбит на части и сжат bzip2 — такой формат не поддерживается.");
        if (total == 0 || total > MaxParts) throw new A2sProtocolException($"Недопустимое число частей ответа: {total}.");
        if (number >= total) throw new A2sProtocolException("Номер части больше общего числа частей.");

        if (_id is null) { _id = id; _total = total; }
        else if (_id != id) return null;
        else if (_total != total) throw new A2sProtocolException("Части одного ответа расходятся в общем числе.");

        _parts[number] = d.AsSpan(HeaderSize).ToArray();
        if (_parts.Count < _total) return null;

        var size = 0;
        foreach (var part in _parts.Values) size += part.Length;
        if (size > MaxTotalBytes) throw new A2sProtocolException("Ответ слишком большой.");
        var result = new byte[size];
        var pos = 0;
        foreach (var part in _parts.Values) { part.CopyTo(result, pos); pos += part.Length; }   // SortedDictionary перебирает части по номеру
        return result;
    }
}

public interface IUdpExchange : IDisposable
{
    Task SendAsync(byte[] datagram, CancellationToken ct);
    Task<byte[]> ReceiveAsync(CancellationToken ct);
}

public interface IUdpExchangeFactory
{
    Task<IUdpExchange> ConnectAsync(string host, int port, CancellationToken ct);
}

internal sealed class UdpExchange : IUdpExchange
{
    private readonly UdpClient _udp;
    public UdpExchange(UdpClient udp) { _udp = udp; }
    public async Task SendAsync(byte[] datagram, CancellationToken ct) => await _udp.SendAsync(datagram.AsMemory(), ct);
    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => (await _udp.ReceiveAsync(ct)).Buffer;
    public void Dispose() => _udp.Dispose();
}

public sealed class UdpExchangeFactory : IUdpExchangeFactory
{
    public async Task<IUdpExchange> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(host, ct);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        if (address is null) throw new SocketException((int)SocketError.HostNotFound);
        var udp = new UdpClient(address.AddressFamily);
        try { udp.Connect(address, port); return new UdpExchange(udp); }
        catch { udp.Dispose(); throw; }
    }
}

public enum PlayerListStatus { Provided, Empty, NotProvided, Partial, NoResponse, Error, Suspicious }

public static class PlayerListStatusText
{
    public static string Ru(this PlayerListStatus s) => s switch
    {
        PlayerListStatus.Provided => "Список получен",
        PlayerListStatus.Empty => "На сервере нет игроков",
        PlayerListStatus.NotProvided => "Сервер не отдаёт список игроков",
        PlayerListStatus.Partial => "Список неполный",
        PlayerListStatus.NoResponse => "Нет ответа на запрос игроков",
        PlayerListStatus.Suspicious => "Список игроков недостоверен (сервер, вероятно, подменяет данные)",
        _ => "Ошибка запроса игроков"
    };
}

/// <summary>Результат опроса сервера: A2S_INFO и A2S_PLAYER с честным статусом списка игроков.</summary>
public sealed record A2sSnapshot(A2sInfoResponse? Info, IReadOnlyList<A2sPlayerEntry> Players, PlayerListStatus ListStatus, string? Note);

/// <summary>
/// Признаки того, что сервер подменяет ответ A2S_PLAYER (выдуманные ники, накрученный онлайн). Протокол Steam Query не даёт способа проверить игроков,
/// поэтому оцениваются только внутренние противоречия самого ответа. Порог осторожный: ложное срабатывание хуже пропуска, но очевидные подделки отсекаются.
/// </summary>
public static class A2sListAuditor
{
    public static IReadOnlyList<string> Audit(IReadOnlyList<A2sPlayerEntry> list, int? reported)
    {
        var reasons = new List<string>(); var n = list.Count;
        if (n < 5) return reasons;
        if (reported is int r && n > r + Math.Max(3, r / 10))
            reasons.Add($"в списке {n} игроков, а сервер заявляет онлайн {r}");
        if (n >= 8)
        {
            var dupNames = n - list.Select(p => p.Name.Trim().ToLowerInvariant()).Distinct().Count();
            if (dupNames * 100 >= n * 30) reasons.Add($"{dupNames} из {n} ников повторяются");
            var valid = list.Where(p => p.HasValidDuration).Select(p => (int)Math.Round(p.DurationSeconds)).ToList();
            if (valid.Count == 0 || valid.All(d => d == 0)) reasons.Add("у всех игроков нулевое или неверное время на сервере");
            else
            {
                var top = valid.GroupBy(d => d).Max(g => g.Count());
                if (top * 100 >= valid.Count * 60) reasons.Add($"у {top} из {valid.Count} игроков одинаковое время на сервере");
            }
        }
        return reasons;
    }
}

public static class A2sSnapshotBuilder
{
    /// <summary>Допуск при сравнении длины списка с онлайном: 2 игрока или 5%.</summary>
    public static int Tolerance(int reported) => Math.Max(2, reported / 20);

    /// <summary>Классифицирует ответ A2S_PLAYER. Пустой список при ненулевом онлайне — «сервер не отдаёт список», а не «игроков нет».</summary>
    public static A2sSnapshot Classify(A2sInfoResponse? info, A2sPlayersResponse players)
    {
        var reported = info?.ReportedPlayers;
        var list = players.Players;
        if (list.Count == 0)
            return reported is > 0
                ? new A2sSnapshot(info, list, PlayerListStatus.NotProvided, $"Сервер сообщает об игроках онлайн ({reported}), но список пуст: сервер скрывает или не отдаёт список игроков по Steam Query.")
                : new A2sSnapshot(info, list, PlayerListStatus.Empty, null);
        if (list.All(p => string.IsNullOrWhiteSpace(p.Name)))
            return new A2sSnapshot(info, Array.Empty<A2sPlayerEntry>(), PlayerListStatus.NotProvided, "Сервер вернул записи без ников — список игроков скрыт.");
        if (players.Truncated)
            return new A2sSnapshot(info, list, PlayerListStatus.Partial, "Ответ сервера оборван: получена только часть списка игроков.");
        if (reported is int n && list.Count < n - Tolerance(n))
            return new A2sSnapshot(info, list, PlayerListStatus.Partial, $"Список неполный: получено {list.Count} из {n} игроков (сервер или протокол ограничивают размер ответа).");
        var reasons = A2sListAuditor.Audit(list, reported);
        if (reasons.Count > 0)
            return new A2sSnapshot(info, list, PlayerListStatus.Suspicious,
                "Список игроков недостоверен: " + string.Join("; ", reasons) + ". Ники и число игроков, которые отдаёт этот сервер по Steam Query, нельзя считать настоящими — приложение не показывает такой список и не делает по нему выводов о присутствии игроков.");
        return new A2sSnapshot(info, list, PlayerListStatus.Provided, null);
    }
}

/// <summary>
/// Прямые UDP-запросы A2S_INFO / A2S_PLAYER: challenge, повторы при потере датаграмм, тайм-ауты, разбитые на части ответы.
/// Ошибки возвращаются как SourceResult (NoResponse — тайм-аут или сетевая ошибка; Error — ответ не по протоколу). Отмена по токену пробрасывается как OperationCanceledException.
/// </summary>
public sealed class A2sQueryClient
{
    public const int MaxChallengeRounds = 3;
    public const int Attempts = 2;
    private readonly IUdpExchangeFactory _factory;
    private readonly ISettingsProvider _settings;
    private readonly RateLimiter _limiter;

    public A2sQueryClient(IUdpExchangeFactory factory, ISettingsProvider settings, RateLimiter limiter)
    { _factory = factory; _settings = settings; _limiter = limiter; }

    private TimeSpan PerAttempt => TimeSpan.FromSeconds(Math.Clamp(_settings.Current.RequestTimeoutSeconds / 2.0, 2, 6));

    /// <summary>rateLimited=false — для пакетного поиска, где число параллельных запросов ограничивает сам вызывающий.</summary>
    public Task<SourceResult<A2sInfoResponse>> GetInfoAsync(string host, int port, CancellationToken ct, bool rateLimited = true) =>
        RunAsync(host, port, async (x, c) => A2sProtocol.ParseInfo(await QueryInfoRawAsync(x, c)), ct, rateLimited);

    public Task<SourceResult<A2sPlayersResponse>> GetPlayersAsync(string host, int port, CancellationToken ct) =>
        RunAsync(host, port, async (x, c) => A2sProtocol.ParsePlayers(await QueryPlayersRawAsync(x, c)), ct);

    /// <summary>INFO + PLAYER по одному сокету. Если INFO получен, а игроки нет — результат Ok со статусом списка NoResponse/Error. Если не получено ничего — Fail.</summary>
    public Task<SourceResult<A2sSnapshot>> GetSnapshotAsync(string host, int port, CancellationToken ct) =>
        RunAsync(host, port, async (x, c) =>
        {
            A2sInfoResponse? info = null;
            string? infoError = null;
            try { info = A2sProtocol.ParseInfo(await QueryInfoRawAsync(x, c)); }
            catch (A2sProtocolException ex) { infoError = ex.Message; }          // таймаут INFO пробросится: без INFO сервер, как правило, не отвечает и на PLAYER

            try
            {
                var players = A2sProtocol.ParsePlayers(await QueryPlayersRawAsync(x, c));
                return A2sSnapshotBuilder.Classify(info, players);
            }
            catch (TimeoutException) when (info is not null)
            { return new A2sSnapshot(info, Array.Empty<A2sPlayerEntry>(), PlayerListStatus.NoResponse, "Сервер ответил на A2S_INFO, но не ответил на запрос списка игроков (A2S_PLAYER)."); }
            catch (A2sProtocolException ex) when (info is not null)
            { return new A2sSnapshot(info, Array.Empty<A2sPlayerEntry>(), PlayerListStatus.Error, "Ответ A2S_PLAYER не распознан: " + ex.Message); }
            catch (A2sProtocolException ex)
            { throw new A2sProtocolException(infoError is null ? ex.Message : infoError + " " + ex.Message); }
        }, ct);

    private async Task<SourceResult<T>> RunAsync<T>(string host, int port, Func<IUdpExchange, CancellationToken, Task<T>> work, CancellationToken ct, bool rateLimited = true)
    {
        if (port is < 1 or > 65535) return SourceResult<T>.Fail(SourceState.Error, "Некорректный порт запросов.");
        if (rateLimited) await _limiter.WaitAsync("a2s", TimeSpan.FromMilliseconds(_settings.Current.A2sMinIntervalMs), ct);
        try
        {
            using var x = await _factory.ConnectAsync(host, port, ct);
            return SourceResult<T>.Ok(await work(x, ct));
        }
        catch (TimeoutException)
        {
            return SourceResult<T>.Fail(SourceState.NoResponse,
                "Нет ответа на Steam Query. Это не означает, что сервер выключен: порт запросов может быть закрыт, указан неверно или запросы блокируются.");
        }
        catch (A2sProtocolException ex)
        { return SourceResult<T>.Fail(SourceState.Error, "Ответ сервера не распознан: " + ex.Message); }
        catch (SocketException ex)
        { return SourceResult<T>.Fail(SourceState.NoResponse, $"Сетевая ошибка при обращении к серверу ({ex.SocketErrorCode}): адрес не найден или порт недоступен."); }
    }

    private Task<byte[]> QueryInfoRawAsync(IUdpExchange x, CancellationToken ct) =>
        QueryWithChallengeAsync(x, c => A2sProtocol.BuildInfoRequest(c), null, A2sProtocol.InfoResponse, ct);

    private Task<byte[]> QueryPlayersRawAsync(IUdpExchange x, CancellationToken ct) =>
        QueryWithChallengeAsync(x, c => A2sProtocol.BuildPlayerRequest(c ?? A2sProtocol.InitialChallenge), A2sProtocol.InitialChallenge, A2sProtocol.PlayerResponse, ct);

    private async Task<byte[]> QueryWithChallengeAsync(IUdpExchange x, Func<int?, byte[]> build, int? firstChallenge, byte expected, CancellationToken ct)
    {
        var challenge = firstChallenge;
        for (var round = 0; round <= MaxChallengeRounds; round++)
        {
            var reply = await ExchangeAsync(x, build(challenge), PerAttempt, ct);
            if (A2sProtocol.TryParseChallenge(reply, out var next)) { challenge = next; continue; }
            if (reply[4] != expected) throw new A2sProtocolException($"Неожиданный тип ответа 0x{reply[4]:X2} (ожидался 0x{expected:X2}).");
            return reply;
        }
        throw new A2sProtocolException("Сервер снова и снова запрашивает challenge — ответ не получен.");
    }

    /// <summary>Отправляет запрос и ждёт полный ответ (простой или собранный из частей). Повторяет отправку при тайм-ауте попытки; после всех попыток — TimeoutException.</summary>
    internal static async Task<byte[]> ExchangeAsync(IUdpExchange x, byte[] request, TimeSpan perAttempt, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            await x.SendAsync(request, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(perAttempt);
            var assembler = new A2sSplitAssembler();
            try
            {
                while (true)
                {
                    var datagram = await x.ReceiveAsync(cts.Token);
                    if (A2sProtocol.IsSimplePacket(datagram)) return datagram;
                    if (A2sSplitAssembler.IsSplit(datagram))
                    {
                        var whole = assembler.Add(datagram);
                        if (whole is not null)
                        {
                            if (!A2sProtocol.IsSimplePacket(whole)) throw new A2sProtocolException("Собранный из частей ответ не содержит заголовка 0xFFFFFFFF.");
                            return whole;
                        }
                    }
                    // посторонние датаграммы игнорируются
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* тайм-аут попытки: повторяем запрос */ }
        }
        throw new TimeoutException("Нет ответа сервера.");
    }
}
