using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;

namespace RustCompanion.App.ViewModels;

public sealed partial class ServerItemViewModel : ObservableObject
{
    public ServerEntry Entry { get; }
    private readonly ServersViewModel _parent;
    public ServerItemViewModel(ServerEntry e, ServersViewModel parent) { Entry = e; _parent = parent; _favorite = e.Favorite; }

    public string Name => Entry.Name;
    public string Address => $"{Entry.Host}:{Entry.QueryPort}";
    [ObservableProperty] private bool _favorite;
    [ObservableProperty] private string _stateText = "Ещё не опрашивался";
    [ObservableProperty] private string _detailsText = "";
    [ObservableProperty] private string _lastSuccessText = "Успешного ответа не было";

    [RelayCommand] private Task ToggleFavoriteAsync() => _parent.ToggleFavoriteAsync(this);
    [RelayCommand] private Task RemoveAsync() => _parent.RemoveAsync(this);
}

public sealed partial class ServersViewModel : ObservableObject
{
    private readonly ServerRepository _repo;
    private readonly IServerInfoSource _source;
    private readonly ISettingsProvider _settings;
    private readonly DiagnosticsService _diag;
    private readonly IClock _clock;

    public ObservableCollection<ServerItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newHost = "";
    [ObservableProperty] private string _newPort = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Список серверов изменился (добавлен/удалён) — другие вкладки должны перечитать свои списки.</summary>
    public event Action? ServersChanged;

    public ServersViewModel(ServerRepository repo, IServerInfoSource source, ISettingsProvider settings, DiagnosticsService diag, IClock clock)
    { _repo = repo; _source = source; _settings = settings; _diag = diag; _clock = clock; }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var s in await _repo.ListAsync()) Items.Add(new ServerItemViewModel(s, this));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        Error = "";
        if (!int.TryParse(NewPort, out var port)) { Error = "Порт запросов (query port) — число от 1 до 65535."; return; }
        try
        {
            var id = await _repo.AddAsync(NewName, NewHost, port);
            Items.Add(new ServerItemViewModel(new ServerEntry { Id = id, Name = NewName.Trim(), Host = NewHost.Trim(), QueryPort = port }, this));
            NewName = NewHost = NewPort = "";
            IsEmpty = false;
            ServersChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }

    internal async Task ToggleFavoriteAsync(ServerItemViewModel i)
    {
        i.Favorite = !i.Favorite; i.Entry.Favorite = i.Favorite;
        await _repo.SetFavoriteAsync(i.Entry.Id, i.Favorite);
    }

    internal async Task RemoveAsync(ServerItemViewModel i) { await _repo.DeleteAsync(i.Entry.Id); Items.Remove(i); IsEmpty = Items.Count == 0; ServersChanged?.Invoke(); }

    [RelayCommand]
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var i in Items.ToArray()) await RefreshOneAsync(i, ct);
    }

    private async Task RefreshOneAsync(ServerItemViewModel i, CancellationToken ct)
    {
        var r = await _source.QueryAsync(i.Entry.Host, i.Entry.QueryPort, ct);
        _diag.Record($"A2S {i.Address}", r.State, r.Message);
        if (r.IsOk && r.Value is not null)
        {
            var v = r.Value;
            i.StateText = "Отвечает — " + DataStatus.Confirmed.Ru();
            i.DetailsText = $"Игроков: {v.Players}/{v.MaxPlayers} • Карта: {v.Map}";
            i.LastSuccessText = "Последний успешный ответ: " + _clock.UtcNow.ToLocalTime().ToString("T");
            await _repo.AddObservationAsync(i.Entry.Id, _clock.UtcNow, v.Players, v.MaxPlayers, ct);
        }
        else
        {
            i.StateText = r.State switch { SourceState.NoResponse => "Нет ответа (не равно «сервер выключен»)", _ => "Недоступно" };
            i.DetailsText = r.Message; // прошлые данные не выдаём за текущие
        }
    }

    public async Task RunPollingAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_settings.Current.ServerMonitoringEnabled && Items.Count > 0) await RefreshAllAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(15, _settings.Current.ServerPollSeconds)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}

public sealed partial class PlayerItemViewModel : ObservableObject
{
    public PlayerEntry Entry { get; }
    private readonly PlayersViewModel _parent;
    public PlayerItemViewModel(PlayerEntry e, PlayersViewModel p) { Entry = e; _parent = p; }
    public string Title => string.IsNullOrWhiteSpace(Entry.Label) ? Entry.SteamId : $"{Entry.Label} ({Entry.SteamId})";
    [ObservableProperty] private string _statusText = "Ещё не проверялся";
    [ObservableProperty] private string _detailsText = "";
    [RelayCommand] private Task RemoveAsync() => _parent.RemoveAsync(this);
}

public sealed partial class PlayersViewModel : ObservableObject
{
    private readonly PlayerRepository _repo;
    private readonly ISteamStatusSource _steam;
    private readonly ISettingsProvider _settings;
    private readonly DiagnosticsService _diag;

    public ObservableCollection<PlayerItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _newSteamId = "";
    [ObservableProperty] private string _newLabel = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _isEmpty = true;

    public PlayersViewModel(PlayerRepository repo, ISteamStatusSource steam, ISettingsProvider settings, DiagnosticsService diag)
    { _repo = repo; _steam = steam; _settings = settings; _diag = diag; }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var p in await _repo.ListAsync()) Items.Add(new PlayerItemViewModel(p, this));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        Error = "";
        try
        {
            var id = await _repo.AddAsync(NewSteamId, NewLabel);
            Items.Add(new PlayerItemViewModel(new PlayerEntry { Id = id, SteamId = NewSteamId.Trim(), Label = NewLabel.Trim() }, this));
            NewSteamId = NewLabel = "";
            IsEmpty = false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }

    internal async Task RemoveAsync(PlayerItemViewModel i) { await _repo.DeleteAsync(i.Entry.Id); Items.Remove(i); IsEmpty = Items.Count == 0; }

    private static string Persona(int s) => s switch
    { 0 => "Не в сети", 1 => "В сети", 2 => "Не беспокоить", 3 => "Отошёл", 4 => "Спит", 5 => "Хочет обмен", 6 => "Хочет играть", _ => "Неизвестно" };

    [RelayCommand]
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var i in Items.ToArray())
        {
            var r = await _steam.GetStatusAsync(i.Entry.SteamId, ct);
            _diag.Record($"Steam {i.Entry.SteamId}", r.State, r.Message);
            if (r.IsOk && r.Value is { } v)
            {
                i.StatusText = Persona(v.PersonaState) + " — " + DataStatus.Confirmed.Ru();
                var game = v.InRust switch { true => "В Rust сейчас (по данным Steam)", false => "Запущена другая игра", null => "Игра не показана (профиль или детали игры скрыты)" };
                var server = string.IsNullOrEmpty(v.ReportedServer) ? "Сервер Steam не сообщает" : "Сервер по данным Steam: " + v.ReportedServer;
                i.DetailsText = $"{game}. {server}. {(v.ProfilePublic ? "Профиль открыт" : "Профиль закрыт")}.";
            }
            else
            {
                i.StatusText = r.State switch
                {
                    SourceState.NotConfigured => "Недоступно: не задан ключ API",
                    SourceState.AccessDenied => "Недоступно: ключ отклонён",
                    SourceState.RateLimited => "Недоступно: лимит запросов",
                    SourceState.NoResponse => "Нет ответа от Steam",
                    SourceState.NoData => "Нет данных",
                    _ => "Ошибка"
                };
                i.DetailsText = r.Message;
            }
        }
    }

    public async Task RunPollingAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_settings.Current.SteamMonitoringEnabled && Items.Count > 0) await RefreshAllAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _settings.Current.SteamPollSeconds)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
