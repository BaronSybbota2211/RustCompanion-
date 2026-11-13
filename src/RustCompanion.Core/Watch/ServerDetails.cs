using System.Globalization;
using RustCompanion.Core.Net;

namespace RustCompanion.Core.Watch;

/// <summary>Сведения о сервере из A2S_INFO: онлайн, очередь, вайп (тег «born»), пинг (время ответа), версия, теги. Ники игроков здесь не используются.</summary>
public sealed record ServerDetails(string Name, string Map, int Players, int MaxPlayers, int? Queue, DateTimeOffset? WipeUtc, int PingMs, bool Password, string Version, IReadOnlyList<string> Tags, int? GamePort = null)
{
    public static ServerDetails From(A2sInfoResponse i, int pingMs)
    {
        DateTimeOffset? wipe = null; var tags = new List<string>();
        foreach (var raw in (i.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("born", StringComparison.OrdinalIgnoreCase) && long.TryParse(raw.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var ts) && ts > 1_000_000_000 && ts < 4_000_000_000)
            { wipe = DateTimeOffset.FromUnixTimeSeconds(ts); continue; }
            if (raw.Length == 0 || raw.StartsWith("mp") && raw.Length > 2 && char.IsDigit(raw[2]) || raw.StartsWith("cp") && raw.Length > 2 && char.IsDigit(raw[2])
                || raw.StartsWith("qp") && raw.Length > 2 && char.IsDigit(raw[2]) || raw.StartsWith("v") && raw.Length > 2 && char.IsDigit(raw[1])) continue;
            if (tags.Count < 12) tags.Add(raw);
        }
        return new ServerDetails(i.Name, i.Map, i.ReportedPlayers, i.ReportedMaxPlayers, i.Queue, wipe, pingMs, i.PasswordProtected, i.Version, tags, i.GamePort);
    }

    /// <summary>Строки для карточки сервера в интерфейсе.</summary>
    public IReadOnlyList<string> Lines(DateTimeOffset nowUtc)
    {
        var l = new List<string>
        {
            $"Онлайн: {Players}/{MaxPlayers}" + (Queue is > 0 ? $", в очереди {Queue}" : ""),
            $"Пинг до сервера: {PingMs} мс (время ответа Steam Query с вашего компьютера)",
            "Карта: " + (string.IsNullOrWhiteSpace(Map) ? "—" : Map)
        };
        if (WipeUtc is { } w)
        {
            var ago = nowUtc - w; var d = (int)Math.Max(0, ago.TotalDays);
            l.Add($"Последний вайп: {w.ToLocalTime():dd.MM.yyyy HH:mm} ({(d == 0 ? "сегодня" : d + " дн. назад")})");
        }
        else l.Add("Последний вайп: сервер не сообщает");
        if (!string.IsNullOrWhiteSpace(Version)) l.Add("Версия: " + Version);
        if (Password) l.Add("Сервер закрыт паролем");
        if (Tags.Count > 0) l.Add("Теги: " + string.Join(", ", Tags));
        return l;
    }
}
