using System.Net;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;

namespace RustCompanion.Core.Watch;

public sealed record MasterAddress(string Host, int Port)
{
    public string Key => $"{Host}:{Port}";
}

/// <summary>
/// Клиент мастер-сервера Steam (UDP, без ключа API): hl2master.steampowered.com:27011. Протокол «Master Server Query Protocol» (Valve Developer Community):
/// запрос 0x31 + регион + начальный адрес + фильтр; ответ FF FF FF FF 66 0A и записи по 6 байт (IPv4 + порт, порядок байт сетевой). Конец списка — 0.0.0.0:0.
/// Мастер-сервер возвращает только адреса (порты запросов), поэтому название и онлайн узнаются отдельным A2S_INFO.
/// Valve ограничивает частоту запросов к мастер-серверу — приложение делает не более одного поиска за действие пользователя и с паузой.
/// </summary>
public sealed class MasterServerClient
{
    public const string DefaultHost = "hl2master.steampowered.com";
    public const int DefaultPort = 27011;
    public const byte RegionAll = 0xFF;
    public const int MaxBatches = 4;
    private static readonly byte[] ResponseHeader = { 0xFF, 0xFF, 0xFF, 0xFF, 0x66, 0x0A };

    private readonly IUdpExchangeFactory _factory;
    private readonly ISettingsProvider _settings;
    private readonly RateLimiter _limiter;
    public string Host { get; }
    public int Port { get; }

    public MasterServerClient(IUdpExchangeFactory factory, ISettingsProvider settings, RateLimiter limiter, string host = DefaultHost, int port = DefaultPort)
    { _factory = factory; _settings = settings; _limiter = limiter; Host = host; Port = port; }

    public static byte[] BuildQuery(string seed, string filter)
    {
        var seedBytes = System.Text.Encoding.ASCII.GetBytes(seed + "\0");
        var filterBytes = System.Text.Encoding.UTF8.GetBytes(filter + "\0");
        var buf = new byte[2 + seedBytes.Length + filterBytes.Length];
        buf[0] = 0x31; buf[1] = RegionAll;
        seedBytes.CopyTo(buf, 2);
        filterBytes.CopyTo(buf, 2 + seedBytes.Length);
        return buf;
    }

    /// <summary>Разбирает один ответ. Done=true, если встретилась запись 0.0.0.0:0 (конец списка).</summary>
    public static (IReadOnlyList<MasterAddress> Addresses, bool Done) ParseResponse(byte[] p)
    {
        if (p.Length < ResponseHeader.Length || !p.AsSpan(0, ResponseHeader.Length).SequenceEqual(ResponseHeader))
            throw new A2sProtocolException("Ответ мастер-сервера с неожиданным заголовком.");
        var body = p.Length - ResponseHeader.Length;
        if (body % 6 != 0) throw new A2sProtocolException("Длина ответа мастер-сервера не кратна 6 байтам.");
        var list = new List<MasterAddress>();
        for (var i = ResponseHeader.Length; i + 6 <= p.Length; i += 6)
        {
            var port = (p[i + 4] << 8) | p[i + 5];
            if (p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 0 && p[i + 3] == 0 && port == 0) return (list, true);
            list.Add(new MasterAddress($"{p[i]}.{p[i + 1]}.{p[i + 2]}.{p[i + 3]}", port));
        }
        return (list, false);
    }

    /// <summary>Фильтр мастер-сервера. Спецсимволы фильтра (\ и управляющие) и «*» из запроса удаляются, чтобы пользователь не мог подмешать свои условия.</summary>
    public static string BuildNameFilter(string query, string appId = "252490")
    {
        var clean = new string(query.Where(c => c != '\\' && c != '*' && !char.IsControl(c)).ToArray()).Trim();
        return $"\\appid\\{appId}\\name_match\\*{clean}*";
    }

    public async Task<SourceResult<IReadOnlyList<MasterAddress>>> QueryAsync(string filter, int maxAddresses, CancellationToken ct)
    {
        await _limiter.WaitAsync("master", TimeSpan.FromMilliseconds(Math.Max(1500, _settings.Current.A2sMinIntervalMs)), ct);
        var perAttempt = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.RequestTimeoutSeconds / 2.0, 2, 6));
        var found = new List<MasterAddress>();
        try
        {
            using var x = await _factory.ConnectAsync(Host, Port, ct);
            var seed = "0.0.0.0:0";
            for (var batch = 0; batch < MaxBatches; batch++)
            {
                var reply = await A2sQueryClient.ExchangeAsync(x, BuildQuery(seed, filter), perAttempt, ct);
                var (addresses, done) = ParseResponse(reply);
                found.AddRange(addresses);
                if (done || addresses.Count == 0 || found.Count >= maxAddresses) break;
                seed = addresses[^1].Key;                                      // следующая порция начинается с последнего полученного адреса
            }
        }
        catch (TimeoutException)
        {
            return SourceResult<IReadOnlyList<MasterAddress>>.Fail(SourceState.NoResponse,
                "Мастер-сервер Steam не ответил (UDP 27011 может быть заблокирован сетью/файрволом или сервер ограничил частоту запросов).");
        }
        catch (A2sProtocolException ex) { return SourceResult<IReadOnlyList<MasterAddress>>.Fail(SourceState.Error, "Ответ мастер-сервера не распознан: " + ex.Message); }
        catch (System.Net.Sockets.SocketException ex) { return SourceResult<IReadOnlyList<MasterAddress>>.Fail(SourceState.NoResponse, $"Сетевая ошибка при обращении к мастер-серверу Steam ({ex.SocketErrorCode})."); }
        return SourceResult<IReadOnlyList<MasterAddress>>.Ok(found.DistinctBy(a => a.Key).Take(maxAddresses).ToList());
    }
}
