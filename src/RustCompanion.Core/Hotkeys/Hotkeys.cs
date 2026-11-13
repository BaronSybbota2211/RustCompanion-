using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;

namespace RustCompanion.Core.Hotkeys;

[Flags] public enum HotkeyMods { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }   // значения совпадают с MOD_* для RegisterHotKey

public enum HotkeyEvent { Chinook, PatrolHelicopter, Bradley, Airdrop, CargoShip }

public static class HotkeyEvents
{
    public static readonly HotkeyEvent[] All = { HotkeyEvent.Chinook, HotkeyEvent.PatrolHelicopter, HotkeyEvent.Bradley, HotkeyEvent.Airdrop, HotkeyEvent.CargoShip };
    public static string Key(HotkeyEvent e) => e switch { HotkeyEvent.Chinook => "chinook", HotkeyEvent.PatrolHelicopter => "heli", HotkeyEvent.Bradley => "bradley", HotkeyEvent.Airdrop => "airdrop", _ => "cargo" };
    public static HotkeyEvent? FromKey(string key) => All.Cast<HotkeyEvent?>().FirstOrDefault(e => Key(e!.Value) == key);
    public static string Name(HotkeyEvent e) => e switch
    { HotkeyEvent.Chinook => "Чинук", HotkeyEvent.PatrolHelicopter => "Патрульный вертолёт", HotkeyEvent.Bradley => "Bradley (танк)", HotkeyEvent.Airdrop => "Аэрдроп", _ => "Грузовой корабль" };
    public static ObservedKind Kind(HotkeyEvent e) => e switch
    { HotkeyEvent.Chinook => ObservedKind.Chinook, HotkeyEvent.PatrolHelicopter => ObservedKind.PatrolHelicopter, HotkeyEvent.Bradley => ObservedKind.BradleyAppeared, HotkeyEvent.Airdrop => ObservedKind.Airdrop, _ => ObservedKind.CargoShip };
}

public readonly record struct HotkeyGesture(HotkeyMods Mods, int VirtualKey)
{
    private static bool IsFunctionKey(int vk) => vk is >= 0x70 and <= 0x87;
    private static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public static string KeyName(int vk) =>
        vk is >= 0x41 and <= 0x5A ? ((char)vk).ToString()
        : vk is >= 0x30 and <= 0x39 ? ((char)vk).ToString()
        : IsFunctionKey(vk) ? $"F{vk - 0x6F}"
        : vk switch { 0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home", 0x2D => "Insert", 0x2E => "Delete", 0xC0 => "`", _ => $"VK_{vk:X2}" };

    public override string ToString()
    {
        var parts = new List<string>();
        if (Mods.HasFlag(HotkeyMods.Control)) parts.Add("Ctrl");
        if (Mods.HasFlag(HotkeyMods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(HotkeyMods.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(HotkeyMods.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Проверка комбинации. Нужна Ctrl/Alt/Win либо F-клавиша, чтобы не перехватывать обычный набор текста и игровой ввод.</summary>
    public string? Validate()
    {
        if (VirtualKey is < 0x08 or > 0xFE) return "Недопустимая клавиша.";
        if (IsModifierKey(VirtualKey)) return "Нажмите клавишу вместе с модификатором, а не один модификатор.";
        if (!Mods.HasFlag(HotkeyMods.Control) && !Mods.HasFlag(HotkeyMods.Alt) && !Mods.HasFlag(HotkeyMods.Win) && !IsFunctionKey(VirtualKey))
            return "Добавьте Ctrl, Alt или Win (одиночные клавиши и Shift+клавиша мешают вводу).";
        if (Mods == HotkeyMods.Alt && VirtualKey is 0x73 or 0x09) return "Сочетание зарезервировано Windows (Alt+F4 / Alt+Tab).";
        if (Mods == HotkeyMods.Control && VirtualKey == 0x1B) return "Сочетание зарезервировано Windows.";
        return null;
    }

    /// <summary>Предупреждение, не запрет: сочетание допустимо, но может пересекаться с игрой.</summary>
    public string? Warning() => Mods == HotkeyMods.None && VirtualKey == 0x70 ? "F1 открывает консоль Rust — лучше выбрать другую клавишу." : null;
}

public static class HotkeyAssignments
{
    /// <summary>Возвращает текст ошибки (недопустимая комбинация или конфликт внутри приложения) либо null.</summary>
    public static string? Validate(IReadOnlyDictionary<HotkeyEvent, HotkeyGesture> current, HotkeyEvent target, HotkeyGesture gesture)
    {
        var err = gesture.Validate();
        if (err is not null) return err;
        foreach (var (ev, g) in current)
            if (ev != target && g == gesture) return $"Сочетание {gesture} уже назначено событию «{HotkeyEvents.Name(ev)}». Выберите другое.";
        return null;
    }
}

public sealed class HotkeyRepository
{
    private readonly Database _db;
    public HotkeyRepository(Database db) { _db = db; }

    public Task<IReadOnlyDictionary<HotkeyEvent, HotkeyGesture>> LoadAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyDictionary<HotkeyEvent, HotkeyGesture>>(c =>
        {
            using var cmd = Sql.Cmd(c, "SELECT event_key,modifiers,vk FROM hotkeys");
            using var r = cmd.ExecuteReader();
            var d = new Dictionary<HotkeyEvent, HotkeyGesture>();
            while (r.Read())
                if (HotkeyEvents.FromKey(r.GetString(0)) is { } ev) d[ev] = new HotkeyGesture((HotkeyMods)r.GetInt32(1), r.GetInt32(2));
            return d;
        }, ct);

    public Task SaveAsync(HotkeyEvent ev, HotkeyGesture? g, CancellationToken ct = default) =>
        _db.RunAsync(c =>
        {
            if (g is null) { using var del = Sql.Cmd(c, "DELETE FROM hotkeys WHERE event_key=@k", ("@k", HotkeyEvents.Key(ev))); del.ExecuteNonQuery(); return; }
            using var cmd = Sql.Cmd(c, "INSERT INTO hotkeys(event_key,modifiers,vk) VALUES(@k,@m,@v) ON CONFLICT(event_key) DO UPDATE SET modifiers=@m,vk=@v",
                ("@k", HotkeyEvents.Key(ev)), ("@m", (int)g.Value.Mods), ("@v", g.Value.VirtualKey));
            cmd.ExecuteNonQuery();
        }, ct);
}

public sealed record HotkeyMarkResult(bool Recorded, string Message, HotkeyEvent Event, long? ServerId = null, DateTimeOffset? AtUtc = null,
    TimeSpan? Interval = null, ForecastResult? Forecast = null);

/// <summary>
/// Обработка нажатия глобальной горячей клавиши (или ручной кнопки): ровно одна отметка на нажатие, подтверждение пользователю.
/// Отметка — действие пользователя: приложение само появление события не обнаруживает. Данные хранятся по выбранному для отметок серверу.
/// </summary>
public sealed class HotkeyService
{
    public static readonly TimeSpan PressDebounce = TimeSpan.FromMilliseconds(800);   // защита от дребезга и удержания клавиши
    public static readonly TimeSpan MinGapBetweenMarks = TimeSpan.FromSeconds(10);    // слишком близкая вторая отметка отклоняется
    private readonly ObservedRepository _obs; private readonly ServerRepository _servers; private readonly IClock _clock; private readonly ISettingsProvider _settings; private readonly IOverlaySink _overlay;
    private readonly Dictionary<HotkeyEvent, DateTimeOffset> _lastPress = new();
    private readonly object _lock = new();
    public event Action<HotkeyMarkResult>? Marked;

    public HotkeyService(ObservedRepository obs, ServerRepository servers, IClock clock, ISettingsProvider settings, IOverlaySink overlay)
    { _obs = obs; _servers = servers; _clock = clock; _settings = settings; _overlay = overlay; }

    public async Task<HotkeyMarkResult> PressAsync(HotkeyEvent ev, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        lock (_lock)
        {
            if (_lastPress.TryGetValue(ev, out var prev) && now - prev < PressDebounce && now >= prev)
                return new HotkeyMarkResult(false, "Повторное срабатывание проигнорировано (удержание клавиши).", ev);
            _lastPress[ev] = now;
        }
        var result = await MarkCoreAsync(ev, now, ct);
        if (_settings.Current.HotkeyConfirmOverlay) _overlay.Show("hotkey", "Rust Companion — " + result.Message);
        Marked?.Invoke(result);
        return result;
    }

    private async Task<HotkeyMarkResult> MarkCoreAsync(HotkeyEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        var name = HotkeyEvents.Name(ev); var kind = HotkeyEvents.Kind(ev);
        var serverId = _settings.Current.HotkeyServerId;
        if (serverId <= 0) return new HotkeyMarkResult(false, $"{name}: не выбран сервер для отметок (вкладка «Горячие клавиши»).", ev);
        if ((await _servers.ListAsync(ct)).All(s => s.Id != serverId))
            return new HotkeyMarkResult(false, $"{name}: выбранный сервер удалён — выберите другой.", ev);

        var existing = await _obs.ListAsync(serverId, kind, ct);
        var last = existing.Count > 0 ? existing.Max(e => e.AtUtc) : (DateTimeOffset?)null;
        if (last is { } l)
        {
            if (now < l) return new HotkeyMarkResult(false, $"{name}: системное время изменилось (раньше предыдущей отметки). Отметка не записана.", ev, serverId);
            if (now - l < MinGapBetweenMarks) return new HotkeyMarkResult(false, $"{name}: слишком близко к предыдущей отметке — не записано.", ev, serverId);
        }
        await _obs.AddAsync(serverId, kind, now, "hotkey", ct);
        var f = ForecastEngine.Compute(await _obs.ListAsync(serverId, kind, ct), now, ForecastService.OptionsFor(kind));
        TimeSpan? interval = last is null ? null : now - last.Value;
        var text = $"{name}: отмечено {now.ToLocalTime():HH:mm:ss}";
        if (interval is { } iv) text += $" • интервал {iv.TotalMinutes:0.#} мин";
        if (f.State == ForecastState.Ready && f.Next is not null)
            text += $" • следующее ≈ {f.Next.Value.ToLocalTime():HH:mm} (оценка{(f.LowSample ? ", по одному интервалу" : "")})";
        else if (last is null) text += " • нужна ещё одна отметка для прогноза";
        return new HotkeyMarkResult(true, text, ev, serverId, now, interval, f);
    }

    public async Task<int> DeleteLastMarkAsync(HotkeyEvent ev, CancellationToken ct = default)
    {
        var serverId = _settings.Current.HotkeyServerId;
        if (serverId <= 0) return 0;
        var list = await _obs.ListAsync(serverId, HotkeyEvents.Kind(ev), ct);
        if (list.Count == 0) return 0;
        await _obs.DeleteAsync(list.OrderBy(e => e.AtUtc).Last().Id, ct);
        return 1;
    }

    /// <summary>Сброс истории события для выбранного сервера (удаляет все отметки этого события).</summary>
    public async Task<int> ResetAsync(HotkeyEvent ev, CancellationToken ct = default)
    {
        var serverId = _settings.Current.HotkeyServerId;
        if (serverId <= 0) return 0;
        var list = await _obs.ListAsync(serverId, HotkeyEvents.Kind(ev), ct);
        foreach (var e in list) await _obs.DeleteAsync(e.Id, ct);
        return list.Count;
    }
}
