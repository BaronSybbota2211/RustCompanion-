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
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Services;
using RustCompanion.Core.Watch;

namespace RustCompanion.App.ViewModels;

public sealed partial class ServerResultItemViewModel : ObservableObject
{
    public FoundServer Server { get; }
    public ServerResultItemViewModel(FoundServer s) { Server = s; }
    public string Title => Server.Name;
    public string Details => string.Join(" • ", new[]
    {
        Server.Map, Server.Players is null ? null : $"{Server.Players}/{(Server.MaxPlayers?.ToString() ?? "?")}", Server.Status, Server.Country
    }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Connect => ConnectCommand.Build(Server.Ip, Server.Port) ?? ConnectCommand.Unavailable;
    public string IdText => "адрес запросов " + Server.Id;

    [ObservableProperty] private bool _isHighlighted;
}

public sealed partial class PlayerRowViewModel : ObservableObject
{
    public static string FormatDuration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{Math.Max(0, t.Minutes)} мин {Math.Max(0, t.Seconds)} с";
    private readonly ServerBrowserViewModel _parent;
    public OnlinePlayer Player { get; }
    public PlayerRowViewModel(OnlinePlayer p, ServerBrowserViewModel parent) { Player = p; _parent = parent; }
    public string Name => Player.Name;
    public string SourceKey => Player.Id;
    /// <summary>Время подключения так, как его сообщил сам сервер (A2S_PLAYER). Приложение его не проверяет.</summary>
    public string SessionTime => Player.SessionSeconds is double s && s >= 0 ? FormatDuration(TimeSpan.FromSeconds(s)) : "не сообщено";
    /// <summary>Локальная история: первое обнаружение и расчётное время присутствия по опросам.</summary>
    [ObservableProperty] private string _observed = "—";
    [ObservableProperty] private string _status = "На сервере";
    [ObservableProperty] private string _lastConfirmed = "";
    [ObservableProperty] private bool _tracked;
    public string TrackLabel => Tracked ? "Отслеживается" : "Следить";
    partial void OnTrackedChanged(bool value) => OnPropertyChanged(nameof(TrackLabel));
    [RelayCommand(CanExecute = nameof(CanTrack))] private Task TrackAsync() => _parent.TrackAsync(this);
    private bool CanTrack() => !Tracked;
    [RelayCommand] private Task SessionsAsync() => _parent.ShowSessionsAsync(this);
}

public sealed partial class ServerBrowserViewModel : ObservableObject
{
    private readonly ServerSearchController _search; private readonly ServerPlayersController _players; private readonly RecentServersRepository _recents;
    private readonly WatchRepository _watch; private readonly IServerQuerySource _source; private readonly ConnectCopier _copier; private readonly ISettingsProvider _settings;
    private readonly IClock _clock; private readonly DiagnosticsService _diag; private readonly IDiagnosableSource _searchDiag; private readonly PresenceHistoryRepository _history;
    private bool _suppressSearch; private long _sessionsVersion;
    private readonly List<PlayerRowViewModel> _allRows = new();

    public ObservableCollection<ServerResultItemViewModel> Results { get; } = new();
    public ObservableCollection<PlayerRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> SessionLines { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isDropDownOpen;
    [ObservableProperty] private string _listTitle = "";
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private string _requestInfo = "";
    [ObservableProperty] private bool _showRetry;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _highlight = -1;

    [ObservableProperty] private FoundServer? _selectedServer;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _serverTitle = "";
    [ObservableProperty] private string _serverSubtitle = "";
    [ObservableProperty] private string _connectText = "";
    [ObservableProperty] private bool _canCopy;
    [ObservableProperty] private string _copyStatus = "";

    [ObservableProperty] private string _playersStatus = "";
    [ObservableProperty] private string _lastUpdateText = "Ещё не обновлялось";
    [ObservableProperty] private bool _isPlayersLoading;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string _playerFilter = "";
    [ObservableProperty] private string _transferStatus = "";
    [ObservableProperty] private string _message = "";

    /// <summary>Сервер выбран и сохранён для вкладки «Наблюдение»: (сервер, id в локальной базе).</summary>
    public event Action<FoundServer, long>? ServerOpened;
    /// <summary>Загружен список игроков онлайн выбранного сервера (ключ сервера, игроки).</summary>
    public event Action<string, IReadOnlyList<FoundPlayer>>? PlayersAvailable;
    public event Action? TrackingChanged;
    public event Action? OpenWatchRequested;
    [ObservableProperty] private string _sessionTitle = "";
    [ObservableProperty] private string _sessionNote = "";
    public ObservableCollection<string> ServerDetailLines { get; } = new();
    [ObservableProperty] private string _detailsStatus = "";

    [RelayCommand]
    private async Task RefreshServerDetailsAsync()
    {
        var s = SelectedServer; if (s is null) return;
        DetailsStatus = "Запрос сведений о сервере…";
        var r = await _source.GetServerDetailsAsync(s.Id, default);
        if (!ReferenceEquals(s, SelectedServer)) return;     // за время запроса выбран другой сервер
        ServerDetailLines.Clear();
        if (r.IsOk && r.Value is not null) { foreach (var l in r.Value.Lines(_clock.UtcNow)) ServerDetailLines.Add(l); DetailsStatus = "Обновлено: " + DateTime.Now.ToString("T"); }
        else DetailsStatus = "Сведения недоступны: " + r.Message;
    }

    public ServerBrowserViewModel(ServerSearchController search, ServerPlayersController players, RecentServersRepository recents, WatchRepository watch,
        IServerQuerySource source, ConnectCopier copier, ISettingsProvider settings, IClock clock, DiagnosticsService diag, IDiagnosableSource searchDiag, PresenceHistoryRepository history)
    {
        _history = history;
        _searchDiag = searchDiag; _search = search; _players = players; _recents = recents; _watch = watch; _source = source; _copier = copier; _settings = settings; _clock = clock; _diag = diag;
        _search.Changed += s => OnUi(() => ApplySearchState(s));
        _players.Changed += s => OnUi(() => _ = ApplyPlayersStateAsync(s));
    }

    private static void OnUi(Action a) { var d = Application.Current?.Dispatcher; if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a); }

    partial void OnSearchTextChanged(string value)
    {
        if (_suppressSearch) return;
        IsDropDownOpen = true;
        if (value.Trim().Length < ServerSearchController.MinQueryLength) _ = ShowRecentsAsync();
        _ = _search.SetQueryAsync(value);
    }

    partial void OnPlayerFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task ShowRecentsAsync()
    {
        if (SearchText.Trim().Length >= ServerSearchController.MinQueryLength) return;
        var recents = await _recents.ListAsync();
        Results.Clear();
        foreach (var r in recents) Results.Add(new ServerResultItemViewModel(new FoundServer(r.SourceKey, r.Name, r.Ip, r.Port, null, null, null, r.Map, r.Country)));
        ListTitle = recents.Count > 0 ? "Недавние серверы" : "";
        SearchStatus = recents.Count > 0 ? "" : "Введите название сервера (минимум 2 символа) или адрес ip:порт запросов";
        HasResults = Results.Count > 0; ShowRetry = false; IsSearching = false; Highlight = -1;
    }

    private void ApplySearchState(ServerSearchState s)
    {
        if (s.Phase != SearchPhase.Idle) RequestInfo = "Последний запрос: " + _searchDiag.LastRequestInfo + (s.Phase is SearchPhase.Results or SearchPhase.Empty ? $" • найдено серверов: {s.Results.Count}" : "");
        if (s.Phase == SearchPhase.Idle) { _ = ShowRecentsAsync(); return; }
        ListTitle = ""; ShowRetry = false; IsSearching = s.Phase == SearchPhase.Loading; Highlight = -1;
        Results.Clear();
        foreach (var r in s.Results) Results.Add(new ServerResultItemViewModel(r));
        HasResults = Results.Count > 0;
        SearchStatus = s.Phase switch
        {
            SearchPhase.Loading => "Поиск…",
            SearchPhase.Empty => "Серверы не найдены",
            SearchPhase.Error => "Ошибка: " + s.Message,
            _ => ""
        };
        ShowRetry = s.Phase == SearchPhase.Error;
        if (s.Phase == SearchPhase.Error) _diag.Record("Поиск серверов", s.ErrorKind ?? SourceState.Error, s.Message ?? "");
    }

    [RelayCommand] private void OpenWatch() => OpenWatchRequested?.Invoke();
    [RelayCommand] private Task RetrySearchAsync() => _search.RetryAsync();

    [RelayCommand]
    private void MoveHighlight(string direction)
    {
        if (Results.Count == 0) return;
        var next = direction == "down" ? Math.Min(Results.Count - 1, Highlight + 1) : Math.Max(0, Highlight - 1);
        for (var i = 0; i < Results.Count; i++) Results[i].IsHighlighted = i == next;
        Highlight = next;
    }

    [RelayCommand] private Task ChooseHighlightedAsync() => Highlight >= 0 && Highlight < Results.Count ? ChooseAsync(Results[Highlight]) : Task.CompletedTask;
    [RelayCommand] private void CloseDropDown() => IsDropDownOpen = false;
    [RelayCommand] private async Task OpenDropDownAsync() { IsDropDownOpen = true; await ShowRecentsAsync(); }

    [RelayCommand]
    private async Task ChooseAsync(ServerResultItemViewModel? item)
    {
        if (item is null) return;
        var s = item.Server;                                   // сервер выбирается по ключу «хост:порт запросов»
        SelectedServer = s; HasSelection = true;
        ServerTitle = s.Name;
        ServerSubtitle = $"Адрес запросов {s.Id}" + (string.IsNullOrEmpty(s.Map) ? "" : $" • {s.Map}") + (s.Players is null ? "" : $" • игроков {s.Players}/{s.MaxPlayers?.ToString() ?? "?"}") + (string.IsNullOrEmpty(s.Status) ? "" : $" • {s.Status}");
        var cmd = ConnectCommand.Build(s.Ip, s.Port);
        ConnectText = cmd ?? ConnectCommand.Unavailable; CanCopy = cmd is not null; CopyStatus = ""; Message = "";
        _suppressSearch = true; SearchText = s.Name; _suppressSearch = false; IsDropDownOpen = false;
        SessionLines.Clear(); SessionTitle = ""; SessionNote = ""; PlayerFilter = ""; _sessionsVersion++;
        _allRows.Clear(); Rows.Clear(); PlayersStatus = "Запрос списка игроков (A2S_PLAYER)…"; IsPlayersLoading = true; LastUpdateText = "Ещё не обновлялось";   // список прежнего сервера убирается сразу
        try { await _recents.TouchAsync(s, _clock.UtcNow); } catch (Exception ex) { _diag.Record("Недавние серверы", SourceState.Error, ex.GetType().Name); }
        ServerDetailLines.Clear();
        await TransferAsync(s);
        _ = RefreshServerDetailsAsync();
        await _players.SelectServerAsync(s.Id);               // отменяет запрос прежнего сервера; поздний ответ отбрасывается
    }

    /// <summary>Сохраняет выбранный сервер локально (адрес запросов) и открывает его во вкладке «Наблюдение».</summary>
    private async Task TransferAsync(FoundServer s)
    {
        TransferStatus = "";
        if (!ServerKey.TryParse(s.Id, out var host, out var queryPort)) { TransferStatus = "Сервер не передан в «Наблюдение»: некорректный адрес запросов."; return; }
        try
        {
            var dbId = await _watch.AddTrackedServerAsync(s.Name, host, queryPort, s.Id);   // сервер сохраняется локально и становится доступен в наблюдении
            ServerOpened?.Invoke(s, dbId);
            TransferStatus = "Сервер передан в «Наблюдение».";
        }
        catch (Exception ex) { TransferStatus = "Не удалось передать сервер в наблюдение: " + ex.Message; _diag.Record("Передача сервера", SourceState.Error, ex.GetType().Name); }
    }

    [RelayCommand] private void Copy() => CopyStatus = _copier.Copy(CanCopy ? ConnectText : null);

    [RelayCommand] private Task RefreshPlayersAsync() => _players.RefreshAsync();

    private async Task ApplyPlayersStateAsync(ServerPlayersState st)
    {
        if (SelectedServer is null || st.ServerId != SelectedServer.Id) return;          // чужой/устаревший сервер — игнорируем
        IsPlayersLoading = st.Phase == PlayersPhase.Loading;
        var tracked = await LoadTrackedAsync(st.ServerId!);
        if (SelectedServer is null || SelectedServer.Id != st.ServerId) return;
        _allRows.Clear();
        foreach (var p in st.Players)
        {
            var row = new PlayerRowViewModel(p, this) { Status = st.Stale ? "Не подтверждено (данные устарели)" : "На сервере" };
            if (tracked.TryGetValue(p.Id, out var t)) { row.Tracked = true; row.LastConfirmed = t.State.LastPresentUtc is null ? "" : "подтверждён " + t.State.LastPresentUtc.Value.ToLocalTime().ToString("t"); }
            _allRows.Add(row);
        }
        await ApplyHistoryAsync(st.ServerId!);
        if (SelectedServer is null || SelectedServer.Id != st.ServerId) return;
        ApplyFilter();
        if (st.Phase is PlayersPhase.Ready or PlayersPhase.Empty) PlayersAvailable?.Invoke(st.ServerId!, st.Players.Select(p => new FoundPlayer(p.Id, p.Name)).ToList());
        PlayersStatus = st.Phase switch
        {
            PlayersPhase.Loading => "Запрос списка игроков (A2S_PLAYER)…",
            PlayersPhase.Ready => $"Игроков в списке: {st.Players.Count}" + (st.ReportedPlayers is int n ? $" (сервер сообщает: {n})" : "") + (st.Partial ? ". " + st.Message : ""),
            PlayersPhase.Empty => st.Message ?? "На сервере сейчас нет игроков.",
            PlayersPhase.Unavailable => st.Message ?? "Список игроков недоступен.",
            PlayersPhase.Error => "Ошибка обновления: " + st.Message + (st.Stale ? " Показаны последние известные данные — они могут быть устаревшими." : ""),
            _ => ""
        };
        if (st.Phase is PlayersPhase.Error or PlayersPhase.Unavailable) _diag.Record("Список игроков (A2S)", st.Phase == PlayersPhase.Error ? SourceState.Error : SourceState.NoData, st.Message ?? "");
        LastUpdateText = st.LastSuccessUtc is null ? "Ещё не обновлялось" : "Обновлено: " + st.LastSuccessUtc.Value.ToLocalTime().ToString("T") + (_players.IsStale(_clock.UtcNow) ? " (устарело)" : "");
    }

    /// <summary>Подписывает строки данными локальной истории: первое обнаружение и расчётное время присутствия (по опросам, не точное игровое время).</summary>
    private async Task ApplyHistoryAsync(string serverKey)
    {
        try
        {
            var summary = (await _history.ListSummaryAsync(serverKey, null, 1000)).ToDictionary(x => x.Name);
            foreach (var row in _allRows)
                if (summary.TryGetValue(row.Name, out var h))
                    row.Observed = $"впервые {h.FirstSeen.ToLocalTime():g}; ≈{PlayerRowViewModel.FormatDuration(TimeSpan.FromSeconds(h.EstimatedSeconds))} по опросам, заходов: {h.Visits}";
        }
        catch (Exception ex) { _diag.Record("История наблюдений", SourceState.Error, ex.GetType().Name); }
    }

    private async Task<Dictionary<string, (WatchEntry Entry, MonitorState State)>> LoadTrackedAsync(string serverKey)
    {
        var result = new Dictionary<string, (WatchEntry, MonitorState)>();
        var server = (await _watch.ListTrackedServersAsync()).FirstOrDefault(x => x.SourceKey == serverKey);
        if (server.SourceKey is null) return result;
        foreach (var w in await _watch.ListWatchAsync(server.Id)) result[w.PlayerKey] = (w, await _watch.GetStateAsync(w.Id));
        return result;
    }

    /// <summary>Перечитывает из базы, кто из игроков выбранного сервера отслеживается: после удаления сервера или игроков на других вкладках отметки «Отслеживается» сбрасываются.</summary>
    public async Task RefreshTrackingAsync()
    {
        var s = SelectedServer; if (s is null) return;
        var serverStillSaved = (await _watch.ListTrackedServersAsync()).Any(x => x.SourceKey == s.Id);
        var tracked = await LoadTrackedAsync(s.Id);
        if (SelectedServer is null || SelectedServer.Id != s.Id) return;
        foreach (var row in _allRows)
        {
            if (tracked.TryGetValue(row.SourceKey, out var t)) { row.Tracked = true; row.LastConfirmed = t.State.LastPresentUtc is null ? "" : "подтверждён " + t.State.LastPresentUtc.Value.ToLocalTime().ToString("t"); }
            else { row.Tracked = false; row.LastConfirmed = ""; }
            row.TrackCommand.NotifyCanExecuteChanged();
        }
        if (!serverStillSaved) TransferStatus = "Сервер удалён из списка серверов и наблюдения. Выберите его снова, чтобы добавить.";
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        foreach (var r in _allRows.Where(r => string.IsNullOrWhiteSpace(PlayerFilter) || r.Name.Contains(PlayerFilter, StringComparison.CurrentCultureIgnoreCase) || r.SourceKey.Contains(PlayerFilter, StringComparison.OrdinalIgnoreCase)))
            Rows.Add(r);
    }

    internal async Task TrackAsync(PlayerRowViewModel row)
    {
        Message = "";
        var s = SelectedServer;
        if (s is null) return;
        if (!ServerKey.TryParse(s.Id, out var host, out var queryPort)) { Message = "Для отслеживания нужен корректный адрес запросов сервера."; return; }
        try
        {
            var serverDbId = await _watch.AddTrackedServerAsync(s.Name, host, queryPort, s.Id);
            await _watch.AddWatchAsync(serverDbId, row.SourceKey, null, row.Name);
            row.Tracked = true; row.TrackCommand.NotifyCanExecuteChanged(); TrackingChanged?.Invoke();
            Message = $"«{row.Name}» добавлен в отслеживание (по нику: Steam Query не передаёт SteamID). Уведомление об уходе включается на вкладке «Уведомления» (по умолчанию выключено); мониторинг и история идут независимо от него.";
        }
        catch (InvalidOperationException ex) { Message = ex.Message; row.Tracked = true; }
    }

    internal async Task ShowSessionsAsync(PlayerRowViewModel row)
    {
        var s = SelectedServer; if (s is null) return;
        var version = ++_sessionsVersion; var serverId = s.Id;
        SessionTitle = "Присутствие: " + row.Name; SessionLines.Clear(); SessionNote = "Загрузка…";
        var r = await _source.GetSessionsAsync(row.SourceKey, serverId, default);
        if (version != _sessionsVersion || SelectedServer?.Id != serverId) return;      // устаревший ответ (другой игрок/сервер)
        if (!r.IsOk) { _diag.Record("История наблюдений", r.State, r.Message); SessionNote = "История недоступна: " + r.Message; return; }
        var rows = SessionView.Build(r.Value!, _clock.UtcNow);
        if (rows.Count == 0) { SessionNote = "Локальных наблюдений этого ника на этом сервере пока нет."; return; }
        foreach (var x in rows.Take(30))
            SessionLines.Add($"{x.StartUtc.ToLocalTime():g} → {(x.StopUtc is null ? "виден в последнем опросе" : x.StopUtc.Value.ToLocalTime().ToString("g"))} • " +
                             (x.Duration is { } d ? $"≈{PlayerRowViewModel.FormatDuration(d)}" : "длительность не определена"));
        SessionNote = $"Записей: {rows.Count}. Время расчётное: от первого до последнего опроса, где ник был в списке. Это нижняя оценка по моментам опроса, а не точное игровое время.";
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        var s = SelectedServer; if (s is null) return;
        var version = ++_sessionsVersion; var serverId = s.Id;
        SessionTitle = "История наблюдений сервера"; SessionLines.Clear(); SessionNote = "Загрузка…";
        try
        {
            var list = await _history.ListSummaryAsync(serverId, PlayerFilter, 100);
            if (version != _sessionsVersion || SelectedServer?.Id != serverId) return;
            foreach (var h in list)
                SessionLines.Add($"{h.Name} • впервые {h.FirstSeen.ToLocalTime():g} • последний раз {h.LastSeen.ToLocalTime():g} • ≈{PlayerRowViewModel.FormatDuration(TimeSpan.FromSeconds(h.EstimatedSeconds))} • заходов: {h.Visits}");
            SessionNote = list.Count == 0 ? "Наблюдений пока нет: история копится, пока выбран сервер (или он под наблюдением)."
                : "Первое и последнее обнаружение — по опросам приложения. Время расчётное (нижняя оценка), не точное игровое.";
        }
        catch (Exception ex) { _diag.Record("История наблюдений", SourceState.Error, ex.GetType().Name); SessionNote = "Не удалось прочитать историю: " + ex.Message; }
    }

    public async Task RunAutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _settings.Current.ServerPanelRefreshSeconds)), ct);
                if (AutoRefresh && SelectedServer is not null) await _players.RefreshAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
