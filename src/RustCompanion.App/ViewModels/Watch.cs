using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;
using RustCompanion.Core.Watch;

namespace RustCompanion.App.ViewModels;

public sealed partial class WatchItemViewModel : ObservableObject
{
    private readonly WatchViewModel _parent;
    public WatchEntry Entry { get; }
    public WatchItemViewModel(WatchEntry e, WatchViewModel p) { Entry = e; _parent = p; }
    public string Name => Entry.Name;
    public string SteamText => string.IsNullOrEmpty(Entry.SteamId) ? "SteamID не указан" : "SteamID: " + Entry.SteamId;
    [ObservableProperty] private string _statusText = "Нет данных";
    [ObservableProperty] private string _detailsText = "Проверок ещё не было";
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _color = "#8F98A3";
    public PresenceStatus Status { get; set; } = PresenceStatus.NoData;
    // индикатор: зелёный — подтверждённо на сервере, красный — подтверждённо отсутствует, серый — неизвестно или совпадение по нику не подтверждено
    [ObservableProperty] private string _indicatorColor = "#8A94A0";
    [ObservableProperty] private string _identityText = "";
    [ObservableProperty] private string _indicatorTip = "";
    [ObservableProperty] private string _steamInput = "";
    [ObservableProperty] private string _linkMessage = "";
    [RelayCommand] private Task LinkSteamAsync() => _parent.LinkSteamAsync(this);
    [RelayCommand] private Task UnlinkSteamAsync() => _parent.UnlinkSteamAsync(this);
    [RelayCommand] private Task SearchAcrossAsync() => _parent.SearchAcrossAsync(this);
    [RelayCommand] private Task ShowProfileAsync() => _parent.ShowProfileAsync(this);
    [RelayCommand] private void OpenBattleMetrics() => _parent.OpenBattleMetrics(this);
    [RelayCommand] private void OpenRustBans() => _parent.OpenRustBans(this);
    /// <summary>Статус по Steam (обновляется каждые 3 секунды) заменяет прежние текст и цвет индикатора, если у игрока есть SteamID64.</summary>
    public void ApplyLive(RustCompanion.Core.Watch.LiveStatus? s)
    {
        if (s is null || string.IsNullOrEmpty(Entry.SteamId)) return;
        StatusText = s.Text; DetailsText = $"{s.Detail}  Проверено: {s.AtUtc.ToLocalTime():T}";
        IndicatorColor = s.Presence switch
        {
            RustCompanion.Core.Watch.LivePresence.OnServer => "#3FB950",
            RustCompanion.Core.Watch.LivePresence.NotInRust => "#E5604D",
            RustCompanion.Core.Watch.LivePresence.OtherServer => "#D9A441",
            RustCompanion.Core.Watch.LivePresence.InRustServerUnknown => "#D9A441",
            RustCompanion.Core.Watch.LivePresence.OnlineGameHidden => "#6FA8DC",
            _ => "#8A94A0"
        };
        IdentityText = "Статус по Steam"; IndicatorTip = s.Detail;
    }

    public void ApplyIndicator(PlayerIndicatorInfo info)
    {
        IndicatorColor = info.Indicator switch { PlayerIndicator.Present => "#3FB950", PlayerIndicator.Absent => "#E5604D", _ => "#8A94A0" };
        IdentityText = info.Level.Ru(); IndicatorTip = info.Detail;
    }
    [RelayCommand] private Task RemoveAsync() => _parent.RemoveAsync(this);
    [RelayCommand] private Task ShowActivityAsync() => _parent.ShowActivityAsync(this);
}

public sealed record DayBar(string Label, double Hours, double Height);
public sealed record ServerChoice(long Id, string Name, string SourceKey) { public override string ToString() => Name; }

public sealed partial class WatchViewModel : ObservableObject
{
    private readonly WatchRepository _repo;
    private readonly IServerQuerySource _source;
    private readonly MonitoringService _mon;
    private readonly ISettingsProvider _settings;
    private readonly IClock _clock;
    private readonly DiagnosticsService _diag;
    private readonly PlayerDirectoryRepository _dir;
    private readonly ISteamStatusSource _steamSource; private readonly RustCompanion.Core.Watch.SteamLiveMonitor _live;
    private System.Collections.Generic.IReadOnlyList<WatchGroup> _groups = Array.Empty<WatchGroup>();
    private readonly System.Collections.Generic.List<WatchItemViewModel> _all = new();

    public ObservableCollection<ServerChoice> Servers { get; } = new();
    public ObservableCollection<FoundServer> ServerResults { get; } = new();
    public ObservableCollection<FoundPlayer> PlayerResults { get; } = new();
    public ObservableCollection<FoundPlayer> OnlinePlayers { get; } = new();
    private string? _onlineServerKey; private IReadOnlyList<FoundPlayer> _onlineCache = Array.Empty<FoundPlayer>(); private DateTime _onlineAt;
    [ObservableProperty] private string _onlineInfo = "Выберите сервер на вкладке «Поиск сервера» — игроки онлайн появятся здесь.";
    public ObservableCollection<WatchItemViewModel> Items { get; } = new();
    public ObservableCollection<WatchGroup> Groups { get; } = new();
    public ObservableCollection<string> RecentEvents { get; } = new();
    public ObservableCollection<string> SessionLines { get; } = new();
    public ObservableCollection<DayBar> Bars { get; } = new();
    public ObservableCollection<string> SightingLines { get; } = new();
    [ObservableProperty] private string _sightingsTitle = "";
    public ObservableCollection<string> ProfileLines { get; } = new();
    [ObservableProperty] private string _profileTitle = "";

    [ObservableProperty] private ServerChoice? _selectedServer;
    [ObservableProperty] private string _serverQuery = "";
    [ObservableProperty] private string _playerQuery = "";
    [ObservableProperty] private string _steamAddInput = "";
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private WatchGroup? _filterGroup;
    [ObservableProperty] private WatchGroup? _assignGroup;
    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _newGroupColor = "#D9653B";
    [ObservableProperty] private string _newGroupThreshold = "0";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _activityTitle = "Выберите игрока, чтобы увидеть историю сессий";
    [ObservableProperty] private string _activityNote = "";
    [ObservableProperty] private bool _noServers = true;

    /// <summary>Сервер удалён на этой вкладке — остальные вкладки (серверы, поиск, прогнозы) должны перечитать свои списки.</summary>
    public event Action? ServersChanged;
    /// <summary>Запрос подтверждения перед удалением сервера (в приложении — окно Да/Нет). Если не задан, удаляется без вопроса.</summary>
    public Func<string, bool>? ConfirmRemoveServer { get; set; }

    public WatchViewModel(WatchRepository repo, IServerQuerySource source, MonitoringService mon, ISettingsProvider settings, IClock clock, DiagnosticsService diag, PlayerDirectoryRepository dir, ISteamStatusSource steamSource, RustCompanion.Core.Watch.SteamLiveMonitor live)
    { _live = live; _live.Updated += OnLiveUpdated; _steamSource = steamSource; _dir = dir; _repo = repo; _source = source; _mon = mon; _settings = settings; _clock = clock; _diag = diag; _mon.CycleCompleted += () => _ = RefreshAsync(); }

    partial void OnSelectedServerChanged(ServerChoice? value)
    {
        ApplyOnline(value);
        _ = RefreshAsync();
    }

    /// <summary>Показать сервер, выбранный в поиске серверов, и сразу выбрать его в списке наблюдения.</summary>
    public Task ShowServerAsync(long serverDbId) => LoadAsync(serverDbId);

    private void ApplyOnline(ServerChoice? value)
    {
        OnlinePlayers.Clear();
        if (value is null || _onlineServerKey != value.SourceKey)
        { OnlineInfo = "Откройте этот сервер на вкладке «Поиск сервера» — игроки онлайн появятся здесь."; return; }
        foreach (var p in _onlineCache.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)) OnlinePlayers.Add(p);
        OnlineInfo = _onlineCache.Count == 0
            ? "На сервере сейчас нет игроков по данным Steam Query (A2S)."
            : $"Игроки онлайн на этом сервере: {_onlineCache.Count} (данные из поиска сервера, обновлено {_onlineAt:T}).";
    }

    /// <summary>Игроки онлайн, полученные при выборе сервера в поиске серверов. Запоминаются, даже если сервер в наблюдении выбран чуть позже.</summary>
    public void SetOnlinePlayers(string serverKey, IReadOnlyList<FoundPlayer> players)
    {
        _onlineServerKey = serverKey; _onlineCache = players; _onlineAt = DateTime.Now;
        ApplyOnline(SelectedServer);
    }
    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnFilterGroupChanged(WatchGroup? value) => ApplyFilter();

    public async Task LoadAsync(long? selectServerId = null)
    {
        var previousId = SelectedServer?.Id;
        var keep = selectServerId ?? previousId;
        Servers.Clear();
        foreach (var s in await _repo.ListTrackedServersAsync()) if (s.SourceKey is not null) Servers.Add(new ServerChoice(s.Id, s.Name, s.SourceKey));
        NoServers = Servers.Count == 0;
        if (previousId is not null && Servers.All(s => s.Id != previousId)) ClearDetails();   // сервер удалён (здесь или на вкладке «Серверы»): история, профиль и поиск по нему больше не показываются
        SelectedServer = Servers.FirstOrDefault(s => s.Id == keep) ?? Servers.FirstOrDefault();
        await RefreshAsync();
    }

    /// <summary>Сбрасывает панели, привязанные к ранее выбранному серверу: результаты поиска, историю активности, профиль, «где наблюдался», игроков онлайн.</summary>
    private void ClearDetails()
    {
        PlayerResults.Clear(); OnlinePlayers.Clear(); RecentEvents.Clear();
        SessionLines.Clear(); Bars.Clear(); SightingLines.Clear(); ProfileLines.Clear();
        ActivityTitle = "Выберите игрока, чтобы увидеть историю сессий"; ActivityNote = ""; SightingsTitle = ""; ProfileTitle = "";
        _onlineServerKey = null; _onlineCache = Array.Empty<FoundPlayer>();
        OnlineInfo = "Выберите сервер на вкладке «Поиск сервера» — игроки онлайн появятся здесь.";
    }

    /// <summary>Удаляет выбранный сервер вместе со всеми наблюдаемыми на нём игроками (их состоянием и историей активности). Сервер общий с вкладкой «Серверы», поэтому исчезает и оттуда.</summary>
    [RelayCommand]
    private async Task RemoveServerAsync()
    {
        Message = "";
        var server = SelectedServer;
        if (server is null) { Message = "Сначала выберите сервер."; return; }
        var count = (await _repo.ListWatchAsync(server.Id)).Count;
        var ask = $"Удалить сервер «{server.Name}»?\n\nБудут удалены и наблюдаемые на нём игроки ({count}) с их историей. Сервер пропадёт и со вкладки «Серверы», а также из прогнозов, респавна РТ и горячих клавиш (если он там выбран).";
        if (ConfirmRemoveServer is { } confirm && !confirm(ask)) return;
        var removed = await _repo.RemoveTrackedServerAsync(server.Id);
        await LoadAsync();
        Message = $"Сервер «{server.Name}» удалён вместе с наблюдаемыми игроками: {removed}.";
        ServersChanged?.Invoke();
    }

    public async Task LinkSteamAsync(WatchItemViewModel item)
    {
        var id = item.SteamInput.Trim();
        if (!SteamClient.IsValidSteamId64(id)) { item.LinkMessage = "SteamID64 — 17 цифр, начинается с 7656119."; return; }
        await _repo.SetIdentityAsync(item.Entry.Id, id);
        await _dir.RecordNameAsync(id, item.Entry.Name, _clock.UtcNow);
        item.LinkMessage = "Связь сохранена: уровень «Подтверждено пользователем». Приложение не проверяет её само; зелёный/красный индикатор теперь включён.";
        await RefreshAsync();
    }

    public async Task UnlinkSteamAsync(WatchItemViewModel item)
    {
        await _repo.SetIdentityAsync(item.Entry.Id, null);
        item.LinkMessage = "Связь снята: совпадение по нику не подтверждено, индикатор серый.";
        await RefreshAsync();
    }

    /// <summary>Открывает BattleMetrics во встроенном окне: при первом разе — вход через Steam (сохраняется между запусками), затем профиль игрока по SteamID64.</summary>
    public void OpenBattleMetrics(WatchItemViewModel item)
    {
        var url = BattleMetricsLink.ForSteamId(item.Entry.SteamId);
        if (url is null) { item.LinkMessage = "Нет SteamID64: добавьте игрока по SteamID64 или свяжите его."; return; }
        try { WebToolWindow.OpenBattleMetrics(url, item.Entry.SteamId!); item.LinkMessage = WebToolWindow.BattleMetricsLoggedIn ? "Открыт профиль на BattleMetrics." : "Войдите в BattleMetrics через Steam, затем откроется профиль."; }
        catch (Exception ex) { item.LinkMessage = "Не удалось открыть окно: " + ex.Message; }
    }

    /// <summary>Открывает статистику игрока на rustbans.ru (вход не нужен): SteamID64 подставляется в поиск страницы.</summary>
    public void OpenRustBans(WatchItemViewModel item)
    {
        var id = item.Entry.SteamId;
        if (RustBansLink.FillScript(id) is null) { item.LinkMessage = "Нет SteamID64: добавьте игрока по SteamID64 или свяжите его."; return; }
        try { WebToolWindow.OpenRustBans(id!); item.LinkMessage = "Открыта статистика rustbans.ru; SteamID64 также скопирован в буфер обмена."; }
        catch (Exception ex) { item.LinkMessage = "Не удалось открыть окно: " + ex.Message; }
    }

    /// <summary>Профиль наблюдаемого: время в Rust по Steam и история ников по SteamID64. К/Д и серверную статистику Steam и Steam Query не отдают — они здесь не выдумываются.</summary>
    public async Task ShowProfileAsync(WatchItemViewModel item)
    {
        ProfileLines.Clear();
        ProfileTitle = $"Профиль «{item.Entry.Name}»";
        var id = item.Entry.SteamId;
        if (string.IsNullOrEmpty(id))
        {
            ProfileLines.Add("SteamID64 не указан: время в игре и историю ников по человеку получить нельзя. Добавьте игрока через «Добавить по Steam» или свяжите SteamID64.");
            return;
        }
        var status = await _steamSource.GetStatusAsync(id, default);
        if (status.IsOk && status.Value is { } st)
        {
            if (!string.IsNullOrWhiteSpace(st.PersonaName)) await _dir.RecordNameAsync(id, st.PersonaName!, _clock.UtcNow);
            ProfileLines.Add($"Текущий ник в Steam: {st.PersonaName ?? "—"} · профиль {(st.ProfilePublic ? "открыт" : "закрыт")}");
        }
        else ProfileLines.Add("Профиль Steam: " + status.Message);
        var play = await _steamSource.GetRustPlaytimeMinutesAsync(id, default);
        ProfileLines.Add(play.IsOk ? $"Время в Rust (Steam, за всё время): {play.Value / 60.0:0.#} ч" : "Время в Rust: " + play.Message);
        var names = await _dir.NamesAsync(id);
        ProfileLines.Add(names.Count == 0 ? "История ников: пока пуста (пополняется при проверках)." : $"История ников ({names.Count}):");
        foreach (var n in names.OrderByDescending(x => x.LastSeen))
            ProfileLines.Add($"  «{n.Name}» — впервые {n.FirstSeen.ToLocalTime():d}, последний раз {n.LastSeen.ToLocalTime():d}");
        ProfileLines.Add("К/Д и статистика убийств недоступны: Steam и Steam Query их не передают (они есть только у отдельных серверов с плагинами статистики).");
    }

    /// <summary>Где ещё наблюдался этот ник (и известные ники связанного SteamID64) по всей собственной базе серверов. Совпадение по нику — не подтверждённая личность.</summary>
    public async Task SearchAcrossAsync(WatchItemViewModel item)
    {
        var names = new List<string> { item.Entry.Name };
        if (!string.IsNullOrEmpty(item.Entry.SteamId)) names.AddRange((await _dir.NamesAsync(item.Entry.SteamId)).Select(n => n.Name));
        var found = await _dir.SearchByNameAsync(names);
        SightingLines.Clear();
        SightingsTitle = $"Где наблюдался «{item.Entry.Name}» — серверов: {found.Select(f => f.ServerKey).Distinct().Count()} (совпадение по нику, личность не подтверждена)";
        foreach (var f in found)
            SightingLines.Add($"{f.ServerName}: «{f.Name}», первое обнаружение {f.FirstSeen.ToLocalTime():g}, последнее {f.LastSeen.ToLocalTime():g}, заходов {f.Visits}, ≈{f.EstimatedSeconds / 3600:0.0} ч по опросам");
        if (found.Count == 0) SightingLines.Add("В базе наблюдений совпадений нет (серверы пополняются по мере опроса).");
    }

    private void OnLiveUpdated()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        void Apply() { foreach (var i in _all.ToArray()) if (i.Entry.Id != 0) i.ApplyLive(_live.For(i.Entry.Id)); }
        if (d is null || d.CheckAccess()) Apply(); else d.BeginInvoke((Action)Apply);
    }

    public async Task RefreshAsync()
    {
        _groups = await _repo.ListGroupsAsync();
        Groups.Clear(); foreach (var g in _groups) Groups.Add(g);
        _all.Clear();
        if (SelectedServer is not null)
        {
            foreach (var w in await _repo.ListWatchAsync(SelectedServer.Id))
            {
                var vm = new WatchItemViewModel(w, this);
                var st = await _repo.GetStateAsync(w.Id);
                vm.Status = st.Status;
                vm.StatusText = st.Status.Ru();
                vm.DetailsText = Describe(st);
                var g = _groups.FirstOrDefault(x => x.Id == w.GroupId);
                vm.GroupName = g?.Name ?? "";
                vm.Color = w.Color ?? g?.Color ?? "#8F98A3";
                vm.ApplyIndicator(_mon.IndicatorFor(w.Id));
                vm.ApplyLive(_live.For(w.Id));
                _all.Add(vm);
            }
            var ev = await _repo.ListActivityAsync(null, null, null, 8);
            var names = (await _repo.ListWatchAsync(SelectedServer.Id)).ToDictionary(x => x.Id, x => x.Name);
            RecentEvents.Clear();
            foreach (var e in ev.Where(e => names.ContainsKey(e.WatchId)))
                RecentEvents.Add($"{e.AtUtc.ToLocalTime():g} — {names[e.WatchId]}: {(e.Kind == "exit_confirmed" ? "выход подтверждён" : "вход подтверждён")}");
        }
        else RecentEvents.Clear();
        ApplyFilter();
        var online = _all.Count(i => i.Status == PresenceStatus.OnServer);
        Summary = $"Отслеживается игроков: {_all.Count} (это не все игроки сервера) • Подтверждённо на сервере: {online}\n" +
                  $"Последнее успешное обновление: {(_mon.LastSuccessUtc is null ? "ещё не было" : _mon.LastSuccessUtc.Value.ToLocalTime().ToString("T"))} • Источник: {_mon.LastSourceState}" +
                  (string.IsNullOrEmpty(_mon.LastSourceMessage) ? "" : " — " + _mon.LastSourceMessage);
    }

    private static string Describe(MonitorState s) =>
        s.LastCheckedUtc is null ? "Проверок ещё не было"
        : $"Последнее подтверждённое присутствие: {(s.LastPresentUtc is null ? "неизвестно" : s.LastPresentUtc.Value.ToLocalTime().ToString("g"))} • " +
          $"Проверено: {s.LastCheckedUtc.Value.ToLocalTime():T} • Подряд подтверждений отсутствия: {s.AbsenceCount}" + (s.LastError is null ? "" : " • " + s.LastError);

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var i in _all.Where(i =>
            (string.IsNullOrWhiteSpace(FilterText) || i.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase) || (i.Entry.SteamId ?? "").Contains(FilterText, StringComparison.OrdinalIgnoreCase)
             || i.StatusText.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) &&
            (FilterGroup is null || i.Entry.GroupId == FilterGroup.Id)))
            Items.Add(i);
    }

    [RelayCommand] private void ClearGroupFilter() => FilterGroup = null;

    [RelayCommand]
    private async Task SearchServersAsync()
    {
        Message = "";
        var r = await _source.SearchServersAsync(ServerQuery, default);
        _diag.Record("Поиск серверов", r.State, r.Message);
        ServerResults.Clear();
        if (!r.IsOk) { Message = r.Message; return; }
        foreach (var s in r.Value!) ServerResults.Add(s);
        if (ServerResults.Count == 0) Message = "Серверы не найдены.";
    }

    [RelayCommand]
    private async Task AddServerAsync(FoundServer? s)
    {
        if (s is null) return;
        if (!ServerKey.TryParse(s.Id, out var host, out var queryPort)) { Message = "У сервера нет корректного адреса."; return; }
        await _repo.AddTrackedServerAsync(s.Name, host, queryPort, s.Id);
        await LoadAsync();
        Message = $"Сервер «{s.Name}» добавлен.";
        ServersChanged?.Invoke();       // сервер общий: он появляется и на вкладке «Серверы»
    }

    [RelayCommand]
    private async Task SearchPlayersAsync()
    {
        Message = "";
        if (SelectedServer is null) { Message = "Сначала выберите сервер."; return; }
        var r = await _source.SearchPlayersAsync(PlayerQuery, SelectedServer.SourceKey, default);
        _diag.Record("Поиск игроков", r.State, r.Message);
        PlayerResults.Clear();
        if (!r.IsOk) { Message = r.Message; return; }
        foreach (var p in r.Value!) PlayerResults.Add(p);
        if (PlayerResults.Count == 0) Message = "Игроки не найдены. Поиск идёт только по игрокам, которых сервер отдал в A2S_PLAYER; список может быть неполным или скрытым.";
    }

    /// <summary>Точное добавление конкретного человека: по SteamID64 или ссылке на профиль. Ник берётся из Steam, личность привязывается к SteamID64.</summary>
    [RelayCommand]
    private async Task AddBySteamAsync()
    {
        Message = "";
        if (SelectedServer is null) { Message = "Сначала выберите сервер."; return; }
        var r = await _steamSource.ResolveAsync(SteamAddInput, default);
        _diag.Record("Добавление по Steam", r.State, r.IsOk ? "профиль найден" : r.Message);
        if (!r.IsOk || r.Value is null) { Message = r.Message; return; }
        var st = r.Value; var name = string.IsNullOrWhiteSpace(st.PersonaName) ? st.SteamId : st.PersonaName!;
        try
        {
            var id = await _repo.AddWatchAsync(SelectedServer.Id, name, st.SteamId, name, AssignGroup?.Id);
            await _repo.SetIdentityAsync(id, st.SteamId);
            await _dir.RecordNameAsync(st.SteamId, name, _clock.UtcNow);
            SteamAddInput = "";
            Message = $"«{name}» добавлен по SteamID64 {st.SteamId}." + (st.ProfilePublic
                ? " Профиль открыт: сервер игрока определяется по данным Steam."
                : " Профиль закрыт: сервер по Steam определить нельзя, присутствие проверяется только по нику в списке сервера (он может быть подделан).");
            await RefreshAsync();
        }
        catch (InvalidOperationException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task AddPlayerAsync(FoundPlayer? p)
    {
        if (p is null || SelectedServer is null) return;
        try
        {
            var steam = RustCompanion.Core.Net.SteamClient.IsValidSteamId64(PlayerQuery.Trim()) ? PlayerQuery.Trim() : null;
            await _repo.AddWatchAsync(SelectedServer.Id, p.Id, steam, p.Name, AssignGroup?.Id);
            Message = $"{p.Name} добавлен в список наблюдения.";
            await RefreshAsync();
        }
        catch (InvalidOperationException ex) { Message = ex.Message; }
    }

    internal async Task RemoveAsync(WatchItemViewModel i) { await _repo.RemoveWatchAsync(i.Entry.Id); await RefreshAsync(); }

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        try
        {
            var id = await _repo.AddGroupAsync(NewGroupName, NewGroupColor);
            if (int.TryParse(NewGroupThreshold, out var t) && t > 0)
                await _repo.UpdateGroupAsync(new WatchGroup { Id = id, Name = NewGroupName.Trim(), Color = NewGroupColor, Threshold = t });
            NewGroupName = ""; await RefreshAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(WatchGroup? g) { if (g is null) return; await _repo.DeleteGroupAsync(g.Id); FilterGroup = null; await RefreshAsync(); }

    [RelayCommand]
    private async Task AssignSelectedGroupAsync(WatchItemViewModel? i)
    {
        if (i is null) return;
        i.Entry.GroupId = AssignGroup?.Id;
        await _repo.UpdateWatchAsync(i.Entry);
        await RefreshAsync();
    }

    internal async Task ShowActivityAsync(WatchItemViewModel i)
    {
        SessionLines.Clear(); Bars.Clear();
        ActivityTitle = $"Активность: {i.Name}";
        var r = await _source.GetSessionsAsync(i.Entry.PlayerKey, SelectedServer?.SourceKey, default);
        _diag.Record("История наблюдений", r.State, r.Message);
        if (!r.IsOk) { ActivityNote = "История сессий недоступна: " + r.Message; return; }
        var sessions = r.Value!.OrderByDescending(s => s.Start).ToList();
        if (sessions.Count == 0) { ActivityNote = "Локальных наблюдений этого игрока на сервере пока нет (история копится, пока открыт мониторинг сервера)."; return; }
        var now = _clock.UtcNow;
        foreach (var s in sessions.Take(30))
        {
            var dur = s.Stop is null ? ActivityStats.ActiveElapsed(s.Start, now) : ActivityStats.Duration(s.Start, s.Stop);
            SessionLines.Add($"{s.Start.ToLocalTime():g} → {(s.Stop is null ? "ещё виден" : s.Stop.Value.ToLocalTime().ToString("g"))} • {(dur is null ? "длительность неизвестна" : $"{(int)dur.Value.TotalHours}ч {dur.Value.Minutes}м")}{(s.Stop is null ? " (игрок виден в последнем опросе)" : "")}");
        }
        var total = ActivityStats.Total(sessions, now);
        var days = ActivityStats.DailyHours(sessions, now, 30, TimeZoneInfo.Local);
        var max = Math.Max(1, days.Values.Max());
        foreach (var kv in days) Bars.Add(new DayBar(kv.Key.ToString("dd.MM"), kv.Value, kv.Value / max * 90));
        ActivityNote = $"Расчётное присутствие за период: не менее {(int)total.TotalHours} ч {total.Minutes} м. Это оценка по интервалам опроса, а не точное игровое время; пропуски возможны.";
    }
}
