using RustCompanion.Core.Data;
using RustCompanion.Core.Services;

namespace RustCompanion.Core.RustPlus;

public enum RpDeviceCommandMode { Toggle = 0, Separate = 1 }
public enum RpDeviceCommandAction { None = 0, Toggle = 1, TurnOn = 2, TurnOff = 3 }

public sealed class RpDeviceCommandSettings
{
    public long DeviceId { get; set; }
    public bool Enabled { get; set; }
    public RpDeviceCommandMode Mode { get; set; }
    public string Prefix { get; set; } = "!";
    public string ToggleCommand { get; set; } = "";
    public string OnCommand { get; set; } = "";
    public string OffCommand { get; set; } = "";
    /// <summary>Имена или SteamID через запятую/точку с запятой/новую строку. Пусто — любой участник командного чата.</summary>
    public string AllowedSenders { get; set; } = "";

    public RpDeviceCommandSettings Clone() => (RpDeviceCommandSettings)MemberwiseClone();
}

public sealed record RpDeviceCommandHistory(
    long DeviceId, string DeviceName, string Sender, string Command, bool? TargetState,
    string Status, string Detail, DateTimeOffset AtUtc);

public static class RpDeviceCommandRules
{
    public static readonly IReadOnlyList<string> Prefixes = new[] { "!", "/", "." };

    public static string FullCommand(string prefix, string command) => $"{prefix}{(command ?? "").Trim()}";

    public static RpDeviceCommandAction Match(RpDeviceCommandSettings s, string? messageText)
    {
        var text = (messageText ?? "").Trim();
        if (text.Length == 0) return RpDeviceCommandAction.None;
        var comparison = StringComparison.Ordinal; // полное совпадение с учётом регистра; кириллица и другие Unicode-символы не разбираются на слова
        if (s.Mode == RpDeviceCommandMode.Toggle)
            return !string.IsNullOrWhiteSpace(s.ToggleCommand) && text.Equals(FullCommand(s.Prefix, s.ToggleCommand), comparison)
                ? RpDeviceCommandAction.Toggle : RpDeviceCommandAction.None;
        if (!string.IsNullOrWhiteSpace(s.OnCommand) && text.Equals(FullCommand(s.Prefix, s.OnCommand), comparison)) return RpDeviceCommandAction.TurnOn;
        if (!string.IsNullOrWhiteSpace(s.OffCommand) && text.Equals(FullCommand(s.Prefix, s.OffCommand), comparison)) return RpDeviceCommandAction.TurnOff;
        return RpDeviceCommandAction.None;
    }

    public static IReadOnlyList<string> ParseSenders(string? value) => (value ?? "")
        .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool SenderAllowed(RpDeviceCommandSettings s, RpChatMessage message)
    {
        var allowed = ParseSenders(s.AllowedSenders);
        if (allowed.Count == 0) return true;
        var steamId = message.SteamId?.ToString();
        return allowed.Any(x => x.Equals(message.Name, StringComparison.OrdinalIgnoreCase) ||
            steamId is not null && x.Equals(steamId, StringComparison.Ordinal));
    }

    public static string? Validate(RpDeviceCommandSettings s)
    {
        if (!Prefixes.Contains(s.Prefix)) return "Выберите префикс !, / или .";
        var commands = s.Mode == RpDeviceCommandMode.Toggle
            ? new[] { s.ToggleCommand } : new[] { s.OnCommand, s.OffCommand };
        if (commands.Any(string.IsNullOrWhiteSpace)) return "Заполните текст команды.";
        if (commands.Any(x => x.Contains('\r') || x.Contains('\n'))) return "Команда должна занимать одну строку.";
        if (commands.Any(x => FullCommand(s.Prefix, x).Length > RustPlusManager.MaxMessageLength))
            return $"Команда вместе с префиксом не должна превышать {RustPlusManager.MaxMessageLength} символов.";
        if (s.Mode == RpDeviceCommandMode.Separate &&
            FullCommand(s.Prefix, s.OnCommand).Equals(FullCommand(s.Prefix, s.OffCommand), StringComparison.Ordinal))
            return "Команды включения и выключения должны различаться.";
        return null;
    }
}

public sealed class RpDeviceCommandRepository
{
    private readonly Database _db;
    public RpDeviceCommandRepository(Database db) { _db = db; }

    private static RpDeviceCommandSettings Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        DeviceId = r.GetInt64(0), Enabled = r.GetInt32(1) != 0, Mode = (RpDeviceCommandMode)r.GetInt32(2),
        Prefix = r.GetString(3), ToggleCommand = r.GetString(4), OnCommand = r.GetString(5),
        OffCommand = r.GetString(6), AllowedSenders = r.GetString(7)
    };

    public Task<IReadOnlyList<RpDeviceCommandSettings>> ListAsync(long serverId, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RpDeviceCommandSettings>>(c =>
        {
            using var cmd = Sql.Cmd(c, @"SELECT c.device_id,c.enabled,c.mode,c.prefix,c.toggle_command,c.on_command,c.off_command,c.allowed_senders
                FROM rp_device_commands c JOIN rp_devices d ON d.id=c.device_id WHERE d.server_id=@s ORDER BY c.device_id", ("@s", serverId));
            using var r = cmd.ExecuteReader();
            var list = new List<RpDeviceCommandSettings>(); while (r.Read()) list.Add(Map(r)); return list;
        }, ct);

    public async Task<RpDeviceCommandSettings> GetAsync(long deviceId, CancellationToken ct = default)
    {
        var found = await _db.RunAsync<RpDeviceCommandSettings?>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT device_id,enabled,mode,prefix,toggle_command,on_command,off_command,allowed_senders FROM rp_device_commands WHERE device_id=@d", ("@d", deviceId));
            using var r = cmd.ExecuteReader(); return r.Read() ? Map(r) : null;
        }, ct);
        return found ?? new RpDeviceCommandSettings { DeviceId = deviceId };
    }

    public Task SaveAsync(RpDeviceCommandSettings s, DateTimeOffset atUtc, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO rp_device_commands(device_id,enabled,mode,prefix,toggle_command,on_command,off_command,allowed_senders,updated_at)
                VALUES(@d,@e,@m,@p,@t,@on,@off,@a,@u)
                ON CONFLICT(device_id) DO UPDATE SET enabled=@e,mode=@m,prefix=@p,toggle_command=@t,on_command=@on,off_command=@off,allowed_senders=@a,updated_at=@u",
                ("@d", s.DeviceId), ("@e", s.Enabled ? 1 : 0), ("@m", (int)s.Mode), ("@p", s.Prefix),
                ("@t", s.ToggleCommand.Trim()), ("@on", s.OnCommand.Trim()), ("@off", s.OffCommand.Trim()),
                ("@a", s.AllowedSenders.Trim()), ("@u", Sql.Iso(atUtc)));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<bool> GetProcessingEnabledAsync(CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT value FROM rp_device_module_settings WHERE key='processing_enabled'");
            return cmd.ExecuteScalar()?.ToString() == "1";
        }, ct);

    public Task SetProcessingEnabledAsync(bool enabled, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO rp_device_module_settings(key,value) VALUES('processing_enabled',@v)
                ON CONFLICT(key) DO UPDATE SET value=@v", ("@v", enabled ? "1" : "0"));
            cmd.ExecuteNonQuery();
        }, ct);

    /// <summary>Атомарно отмечает сообщение до выполнения: повтор того же сообщения не запускает устройство даже после ошибки.</summary>
    public Task<bool> TryMarkProcessedAsync(long serverId, string messageKey, DateTimeOffset atUtc, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            using (var cmd = Sql.Cmd(c, "INSERT OR IGNORE INTO rp_device_processed_messages(server_id,message_key,at) VALUES(@s,@k,@a)",
                ("@s", serverId), ("@k", messageKey), ("@a", Sql.Iso(atUtc))))
            { cmd.Transaction = tx; if (cmd.ExecuteNonQuery() == 0) { tx.Rollback(); return false; } }
            using (var prune = Sql.Cmd(c, "DELETE FROM rp_device_processed_messages WHERE at<@cutoff", ("@cutoff", Sql.Iso(atUtc.AddDays(-14)))))
            { prune.Transaction = tx; prune.ExecuteNonQuery(); }
            tx.Commit(); return true;
        }, ct);

    public Task AddHistoryAsync(long serverId, long deviceId, string messageKey, RpChatMessage message,
        string command, bool? targetState, string status, string detail, DateTimeOffset atUtc, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            using var cmd = Sql.Cmd(c, @"INSERT INTO rp_device_command_history(server_id,device_id,message_key,sender,command,target_state,status,detail,at)
                VALUES(@s,@d,@k,@sender,@c,@target,@status,@detail,@at)",
                ("@s", serverId), ("@d", deviceId), ("@k", messageKey), ("@sender", message.Name),
                ("@c", command), ("@target", targetState is null ? null : targetState.Value ? 1 : 0),
                ("@status", status), ("@detail", detail), ("@at", Sql.Iso(atUtc)));
            cmd.ExecuteNonQuery();
        }, ct);

    public Task<IReadOnlyList<RpDeviceCommandHistory>> HistoryAsync(long serverId, int limit = 30, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<RpDeviceCommandHistory>>(c =>
        {
            using var cmd = Sql.Cmd(c, @"SELECT h.device_id,COALESCE(d.name,'Удалённое устройство'),h.sender,h.command,h.target_state,h.status,h.detail,h.at
                FROM rp_device_command_history h LEFT JOIN rp_devices d ON d.id=h.device_id
                WHERE h.server_id=@s ORDER BY h.id DESC LIMIT @limit", ("@s", serverId), ("@limit", Math.Clamp(limit, 1, 200)));
            using var r = cmd.ExecuteReader();
            var list = new List<RpDeviceCommandHistory>();
            while (r.Read()) list.Add(new RpDeviceCommandHistory(r.IsDBNull(0) ? 0 : r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt32(4) != 0, r.GetString(5), r.GetString(6), Sql.ParseIso(r.GetValue(7))!.Value));
            return list;
        }, ct);
}

/// <summary>
/// Исполняет пользовательские Team Chat-команды только через существующее Rust+ подключение.
/// Старые сообщения отсекает RustPlusManager; здесь дополнительно есть постоянная дедупликация и сериализация выполнения.
/// </summary>
public sealed class RustPlusDeviceCommandService
{
    private readonly RustPlusManager _manager;
    private readonly RpDeviceRepository _devices;
    private readonly RpDeviceCommandRepository _commands;
    private readonly IClock _clock;
    private readonly RustPlusHub? _hub;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    public bool ProcessingEnabled { get; private set; }
    public event Action<RpDeviceCommandHistory>? ExecutionCompleted;
    public event Action<string>? StatusChanged;

    public RustPlusDeviceCommandService(RustPlusManager manager, RpDeviceRepository devices, RpDeviceCommandRepository commands, IClock clock, RustPlusHub? hub = null)
    {
        _manager = manager; _devices = devices; _commands = commands; _clock = clock; _hub = hub;
        _manager.ChatMessage += m => _ = HandleSafelyAsync(m);
    }

    public async Task InitializeAsync(CancellationToken ct = default) =>
        ProcessingEnabled = await _commands.GetProcessingEnabledAsync(ct);

    public async Task SetProcessingEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await _commands.SetProcessingEnabledAsync(enabled, ct);
        ProcessingEnabled = enabled;
        StatusChanged?.Invoke(enabled ? "Обработка Team Chat включена." : "Обработка Team Chat приостановлена.");
    }

    private async Task HandleSafelyAsync(RpChatMessage message)
    {
        try { await HandleAsync(message); }
        catch (Exception ex) { StatusChanged?.Invoke("Ошибка обработки команды устройства: " + ex.Message); }
    }

    /// <summary>Публично для интеграционных тестов; в приложении вызывается только событием общего RustPlusManager.</summary>
    public async Task HandleAsync(RpChatMessage message, CancellationToken ct = default)
    {
        var server = _manager.Server;
        if (!ProcessingEnabled || !_manager.IsConnected || server is null)
        { _manager.Log($"Team Chat → команды устройств: сообщение «{message.Text}» не обработано (обработка включена={ProcessingEnabled}, подключено={_manager.IsConnected})"); return; }
        // Сообщения владельца сопряжения — обычные команды (игрок пишет их из своего клиента). Исключаются только ответы самого приложения: они не отправляются как команды.
        if (message.Text.TrimStart().StartsWith(BotCommandEngine.ReplyMarker.Trim(), StringComparison.Ordinal)) return;

        var devices = await _devices.ListAsync(server.Id, ct);
        var settings = await _commands.ListAsync(server.Id, ct);
        var byId = settings.ToDictionary(x => x.DeviceId);
        var matches = devices.Where(d => d.Kind == RpDeviceKind.SmartSwitch && byId.ContainsKey(d.Id))
            .Select(d => (Device: d, Settings: byId[d.Id], Action: RpDeviceCommandRules.Match(byId[d.Id], message.Text)))
            .Where(x => x.Settings.Enabled && x.Action != RpDeviceCommandAction.None &&
                RpDeviceCommandRules.SenderAllowed(x.Settings, message)).ToList();
        if (matches.Count == 0) { _manager.Log($"Team Chat → команды устройств: «{message.Text}» не совпало ни с одной включённой командой"); return; }
        if (!await _commands.TryMarkProcessedAsync(server.Id, message.Key, _clock.UtcNow, ct)) return;

        await _executionGate.WaitAsync(ct);
        try
        {
            foreach (var match in matches)
            {
                // Сообщение могло ждать другую команду: пауза, смена сервера и отключение должны отменить выполнение.
                if (!ProcessingEnabled || !_manager.IsConnected || _manager.Server?.Id != server.Id) return;
                var current = await _commands.GetAsync(match.Device.Id, ct);
                var action = RpDeviceCommandRules.Match(current, message.Text);
                if (!current.Enabled || action == RpDeviceCommandAction.None || !RpDeviceCommandRules.SenderAllowed(current, message)) continue;
                await ExecuteAsync(server.Id, match.Device, current, action, message, ct);
            }
        }
        finally { _executionGate.Release(); }
    }

    private async Task ExecuteAsync(long serverId, RpDevice device, RpDeviceCommandSettings settings,
        RpDeviceCommandAction action, RpChatMessage message, CancellationToken ct)
    {
        bool? target = action switch
        {
            RpDeviceCommandAction.TurnOn => true,
            RpDeviceCommandAction.TurnOff => false,
            _ => null
        };
        string status = "failed", detail;
        _manager.Log($"Team Chat → команда «{message.Text}» от {message.Name}: устройство «{device.Name}» entity={device.EntityId}, действие={action}");
        try
        {
            if (!_manager.IsConnected || _manager.Server?.Id != serverId)
            {
                detail = "Нет подключения к этому серверу Rust+. Команда не отправлена.";
            }
            else
            {
                var before = await _manager.ReadDeviceAsync(device.EntityId, RpDeviceKind.SmartSwitch, ct);
                if (!before.Ok || before.Value?.IsOn is null)
                {
                    _hub?.SetAvailability(serverId, device.EntityId, DeviceAvailability.Unknown, "состояние перед командой не получено");
                    detail = "Устройство недоступно или его состояние неизвестно. Команда не отправлена: " + before.Message;
                }
                else
                {
                    target ??= !before.Value.IsOn;
                    if (before.Value.IsOn == target.Value)
                    {
                        status = "confirmed";
                        _hub?.SetState(serverId, device.EntityId, before.Value);
                        _hub?.SetAvailability(serverId, device.EntityId, DeviceAvailability.Available, null);
                        await _devices.MarkSynchronizedAsync(device.Id, before.Value, _clock.UtcNow, ct);
                        detail = target.Value ? "Уже включено (состояние подтверждено)." : "Уже выключено (состояние подтверждено).";
                    }
                    else
                    {
                        var result = await _manager.SetSwitchVerifiedAsync(device.EntityId, target.Value, ct, serverId);
                        detail = result.Message ?? (result.Ok ? "Состояние подтверждено." : "Состояние не подтверждено.");
                        if (result.Ok)
                        {
                            status = "confirmed";
                            var state = new RpDeviceState(RpDeviceKind.SmartSwitch, target.Value);
                            _hub?.SetState(serverId, device.EntityId, state);
                            _hub?.SetAvailability(serverId, device.EntityId, DeviceAvailability.Available, null);
                            await _devices.MarkSynchronizedAsync(device.Id, state, _clock.UtcNow, ct);
                        }
                        else _hub?.SetAvailability(serverId, device.EntityId, DeviceAvailability.Unknown, "результат команды не подтверждён повторным чтением");
                    }
                }
            }
        }
        catch (OperationCanceledException) { detail = "Выполнение отменено. Повторной отправки не будет."; }
        catch (Exception ex) { detail = "Команда не выполнена: " + ex.Message; }

        var history = new RpDeviceCommandHistory(device.Id, device.Name, message.Name, message.Text, target, status, detail, _clock.UtcNow);
        await _commands.AddHistoryAsync(serverId, device.Id, message.Key, message, message.Text, target, status, detail, _clock.UtcNow, CancellationToken.None);
        ExecutionCompleted?.Invoke(history);
        StatusChanged?.Invoke($"{device.Name}: {detail}");
    }
}
