using System.Text.Json;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;

namespace RustCompanion.Core.Watch;

public sealed class ExportDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public AppSettings Settings { get; set; } = new();
    public List<ExportServer> Servers { get; set; } = new();
    public List<WatchGroup> Groups { get; set; } = new();
    public List<ExportWatch> Watch { get; set; } = new();
    public List<ActivityRow>? Activity { get; set; }
}
public sealed class ExportServer { public string Name { get; set; } = ""; public string Host { get; set; } = ""; public int Port { get; set; } public string? SourceKey { get; set; } public bool Favorite { get; set; } }
public sealed class ExportWatch { public string ServerKey { get; set; } = ""; public string PlayerKey { get; set; } = ""; public string? SteamId { get; set; } public string Name { get; set; } = ""; public string? GroupName { get; set; } public string? Color { get; set; } public bool NotifyJoin { get; set; } = true; public bool NotifyLeave { get; set; } = true; }
public sealed class ActivityRow { public string ServerKey { get; set; } = ""; public string PlayerKey { get; set; } = ""; public DateTimeOffset At { get; set; } public string Kind { get; set; } = ""; public string Detail { get; set; } = ""; }

public sealed record ImportReport(int ServersAdded, int GroupsAdded, int WatchAdded, int Skipped);

/// <summary>Экспорт/импорт. Секреты (ключи API, токены, URL вебхука) в документ не попадают по построению — AppSettings их не содержит.</summary>
public sealed class ExportService
{
    private readonly Database _db; private readonly WatchRepository _watch; private readonly ServerRepository _servers; private readonly ISettingsProvider _settings;
    public const int SupportedVersion = 1;
    public ExportService(Database db, WatchRepository watch, ServerRepository servers, ISettingsProvider settings) { _db = db; _watch = watch; _servers = servers; _settings = settings; }

    private static string Key(string host, int port) => $"{host}:{port}";

    public async Task<string> ExportAsync(bool includeActivity, DateTimeOffset now, CancellationToken ct = default)
    {
        var servers = await _servers.ListAsync(ct);
        var bm = (await _watch.ListTrackedServersAsync(ct)).ToDictionary(x => x.Id, x => x.SourceKey);
        var groups = await _watch.ListGroupsAsync(ct);
        var watch = await _watch.ListWatchAsync(null, ct);
        var byServer = servers.ToDictionary(s => s.Id);
        var doc = new ExportDocument { ExportedAtUtc = now, Settings = _settings.Current };
        doc.Servers = servers.Select(s => new ExportServer { Name = s.Name, Host = s.Host, Port = s.QueryPort, Favorite = s.Favorite, SourceKey = bm.GetValueOrDefault(s.Id) }).ToList();
        doc.Groups = groups.ToList();
        doc.Watch = watch.Where(w => byServer.ContainsKey(w.ServerId)).Select(w => new ExportWatch
        {
            ServerKey = Key(byServer[w.ServerId].Host, byServer[w.ServerId].QueryPort), PlayerKey = w.PlayerKey, SteamId = w.SteamId, Name = w.Name,
            GroupName = groups.FirstOrDefault(g => g.Id == w.GroupId)?.Name, Color = w.Color, NotifyJoin = w.NotifyJoin, NotifyLeave = w.NotifyLeave
        }).ToList();
        if (includeActivity)
        {
            var events = await _watch.ListActivityAsync(null, null, null, 5000, ct);
            var wmap = watch.ToDictionary(w => w.Id);
            doc.Activity = events.Where(e => wmap.ContainsKey(e.WatchId) && byServer.ContainsKey(wmap[e.WatchId].ServerId)).Select(e => new ActivityRow
            { ServerKey = Key(byServer[wmap[e.WatchId].ServerId].Host, byServer[wmap[e.WatchId].ServerId].QueryPort), PlayerKey = wmap[e.WatchId].PlayerKey, At = e.AtUtc, Kind = e.Kind, Detail = e.Detail }).ToList();
        }
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<ImportReport> ImportAsync(string json, CancellationToken ct = default)
    {
        ExportDocument? doc;
        try { doc = JsonSerializer.Deserialize<ExportDocument>(json); }
        catch (JsonException) { throw new InvalidDataException("Файл не является корректным JSON экспорта Rust Companion."); }
        if (doc is null) throw new InvalidDataException("Файл пуст или повреждён.");
        if (doc.Version > SupportedVersion) throw new InvalidDataException($"Версия файла ({doc.Version}) новее поддерживаемой ({SupportedVersion}).");
        if (doc.Version < 1) throw new InvalidDataException("Неизвестная версия файла.");
        if (doc.Settings.Validate().Count > 0) throw new InvalidDataException("Настройки в файле некорректны.");

        int sAdd = 0, gAdd = 0, wAdd = 0, skipped = 0;
        var existingServers = (await _servers.ListAsync(ct)).ToDictionary(s => Key(s.Host, s.QueryPort), s => s.Id);
        foreach (var s in doc.Servers)
        {
            try { ServerRepository.Validate(s.Name, s.Host, s.Port); } catch (ArgumentException) { skipped++; continue; }
            var k = Key(s.Host, s.Port);
            if (existingServers.ContainsKey(k)) { skipped++; continue; }
            var id = s.SourceKey is null ? await _servers.AddAsync(s.Name, s.Host, s.Port, ct) : await _watch.AddTrackedServerAsync(s.Name, s.Host, s.Port, s.SourceKey, ct);
            if (s.Favorite) await _servers.SetFavoriteAsync(id, true, ct);
            existingServers[k] = id; sAdd++;
        }
        var groups = (await _watch.ListGroupsAsync(ct)).ToDictionary(g => g.Name, g => g.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var g in doc.Groups)
        {
            if (string.IsNullOrWhiteSpace(g.Name) || groups.ContainsKey(g.Name)) { skipped++; continue; }
            var id = await _watch.AddGroupAsync(g.Name, g.Color, ct);
            await _watch.UpdateGroupAsync(new WatchGroup { Id = id, Name = g.Name, Color = g.Color, NotifyJoin = g.NotifyJoin, NotifyLeave = g.NotifyLeave, Threshold = g.Threshold }, ct);
            groups[g.Name] = id; gAdd++;
        }
        foreach (var w in doc.Watch)
        {
            if (!existingServers.TryGetValue(w.ServerKey, out var sid) || string.IsNullOrWhiteSpace(w.PlayerKey)) { skipped++; continue; }
            long? gid = w.GroupName is not null && groups.TryGetValue(w.GroupName, out var g) ? g : null;
            try { await _watch.AddWatchAsync(sid, w.PlayerKey, w.SteamId, w.Name, gid, ct); wAdd++; }
            catch (InvalidOperationException) { skipped++; }
        }
        return new ImportReport(sAdd, gAdd, wAdd, skipped);
    }
}
