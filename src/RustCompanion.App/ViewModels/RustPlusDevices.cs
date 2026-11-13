using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;

namespace RustCompanion.App.ViewModels;

public sealed partial class RustPlusDeviceCommandRowViewModel : ObservableObject
{
    private readonly RustPlusDevicesViewModel _parent;
    public RpDevice Device { get; }
    public IReadOnlyList<string> Prefixes => RpDeviceCommandRules.Prefixes;
    public bool IsSwitch => Device.Kind == RpDeviceKind.SmartSwitch;
    public bool NotSwitch => !IsSwitch;
    public bool IsAlarm => Device.Kind == RpDeviceKind.SmartAlarm;
    [ObservableProperty] private System.Windows.Media.Brush _bellBrush = BellBrushes.Gray;
    [ObservableProperty] private string _bellTip = RpBell.Tip(RpBellState.Gray, false);
    private DeviceLive? _lastLive;
    internal void RecomputeBell(DeviceLive? live) { _lastLive = live; var st = RpBell.Resolve(AlarmNotify, live); BellBrush = BellBrushes.For(st); BellTip = RpBell.Tip(st, AlarmNotify); }
    private bool _suppressBell;
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
    partial void OnAlarmNotifyChanged(bool value) { RecomputeBell(_lastLive); if (!_suppressBell) _ = _parent.SaveBellAsync(this, value); }
    internal void SetBellSilently(bool value) { _suppressBell = true; AlarmNotify = value; _suppressBell = false; }
    public bool IsToggle => !IsSeparate;
    public string KindText => Device.Kind switch
    {
        RpDeviceKind.SmartSwitch => "Smart Switch",
        RpDeviceKind.SmartAlarm => "Smart Alarm — только чтение",
        _ => "Storage Monitor — только чтение"
    };
    public string EntityText => $"Entity ID: {Device.EntityId}";
    public string ServerName { get; }
    public string ServerText => $"Сервер: {ServerName}";
    public string TypeIcon => Device.Kind switch { RpDeviceKind.SmartSwitch => "⇄", RpDeviceKind.SmartAlarm => "⚠", _ => "▤" };

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsToggle))] private bool _isSeparate;
    [ObservableProperty] private string _prefix = "!";
    [ObservableProperty] private string _toggleText = "";
    [ObservableProperty] private string _enableText = "";
    [ObservableProperty] private string _disableText = "";
    [ObservableProperty] private string _allowedSenders = "";
    [ObservableProperty] private string _stateText = "Состояние не получено";
    [ObservableProperty] private string _availabilityText = "Доступность не проверена";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _stateColor = "#8A94A0";

    public RustPlusDeviceCommandRowViewModel(RpDevice device, RpDeviceCommandSettings settings, RustPlusDevicesViewModel parent, string serverName = "")
    {
        Device = device; _parent = parent; ServerName = serverName; _name = device.Name; _alarmNotify = device.AlarmNotify; _alarmText = device.AlarmText; _alarmChannel = device.AlarmChannel;
        _enabled = settings.Enabled; _isSeparate = settings.Mode == RpDeviceCommandMode.Separate;
        _prefix = settings.Prefix; _toggleText = settings.ToggleCommand;
        _enableText = settings.OnCommand; _disableText = settings.OffCommand; _allowedSenders = settings.AllowedSenders;
    }

    public RpDeviceCommandSettings ToSettings() => new()
    {
        DeviceId = Device.Id, Enabled = Enabled,
        Mode = IsSeparate ? RpDeviceCommandMode.Separate : RpDeviceCommandMode.Toggle,
        Prefix = Prefix, ToggleCommand = ToggleText.Trim(), OnCommand = EnableText.Trim(),
        OffCommand = DisableText.Trim(), AllowedSenders = AllowedSenders.Trim()
    };

    public void SetState(RpDeviceState state)
    {
        StateText = Device.Kind switch
        {
            RpDeviceKind.SmartSwitch => state.IsOn is null ? "Состояние неизвестно" : state.IsOn.Value ? "Включён" : "Выключен",
            RpDeviceKind.SmartAlarm => state.IsOn is null ? "Состояние неизвестно" : state.IsOn.Value ? "Сигнал активен" : "Сигнала нет",
            _ => state.Items is null ? "Состояние получено" : $"Предметов: {state.Items.Count}"
        };
    }

    /// <summary>Состояние и доступность из общего хранилища (обновляются опросом доступности, событиями Rust+ и ручным чтением).</summary>
    public void ApplyLive(DeviceLive? live)
    {
        RecomputeBell(live);
        StateColor = "#8A94A0";
        if (live is null) { StateText = "Состояние неизвестно"; AvailabilityText = "Доступность не проверена"; return; }
        if (live.State is not null) SetState(live.State); else StateText = "Состояние неизвестно";
        AvailabilityText = live.Availability switch
        {
            DeviceAvailability.Available => "Устройство отвечает",
            DeviceAvailability.Unavailable => "НЕДОСТУПНО" + (live.Note is null ? "" : " — " + live.Note),
            _ => live.Note ?? "Доступность не проверена"
        };
        // Для Smart Alarm значение описывает активность сигнала, поэтому квадрат остаётся серым.
        if (IsSwitch && live.Availability == DeviceAvailability.Available && live.State?.IsOn is { } on)
            StateColor = on ? "#3FB950" : "#E5604D";
    }

    public void ApplyExecution(RpDeviceCommandHistory h) => ResultText = $"{h.Sender}: {h.Detail}";

    [RelayCommand] private void AddMode()
    {
        if (string.IsNullOrWhiteSpace(EnableText)) EnableText = ToggleText;
        IsSeparate = true;
    }
    [RelayCommand] private void ToggleMode() => IsSeparate = false;
    [RelayCommand] private Task SaveAsync() => _parent.SaveDeviceAsync(this);
    [RelayCommand] private Task RefreshAsync() => _parent.RefreshDeviceAsync(this);
    [RelayCommand] private Task TurnOnAsync() => _parent.SetSwitchAsync(this, true);
    [RelayCommand] private Task TurnOffAsync() => _parent.SetSwitchAsync(this, false);
}

public sealed partial class RustPlusDevicesViewModel : ObservableObject
{
    private readonly RustPlusManager _manager;
    private readonly PairedServerRepository _servers;
    private readonly RpDeviceRepository _devices;
    private readonly RpDeviceCommandRepository _commands;
    private readonly RustPlusDeviceController _controller;
    private readonly RustPlusDeviceCommandService _service;
    private readonly RustPlusHub _hub;
    private readonly RpDevicePairingRepository _pairingRequests;
    private readonly RustPlusDevicePairingService _pairingService;
    private bool _loading; private bool _syncing;

    public ObservableCollection<RpServerItem> Servers { get; } = new();
    public ObservableCollection<RustPlusDeviceCommandRowViewModel> Devices { get; } = new();
    public ObservableCollection<RpDevicePairingRowViewModel> PairingRequests { get; } = new();
    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty] private RpServerItem? _selectedServer;
    [ObservableProperty] private bool _processingEnabled;
    [ObservableProperty] private string _connectionText = "Rust+ отключён";
    [ObservableProperty] private string _statusMessage = "Обработка команд по умолчанию выключена.";
    [ObservableProperty] private string _emptyText = "Нет проверенных устройств. Нажмите Pair на устройстве в игре, затем подтвердите чтение в карточке обнаружения.";

    public RustPlusDevicesViewModel(RustPlusManager manager, PairedServerRepository servers, RpDeviceRepository devices,
        RpDeviceCommandRepository commands, RustPlusDeviceCommandService service, RustPlusHub hub,
        RpDevicePairingRepository pairingRequests, RustPlusDevicePairingService pairingService, RustPlusDeviceController controller)
    {
        _controller = controller; _manager = manager; _servers = servers; _devices = devices; _commands = commands; _service = service; _hub = hub; _pairingRequests = pairingRequests; _pairingService = pairingService;
        _manager.Changed += () => OnUi(RefreshConnection);
        // общее состояние со вкладкой «Rust+»: выбранный сервер, список серверов/устройств, состояния и доступность
        _hub.ServersChanged += () => OnUi(RebuildServers);
        _hub.SelectedChanged += p => OnUi(() => ApplyHubSelection(p));
        _hub.DevicesChanged += id => OnUi(() => { if (SelectedServer?.Server.Id == id) _ = LoadSelectedSafelyAsync(); });
        _hub.DeviceStateChanged += (sid, eid) => OnUi(() => ApplyLive(sid, eid));
        _hub.PairingRequestsChanged += () => OnUi(() => _ = LoadPairingRequestsSafelyAsync());
        _service.ExecutionCompleted += h => OnUi(() => OnExecution(h));
        _service.StatusChanged += text => OnUi(() => StatusMessage = text);
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _loading = true;
        try
        {
            await _service.InitializeAsync(ct);
            ProcessingEnabled = _service.ProcessingEnabled;
            await _hub.ReloadServersAsync();
            RebuildServersCore();
            RefreshConnection();
            if (SelectedServer is null) { Devices.Clear(); History.Clear(); EmptyText = "Нет сопряжённого Rust+ сервера. Подключение и сопряжение выполняются на вкладке «Rust+»."; }
            else { await LoadDevicesAsync(); await LoadHistoryAsync(); }
        }
        finally { _loading = false; }
    }

    private void RebuildServersCore()
    {
        _syncing = true;
        try
        {
            var keep = _hub.Selected?.Id;
            Servers.Clear(); foreach (var server in _hub.Servers) Servers.Add(new RpServerItem(server));
            SelectedServer = Servers.FirstOrDefault(x => x.Server.Id == keep);
        }
        finally { _syncing = false; }
    }

    private void RebuildServers() { RebuildServersCore(); _ = LoadSelectedSafelyAsync(); }

    private void ApplyHubSelection(PairedServer? p)
    {
        if (SelectedServer?.Server.Id == p?.Id) return;
        _syncing = true;
        try { SelectedServer = Servers.FirstOrDefault(x => x.Server.Id == p?.Id); }
        finally { _syncing = false; }
        _ = LoadSelectedSafelyAsync();
    }

    partial void OnSelectedServerChanged(RpServerItem? value)
    {
        if (_loading || _syncing) return;
        _hub.Select(value?.Server.Id);           // выбор синхронно переходит во вкладку «Rust+»
        _ = LoadSelectedSafelyAsync();
    }

    partial void OnProcessingEnabledChanged(bool value)
    {
        if (!_loading) _ = ChangeProcessingSafelyAsync(value);
    }

    private async Task ChangeProcessingSafelyAsync(bool enabled)
    {
        try { await _service.SetProcessingEnabledAsync(enabled); }
        catch (Exception ex) { StatusMessage = "Не удалось сохранить состояние обработки: " + ex.Message; }
    }

    private async Task LoadSelectedSafelyAsync()
    {
        try { await LoadDevicesAsync(); await LoadPairingRequestsAsync(); await LoadHistoryAsync(); }
        catch (Exception ex) { StatusMessage = "Не удалось загрузить устройства: " + ex.Message; }
    }

    private async Task LoadDevicesAsync()
    {
        if (SelectedServer is null) { Devices.Clear(); return; }
        EmptyText = "Нет проверенных устройств. Нажмите Pair на устройстве в игре, затем используйте карточку обнаружения выше.";
        var serverId = SelectedServer.Server.Id;
        var devices = await _devices.ListAsync(serverId);
        var rows = new List<RustPlusDeviceCommandRowViewModel>();
        foreach (var device in devices)
        {
            var settings = await _commands.GetAsync(device.Id);
            rows.Add(new RustPlusDeviceCommandRowViewModel(device, settings, this, SelectedServer.Server.Name));
        }
        if (SelectedServer?.Server.Id != serverId) return;     // за время чтения выбран другой сервер
        Devices.Clear();
        foreach (var row in rows) { Devices.Add(row); row.ApplyLive(_hub.GetLive(serverId, row.Device.EntityId)); }
        RefreshConnection();
        if (_manager.IsConnected && _manager.Server?.Id == SelectedServer.Server.Id)
            foreach (var row in Devices) await RefreshDeviceAsync(row, false);
    }

    private async Task LoadPairingRequestsAsync()
    {
        PairingRequests.Clear();
        if (SelectedServer is null) return;
        var server = SelectedServer.Server;
        var requests = await _pairingRequests.ListPendingAsync(server.Id);
        if (SelectedServer?.Server.Id != server.Id) return;
        foreach (var request in requests) PairingRequests.Add(new RpDevicePairingRowViewModel(request, server.Name, _pairingService));
    }

    private async Task LoadPairingRequestsSafelyAsync()
    {
        try { await LoadPairingRequestsAsync(); }
        catch (Exception ex) { StatusMessage = "Не удалось загрузить карточки сопряжения: " + ex.Message; }
    }

    private async Task LoadHistoryAsync()
    {
        History.Clear();
        if (SelectedServer is null) return;
        var items = await _commands.HistoryAsync(SelectedServer.Server.Id, 30);
        foreach (var h in items)
            History.Add($"{h.AtUtc.ToLocalTime():dd.MM HH:mm:ss} · {h.DeviceName} · {h.Sender} · {h.Command} — {h.Detail}");
    }

    private void RefreshConnection()
    {
        var selected = SelectedServer?.Server.Id;
        ConnectionText = _manager.IsConnected && selected == _manager.Server?.Id
            ? $"Rust+ подключён: {_manager.Server!.Name}"
            : selected is null ? "Rust+ сервер не выбран" : "Нет активного Rust+ подключения к выбранному серверу";
        if (selected is not null && (!_manager.IsConnected || selected != _manager.Server?.Id))
        {
            _hub.ResetAvailability(selected.Value, "нет подключения к этому серверу Rust+ — состояние неизвестно");
            foreach (var row in Devices) row.AvailabilityText = "Состояние неизвестно: нет подключения";
        }
    }

    private void ApplyLive(long serverId, ulong entityId)
    {
        if (SelectedServer?.Server.Id != serverId) return;
        Devices.FirstOrDefault(x => x.Device.EntityId == entityId)?.ApplyLive(_hub.GetLive(serverId, entityId));
    }

    public async Task SaveBellAsync(RustPlusDeviceCommandRowViewModel row, bool enabled)
    {
        try
        {
            await _devices.SetAlarmNotifyAsync(row.Device.Id, enabled);
            row.Device.AlarmNotify = enabled; _hub.NotifyDevicesChanged(row.Device.ServerId);
            row.ResultText = enabled ? "Уведомления включены: срабатывание сигнализации отслеживается." : "Уведомления выключены: срабатывание останется только в истории.";
        }
        catch (Exception ex) { row.SetBellSilently(!enabled); row.ResultText = "Не удалось сохранить колокольчик: " + ex.Message; }
    }

    private void OnExecution(RpDeviceCommandHistory history)
    {
        Devices.FirstOrDefault(x => x.Device.Id == history.DeviceId)?.ApplyExecution(history);
        _ = LoadHistorySafelyAsync();
    }

    private async Task LoadHistorySafelyAsync() { try { await LoadHistoryAsync(); } catch (Exception) { } }

    public async Task SaveAlarmSettingsAsync(RustPlusDeviceCommandRowViewModel row)
    {
        await _devices.SetAlarmSettingsAsync(row.Device.Id, row.AlarmText, row.AlarmChannel);
        row.Device.AlarmText = row.AlarmText; row.Device.AlarmChannel = row.AlarmChannel;
        row.ResultText = "Уведомление сохранено";
    }

    public async Task SaveDeviceAsync(RustPlusDeviceCommandRowViewModel row)
    {
        try
        {
            row.Name = row.Name.Trim();
            if (row.Name.Length == 0) { row.ResultText = "Введите название устройства."; return; }
            var settings = row.ToSettings();
            var error = RpDeviceCommandRules.Validate(settings);
            if (error is not null && (row.IsSwitch || settings.Enabled)) { row.ResultText = error; return; }
            row.Device.Name = row.Name;
            await _devices.UpdateAsync(row.Device);
            await _devices.SetAlarmSettingsAsync(row.Device.Id, row.AlarmText, row.AlarmChannel);
            await _commands.SaveAsync(settings, DateTimeOffset.UtcNow);
            _hub.NotifyDevicesChanged(row.Device.ServerId);
            row.ResultText = "Настройки сохранены. Изменения применяются к следующим сообщениям.";
            StatusMessage = $"Сохранено: {row.Name}";
        }
        catch (Exception ex) { row.ResultText = "Не удалось сохранить: " + ex.Message; }
    }

    public async Task RefreshDeviceAsync(RustPlusDeviceCommandRowViewModel row, bool report = true)
    {
        if (SelectedServer is null || !_manager.IsConnected || _manager.Server?.Id != SelectedServer.Server.Id)
        {
            row.AvailabilityText = "Недоступно: нет подключения к этому серверу";
            if (report) row.ResultText = "Команда не отправлена. Подключитесь к этому серверу на вкладке «Rust+».";
            return;
        }
        var result = await _manager.ReadDeviceAsync(row.Device.EntityId, row.Device.Kind);
        if (result.Ok && result.Value is not null)
        {
            _hub.SetState(row.Device.ServerId, row.Device.EntityId, result.Value);
            _hub.SetAvailability(row.Device.ServerId, row.Device.EntityId, DeviceAvailability.Available, null);
            await _devices.MarkSynchronizedAsync(row.Device.Id, result.Value, DateTimeOffset.UtcNow);
            row.ApplyLive(_hub.GetLive(row.Device.ServerId, row.Device.EntityId));
            if (report) row.ResultText = "Состояние получено из Rust+.";
        }
        else
        {
            _hub.SetAvailability(row.Device.ServerId, row.Device.EntityId, DeviceAvailability.Unknown, "одиночное чтение не удалось; недоступность определяется после серии проверок");
            row.ResultText = result.Message ?? "Rust+ не вернул состояние устройства.";
        }
    }

    public async Task SetSwitchAsync(RustPlusDeviceCommandRowViewModel row, bool on)
    {
        if (!row.IsSwitch) return;
        row.ResultText = "Отправка и ожидание подтверждения…";
        var r = await _controller.SetSwitchAsync(row.Device, on, "вкладка «Rust+ Устройства»");   // тот же путь, что и во вкладке «Rust+»
        row.ResultText = r.Message;
    }

    [RelayCommand] private async Task RefreshAllAsync()
    {
        await LoadSelectedSafelyAsync();
        StatusMessage = "Список и состояния устройств обновлены.";
    }

    [RelayCommand] private async Task SaveAllAsync()
    {
        foreach (var row in Devices) await SaveDeviceAsync(row);
    }

    [RelayCommand] private async Task ReloadHistoryAsync() => await LoadHistorySafelyAsync();
}
