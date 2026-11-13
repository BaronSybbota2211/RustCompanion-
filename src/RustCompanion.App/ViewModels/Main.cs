using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Models;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;
using RustCompanion.Core.Watch;

namespace RustCompanion.App.ViewModels;

public sealed record NavItem(string Title, object ViewModel);

public sealed record GroupSection(string Header, object ViewModel);
/// <summary>Одна вкладка меню с внутренними разделами (ручной таймер + прогноз одного и того же события).</summary>
public sealed partial class GroupViewModel : ObservableObject
{
    public string Title { get; }
    public IReadOnlyList<GroupSection> Sections { get; }
    [ObservableProperty] private GroupSection _selected;
    [ObservableProperty] private object _current;
    public GroupViewModel(string title, params GroupSection[] sections) { Title = title; Sections = sections; _selected = sections[0]; _current = sections[0].ViewModel; }
    [RelayCommand] private void Select(GroupSection s) { Selected = s; Current = s.ViewModel; }
}

public sealed record ForecastSet(ForecastViewModel Chinook, ForecastViewModel Heli, ForecastViewModel Airdrop, ForecastViewModel BradleyAppear, ForecastViewModel Cargo);

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ServersViewModel _servers;
    private readonly IReadOnlyList<TrackerViewModel> _trackers;
    private readonly IClock _clock;
    private readonly Action<string> _navigate;

    public ObservableCollection<string> FavoriteServers { get; } = new();
    public ObservableCollection<string> ActiveTimers { get; } = new();
    [ObservableProperty] private bool _noServers = true;
    [ObservableProperty] private bool _noTimers = true;

    public DashboardViewModel(ServersViewModel servers, IReadOnlyList<TrackerViewModel> trackers, IClock clock, Action<string> navigate)
    { _servers = servers; _trackers = trackers; _clock = clock; _navigate = navigate; }

    public void Refresh()
    {
        FavoriteServers.Clear();
        foreach (var s in _servers.Items.Where(i => i.Favorite))
            FavoriteServers.Add($"{s.Name}: {s.StateText}. {s.DetailsText}");
        NoServers = FavoriteServers.Count == 0;

        var active = _trackers.SelectMany(t => t.Items.Select(i => (t, i)))
            .Where(x => TimerCalculator.DueAt(x.i.Entry) is not null)
            .OrderBy(x => TimerCalculator.DueAt(x.i.Entry))
            .Take(8).ToList();
        ActiveTimers.Clear();
        foreach (var (t, i) in active) ActiveTimers.Add($"{t.Title} • {i.Name}: {i.RemainingText}");
        NoTimers = ActiveTimers.Count == 0;
    }

    [RelayCommand] private void GoTimers() => _navigate("Мониторинг РТ");
    [RelayCommand] private void GoServers() => _navigate("Серверы");
    [RelayCommand] private void GoDecay() => _navigate("Гниение");
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ServersViewModel _servers;
    private readonly PlayersViewModel _players;
    private readonly WatchViewModel _watch;
    private readonly MonitoringService _monitoring; private readonly RustCompanion.Core.Watch.SteamLiveMonitor _live;
    private readonly SettingsViewModel _settingsVm;
    private readonly NotificationsViewModel _notifications;
    private readonly RustCompanion.Core.Data.SettingsRepository _settingsRepo;
    private readonly ISettingsProvider _settings;
    private readonly ForecastService _forecast;
    private readonly BradleyService _bradleySvc;
    private readonly ContainerService _containerSvc;
    private readonly DecayService _decaySvc;
    private readonly MonumentRespawnService _monumentSvc;
    private readonly RustCompanion.App.Services.OverlayService _overlay;
    private readonly RustPlusDevicesViewModel _rustPlusDevices;
    private readonly DeviceAvailabilityService _availability;
    private readonly ServerCrawler _crawler;
    private bool _ticking;
    private int _tickCount;

    public IReadOnlyList<TrackerViewModel> Trackers { get; }
    public IReadOnlyList<ForecastViewModel> Forecasts { get; }
    public BradleyViewModel Bradley { get; }
    public ContainerViewModel Container { get; }
    public DecayViewModel Decay { get; }
    public MonumentsViewModel Monuments { get; }
    public RustPlusViewModel RustPlus { get; }
    public ServerBrowserViewModel Browser { get; }
    public HotkeysViewModel Hotkeys { get; }
    public OverlayViewModel OverlayVm { get; }
    public DashboardViewModel Dashboard { get; }
    public IReadOnlyList<NavItem> Items { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Current))] private NavItem? _selected;
    public object? Current => Selected?.ViewModel;

    public MainViewModel(TimerService timers, ITimerRepository timerRepo, IClock clock, ServersViewModel servers, PlayersViewModel players,
        SettingsViewModel settingsVm, RustCompanion.Core.Data.SettingsRepository settingsRepo, ISettingsProvider settings,
        WatchViewModel watch, MonitoringService monitoring, NotificationsViewModel notifications,
        ForecastSet forecasts, BradleyViewModel bradley, ContainerViewModel container, DecayViewModel decay,
        ForecastService forecast, BradleyService bradleySvc, ContainerService containerSvc, DecayService decaySvc,
        MonumentsViewModel monuments, MonumentRespawnService monumentSvc,
        ServerBrowserViewModel browser, HotkeysViewModel hotkeys, OverlayViewModel overlayVm, RustCompanion.App.Services.OverlayService overlay, RustPlusViewModel rustPlus,
        RustPlusDevicesViewModel rustPlusDevices, DeviceAvailabilityService availability, ServerCrawler crawler, PlayerDirectoryRepository directory, IClock directoryClock,
        RustCompanion.Core.Watch.SteamLiveMonitor live)
    {
        _live = live; _watch = watch; _monitoring = monitoring; _servers = servers; _players = players; _settingsVm = settingsVm; _notifications = notifications;
        _settingsRepo = settingsRepo; _settings = settings; _forecast = forecast; _bradleySvc = bradleySvc; _containerSvc = containerSvc; _decaySvc = decaySvc;
        browser.ServerOpened += async (srv, dbId) => { await RefreshServerListsAsync(); await watch.ShowServerAsync(dbId); };
        servers.ServersChanged += () => _ = OnServersChangedAsync(reloadWatch: true);       // сервер удалён/добавлен на вкладке «Серверы» → «Наблюдение» и «Поиск сервера» обновляются
        watch.ServersChanged += () => _ = OnServersChangedAsync(reloadWatch: false);       // сервер удалён на вкладке «Наблюдение» → «Серверы», прогнозы и т.д. обновляются
        watch.ConfirmRemoveServer = text => System.Windows.MessageBox.Show(text, "Rust Companion", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes;
        _rustPlusDevices = rustPlusDevices; _availability = availability; _crawler = crawler;
        browser.PlayersAvailable += (sourceKey, list) => { watch.SetOnlinePlayers(sourceKey, list); _ = directory.UpsertKnownServerAsync(sourceKey, "", directoryClock.UtcNow); };   // открытый в поиске сервер попадает в собственную базу
        browser.TrackingChanged += () => _ = watch.RefreshAsync();
        browser.OpenWatchRequested += () => Selected = Items.FirstOrDefault(i => i.ViewModel == watch) ?? Selected;
        RustPlus = rustPlus; Monuments = monuments; _monumentSvc = monumentSvc; Browser = browser; Hotkeys = hotkeys; OverlayVm = overlayVm; _overlay = overlay;
        Bradley = bradley; Container = container; Decay = decay; Forecasts = new[] { forecasts.Chinook, forecasts.Heli, forecasts.Airdrop, forecasts.BradleyAppear, forecasts.Cargo };
        Trackers = new[]
        {
            new TrackerViewModel(TrackerKind.Monument, timers, timerRepo, clock),
            new TrackerViewModel(TrackerKind.DeepSea, timers, timerRepo, clock),
            new TrackerViewModel(TrackerKind.PatrolHelicopter, timers, timerRepo, clock),
            new TrackerViewModel(TrackerKind.Bradley, timers, timerRepo, clock),
        };
        Dashboard = new DashboardViewModel(servers, Trackers, clock, title => Selected = Items.FirstOrDefault(i => i.Title == title) ?? Selected);
        Items = new List<NavItem>
        {
            new("Обзор", Dashboard), new("Rust+", rustPlus), new("Rust+ Устройства", rustPlusDevices), new("Поиск сервера", browser), new("Серверы", servers), new("Наблюдение", watch),
            new("Мониторинг РТ", Trackers[0]), new("Респавн РТ (счётчик)", monuments), new("Deep Sea", Trackers[1]),
            new("Вертолёт", new GroupViewModel("Вертолёт", new("Ручной таймер", Trackers[2]), new("Прогноз", forecasts.Heli))),
            new("Bradley", new GroupViewModel("Bradley", new("Ручной таймер", Trackers[3]), new("Прогноз (отметки)", forecasts.BradleyAppear), new("После уничтожения", bradley))),
            new("Чинук", forecasts.Chinook), new("Аэрдроп", forecasts.Airdrop), new("Грузовой корабль", forecasts.Cargo),
            new("Закрытый контейнер", container),
            new("Гниение", decay),
            new("Горячие клавиши", hotkeys), new("Оверлей", overlayVm), new("Уведомления", notifications), new("Настройки", settingsVm), new("О программе", new AboutViewModel()),
        };
        _selected = Items[0];
    }

    /// <summary>После добавления/удаления сервера обновляет списки серверов на всех вкладках (без перезапуска).</summary>
    public async Task RefreshServerListsAsync()
    {
        await _servers.LoadAsync();
        foreach (var f in Forecasts) await f.ReloadServersAsync();
        await Bradley.ReloadServersAsync();
        await Monuments.ReloadServersAsync();
        await Hotkeys.ReloadServersAsync(selectFirst: false);   // для горячих клавиш выбор сервера всегда явный
    }

    /// <summary>Общий сервер добавлен или удалён: перечитывает списки серверов на всех вкладках, при необходимости — «Наблюдение», и отметки «Отслеживается» в поиске сервера.</summary>
    private async Task OnServersChangedAsync(bool reloadWatch)
    {
        await RefreshServerListsAsync();
        if (reloadWatch) await _watch.LoadAsync();
        await Browser.RefreshTrackingAsync();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _settings.Update(await _settingsRepo.LoadAsync(ct));
        _settingsVm.Reload();
        await _notifications.LoadAsync();            // переключатели загружаются до первого тика; по умолчанию все выключены
        foreach (var t in Trackers) await t.LoadAsync();
        await _servers.LoadAsync();
        foreach (var f in Forecasts) await f.ReloadServersAsync();
        await Bradley.ReloadServersAsync();
        await Monuments.ReloadServersAsync();
        await Container.LoadAsync();
        await Decay.LoadAsync();
        await RustPlus.LoadAsync();
        await _rustPlusDevices.LoadAsync();
        await Hotkeys.LoadAsync();
        OverlayVm.Load();
        _overlay.Start();
        _ = Browser.RunAutoRefreshAsync(ct);
        await _players.LoadAsync();
        await _watch.LoadAsync();
        Dashboard.Refresh();
        _ = _servers.RunPollingAsync(ct);
        _ = _live.RunLoopAsync(ct);               // проверка наблюдаемых игроков через Steam каждые 3 секунды
        _ = _monitoring.RunLoopAsync(ct);
        _ = _crawler.RunLoopAsync(ct);
        _ = _availability.RunLoopAsync(ct);
    }

    public async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            foreach (var t in Trackers) await t.TickAsync();
            foreach (var f in Forecasts) f.Recompute();
            Bradley.Tick(); Container.Tick(); Decay.Tick(); Monuments.Tick(); Hotkeys.Recompute();
            Dashboard.Refresh();
            if (++_tickCount % 10 == 0)               // предупреждения проверяются раз в 10 с по сохранённым временным меткам
            {
                await _forecast.CheckWarningsAsync();
                await _bradleySvc.CheckWarningsAsync();
                await _containerSvc.CheckWarningsAsync();
                await _decaySvc.CheckWarningsAsync();
                await _monumentSvc.CheckWarningsAsync();
            }
        }
        finally { _ticking = false; }
    }
}
