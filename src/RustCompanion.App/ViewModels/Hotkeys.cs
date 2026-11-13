using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.App.Services;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Hotkeys;
using RustCompanion.Core.Models;

namespace RustCompanion.App.ViewModels;

public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly HotkeysViewModel _parent;
    public HotkeyEvent Event { get; }
    public HotkeyRowViewModel(HotkeyEvent e, HotkeysViewModel p) { Event = e; _parent = p; }
    public string Name => HotkeyEvents.Name(Event);
    [ObservableProperty] private string _gestureText = "не назначена";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastMark = "—";
    [ObservableProperty] private string _interval = "—";
    [ObservableProperty] private string _next = "—";
    [ObservableProperty] private string _confidence = "";
    [ObservableProperty] private string _error = "";
    internal List<ObservedEvent> Events { get; set; } = new();

    [RelayCommand] private Task AssignAsync(HotkeyGesture gesture) => _parent.AssignAsync(this, gesture);
    [RelayCommand] private Task ClearAsync() => _parent.ClearAsync(this);
    [RelayCommand] private Task MarkAsync() => _parent.MarkManualAsync(this);
    [RelayCommand] private Task DeleteLastAsync() => _parent.DeleteLastAsync(this);
    [RelayCommand] private Task ResetAsync() => _parent.ResetAsync(this);
}

public sealed partial class HotkeysViewModel : ServerBoundViewModel
{
    private readonly HotkeyService _service; private readonly HotkeyRepository _repo; private readonly HotkeyManager _manager; private readonly ObservedRepository _obs;
    private readonly ISettingsProvider _settings; private readonly RustCompanion.Core.Data.SettingsRepository _settingsRepo; private readonly IClock _clock; private readonly OverlayService _overlay;
    private Dictionary<HotkeyEvent, HotkeyGesture> _assignments = new();
    private bool _loading;
    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = new();
    [ObservableProperty] private string _banner = "";
    [ObservableProperty] private string _lastConfirmation = "";
    [ObservableProperty] private bool _confirmOverlay = true;
    [ObservableProperty] private int _leadMinutes = 10;
    [ObservableProperty] private string _settingsMessage = "";

    public HotkeysViewModel(HotkeyService service, HotkeyRepository repo, HotkeyManager manager, ObservedRepository obs, ServerRepository servers,
        ISettingsProvider settings, RustCompanion.Core.Data.SettingsRepository settingsRepo, IClock clock, OverlayService overlay) : base(servers)
    {
        _service = service; _repo = repo; _manager = manager; _obs = obs; _settings = settings; _settingsRepo = settingsRepo; _clock = clock; _overlay = overlay;
        foreach (var e in HotkeyEvents.All) Rows.Add(new HotkeyRowViewModel(e, this));
        _manager.HotkeyPressed += ev => _ = OnPressedAsync(ev);
    }

    public async Task LoadAsync()
    {
        _loading = true;
        _assignments = (await _repo.LoadAsync()).ToDictionary(k => k.Key, v => v.Value);
        await ReloadServersAsync();
        var saved = _settings.Current.HotkeyServerId;
        SelectedServer = Servers.FirstOrDefault(s => s.Id == saved);   // без выбора сервера отметки не записываются — это показывается явно
        ConfirmOverlay = _settings.Current.HotkeyConfirmOverlay; LeadMinutes = _settings.Current.ForecastLeadMinutes;
        _loading = false;
        foreach (var r in Rows) r.GestureText = _assignments.TryGetValue(r.Event, out var g) ? g.ToString() : "не назначена";
        ApplyRegistrations();
        await RefreshAsync();
    }

    protected override async Task OnServerChangedAsync()
    {
        if (_loading) return;
        await SaveSettingsAsync(s => s.HotkeyServerId = SelectedServer?.Id ?? 0);
        _overlay.Invalidate();
        await RefreshAsync();
    }

    private async Task SaveSettingsAsync(Action<AppSettings> change)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
        change(s);
        var errors = s.Validate();
        if (errors.Count > 0) { SettingsMessage = string.Join(" ", errors); return; }
        await _settingsRepo.SaveAsync(s); _settings.Update(s); SettingsMessage = "";
    }

    [RelayCommand] private Task SaveOptionsAsync() => SaveSettingsAsync(s => { s.HotkeyConfirmOverlay = ConfirmOverlay; s.ForecastLeadMinutes = LeadMinutes; });

    private void ApplyRegistrations()
    {
        var results = _manager.Apply(_assignments);
        foreach (var r in Rows)
        {
            if (!_assignments.TryGetValue(r.Event, out var g)) { r.Status = "Клавиша не назначена — можно отмечать кнопкой"; continue; }
            var reg = results.FirstOrDefault(x => x.Event == r.Event);
            r.Status = reg is null ? "" : reg.Success ? "Работает глобально" + (g.Warning() is { } w ? ". " + w : "") : reg.Message;
        }
        var failed = results.Count(x => !x.Success);
        Banner = failed > 0 ? "Часть сочетаний не удалось зарегистрировать — для них используйте кнопку «Отметить» или другое сочетание." : "";
    }

    internal async Task AssignAsync(HotkeyRowViewModel row, HotkeyGesture g)
    {
        row.Error = "";
        var err = HotkeyAssignments.Validate(_assignments, row.Event, g);
        if (err is not null) { row.Error = err; return; }
        _assignments[row.Event] = g; await _repo.SaveAsync(row.Event, g);
        row.GestureText = g.ToString(); ApplyRegistrations();
    }

    internal async Task ClearAsync(HotkeyRowViewModel row)
    {
        _assignments.Remove(row.Event); await _repo.SaveAsync(row.Event, null);
        row.GestureText = "не назначена"; row.Error = ""; ApplyRegistrations();
    }

    public async Task OnPressedAsync(HotkeyEvent ev)
    {
        var r = await _service.PressAsync(ev);
        LastConfirmation = r.Message;
        _overlay.Invalidate();
        await RefreshAsync();
    }

    internal Task MarkManualAsync(HotkeyRowViewModel row) => OnPressedAsync(row.Event);

    internal async Task DeleteLastAsync(HotkeyRowViewModel row) { await _service.DeleteLastMarkAsync(row.Event); _overlay.Invalidate(); await RefreshAsync(); }
    internal async Task ResetAsync(HotkeyRowViewModel row) { await _service.ResetAsync(row.Event); _overlay.Invalidate(); await RefreshAsync(); }

    public async Task RefreshAsync()
    {
        foreach (var r in Rows)
            r.Events = SelectedServer is null ? new() : (await _obs.ListAsync(SelectedServer.Id, HotkeyEvents.Kind(r.Event))).ToList();
        Recompute();
    }

    public void Recompute()
    {
        var now = _clock.UtcNow;
        foreach (var r in Rows)
        {
            var kind = HotkeyEvents.Kind(r.Event);
            var f = ForecastEngine.Compute(r.Events, now, ForecastService.OptionsFor(kind));
            r.LastMark = f.Last is null ? "—" : Fmt.Local(f.Last);
            r.Interval = f.MedianInterval is { } mi ? $"{mi.TotalMinutes:0.#} мин" + (f.Observations > 2 ? $" (по {f.Observations - 1} интервалам)" : "") : "—";
            if (f.State == ForecastState.Ready && f.Next is not null)
            { r.Next = $"≈ {Fmt.Local(f.Next)} (через {Fmt.Span(f.Next.Value - now)})"; r.Confidence = f.ConfidenceText; }
            else { r.Next = f.State == ForecastState.Overdue ? "ожидаемое время прошло" : f.Observations == 1 ? "нужна ещё одна отметка" : "прогноз недоступен"; r.Confidence = f.State == ForecastState.Unavailable ? f.Reason : ""; }
        }
    }
}
