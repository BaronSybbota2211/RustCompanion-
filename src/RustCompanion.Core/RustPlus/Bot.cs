using RustCompanion.Core.Models;

namespace RustCompanion.Core.RustPlus;

public sealed record BotSettings(bool Enabled, string Prefix, string Language, ISet<string> Disabled)
{
    public static BotSettings From(AppSettings s) =>
        new(s.BotEnabled, string.IsNullOrWhiteSpace(s.BotPrefix) ? "!" : s.BotPrefix, s.BotLanguage is "en" ? "en" : "ru",
            new HashSet<string>((s.BotDisabledCommands ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase));
}

public interface ITimerSource { IReadOnlyList<(string Name, TimeSpan Remaining)> Active(DateTimeOffset now); }
public sealed record BotContext(RustPlusSnapshot Snapshot, ITimerSource Timers, DateTimeOffset Now, string Language);

public interface IBotCommand
{
    string Name { get; }
    IReadOnlyList<string> Aliases { get; }
    string Describe(string language);
    /// <summary>Команда доступна, только если для неё есть реальные данные от сервера (проверяется до ответа).</summary>
    bool IsSupported(RustPlusSnapshot snapshot);
    string Execute(BotContext ctx, string args);
}

/// <summary>Расширяемый обработчик команд командного чата. Новые команды добавляются реализацией IBotCommand и Register — основной код не меняется.</summary>
public sealed class BotCommandEngine
{
    public const string ReplyMarker = "[RC] ";
    private readonly List<IBotCommand> _commands = new();
    public IReadOnlyList<IBotCommand> Commands => _commands;

    public BotCommandEngine Register(IBotCommand c) { _commands.Add(c); return this; }
    public static BotCommandEngine CreateDefault() => new BotCommandEngine().Register(new TimeCommand()).Register(new PopCommand()).Register(new TimerCommand());

    public string? Handle(RpChatMessage msg, BotSettings st, BotContext ctx)
    {
        if (!st.Enabled) return null;
        var text = (msg.Text ?? "").Trim();
        if (text.StartsWith(ReplyMarker.Trim(), StringComparison.Ordinal)) return null;       // собственные ответы бота не обрабатываются (нет цикла)
        if (!text.StartsWith(st.Prefix, StringComparison.Ordinal)) return null;
        var body = text[st.Prefix.Length..].Trim();
        if (body.Length == 0) return null;
        var space = body.IndexOf(' ');
        var name = (space < 0 ? body : body[..space]).ToLowerInvariant();
        var args = space < 0 ? "" : body[(space + 1)..].Trim();
        var cmd = _commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (cmd is null || st.Disabled.Contains(cmd.Name)) return null;                         // неизвестные и отключённые команды молчат
        string reply;
        try
        {
            reply = cmd.IsSupported(ctx.Snapshot)
                ? cmd.Execute(ctx, args)
                : (ctx.Language == "en" ? $"{st.Prefix}{cmd.Name}: no data on this server yet" : $"{st.Prefix}{cmd.Name}: нет данных от этого сервера");
        }
        catch (Exception) { reply = ctx.Language == "en" ? "command failed" : "ошибка выполнения команды"; }
        reply = ReplyMarker + reply;
        return reply.Length > RustPlusManager.MaxMessageLength ? reply[..(RustPlusManager.MaxMessageLength - 1)] + "…" : reply;
    }
}

public sealed class TimeCommand : IBotCommand
{
    public string Name => "time"; public IReadOnlyList<string> Aliases => new[] { "время" };
    public string Describe(string l) => l == "en" ? "in-game time" : "игровое время";
    public bool IsSupported(RustPlusSnapshot s) => s.Time?.Time is not null;
    public string Execute(BotContext c, string args)
    {
        var t = c.Snapshot.Time!; var h = ((t.Time!.Value % 24) + 24) % 24; var hh = TimeSpan.FromHours(h);
        var clock = $"{(int)hh.TotalHours:D2}:{hh.Minutes:D2}";
        string? phase = t.Sunrise is { } sr && t.Sunset is { } ss ? (h >= sr && h < ss ? (c.Language == "en" ? "day" : "день") : (c.Language == "en" ? "night" : "ночь")) : null;
        return c.Language == "en" ? $"Game time {clock}" + (phase is null ? "" : $", {phase}") : $"Время в игре {clock}" + (phase is null ? "" : $", {phase}");
    }
}

public sealed class PopCommand : IBotCommand
{
    public string Name => "pop"; public IReadOnlyList<string> Aliases => new[] { "онлайн" };
    public string Describe(string l) => l == "en" ? "players on the server" : "число игроков на сервере";
    public bool IsSupported(RustPlusSnapshot s) => s.Info?.Players is not null;
    public string Execute(BotContext c, string args)
    {
        var i = c.Snapshot.Info!;
        var main = $"{i.Players}/{(i.MaxPlayers?.ToString() ?? "?")}";
        var queue = i.Queued is > 0 ? (c.Language == "en" ? $", queue {i.Queued}" : $", в очереди {i.Queued}") : "";
        return (c.Language == "en" ? "Players: " : "Игроков: ") + main + queue;
    }
}

public sealed class TimerCommand : IBotCommand
{
    public string Name => "timer"; public IReadOnlyList<string> Aliases => new[] { "таймер" };
    public string Describe(string l) => l == "en" ? "active timers" : "активные таймеры";
    public bool IsSupported(RustPlusSnapshot s) => true;     // таймеры приложения не зависят от данных сервера
    public string Execute(BotContext c, string args)
    {
        var list = c.Timers.Active(c.Now).Where(t => string.IsNullOrEmpty(args) || t.Name.Contains(args, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(t => t.Remaining).Take(3).ToList();
        if (list.Count == 0) return c.Language == "en" ? "No active timers" : "Активных таймеров нет";
        static string F(TimeSpan t) => t <= TimeSpan.Zero ? "0:00" : $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return (c.Language == "en" ? "Timers: " : "Таймеры: ") + string.Join(", ", list.Select(t => $"{t.Name} {F(t.Remaining)}"));
    }
}

/// <summary>Связывает чат Rust+ с обработчиком команд. Бот по умолчанию выключен; отвечает только на сообщения, появившиеся после подключения.</summary>
public sealed class RustPlusBotService
{
    private readonly RustPlusManager _manager; private readonly BotCommandEngine _engine; private readonly ISettingsProvider _settings;
    private readonly ITimerSource _timers; private readonly IClock _clock; private readonly Services.DiagnosticsService? _diag;

    public RustPlusBotService(RustPlusManager manager, BotCommandEngine engine, ISettingsProvider settings, ITimerSource timers, IClock clock, Services.DiagnosticsService? diag = null)
    { _manager = manager; _engine = engine; _settings = settings; _timers = timers; _clock = clock; _diag = diag; _manager.ChatMessage += m => _ = HandleAsync(m); }

    public async Task<string?> HandleAsync(RpChatMessage msg)
    {
        var st = BotSettings.From(_settings.Current);
        var reply = _engine.Handle(msg, st, new BotContext(_manager.Snapshot, _timers, _clock.UtcNow, st.Language));
        if (reply is null) { if (st.Enabled && msg.Text.TrimStart().StartsWith(st.Prefix, StringComparison.Ordinal)) _diag?.Record("Бот Rust+", SourceState.NoData, $"Сообщение «{msg.Text}» от {msg.Name} не распознано как команда"); return null; }
        _diag?.Record("Бот Rust+", SourceState.Ok, $"Команда «{msg.Text}» от {msg.Name} распознана");
        var r = await _manager.SendMessageAsync(reply);
        if (!r.Ok) { _diag?.Record("Бот Rust+", SourceState.Error, $"Ответ не отправлен: {r.Message}"); return null; }
        _diag?.Record("Бот Rust+", SourceState.Ok, "Ответ отправлен в чат команды");
        return reply;
    }
}
