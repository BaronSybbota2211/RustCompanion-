using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RustCompanion.Core;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.RustPlus;
using RustCompanion.Core.Services;
using RustCompanion.Core.Watch;

namespace RustCompanion.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly RustCompanion.Core.Data.SettingsRepository _repo;
    private readonly ISettingsProvider _provider;
    private readonly ISecretStore _secrets;
    private readonly DiagnosticsService _diag;
    private readonly TelegramChannel _telegram;
    private readonly DiscordChannel _discord;
    private readonly ExportService _export;
    private readonly RustCompanion.Core.Data.Database _database;

    [ObservableProperty] private int _watchPollSeconds;
    [ObservableProperty] private int _absenceConfirmations;
    [ObservableProperty] private int _notifyMinIntervalSeconds;
    [ObservableProperty] private bool _telegramEnabled;
    [ObservableProperty] private string _telegramChatId = "";
    [ObservableProperty] private string _telegramTokenInput = "";
    [ObservableProperty] private bool _discordEnabled;
    [ObservableProperty] private string _discordWebhookInput = "";
    [ObservableProperty] private string _integrationStatus = "";
    [ObservableProperty] private bool _exportActivity;
    [ObservableProperty] private int _serverPollSeconds;
    [ObservableProperty] private int _steamPollSeconds;
    [ObservableProperty] private int _requestTimeoutSeconds;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _serverMonitoringEnabled;
    [ObservableProperty] private bool _steamMonitoringEnabled;
    [ObservableProperty] private string _steamKeyInput = "";
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _rustMapsKeyInput = "";
    [ObservableProperty] private string _rustMapsKeyStatus = "";
    [ObservableProperty] private string _message = "";
    public ObservableCollection<string> Diagnostics { get; } = new();

    public SettingsViewModel(RustCompanion.Core.Data.SettingsRepository repo, ISettingsProvider provider, ISecretStore secrets, DiagnosticsService diag,
        TelegramChannel telegram, DiscordChannel discord, ExportService export, RustCompanion.Core.Data.Database database)
    { _repo = repo; _provider = provider; _secrets = secrets; _diag = diag; _telegram = telegram; _discord = discord; _export = export; _database = database; Reload(); }

    public void Reload()
    {
        var s = _provider.Current;
        ServerPollSeconds = s.ServerPollSeconds; SteamPollSeconds = s.SteamPollSeconds; RequestTimeoutSeconds = s.RequestTimeoutSeconds;
        CloseToTray = s.CloseToTray;
        ServerMonitoringEnabled = s.ServerMonitoringEnabled; SteamMonitoringEnabled = s.SteamMonitoringEnabled;
        WatchPollSeconds = s.WatchPollSeconds; AbsenceConfirmations = s.AbsenceConfirmations; NotifyMinIntervalSeconds = s.NotifyMinIntervalSeconds;
        TelegramEnabled = s.TelegramEnabled; TelegramChatId = s.TelegramChatId; DiscordEnabled = s.DiscordEnabled;
        IntegrationStatus = $"Telegram: {(string.IsNullOrEmpty(_secrets.Get(SecretNames.TelegramBotToken)) ? "токен не задан" : "токен сохранён")} • " +
                            $"Discord: {(string.IsNullOrEmpty(_secrets.Get(SecretNames.DiscordWebhookUrl)) ? "вебхук не задан" : "вебхук сохранён")}";
        RustMapsKeyStatus = string.IsNullOrEmpty(_secrets.Get(SecretNames.RustMapsApiKey)) ? "Ключ RustMaps API не задан" : "Ключ RustMaps API сохранён (зашифрован DPAPI)";
        KeyStatus = string.IsNullOrEmpty(_secrets.Get(SecretNames.SteamApiKey)) ? "Ключ Steam API не задан" : "Ключ Steam API сохранён (зашифрован DPAPI)";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(_provider.Current))!;
        s.ServerPollSeconds = ServerPollSeconds; s.SteamPollSeconds = SteamPollSeconds; s.RequestTimeoutSeconds = RequestTimeoutSeconds;
        s.CloseToTray = CloseToTray;
        s.ServerMonitoringEnabled = ServerMonitoringEnabled; s.SteamMonitoringEnabled = SteamMonitoringEnabled;
        s.WatchPollSeconds = WatchPollSeconds; s.AbsenceConfirmations = AbsenceConfirmations; s.NotifyMinIntervalSeconds = NotifyMinIntervalSeconds;
        s.TelegramEnabled = TelegramEnabled; s.TelegramChatId = TelegramChatId?.Trim() ?? "";
        s.DiscordEnabled = DiscordEnabled;
        var errors = s.Validate();
        if (errors.Count > 0) { Message = string.Join(" ", errors); return; }
        await _repo.SaveAsync(s);
        _provider.Update(s);
        if (!string.IsNullOrWhiteSpace(SteamKeyInput)) { _secrets.Set(SecretNames.SteamApiKey, SteamKeyInput.Trim()); SteamKeyInput = ""; }
        if (!string.IsNullOrWhiteSpace(RustMapsKeyInput)) { _secrets.Set(SecretNames.RustMapsApiKey, RustMapsKeyInput.Trim()); RustMapsKeyInput = ""; }
        if (!string.IsNullOrWhiteSpace(TelegramTokenInput)) { _secrets.Set(SecretNames.TelegramBotToken, TelegramTokenInput.Trim()); TelegramTokenInput = ""; }
        if (!string.IsNullOrWhiteSpace(DiscordWebhookInput))
        {
            if (!DiscordChannel.IsValidWebhook(DiscordWebhookInput.Trim())) { Message = "Некорректный URL вебхука Discord."; return; }
            _secrets.Set(SecretNames.DiscordWebhookUrl, DiscordWebhookInput.Trim()); DiscordWebhookInput = "";
        }
        Reload();
        Message = "Сохранено.";
    }

    [RelayCommand]
    private void OpenRustMapsDashboard() { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RustMapsClient.DashboardUrl) { UseShellExecute = true }); } catch (Exception) { } }

    [RelayCommand]
    private void DeleteRustMapsKey() { _secrets.Delete(SecretNames.RustMapsApiKey); Reload(); Message = "Ключ RustMaps удалён."; }

    [RelayCommand]
    private void DeleteKey() { _secrets.Delete(SecretNames.SteamApiKey); Reload(); Message = "Ключ удалён."; }

    [RelayCommand]
    private void RefreshDiagnostics()
    {
        Diagnostics.Clear();
        foreach (var d in _diag.Snapshot().Reverse().Take(50))
            Diagnostics.Add($"{d.AtUtc.ToLocalTime():T} [{d.State}] {d.Source} {d.Message}");
    }

    [RelayCommand]
    private async Task TestTelegramAsync()
    {
        await SaveAsync();
        var r = await _telegram.SendAsync("Rust Companion", "Тестовое сообщение: интеграция настроена.", default);
        _diag.Record("Telegram test", r.State, r.Message);
        IntegrationStatus = r.IsOk ? "Telegram: тестовое сообщение доставлено." : r.Message;
    }

    [RelayCommand]
    private async Task TestDiscordAsync()
    {
        await SaveAsync();
        var r = await _discord.SendAsync("Rust Companion", "Тестовое сообщение: интеграция настроена.", default);
        _diag.Record("Discord test", r.State, r.Message);
        IntegrationStatus = r.IsOk ? "Discord: тестовое сообщение доставлено." : r.Message;
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "rust-companion-export.json" };
        if (dlg.ShowDialog() != true) return;
        var json = await _export.ExportAsync(ExportActivity, DateTimeOffset.UtcNow);
        await System.IO.File.WriteAllTextAsync(dlg.FileName, json);
        Message = "Экспорт сохранён (ключи API, токены и вебхуки в файл не включаются).";
    }

    [RelayCommand]
    private async Task ImportDataAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var rep = await _export.ImportAsync(await System.IO.File.ReadAllTextAsync(dlg.FileName));
            Message = $"Импорт: серверов +{rep.ServersAdded}, групп +{rep.GroupsAdded}, игроков +{rep.WatchAdded}, пропущено {rep.Skipped}.";
        }
        catch (System.IO.InvalidDataException ex) { Message = ex.Message; }
    }

    [RelayCommand]
    private void BackupDatabase()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "SQLite (*.db)|*.db", FileName = $"companion-backup-{DateTime.Now:yyyyMMdd-HHmm}.db" };
        if (dlg.ShowDialog() != true) return;
        if (System.IO.File.Exists(dlg.FileName)) System.IO.File.Delete(dlg.FileName);
        _database.BackupTo(dlg.FileName);
        Message = "Резервная копия базы сохранена.";
    }
}
