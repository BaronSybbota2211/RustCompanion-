using System.Text;
using System.Text.RegularExpressions;

namespace RustCompanion.Core.RustPlus;

public enum RpRouteKind { CompanionSocket, Direct }

public sealed record RpRoute(RpRouteKind Kind, string Name);

/// <summary>План подключения: в каком порядке пробовать маршруты и чего не хватает для нового канала.</summary>
public sealed record RpRoutePlan(IReadOnlyList<RpRoute> Routes, string? CompanionMissing)
{
    public bool HasCompanion => Routes.Any(r => r.Kind == RpRouteKind.CompanionSocket);
}

/// <summary>
/// Маршруты подключения к Rust+ после того, как Facepunch убрал прямые подключения (ноябрь 2026).
/// Источники (сторонние, код проверен, это не документация Facepunch):
///  — HandyS11/RustPlusApi, ветка feat/companion-socket, RustPlusEndpoint.cs: адрес wss://companion-rust.facepunch.com/socket/{ServerId:D},
///    токен входа передаётся двумя WebSocket-субпротоколами: «rustplus.auth.v1» и base64url(UTF-8(токен)) без «=»;
///  — Pronwan/rustplus-desktop, Services/RustPlusClientReal.cs: порядок «сначала сокет, затем прямое подключение»; старый /game/{ip}/{port} не пробуется (всегда 418);
///    403 при рукопожатии сокета = токен входа отклонён, getInfo «not_found» = неверный токен игрока.
/// Класс чистый (без сети), чтобы выбор маршрута и разбор ошибок проверялись тестами.
/// </summary>
public static class RustPlusRoutes
{
    public static readonly Uri CompanionBase = new("wss://companion-rust.facepunch.com/");
    /// <summary>Первый субпротокол: объявляет, что следующий — токен входа.</summary>
    public const string AuthSubProtocol = "rustplus.auth.v1";

    public static bool TryGetServerId(string? text, out Guid id)
    {
        id = Guid.Empty;
        return !string.IsNullOrWhiteSpace(text) && Guid.TryParse(text.Trim(), out id) && id != Guid.Empty;
    }

    public static Uri CompanionUri(Guid serverId) => new(CompanionBase, $"socket/{serverId:D}");

    public static string ToBase64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Субпротоколы рукопожатия нового канала, по порядку.</summary>
    public static IReadOnlyList<string> SubProtocols(string companionToken) => new[] { AuthSubProtocol, ToBase64Url(companionToken) };

    /// <summary>Префикс служебного «адреса» сервера, у которого уведомление о сопряжении пришло без IP/порта (новый протокол Facepunch): работает только новый канал.</summary>
    public const string NoAddressPrefix = "companion:";
    public static bool HasDirectAddress(string? ip, int port) => !string.IsNullOrWhiteSpace(ip) && !ip.StartsWith(NoAddressPrefix, StringComparison.Ordinal) && port is >= 1 and <= 65535;

    public static RpRoutePlan Plan(RpCredentials c)
    {
        var routes = new List<RpRoute>();
        var missing = new List<string>();
        if (!TryGetServerId(c.ServerId, out _)) missing.Add("нет ID сервера (он приходит в уведомлении о сопряжении: в игре Esc → Rust+ → Pair with server)");
        if (string.IsNullOrWhiteSpace(c.CompanionToken)) missing.Add("нет токена входа Facepunch (на вкладке «Rust+» нажмите «1. Вход через Steam»)");
        if (missing.Count == 0) routes.Add(new RpRoute(RpRouteKind.CompanionSocket, "новый канал Facepunch"));
        if (HasDirectAddress(c.Ip, c.Port)) routes.Add(new RpRoute(RpRouteKind.Direct, "прямое подключение"));       // запасной путь: пока сервер ещё принимает прямые подключения
        return new RpRoutePlan(routes, missing.Count == 0 ? null : string.Join("; ", missing));
    }

    private static readonly Regex StatusCode = new(@"status code '(\d{3})'", RegexOptions.Compiled);

    /// <summary>HTTP-код ответа на рукопожатие WebSocket из текста ошибки («The server returned status code '418' when status code '101' was expected»).</summary>
    public static int? HandshakeStatus(string? message)
    {
        var m = StatusCode.Match(message ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>
    /// Итог неудачного подключения по результатам всех попыток. Порядок важен: сначала то, что требует действий пользователя (403/404 нового канала),
    /// затем «прямое подключение закрыто» (418), затем нехватка данных для нового канала, затем общая ошибка. Секреты в тексты не попадают.
    /// </summary>
    public static RpResult Summarize(RpCredentials c, RpRoutePlan plan, IReadOnlyList<(RpRoute Route, RpResult Result)> attempts, string? portProbe)
    {
        var addr = HasDirectAddress(c.Ip, c.Port) ? $"{c.Ip}:{c.Port}" : "(адрес не передан Facepunch)";
        var companion = attempts.Where(a => a.Route.Kind == RpRouteKind.CompanionSocket).Select(a => a.Result).FirstOrDefault();
        var cStatus = companion is null ? null : HandshakeStatus(companion.Message);
        if (cStatus == 403)
            return RpResult.Fail(RpErrors.CompanionLoginRejected, "Facepunch отклонил вход в Rust+ (HTTP 403): токен входа устарел или не принят. На вкладке «Rust+» нажмите «1. Вход через Steam» и подключитесь снова.");
        if (cStatus == 404)
            return RpResult.Fail(RpErrors.CompanionServerUnknown, $"Facepunch не знает сервер {addr} (HTTP 404): ID сервера неверный или устарел. Сопрягите сервер в игре ещё раз (Esc → Rust+ → Pair with server).");
        if (attempts.Any(a => RpErrors.IsDirectRemoved(a.Result) || HandshakeStatus(a.Result.Message) == 418))
            return RpResult.Fail(RpErrors.DirectRemoved, $"{RpErrors.DirectRemovedMessage} Сервер: {addr}.");

        if (plan.CompanionMissing is not null)
        {
            var direct = attempts.Select(a => a.Result).LastOrDefault();
            var why = direct is null ? "" : $" Ответ прямого подключения: {direct.Message}.";
            return RpResult.Fail(RpErrors.CompanionNotReady, $"Прямое подключение к {addr} не удалось (Facepunch закрывает его), а новый канал не настроен: {plan.CompanionMissing}.{why}");
        }

        var last = attempts.Select(a => a.Result).LastOrDefault() ?? RpResult.Fail("no_route", "Нет доступных маршрутов подключения.");
        var hint = portProbe is null
            ? $"Порт {addr} доступен, но Rust+ не ответил: проверьте, что Rust+ включён на сервере."
            : $"Порт Rust+ {addr} недоступен с этого компьютера ({portProbe}). Если новый канал тоже не сработал — проверьте брандмауэр/антивирус, VPN и доступ к companion-rust.facepunch.com:443.";
        return RpResult.Fail(last.Code, $"{last.Message} — {hint}");
    }
}
