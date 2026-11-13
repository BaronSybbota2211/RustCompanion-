using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.App.Services;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;
using RustCompanion.RustPlusAdapter;

namespace RustCompanion.App.ViewModels;

/// <summary>Игрок команды: только квадрат состояния (зелёный — в сети, красный — мёртв, серый — не в сети) и квадрат карты.</summary>
public sealed record TeamMemberRow(string Name, System.Windows.Media.Brush StateBrush, string? Cell, string Tip);

public sealed record RpServerItem(PairedServer Server) { public override string ToString() => RustPlusRoutes.HasDirectAddress(Server.Ip, Server.Port) ? $"{Server.Name}  ({Server.Ip}:{Server.Port})" : Server.Name; }

public sealed partial class RpDeviceRowViewModel : ObservableObject
{
    private readonly RustPlusViewModel _parent;
    public RpDevice Device { get; }
    public string ServerName { get; }
    public string ServerText => $"Сервер: {ServerName}";
    public RpDeviceRowViewModel(RpDevice d, RustPlusViewModel p) { Device = d; _parent = p; ServerName = p.SelectedServer?.Server.Name ?? ""; _name = d.Name; _group = d.Group; _note = d.Note; _alarmNotify = d.AlarmNotify; _alarmText = d.AlarmText; _alarmChannel = d.AlarmChannel; }
    public string KindText => Device.Kind switch { RpDeviceKind.SmartSwitch => "Умный переключатель", RpDeviceKind.SmartAlarm => "Умная сигнализация", _ => "Монитор хранилища" };
    public bool IsSwitch => Device.Kind == RpDeviceKind.SmartSwitch;
    public bool IsAlarm => Device.Kind == RpDeviceKind.SmartAlarm;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _group;
    [ObservableProperty] private string _note;
    [ObservableProperty] private bool _alarmNotify;
    [ObservableProperty] private string _alarmText = "";
    public IReadOnlyList<string> AlarmChannelOptions { get; } = new[] { "Отключено", "Rust+ Team Chat", "Overlay поверх игры", "Team Chat и Overlay" };
    [ObservableProperty] private int _alarmChannel = 2;
    private int _saveSeq;
    partial void OnAlarmTextChanged(string value) => QueueAlarmSave();
    partial void OnAlarmChannelChanged(int value) => QueueAlarmSave();
    private async void QueueAlarmSave()
    {
        var seq = ++_saveSeq;
        try { await System.Threading.Tasks.Task.Delay(500); if (seq == _saveSeq) await _parent.SaveAlarmSettingsAsync(this); } catch (Exception) { }
    }
    [ObservableProperty] private string _stateText = "состояние не получено";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private string _availabilityText = "";
    [ObservableProperty] private string _stateColor = "#8A94A0";
    public string TypeIcon => Device.Kind switch { RpDeviceKind.SmartSwitch => "⇄", RpDeviceKind.SmartAlarm => "⚠", _ => "▤" };
    public string EntityText => $"Entity ID: {Device.EntityId}";
    [ObservableProperty] private System.Windows.Media.Brush _bellBrush = BellBrushes.Gray;
    [ObservableProperty] private string _bellTip = RpBell.Tip(RpBellState.Gray, false);
    private DeviceLive? _lastLive;
    internal void RecomputeBell(DeviceLive? live) { _lastLive = live; var st = RpBell.Resolve(AlarmNotify, live); BellBrush = BellBrushes.For(st); BellTip = RpBell.Tip(st, AlarmNotify); }
    private bool _suppressBell;
    partial void OnAlarmNotifyChanged(bool value) { RecomputeBell(_lastLive); if (!_suppressBell) _ = _parent.SaveBellAsync(this, value); }
    internal void SetBellSilently(bool value) { _suppressBell = true; AlarmNotify = value; _suppressBell = false; }
    [RelayCommand] private Task OnAsync() => _parent.SetSwitchAsync(this, true);
    [RelayCommand] private Task OffAsync() => _parent.SetSwitchAsync(this, false);
    [RelayCommand] private Task RefreshAsync() => _parent.RefreshDeviceAsync(this);
    [RelayCommand] private Task SaveAsync() => _parent.SaveDeviceAsync(this);
    [RelayCommand] private Task DeleteAsync() => _parent.DeleteDeviceAsync(this);
    [RelayCommand] private Task HistoryAsync() => _parent.ShowHistoryAsync(this);
}

public sealed partial class RpDevicePairingRowViewModel : ObservableObject
{
    private readonly RustPlusDevicePairingService _service;
    public RpDevicePairingRequest Request { get; }
    public string ServerName { get; }
    public string KindText => Request.Kind switch { RpDeviceKind.SmartSwitch => "Smart Switch", RpDeviceKind.SmartAlarm => "Smart Alarm", _ => "Storage Monitor" };
    public string EntityText => $"Entity ID: {Request.EntityId}";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _resultText = "";

    public RpDevicePairingRowViewModel(RpDevicePairingRequest request, string serverName, RustPlusDevicePairingService service)
    { Request = request; ServerName = serverName; _service = service; _resultText = request.LastError; }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (IsBusy) return;
        IsBusy = true; ResultText = "Проверка сопряжения через Rust+…";
        try { var r = await _service.VerifyAndAddAsync(Request.Id); ResultText = r.Message ?? (r.Ok ? "Добавлено." : "Не добавлено."); }
        catch (Exception ex) { ResultText = "Не добавлено: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DismissAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { var r = await _service.DismissAsync(Request.Id); ResultText = r.Message ?? ""; }
        catch (Exception ex) { ResultText = "Не удалось скрыть: " + ex.Message; }
        finally { IsBusy = false; }
    }
}

public sealed partial class BotCommandRowViewModel : ObservableObject
{
    public string Name { get; }
    public string Description { get; }
    [ObservableProperty] private bool _enabled;
    public BotCommandRowViewModel(string name, string description, bool enabled) { Name = name; Description = description; _enabled = enabled; }
}

public sealed partial class RustPlusViewModel : ObservableObject
{
    private readonly RustPlusDeviceController _controller;
    private readonly RustPlusManager _mgr; private readonly PairedServerRepository _servers; private readonly RpDeviceRepository _devices; private readonly RustPlusDeviceEvents _events;
    private readonly RustPlusPairing _pairing; private readonly BotCommandEngine _engine; private readonly ISettingsProvider _settings; private readonly RustCompanion.Core.Data.SettingsRepository _settingsRepo;
    private readonly AppTimerSource _timers; private readonly IClock _clock; private readonly DiagnosticsService _diag;
    private readonly RustPlusHub _hub; private readonly RustPlusFcmService _fcmService; private readonly FcmEventRouter _router;
    private readonly RpDevicePairingRepository _pairingRequests; private readonly RustPlusDevicePairingService _pairingService;
    private CancellationTokenSource? _pairCts;
    private bool _loading; private bool _syncing;

    public ObservableCollection<RpServerItem> Servers { get; } = new();
    public ObservableCollection<TeamMemberRow> Team { get; } = new();
    public ObservableCollection<RpDeviceRowViewModel> Devices { get; } = new();
    public ObservableCollection<RpDevicePairingRowViewModel> PairingRequests { get; } = new();
    public ObservableCollection<string> Chat { get; } = new();
    public ObservableCollection<BotCommandRowViewModel> BotCommands { get; } = new();
    public IReadOnlyList<string> Languages { get; } = new[] { "ru", "en" };

    [ObservableProperty] private RpServerItem? _selectedServer;
    [ObservableProperty] private string _stateText = "Отключено";
    [ObservableProperty] private string _stateColor = "#8A94A0";
    [ObservableProperty] private string _statusMessage = "Не подключено";
    [ObservableProperty] private string _serverLine = "";
    [ObservableProperty] private string _timeLine = "";
    [ObservableProperty] private string _updatedLine = "";
    [ObservableProperty] private string _pairingStatus = "";
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private bool _hasCredentials;
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private string _chatStatus = "";
    [ObservableProperty] private bool _botEnabled;
    [ObservableProperty] private string _botPrefix = "!";
    [ObservableProperty] private string _botLanguage = "ru";
    [ObservableProperty] private string _botMessage = "";
    [ObservableProperty] private string _fcmStatus = "Приём push-уведомлений не запущен";
    [ObservableProperty] private string _fcmLastEvent = "Событий от Rust+ ещё не получено";

    private RustMapsWindow? _rustMapsWindow;
    [RelayCommand]
    private void OpenRustMaps()
    {
        if (_rustMapsWindow is { IsLoaded: true }) { _rustMapsWindow.Activate(); return; }
        _rustMapsWindow = new RustMapsWindow(_mgr, _rustMaps); _rustMapsWindow.Closed += (_, _) => _rustMapsWindow = null; _rustMapsWindow.Show();
    }

    private readonly RustMapsClient _rustMaps;
    private MapWindow? _mapWindow;
    [RelayCommand]
    private void OpenMap()
    {
        if (_mapWindow is { IsLoaded: true }) { _mapWindow.Activate(); return; }
        _mapWindow = new MapWindow(_mgr, _rustMaps); _mapWindow.Closed += (_, _) => _mapWindow = null; _mapWindow.Show();
    }

    public RustPlusViewModel(RustPlusManager mgr, PairedServerRepository servers, RpDeviceRepository devices, RustPlusDeviceEvents events, RustPlusPairing pairing,
        RustPlusBotService bot, BotCommandEngine engine, ISettingsProvider settings, RustCompanion.Core.Data.SettingsRepository settingsRepo, AppTimerSource timers, IClock clock, DiagnosticsService diag,
        RustPlusHub hub, RustPlusFcmService fcmService, FcmEventRouter router, RpDevicePairingRepository pairingRequests, RustPlusDevicePairingService pairingService, RustPlusDeviceController controller, RustMapsClient rustMaps)
    { _rustMaps = rustMaps; _controller = controller; _mgr = mgr; _servers = servers; _devices = devices; _events = events; _pairing = pairing; _engine = engine; _settings = settings; _settingsRepo = settingsRepo; _timers = timers; _clock = clock; _diag = diag; _hub = hub; _fcmService = fcmService; _router = router; _pairingRequests = pairingRequests; _pairingService = pairingService;
        _ = bot;   // сервис бота подписывается на чат при создании
        _mgr.Changed += () => OnUi(RefreshState);
        _mgr.ChatMessage += m => OnUi(() => AddChat(m));
        _mgr.DeviceChanged += (srv, change) => OnUi(() => _ = OnDeviceChangedAsync(srv, change));
        // единое состояние: выбранный сервер, список серверов и устройств, состояния и FCM-события общие с вкладкой «Rust+ Устройства»
        _hub.ServersChanged += () => OnUi(RebuildServers);
        _hub.SelectedChanged += p => OnUi(() => ApplyHubSelection(p));
        _hub.DevicesChanged += id => OnUi(() => { if (SelectedServer?.Server.Id == id) _ = ReloadDevicesAsync(); });
        _hub.DeviceStateChanged += (sid, eid) => OnUi(() => ApplyLive(sid, eid));
        _hub.PairingRequestsChanged += () => OnUi(() => _ = ReloadPairingRequestsAsync());
        _fcmService.StatusChanged += () => OnUi(() => FcmStatus = _fcmService.Status);
        _router.Received += () => OnUi(() => FcmLastEvent = _router.LastEventText);
    }

    private static void OnUi(Action a) { var d = Application.Current?.Dispatcher; if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a); }

    public async Task LoadAsync()
    {
        _loading = true;
        await ReloadServersAsync();
        HasCredentials = _pairing.HasCredentials;
        var s = _settings.Current;
        BotEnabled = s.BotEnabled; BotPrefix = s.BotPrefix; BotLanguage = s.BotLanguage;
        var disabled = BotSettings.From(s).Disabled;
        BotCommands.Clear();
        foreach (var c in _engine.Commands) BotCommands.Add(new BotCommandRowViewModel(c.Name, c.Describe(s.BotLanguage), !disabled.Contains(c.Name)));
        _loading = false;
        RefreshState();
        FcmStatus = _fcmService.Status;
        await ReloadPairingRequestsAsync();
        if (HasCredentials) _ = _fcmService.EnsureStartedAsync();
    }

    private async Task ReloadServersAsync(long? preferId = null)
    {
        await _hub.ReloadServersAsync(preferId ?? SelectedServer?.Server.Id);
        RebuildServers();
    }

    private void RebuildServers()
    {
        _syncing = true;
        try
        {
            var keep = _hub.Selected?.Id;
            Servers.Clear(); foreach (var s in _hub.Servers) Servers.Add(new RpServerItem(s));
            SelectedServer = Servers.FirstOrDefault(x => x.Server.Id == keep);
        }
        finally { _syncing = false; }
        _ = ReloadDevicesAsync(); _ = ReloadPairingRequestsAsync();
    }

    private void ApplyHubSelection(PairedServer? p)
    {
        if (SelectedServer?.Server.Id == p?.Id) return;
        _syncing = true;
        try { SelectedServer = Servers.FirstOrDefault(x => x.Server.Id == p?.Id); }
        finally { _syncing = false; }
        _ = ReloadDevicesAsync(); _ = ReloadPairingRequestsAsync();
    }

    partial void OnSelectedServerChanged(RpServerItem? value)
    {
        if (_loading || _syncing) return;
        _hub.Select(value?.Server.Id);          // выбор синхронно переходит во вкладку «Rust+ Устройства»
        _ = ReloadDevicesAsync(); _ = ReloadPairingRequestsAsync();
    }

    private void RefreshState()
    {
        (StateText, StateColor) = _mgr.State switch
        {
            RustPlusState.Connected => ("Подключено", "#3FB950"),
            RustPlusState.Connecting => ("Подключение…", "#D29922"),
            RustPlusState.Reconnecting => ("Переподключение…", "#D29922"),
            RustPlusState.AuthFailed => ("Нужно повторное сопряжение", "#E5604D"),
            _ => ("Отключено", "#8A94A0")
        };
        StatusMessage = _mgr.StatusMessage;
        if (!_mgr.IsConnected && SelectedServer is not null)
            _hub.ResetAvailability(SelectedServer.Server.Id, "нет соединения с сервером Rust+ — состояние неизвестно");
        var snap = _mgr.Snapshot;
        ServerLine = snap.Info is { } i && _mgr.IsConnected
            ? $"{i.Name ?? "?"} • игроков {i.Players?.ToString() ?? "?"}/{i.MaxPlayers?.ToString() ?? "?"}" + (i.Queued is > 0 ? $" • в очереди {i.Queued}" : "") + (i.MapSize is { } ms ? $" • карта {ms}" : "")
            : "Нет данных (нет соединения)";
        TimeLine = snap.Time?.Time is { } t && _mgr.IsConnected ? $"Время в игре: {TimeSpan.FromHours(((t % 24) + 24) % 24):hh\\:mm}" : "";
        UpdatedLine = snap.UpdatedUtc is { } u ? "Обновлено: " + u.ToLocalTime().ToString("T") : "";
        Team.Clear();
        if (_mgr.IsConnected && snap.Team is { } team)
            foreach (var m in team.Members)
            {
                var online = m.Online == true;
                var brush = !online ? BellBrushes.Gray : m.Alive == false ? BellBrushes.Red : BellBrushes.Green;
                var cell = online ? RpGrid.Cell(m.X, m.Y, snap.Info?.MapSize) : null;
                Team.Add(new TeamMemberRow(m.Name, brush, cell, !online ? "Не в сети" : m.Alive == false ? "Мёртв" : "В сети"));
            }
    }

    private async Task OnDeviceChangedAsync(PairedServer srv, RpDeviceChange change)
    {
        // тревога и состояние обрабатываются фоновым RustPlusDeviceRuntime; здесь только обновление строки
        await Task.CompletedTask;
        ApplyLive(srv.Id, change.EntityId);
    }

    private void ApplyLive(long serverId, ulong entityId)
    {
        var row = Devices.FirstOrDefault(d => d.Device.EntityId == entityId && d.Device.ServerId == serverId);
        if (row is null) return;
        var live = _hub.GetLive(serverId, entityId);
        row.RecomputeBell(live);
        row.StateColor = "#8A94A0";
        if (live is null) { row.StateText = "состояние неизвестно"; row.AvailabilityText = "доступность не проверена"; return; }
        row.AvailabilityText = live.Availability switch
        {
            DeviceAvailability.Available => "доступно",
            DeviceAvailability.Unavailable => "НЕДОСТУПНО" + (live.Note is null ? "" : " — " + live.Note),
            _ => live.Note ?? "доступность не проверена"
        };
        if (live.State is not { } st) { row.StateText = "состояние неизвестно"; return; }
        if (row.IsAlarm)
        {
            // AppEntityPayload.value у Smart Alarm — активность сигнала, а не подтверждение питания/охраны.
            row.StateText = st.IsOn is null ? "состояние сигнала неизвестно" : st.IsOn.Value ? "сигнал активен" : "сигнала нет";
            return;
        }
        if (row.IsSwitch && live.Availability == DeviceAvailability.Available && st.IsOn is { } on)
        {
            row.StateText = on ? "включено" : "выключено";
            row.StateColor = on ? "#3FB950" : "#E5604D";
        }
        else if (row.IsSwitch) row.StateText = "состояние неизвестно";
        else row.StateText = st.Items is { Count: > 0 } items ? $"предметов: {items.Count}" : "данные получены";
    }

    private void AddChat(RpChatMessage m)
    {
        Chat.Add($"{(m.AtUtc?.ToLocalTime().ToString("HH:mm") ?? "--:--")} {m.Name}: {m.Text}");
        while (Chat.Count > 200) Chat.RemoveAt(0);
    }

    // ---------- сопряжение ----------
    [RelayCommand]
    private async Task RegisterAsync()
    {
        PairingStatus = "Откроется окно входа: войдите в Steam (Steam Guard подтверждаете вы сами). Окно закроется само, как только вход завершится. Ожидание — до 5 минут…";
        IsPairing = true; _pairCts = new CancellationTokenSource();
        try
        {
            var r = await _pairing.RegisterAsync(_pairCts.Token);
            PairingStatus = r.Ok ? "Вход выполнен. Теперь нажмите «2. Ждать сопряжения» и в игре подтвердите сопряжение." : "Не удалось: " + r.Message;
            HasCredentials = _pairing.HasCredentials;
            if (r.Ok) _ = _fcmService.RestartAsync();
            if (!r.Ok) _diag.Record("Rust+ вход", SourceState.Error, $"{r.Code} {r.Message}");
        }
        catch (OperationCanceledException) { PairingStatus = "Отменено."; }
        finally { IsPairing = false; }
    }

    [RelayCommand]
    private async Task WaitPairingAsync()
    {
        if (!_pairing.HasCredentials) { PairingStatus = "Сначала выполните шаг 1 (вход через Steam)."; return; }
        PairingStatus = "Ожидание: в игре нажмите Esc → Rust+ → «Pair with server» (сопряжение с сервером). Ожидание можно отменить.";
        IsPairing = true; _pairCts = new CancellationTokenSource();
        try
        {
            var r = await _pairing.WaitForServerPairingAsync(_pairCts.Token);
            if (!r.Ok || r.Value is null) { PairingStatus = "Сопряжение не получено: " + r.Message; _diag.Record("Rust+ сопряжение", SourceState.Error, $"{r.Code} {r.Message}"); return; }
            var p = r.Value;
            PairedServer server;
            try { server = await _servers.UpsertAsync(p.Name, p.Ip, p.Port, p.PlayerId, p.PlayerToken, _clock.UtcNow, rustPlusId: p.ServerUid); }
            catch (ArgumentException ex)
            {
                PairingStatus = "Сопряжение не сохранено: " + ex.Message + $" (получено: «{p.Ip}»:{p.Port})";
                _diag.Record("Rust+ сопряжение", SourceState.Error, $"bad_pairing_address ip='{p.Ip}' port={p.Port}");
                return;
            }
            await ReloadServersAsync(server.Id); _hub.Select(server.Id);
            PairingStatus = $"Сервер «{server.Name}» сопряжён. Нажмите «Подключиться».";
        }
        catch (OperationCanceledException) { PairingStatus = "Ожидание отменено."; }
        finally { IsPairing = false; }
    }

    [RelayCommand] private void CancelPairing() => _pairCts?.Cancel();

    // ---------- соединение ----------
    [RelayCommand] private async Task ConnectAsync()
    {
        if (SelectedServer is null) { StatusMessage = "Выберите сопряжённый сервер или выполните сопряжение."; return; }
        _hub.Select(SelectedServer.Server.Id);
        var r = await _mgr.ConnectAsync(SelectedServer.Server.Id);
        if (!r.Ok) StatusMessage = r.Message ?? "Не удалось подключиться.";
        else _ = RunLoopAsync();
    }
    [RelayCommand] private Task DisconnectAsync() => _mgr.DisconnectAsync();

    [RelayCommand]
    private async Task DeleteServerAsync()
    {
        if (SelectedServer is null) return;
        await _mgr.DisconnectAsync(); await _servers.DeleteAsync(SelectedServer.Server.Id);
        await ReloadServersAsync();
    }

    private async Task RunLoopAsync()
    {
        while (_mgr.State != RustPlusState.Disconnected && _mgr.State != RustPlusState.AuthFailed)
        {
            try { await _timers.RefreshAsync(); if (_mgr.IsConnected) foreach (var d in Devices.ToArray()) await RefreshDeviceAsync(d); }
            catch (Exception ex) { _diag.Record("Rust+ обновление", SourceState.Error, ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
    }

    // ---------- чат ----------
    [RelayCommand]
    private async Task SendChatAsync()
    {
        var text = ChatInput; if (string.IsNullOrWhiteSpace(text)) return;
        var r = await _mgr.SendMessageAsync(text);
        ChatStatus = r.Ok ? "Отправлено" : "Не отправлено: " + r.Message;
        if (r.Ok) ChatInput = "";
    }

    // ---------- устройства ----------
    private async Task ReloadDevicesAsync()
    {
        Devices.Clear();
        if (SelectedServer is null) return;
        var serverId = SelectedServer.Server.Id;
        var list = await _devices.ListAsync(serverId);
        if (SelectedServer?.Server.Id != serverId) return;      // за время чтения выбран другой сервер
        Devices.Clear();
        foreach (var d in list) { Devices.Add(new RpDeviceRowViewModel(d, this)); ApplyLive(serverId, d.EntityId); }
    }

    private async Task ReloadPairingRequestsAsync()
    {
        PairingRequests.Clear();
        if (SelectedServer is null) return;
        var server = SelectedServer.Server;
        var requests = await _pairingRequests.ListPendingAsync(server.Id);
        if (SelectedServer?.Server.Id != server.Id) return;
        foreach (var request in requests) PairingRequests.Add(new RpDevicePairingRowViewModel(request, server.Name, _pairingService));
    }

    internal async Task RefreshDeviceAsync(RpDeviceRowViewModel row)
    {
        var r = await _mgr.ReadDeviceAsync(row.Device.EntityId, row.Device.Kind);
        if (!r.Ok || r.Value is null)
        {
            _hub.SetAvailability(row.Device.ServerId, row.Device.EntityId, DeviceAvailability.Unknown, "состояние не получено: " + r.Message);
            row.ResultText = "Состояние не получено: " + r.Message;
            return;
        }
        _hub.SetState(row.Device.ServerId, row.Device.EntityId, r.Value);
        _hub.SetAvailability(row.Device.ServerId, row.Device.EntityId, DeviceAvailability.Available, null);
        await _devices.MarkSynchronizedAsync(row.Device.Id, r.Value, _clock.UtcNow);
        ApplyLive(row.Device.ServerId, row.Device.EntityId);
    }

    internal async Task SetSwitchAsync(RpDeviceRowViewModel row, bool on)
    {
        row.ResultText = "Выполняется…";
        var r = await _controller.SetSwitchAsync(row.Device, on, "вкладка «Rust+»");       // тот же путь, что и во вкладке «Rust+ Устройства»
        row.ResultText = r.Message;
        ApplyLive(row.Device.ServerId, row.Device.EntityId);
    }

    internal async Task SaveDeviceAsync(RpDeviceRowViewModel row)
    {
        row.Device.Name = row.Name; row.Device.Group = row.Group; row.Device.Note = row.Note; row.Device.AlarmNotify = row.AlarmNotify;
        await _devices.UpdateAsync(row.Device); await _devices.SetAlarmSettingsAsync(row.Device.Id, row.AlarmText, row.AlarmChannel); _hub.NotifyDevicesChanged(row.Device.ServerId); row.ResultText = "Сохранено";
    }

    internal async Task DeleteDeviceAsync(RpDeviceRowViewModel row)
    {
        await _devices.DeleteAsync(row.Device.Id); _hub.Forget(row.Device.ServerId, row.Device.EntityId); Devices.Remove(row);
        _hub.NotifyDevicesChanged(row.Device.ServerId);
    }

    internal async Task SaveAlarmSettingsAsync(RpDeviceRowViewModel row)
    {
        await _devices.SetAlarmSettingsAsync(row.Device.Id, row.AlarmText, row.AlarmChannel);
        row.Device.AlarmText = row.AlarmText; row.Device.AlarmChannel = row.AlarmChannel;
        row.ResultText = "Уведомление сохранено";
    }

    internal async Task SaveBellAsync(RpDeviceRowViewModel row, bool enabled)
    {
        try
        {
            await _devices.SetAlarmNotifyAsync(row.Device.Id, enabled);
            row.Device.AlarmNotify = enabled; _hub.NotifyDevicesChanged(row.Device.ServerId);
            row.ResultText = enabled ? "Уведомления включены: срабатывание этой сигнализации будет отслеживаться." : "Уведомления выключены: срабатывание останется только в истории.";
        }
        catch (Exception ex) { row.SetBellSilently(!enabled); row.ResultText = "Не удалось сохранить колокольчик: " + ex.Message; }
    }

    internal async Task ShowHistoryAsync(RpDeviceRowViewModel row)
    {
        var ev = await _devices.EventsAsync(row.Device.Id, 10);
        row.HistoryText = ev.Count == 0 ? "Событий нет" : string.Join("\n", ev.Select(e => $"{e.AtUtc.ToLocalTime():g} — {(e.Kind switch { "alarm" => "тревога", "switch_on" => "включено", "switch_off" => "выключено", _ => e.Kind })}{(string.IsNullOrEmpty(e.Detail) ? "" : " (" + e.Detail + ")")}"));
    }

    // ---------- бот ----------
    [RelayCommand]
    private async Task SaveBotAsync()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_settings.Current))!;
        s.BotEnabled = BotEnabled; s.BotPrefix = BotPrefix.Trim(); s.BotLanguage = BotLanguage;
        s.BotDisabledCommands = string.Join(",", BotCommands.Where(c => !c.Enabled).Select(c => c.Name));
        var errors = s.Validate();
        if (errors.Count > 0) { BotMessage = string.Join(" ", errors); return; }
        await _settingsRepo.SaveAsync(s); _settings.Update(s);
        BotMessage = BotEnabled ? "Бот включён: отвечает на команды в командном чате при активном соединении." : "Бот выключен.";
    }
}
