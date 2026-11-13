using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Notifications;

namespace RustCompanion.App.ViewModels;

public sealed record MonumentChoice(string Id, string Display, string Group) { public override string ToString() => Display; }

public sealed partial class PressItemViewModel : ObservableObject
{
    private readonly MonumentItemViewModel _parent;
    public MonumentPress Press { get; }
    public PressItemViewModel(MonumentPress p, MonumentItemViewModel parent) { Press = p; _parent = parent; }
    public string Text => Fmt.Local(Press.AtUtc) + (Press.Active ? "" : "  (до сброса)");
    [RelayCommand] private Task DeleteAsync() => _parent.DeletePressAsync(this);
}

public sealed partial class MonumentItemViewModel : ObservableObject
{
    private readonly MonumentsViewModel _parent;
    private ForecastResult? _forecast;
    public TrackedMonument Tracked { get; }
    public ObservableCollection<PressItemViewModel> Presses { get; } = new();
    public MonumentItemViewModel(TrackedMonument t, MonumentsViewModel p) { Tracked = t; _parent = p; }
    public string Name => Tracked.Name;
    public string Subtitle => MonumentCatalog.Find(Tracked.MonumentId) is { } m ? m.NameEn : "свой РТ";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _confidenceText = "";
    [ObservableProperty] private string _hintText = "";

    public async Task RefreshAsync()
    {
        Presses.Clear();
        var all = await _parent.Service.ListPressesAsync(Tracked.ServerId, Tracked.MonumentId, false);
        foreach (var p in all.OrderByDescending(x => x.AtUtc).Take(8)) Presses.Add(new PressItemViewModel(p, this));
        _forecast = await _parent.Service.ComputeAsync(Tracked.ServerId, Tracked.MonumentId);
        var active = all.Where(p => p.Active).Select(p => p.AtUtc).ToList();
        HintText = MonumentRespawnService.Hint(active, _forecast) ?? "";
        Recompute();
    }

    public void Recompute()
    {
        var f = _forecast;
        if (f is null) return;
        var now = _parent.Now;
        LastText = "Последнее нажатие: " + Fmt.Local(f.Last);
        CountText = $"Нажатий в текущем отсчёте: {f.Observations}";
        if (f.State == ForecastState.Ready && f.Next is not null)
        {
            var rem = f.Next.Value - now;
            NextText = rem > TimeSpan.Zero ? $"Следующий респавн (оценка): {Fmt.Local(f.Next)} — через {Fmt.Span(rem)}"
                                           : "Расчётное время респавна наступило — нажмите «РТ респавн», когда увидите уведомление";
            RangeText = f.From != f.To ? $"Диапазон: {Fmt.Local(f.From)} … {Fmt.Local(f.To)}" : (f.MedianInterval is { } mi ? $"Интервал между нажатиями: {mi.TotalMinutes:0.#} мин" : "");
            ConfidenceText = "Уверенность: " + f.ConfidenceText;
        }
        else
        {
            NextText = f.State == ForecastState.Overdue ? "Ожидаемое окно прошло" : (f.Observations == 1 ? "Нужно ещё одно нажатие, чтобы вычислить интервал" : "Прогноз недоступен");
            RangeText = ""; ConfidenceText = "";
            if (f.State == ForecastState.Insufficient && f.Observations == 0) HintText = "Нажмите «РТ респавн» после первого уведомления о респавне, затем ещё раз после следующего.";
        }
    }

    [RelayCommand] private Task PressAsync() => _parent.PressAsync(this);
    [RelayCommand] private Task ResetAsync() => _parent.ResetAsync(this);
    [RelayCommand] private Task UntrackAsync() => _parent.UntrackAsync(this);
    internal async Task DeletePressAsync(PressItemViewModel p) { await _parent.Service.DeletePressAsync(p.Press.Id); await RefreshAsync(); }
}

public sealed partial class MonumentsViewModel : ServerBoundViewModel
{
    private readonly IClock _clock; private readonly NotificationPrefs _prefs;
    private readonly System.Collections.Generic.List<MonumentChoice> _allChoices;
    public MonumentRespawnService Service { get; }
    public DateTimeOffset Now => _clock.UtcNow;
    public ObservableCollection<MonumentChoice> Choices { get; } = new();
    public ObservableCollection<MonumentItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private MonumentChoice? _selectedChoice;
    [ObservableProperty] private string _customName = "";
    [ObservableProperty] private string _notifyText = "";
    [ObservableProperty] private string _catalogInfo = "";

    public MonumentsViewModel(MonumentRespawnService service, ServerRepository repo, IClock clock, NotificationPrefs prefs) : base(repo)
    {
        Service = service; _clock = clock; _prefs = prefs;
        _allChoices = MonumentCatalog.All.Select(m => new MonumentChoice(m.Id, $"{m.Display} — {m.Group}", m.Group)).ToList();
        CatalogInfo = $"В справочнике {MonumentCatalog.All.Count} РТ (по вики Facepunch, Fandom и гайдам); список меняется между версиями — недостающий РТ можно добавить как свой.";
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Choices.Clear();
        foreach (var c in _allChoices.Where(c => string.IsNullOrWhiteSpace(FilterText) || c.Display.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || c.Group.Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
            Choices.Add(c);
        SelectedChoice = Choices.FirstOrDefault();
    }

    protected override async Task OnServerChangedAsync() => await RefreshAsync();

    public async Task RefreshAsync()
    {
        Items.Clear();
        if (SelectedServer is null) return;
        foreach (var t in await Service.ListTrackedAsync(SelectedServer.Id)) { var i = new MonumentItemViewModel(t, this); await i.RefreshAsync(); Items.Add(i); }
    }

    public void Tick() { NotifyText = Fmt.NotifyStatus(_prefs, NotifyEventIds.ForecastMonument); foreach (var i in Items) i.Recompute(); }

    [RelayCommand]
    private async Task TrackAsync()
    {
        Error = "";
        if (SelectedServer is null) { Error = "Сначала выберите сервер (вкладка «Серверы»)."; return; }
        if (SelectedChoice is null) { Error = "Выберите РТ из списка."; return; }
        try { await Service.TrackAsync(SelectedServer.Id, SelectedChoice.Id); await RefreshAsync(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task TrackCustomAsync()
    {
        Error = "";
        if (SelectedServer is null) { Error = "Сначала выберите сервер (вкладка «Серверы»)."; return; }
        if (string.IsNullOrWhiteSpace(CustomName)) { Error = "Введите название РТ."; return; }
        try { await Service.TrackAsync(SelectedServer.Id, MonumentCatalog.CustomId(CustomName)); CustomName = ""; await RefreshAsync(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }

    internal async Task PressAsync(MonumentItemViewModel i) { await Service.PressAsync(i.Tracked.ServerId, i.Tracked.MonumentId); await i.RefreshAsync(); }
    internal async Task ResetAsync(MonumentItemViewModel i) { await Service.ResetAsync(i.Tracked.ServerId, i.Tracked.MonumentId); await i.RefreshAsync(); }
    internal async Task UntrackAsync(MonumentItemViewModel i) { await Service.UntrackAsync(i.Tracked.ServerId, i.Tracked.MonumentId); await RefreshAsync(); }
}
