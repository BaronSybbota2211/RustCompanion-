using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RustCompanion.App.ViewModels;

/// <summary>Сведения о программе: версия и дата обновления обновляются при каждом выпуске (версия — в RustCompanion.App.csproj).</summary>
public static class AppInfo
{
    public const string ProductName = "Rust Companion";
    public const string ReleaseDate = "13.11.2026";
    public const string Author = "Барон Суббота";
    public const string TelegramHandle = "@Gigibon";
    public const string TelegramUrl = "https://t.me/Gigibon";
    public const string DonateUrl = "https://messenger.sbrf.ru/sl/78GY7ELnLQKA7T4d1";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }
}

public sealed record AboutSource(string Name, string Purpose, string? Url);

public sealed partial class AboutViewModel : ObservableObject
{
    public string ProductName => AppInfo.ProductName;
    public string Version => AppInfo.Version;
    public string ReleaseDate => AppInfo.ReleaseDate;
    public string Author => AppInfo.Author;
    public string TelegramHandle => AppInfo.TelegramHandle;
    public string TelegramUrl => AppInfo.TelegramUrl;
    public string DonateUrl => AppInfo.DonateUrl;

    /// <summary>Откуда программа берёт данные (подробнее — docs/DATA_SOURCES.md).</summary>
    public IReadOnlyList<AboutSource> Sources { get; } = new AboutSource[]
    {
        new("Rust+ (Facepunch Companion API)", "карта сервера, команда, метки, умные устройства, события; по подключению к вашему серверу", "https://rust.facepunch.com"),
        new("RustMaps.com", "карты серверов, монументы, 3D-просмотр, слои ресурсов, животных и зон; требуется ваш ключ API", "https://rustmaps.com"),
        new("BattleMetrics", "поиск карты сервера по названию (ссылка на карту RustMaps)", "https://www.battlemetrics.com"),
        new("Steam Web API", "профили Steam, список серверов (нужен ваш ключ)", "https://steamcommunity.com/dev"),
        new("Valve Server Queries (A2S)", "онлайн, карта и игроки серверов по прямым запросам", "https://developer.valvesoftware.com/wiki/Server_queries"),
        new("Ввод пользователя", "таймеры РТ, вертолёта, Bradley, гниение; прогнозы — оценка по вашим отметкам", null),
    };

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        // открываем только https-адреса, заданные в этой модели
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { }
    }
}
