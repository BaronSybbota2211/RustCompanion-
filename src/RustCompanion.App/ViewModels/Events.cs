using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.App.Services;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.RustPlus;

namespace RustCompanion.App.ViewModels;

public sealed record ServerOption(long Id, string Name) { public override string ToString() => Name; }

internal static class Fmt
{
    public static string Local(DateTimeOffset? t) => t is null ? "—" : t.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public static string Span(TimeSpan t) { if (t < TimeSpan.Zero) t = -t; return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}"; }
    public static bool TryLocal(string text, out DateTimeOffset utc)
    {
        utc = default;
        if (!DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var d)) return false;
        utc = new DateTimeOffset(d).ToUniversalTime(); return true;
    }
    public static string NotifyStatus(NotificationPrefs prefs, string eventId)
    {
        var c = prefs.Get(eventId);
        return $"Уведомления: оверлей — {(c.Overlay ? "включено" : "выключено")}, Windows — {(c.System ? "включено" : "выключено")}";
    }
}

public interface IObservedHost { Task SaveItemAsync(ObservedItemViewModel i); Task DeleteItemAsync(ObservedItemViewModel i); }

public sealed partial class ObservedItemViewModel : ObservableObject
{
    private readonly IObservedHost _host;
    public ObservedEvent Event { get; }
    [ObservableProperty] private string _timeText;
    [ObservableProperty] private string _note;
    [ObservableProperty] private string _error = "";
    public ObservedItemViewModel(ObservedEvent e, IObservedHost host) { Event = e; _host = host; _timeText = e.AtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture); _note = e.Note; }
    [RelayCommand] private Task SaveAsync() => _host.SaveItemAsync(this);
    [RelayCommand] private Task DeleteAsync() => _host.DeleteItemAsync(this);
}

/// <summary>Общая основа для модулей, привязанных к выбранному серверу (данные разных серверов не смешиваются).</summary>
public abstract partial class ServerBoundViewModel : ObservableObject
{
    protected readonly ServerRepository ServerRepo;
    protected ServerBoundViewModel(ServerRepository repo) { ServerRepo = repo; }
    public ObservableCollection<ServerOption> Servers { get; } = new();
    [ObservableProperty] private ServerOption? _selectedServer;
    [ObservableProperty] private bool _noServers = true;
    [ObservableProperty] private string _error = "";

    /// <summary>Перечитать список серверов. selectFirst=false — не выбирать первый сервер автоматически (для вкладок, где выбор должен быть явным).</summary>
    public async Task ReloadServersAsync(bool selectFirst = true)
    {
        var keep = SelectedServer?.Id;
        Servers.Clear();
        foreach (var s in await ServerRepo.ListAsync()) Servers.Add(new ServerOption(s.Id, s.Name));
        NoServers = Servers.Count == 0;
        var same = Servers.FirstOrDefault(s => s.Id == keep);
        if (same is not null && SelectedServer is not null && SelectedServer.Id == same.Id) { SelectedServer = same; return; }
        SelectedServer = same ?? (selectFirst ? Servers.FirstOrDefault() : null);
    }
    partial void OnSelectedServerChanged(ServerOption? value) => _ = OnServerChangedAsync();
    protected abstract Task OnServerChangedAsync();
}

public sealed partial class ForecastViewModel : ServerBoundViewModel, IObservedHost
{
    private readonly ObservedRepository _obs; private readonly IClock _clock; private readonly NotificationPrefs _prefs;
    private List<ObservedEvent> _events = new();
    public ObservedKind Kind { get; }
    public string Title { get; }
    public string ButtonText { get; }
    public string Hint { get; }
    public ObservableCollection<ObservedItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _lastText = "—";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _confidenceText = "";
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private string _notifyText = "";

    public ForecastViewModel(ObservedKind kind, ServerRepository repo, ObservedRepository obs, IClock clock, NotificationPrefs prefs) : base(repo)
    {
        Kind = kind; _obs = obs; _clock = clock; _prefs = prefs;
        (Title, ButtonText, Hint) = kind switch
        {
            ObservedKind.Chinook => ("Чинук", "Чинук замечен", "Нажимайте кнопку, когда видите уведомление игры о Чинуке или само событие. Прогноз строится по интервалам между вашими нажатиями на выбранном сервере."),
            ObservedKind.PatrolHelicopter => ("Патрульный вертолёт — прогноз", "Вертолёт замечен", "История и прогноз независимы от Чинука и от ручного таймера вертолёта. Событие не считается строго регулярным."),
            ObservedKind.BradleyAppeared => ("Bradley — отметки появления", "Bradley замечен", "Отметки появления Bradley (кнопкой или горячей клавишей). Это отдельная история от отсчёта «после уничтожения»; прогноз приблизителен."),
            ObservedKind.CargoShip => ("Грузовой корабль", "Корабль замечен", "Отметки появления грузового корабля на выбранном сервере; прогноз приблизителен и строится по вашим отметкам."),
            _ => ("Аэрдроп", "Аэрдроп замечен", "Аэрдропы могут быть нерегулярными. Если интервалы не складываются в обоснованный ритм, прогноз не показывается — это нормально."),
        };
    }

    protected override async Task OnServerChangedAsync() => await RefreshAsync();

    public async Task RefreshAsync()
    {
        Items.Clear(); _events = new();
        if (SelectedServer is null) { Recompute(); return; }
        _events = (await _obs.ListAsync(SelectedServer.Id, Kind)).ToList();
        foreach (var e in _events.OrderByDescending(x => x.AtUtc)) Items.Add(new ObservedItemViewModel(e, this));
        Recompute();
    }

    public void Recompute()
    {
        NotifyText = Fmt.NotifyStatus(_prefs, ForecastService.EventIdFor(Kind));
        var f = ForecastEngine.Compute(_events, _clock.UtcNow, ForecastService.OptionsFor(Kind));
        LastText = "Последнее наблюдение: " + Fmt.Local(f.Last);
        CountText = $"Наблюдений: {f.Observations}";
        if (f.State == ForecastState.Ready && f.Next is not null)
        {
            var rem = f.Next.Value - _clock.UtcNow;
            NextText = $"Следующее (ориентировочно): {Fmt.Local(f.Next)} — через {Fmt.Span(rem)}";
            RangeText = $"Диапазон: {Fmt.Local(f.From)} … {Fmt.Local(f.To)}";
            ConfidenceText = "Уверенность: " + f.ConfidenceText;
            ReasonText = "Это оценка по вашим наблюдениям, а не подтверждённый факт.";
        }
        else
        {
            NextText = f.State == ForecastState.Overdue ? "Ожидаемое окно прошло" : "Прогноз недоступен";
            RangeText = f.State == ForecastState.Overdue ? $"Было: {Fmt.Local(f.From)} … {Fmt.Local(f.To)}" : "";
            ConfidenceText = "";
            ReasonText = f.Reason;
        }
    }

    [RelayCommand]
    private async Task RecordAsync()
    {
        Error = "";
        if (SelectedServer is null) { Error = "Сначала выберите сервер (вкладка «Серверы»)."; return; }
        try { await _obs.AddAsync(SelectedServer.Id, Kind, _clock.UtcNow); await RefreshAsync(); }
        catch (InvalidOperationException ex) { Error = ex.Message; await ReloadServersAsync(); }
    }

    public async Task SaveItemAsync(ObservedItemViewModel i)
    {
        if (!Fmt.TryLocal(i.TimeText, out var utc)) { i.Error = "Не удалось разобрать дату и время."; return; }
        if (utc > _clock.UtcNow.AddMinutes(5)) { i.Error = "Время наблюдения не может быть в будущем."; return; }
        await _obs.UpdateAsync(i.Event.Id, utc, i.Note); await RefreshAsync();
    }
    public async Task DeleteItemAsync(ObservedItemViewModel i) { await _obs.DeleteAsync(i.Event.Id); await RefreshAsync(); }
}

public sealed partial class BradleyViewModel : ServerBoundViewModel, IObservedHost
{
    private readonly BradleyService _svc; private readonly ObservedRepository _obs; private readonly IClock _clock; private readonly NotificationPrefs _prefs;
    private BradleyStatus? _status;
    public ObservableCollection<ObservedItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _notifyText = "";

    public BradleyViewModel(BradleyService svc, ObservedRepository obs, ServerRepository repo, IClock clock, NotificationPrefs prefs) : base(repo)
    { _svc = svc; _obs = obs; _clock = clock; _prefs = prefs; }

    protected override async Task OnServerChangedAsync() => await RefreshAsync();

    public async Task RefreshAsync()
    {
        Items.Clear();
        if (SelectedServer is null) { StatusText = "Выберите сервер."; CountdownText = ""; return; }
        foreach (var e in (await _obs.ListAsync(SelectedServer.Id, ObservedKind.BradleyDestroyed)).OrderByDescending(x => x.AtUtc)) Items.Add(new ObservedItemViewModel(e, this));
        _status = await _svc.GetStatusAsync(SelectedServer.Id);
        MinText = _status.MinMinutes?.ToString() ?? ""; MaxText = _status.MaxMinutes?.ToString() ?? "";
        Tick();
    }

    public void Tick()
    {
        NotifyText = Fmt.NotifyStatus(_prefs, NotifyEventIds.BradleyRespawn);
        if (_status is null) return;
        var st = _status; var now = _clock.UtcNow;
        // фаза пересчитывается локально по сохранённым меткам времени — таймер не «перезапускается» после перезапуска приложения
        if (st.EarliestUtc is { } e && st.LatestUtc is { } l)
        {
            var phase = now < e ? BradleyPhase.Counting : (l > e && now <= l ? BradleyPhase.InWindow : BradleyPhase.WindowEnded);
            _status = st with { Phase = phase };
            StatusText = phase switch
            {
                BradleyPhase.Counting => "Идёт расчётный отсчёт (оценка по заданному вами интервалу).",
                BradleyPhase.InWindow => "Расчётное окно респавна началось (появление не подтверждено).",
                _ => "Расчётный интервал завершился. Это не подтверждает, что Bradley появился."
            };
            CountdownText = phase == BradleyPhase.Counting
                ? $"До расчётного респавна: {Fmt.Span(e - now)}   (уничтожен {Fmt.Local(st.DestroyedAtUtc)}; расчётное время {Fmt.Local(e)}{(l > e ? " … " + Fmt.Local(l) : "")})"
                : $"Уничтожен {Fmt.Local(st.DestroyedAtUtc)}; расчётное время было {Fmt.Local(e)}{(l > e ? " … " + Fmt.Local(l) : "")}";
        }
        else { StatusText = st.Message; CountdownText = st.DestroyedAtUtc is null ? "" : $"Уничтожен {Fmt.Local(st.DestroyedAtUtc)}"; }
    }

    [RelayCommand]
    private async Task DestroyedAsync()
    {
        Error = "";
        if (SelectedServer is null) { Error = "Сначала выберите сервер (вкладка «Серверы»)."; return; }
        try { await _svc.MarkDestroyedAsync(SelectedServer.Id); await RefreshAsync(); }
        catch (InvalidOperationException ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task SaveIntervalAsync()
    {
        Error = "";
        if (SelectedServer is null) { Error = "Выберите сервер."; return; }
        if (!int.TryParse(MinText.Trim(), out var min)) { Error = "Минимальный интервал — целое число минут."; return; }
        int? max = null;
        if (!string.IsNullOrWhiteSpace(MaxText)) { if (!int.TryParse(MaxText.Trim(), out var m)) { Error = "Максимум — целое число минут или пусто."; return; } max = m; }
        try { await _svc.SetIntervalAsync(SelectedServer.Id, min, max); await RefreshAsync(); }
        catch (ArgumentException ex) { Error = ex.Message; }
    }

    public async Task SaveItemAsync(ObservedItemViewModel i)
    {
        if (!Fmt.TryLocal(i.TimeText, out var utc)) { i.Error = "Не удалось разобрать дату и время."; return; }
        await _obs.UpdateAsync(i.Event.Id, utc, i.Note); await RefreshAsync();
    }
    public async Task DeleteItemAsync(ObservedItemViewModel i) { await _obs.DeleteAsync(i.Event.Id); await RefreshAsync(); }
}

public sealed partial class ContainerItemViewModel : ObservableObject
{
    private readonly ContainerViewModel _parent;
    public ContainerTimer Timer { get; }
    public ContainerItemViewModel(ContainerTimer t, ContainerViewModel p) { Timer = t; _parent = p; }
    public string Label => Timer.Label;
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _statusText = "";
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteAsync(this);
    public void Refresh(DateTimeOffset now)
    {
        var rem = Timer.Remaining(now);
        RemainingText = rem > TimeSpan.Zero ? Fmt.Span(rem) : "00:00:00";
        StatusText = rem <= TimeSpan.Zero ? "Расчётное время открытия прошло" : rem <= ContainerTimer.Warn ? "Скоро откроется" : "Идёт отсчёт 15 минут";
    }
}

public sealed partial class ContainerViewModel : ObservableObject
{
    private readonly ContainerService _svc; private readonly IClock _clock; private readonly NotificationPrefs _prefs;
    public ObservableCollection<ContainerItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _notifyText = "";
    [ObservableProperty] private bool _isEmpty = true;
    public ContainerViewModel(ContainerService svc, IClock clock, NotificationPrefs prefs) { _svc = svc; _clock = clock; _prefs = prefs; }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var t in await _svc.ListAsync()) { var i = new ContainerItemViewModel(t, this); i.Refresh(_clock.UtcNow); Items.Add(i); }
        IsEmpty = Items.Count == 0;
    }
    public void Tick() { NotifyText = Fmt.NotifyStatus(_prefs, NotifyEventIds.Container); foreach (var i in Items) i.Refresh(_clock.UtcNow); }

    [RelayCommand]
    private async Task ActivateAsync()
    {
        Error = "";
        try { await _svc.ActivateAsync(Label, null); Label = ""; await LoadAsync(); }
        catch (ArgumentException ex) { Error = ex.Message; }
    }
    internal async Task DeleteAsync(ContainerItemViewModel i) { await _svc.DeleteAsync(i.Timer.Id); await LoadAsync(); }
}

public sealed partial class DecayItemViewModel : ObservableObject
{
    private readonly DecayViewModel _parent;
    public DecayObject Obj { get; }
    public DecayItemViewModel(DecayObject o, DecayViewModel p) { Obj = o; _parent = p; _notify = o.Notify; }
    public string Title => $"{Obj.Label} — {Obj.Element}, {DecayCalculator.MaterialRu(Obj.Material)}, {Obj.Grid.ToUpperInvariant()}";
    public ObservableCollection<string> Lines { get; } = new();
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _notify;
    partial void OnNotifyChanged(bool value) { if (value != Obj.Notify) { Obj.Notify = value; _ = _parent.SetNotifyAsync(this, value); } }
    [RelayCommand] private void Edit() => _parent.BeginEdit(this);
    [RelayCommand] private Task DestroyedAsync() => _parent.MarkDestroyedAsync(this);
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteAsync(this);

    public void Refresh(AppSettings s, DateTimeOffset now)
    {
        var r = DecayCalculator.Calculate(Obj, s, now);
        Lines.Clear();
        Lines.Add($"Обнаружено гниющим: {Fmt.Local(Obj.NoticedAtUtc)} • HP: {(Obj.Hp is null ? "неизвестно" : Obj.Hp.Value.ToString("0.##"))} / {(Obj.MaxHp is null ? "неизвестно" : Obj.MaxHp.Value.ToString("0.##"))}");
        switch (r.State)
        {
            case DecayState.Estimate:
                Headline = $"Осталось около {Fmt.Span(r.EstimateUtc!.Value - now)} (оценка)";
                Lines.Add($"Расчётное разрушение: {Fmt.Local(r.EstimateUtc)} (диапазон {Fmt.Local(r.FromUtc)} … {Fmt.Local(r.ToUtc)})");
                break;
            case DecayState.TimePassed:
                Headline = "Расчётное время прошло"; Lines.Add(r.Message); break;
            case DecayState.Insufficient:
                Headline = "Точное время не рассчитано"; Lines.Add(r.Message);
                if (r.UpperBoundUtc is not null) Lines.Add($"Верхняя граница (не позже, чем): {Fmt.Local(r.UpperBoundUtc)}");
                break;
            default: Headline = r.State == DecayState.Destroyed ? "Разрушен" : "Ошибка данных"; Lines.Add(r.Message); break;
        }
        foreach (var a in r.Assumptions) Lines.Add("• " + a);
    }
}

public sealed partial class DecayViewModel : ObservableObject
{
    private readonly DecayRepository _repo; private readonly ISettingsProvider _settings; private readonly RustCompanion.Core.Data.SettingsRepository _settingsRepo; private readonly IClock _clock; private readonly NotificationPrefs _prefs;
    public static readonly string[] Elements = { "Стена", "Фундамент", "Потолок/перекрытие", "Крыша", "Дверной проём", "Оконный проём", "Лестница", "Другое" };
    public static readonly DecayMaterial[] Materials = { DecayMaterial.Wood, DecayMaterial.Stone, DecayMaterial.Metal, DecayMaterial.Hqm };
    public IReadOnlyList<string> ElementNames => Elements;
    public IReadOnlyList<string> MaterialNames { get; } = Materials.Select(DecayCalculator.MaterialRu).ToList();
    public ObservableCollection<DecayItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _element = "Стена";
    [ObservableProperty] private int _materialIndex = 1;
    [ObservableProperty] private string _grid = "";
    [ObservableProperty] private string _noticedText = "";
    [ObservableProperty] private string _hpText = "";
    [ObservableProperty] private string _maxHpText = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _formTitle = "Новый объект";
    [ObservableProperty] private string _notifyText = "";
    [ObservableProperty] private string _woodH = ""; [ObservableProperty] private string _stoneH = ""; [ObservableProperty] private string _metalH = "";
    [ObservableProperty] private string _hqmH = ""; [ObservableProperty] private string _scaleText = ""; [ObservableProperty] private string _paramsMessage = "";
    private long? _editingId;

    public DecayViewModel(DecayRepository repo, ISettingsProvider settings, RustCompanion.Core.Data.SettingsRepository settingsRepo, IClock clock, NotificationPrefs prefs)
    { _repo = repo; _settings = settings; _settingsRepo = settingsRepo; _clock = clock; _prefs = prefs; ResetForm(); LoadParams(); }

    private void LoadParams()
    {
        var s = _settings.Current;
        WoodH = s.DecayHoursWood.ToString(CultureInfo.CurrentCulture); StoneH = s.DecayHoursStone.ToString(CultureInfo.CurrentCulture);
        MetalH = s.DecayHoursMetal.ToString(CultureInfo.CurrentCulture); HqmH = s.DecayHoursHqm.ToString(CultureInfo.CurrentCulture); ScaleText = s.DecayScale.ToString(CultureInfo.CurrentCulture);
    }

    private void ResetForm()
    {
        _editingId = null; FormTitle = "Новый объект"; Label = ""; Grid = ""; HpText = ""; MaxHpText = ""; MaterialIndex = 1; Element = "Стена";
        NoticedText = DateTime.Now.ToString("G", CultureInfo.CurrentCulture);
    }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var o in await _repo.ListAsync(true)) { var i = new DecayItemViewModel(o, this); i.Refresh(_settings.Current, _clock.UtcNow); Items.Add(i); }
        var active = Items.Count(i => !i.Obj.Destroyed);
        CountText = $"Активных объектов: {active} из {DecayRepository.MaxActive}";
    }

    public void Tick()
    {
        NotifyText = Fmt.NotifyStatus(_prefs, NotifyEventIds.Decay) + " (у каждой записи — свой переключатель, по умолчанию выключен)";
        foreach (var i in Items) i.Refresh(_settings.Current, _clock.UtcNow);
    }

    private static bool TryNum(string t, out double v) => double.TryParse(t.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = "";
        if (!Fmt.TryLocal(NoticedText, out var noticed)) { Error = "Не удалось разобрать дату и время обнаружения гниения."; return; }
        double? hp = null, max = null;
        if (!string.IsNullOrWhiteSpace(HpText)) { if (!TryNum(HpText, out var h)) { Error = "HP — число."; return; } hp = h; }
        if (!string.IsNullOrWhiteSpace(MaxHpText)) { if (!TryNum(MaxHpText, out var m)) { Error = "Максимальный HP — число."; return; } max = m; }
        var o = new DecayObject { Id = _editingId ?? 0, Label = Label, Element = Element, Material = Materials[Math.Clamp(MaterialIndex, 0, 3)], Grid = Grid, NoticedAtUtc = noticed, Hp = hp, MaxHp = max };
        try
        {
            if (_editingId is null) await _repo.AddAsync(o); else await _repo.UpdateAsync(o);
            ResetForm(); await LoadAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }

    [RelayCommand] private void ClearForm() { Error = ""; ResetForm(); }

    internal void BeginEdit(DecayItemViewModel i)
    {
        var o = i.Obj; _editingId = o.Id; FormTitle = "Редактирование объекта";
        Label = o.Label; Element = o.Element; MaterialIndex = Array.IndexOf(Materials, o.Material); Grid = o.Grid;
        NoticedText = o.NoticedAtUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        HpText = o.Hp?.ToString(CultureInfo.CurrentCulture) ?? ""; MaxHpText = o.MaxHp?.ToString(CultureInfo.CurrentCulture) ?? "";
    }

    internal async Task SetNotifyAsync(DecayItemViewModel i, bool on) => await _repo.SetNotifyAsync(i.Obj.Id, on);
    internal async Task MarkDestroyedAsync(DecayItemViewModel i) { await _repo.MarkDestroyedAsync(i.Obj.Id); await LoadAsync(); }
    internal async Task DeleteAsync(DecayItemViewModel i) { await _repo.DeleteAsync(i.Obj.Id); await LoadAsync(); }

    [RelayCommand]
    private async Task SaveParamsAsync()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
        if (!TryNum(WoodH, out var w) || !TryNum(StoneH, out var st) || !TryNum(MetalH, out var m) || !TryNum(HqmH, out var h) || !TryNum(ScaleText, out var sc)) { ParamsMessage = "Все параметры — числа."; return; }
        s.DecayHoursWood = w; s.DecayHoursStone = st; s.DecayHoursMetal = m; s.DecayHoursHqm = h; s.DecayScale = sc;
        var errors = s.Validate();
        if (errors.Count > 0) { ParamsMessage = string.Join(" ", errors); return; }
        await _settingsRepo.SaveAsync(s); _settings.Update(s); ParamsMessage = "Параметры расчёта сохранены."; Tick();
    }
}

public sealed partial class NotifyRowViewModel : ObservableObject
{
    private readonly NotificationPrefs _prefs; private readonly NotificationPrefsRepository _repo;
    public NotifyEventInfo Info { get; }
    [ObservableProperty] private bool _overlay;
    [ObservableProperty] private bool _system;
    [ObservableProperty] private bool _external;
    private bool _loading = true;
    public NotifyRowViewModel(NotifyEventInfo info, DeliveryChoice c, NotificationPrefs prefs, NotificationPrefsRepository repo)
    { Info = info; _prefs = prefs; _repo = repo; _overlay = c.Overlay; _system = c.System; _external = c.External; _loading = false; }
    partial void OnOverlayChanged(bool value) => Save();
    partial void OnSystemChanged(bool value) => Save();
    partial void OnExternalChanged(bool value) => Save();
    private void Save()
    {
        if (_loading) return;
        var c = new DeliveryChoice(Overlay, System, Info.HasExternal && External);
        _prefs.Set(Info.Id, c);                       // отключение одного события не затрагивает остальные
        _ = _repo.SaveAsync(Info.Id, c);
    }
}

/// <summary>Строка выбора канала доставки для одного типа игрового уведомления (сохраняется сразу и переживает перезапуск).</summary>
public sealed partial class GameChannelRowViewModel : ObservableObject
{
    private readonly NotificationsViewModel _owner; private bool _loading = true;
    public string Kind { get; }
    public string Title => GameNotifyKind.Title(Kind);
    public IReadOnlyList<string> Options { get; } = new[] { "Отключено", "Rust+ Team Chat", "Overlay поверх игры", "Team Chat и Overlay" };
    [ObservableProperty] private int _selectedIndex;
    public GameChannelRowViewModel(string kind, NotifyChannel channel, NotificationsViewModel owner) { Kind = kind; _owner = owner; _selectedIndex = (int)channel; _loading = false; }
    partial void OnSelectedIndexChanged(int value) { if (!_loading) _ = _owner.SaveChannelAsync(this); }
}

public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly NotificationPrefs _prefs; private readonly NotificationPrefsRepository _repo;
    private readonly ISettingsProvider _settings; private readonly RustCompanion.Core.Data.SettingsRepository _settingsRepo; private readonly GameNotifyLog _log;
    public ObservableCollection<NotifyRowViewModel> Rows { get; } = new();
    public ObservableCollection<GameChannelRowViewModel> Channels { get; } = new();
    public ObservableCollection<string> DeliveryLog { get; } = new();
    [ObservableProperty] private string _channelMessage = "Team Chat требует активного соединения Rust+ с соответствующим сервером; результат каждой отправки показан ниже.";

    public NotificationsViewModel(NotificationPrefs prefs, NotificationPrefsRepository repo, ISettingsProvider settings,
        RustCompanion.Core.Data.SettingsRepository settingsRepo, GameNotifyLog log)
    {
        _prefs = prefs; _repo = repo; _settings = settings; _settingsRepo = settingsRepo; _log = log;
        _log.Added += e => { var d = System.Windows.Application.Current?.Dispatcher; if (d is null || d.CheckAccess()) AddLog(e); else d.BeginInvoke(() => AddLog(e)); };
    }

    private void AddLog(GameNotifyLogEntry e) { DeliveryLog.Insert(0, e.Line); while (DeliveryLog.Count > 50) DeliveryLog.RemoveAt(DeliveryLog.Count - 1); }

    public async Task LoadAsync()
    {
        _prefs.Load(await _repo.LoadAsync());
        Rows.Clear();
        foreach (var info in NotifyCatalog.All) Rows.Add(new NotifyRowViewModel(info, _prefs.Get(info.Id), _prefs, _repo));
        Channels.Clear();
        foreach (var k in GameNotifyKind.All.Where(x => x != GameNotifyKind.Alarm)) Channels.Add(new GameChannelRowViewModel(k, GameNotifyKind.ChannelFor(_settings.Current, k), this));
        DeliveryLog.Clear(); foreach (var e in _log.Items.Reverse()) DeliveryLog.Add(e.Line);
    }

    public async Task SaveChannelAsync(GameChannelRowViewModel row)
    {
        try
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
            GameNotifyKind.SetChannel(copy, row.Kind, NotifyChannelExt.FromInt(row.SelectedIndex));
            var errors = copy.Validate();
            if (errors.Count > 0) { ChannelMessage = string.Join(" ", errors); return; }
            await _settingsRepo.SaveAsync(copy); _settings.Update(copy);
            ChannelMessage = $"Сохранено: «{row.Title}» → {NotifyChannelExt.FromInt(row.SelectedIndex).Ru()}.";
        }
        catch (Exception ex) { ChannelMessage = "Не удалось сохранить канал: " + ex.Message; }
    }
}
