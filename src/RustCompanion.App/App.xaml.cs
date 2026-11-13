using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RustCompanion.App.Services;
using RustCompanion.App.ViewModels;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Forecast;
using RustCompanion.Core.Hotkeys;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.RustPlus;
using RustCompanion.RustPlusAdapter;
using RustCompanion.Core.Models;
using RustCompanion.Core.Net;
using RustCompanion.Core.Services;
using RustCompanion.Core.Watch;
using Serilog;

namespace RustCompanion.App;

public partial class App : Application
{
    private ServiceProvider? _sp;
    private MainWindow? _window;
    private DispatcherTimer? _timer;
    private readonly CancellationTokenSource _cts = new();
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RustCompanion");
        Directory.CreateDirectory(dataDir);

        var serilog = new LoggerConfiguration().MinimumLevel.Information()
            .WriteTo.File(Path.Combine(dataDir, "logs", "app-.log"), rollingInterval: RollingInterval.Day,
                          fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true, retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += (_, args) =>
        {
            serilog.Error(args.Exception, "Unhandled UI exception");
            ShowError("Непредвиденная ошибка", args.Exception.GetType().Name + ": " + args.Exception.Message + "\n\nПодробности в журнале:\n" + Path.Combine(dataDir, "logs"));
            args.Handled = true;
        };

        try
        {
            var db = new Database(Path.Combine(dataDir, "companion.db"));
            var recovered = db.Initialize();

            var services = new ServiceCollection();
            services.AddLogging(b =>
            {
                b.AddSerilog(serilog, dispose: true);
                b.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning); // URL запроса Steam содержит ключ — не логируем
            });
            services.AddSingleton(db);
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<ISettingsProvider, SettingsProvider>();
            services.AddSingleton<ISecretStore>(new DpapiSecretStore(Path.Combine(dataDir, "secrets")));
            services.AddSingleton<RateLimiter>();
            services.AddSingleton<DiagnosticsService>();
            services.AddSingleton<ITimerRepository, SqliteTimerRepository>();
            services.AddSingleton<ServerRepository>();
            services.AddSingleton<PlayerRepository>();
            services.AddSingleton<SettingsRepository>();
            services.AddSingleton<IServerInfoSource, A2sClient>();
            services.AddHttpClient<ISteamStatusSource, SteamClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddSingleton<WatchRepository>();
            services.AddSingleton<RustMapsBindingStore>();
            services.AddSingleton<RustMapsLayerStore>();
            services.AddHttpClient<RustMapsClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
            services.AddHttpClient<TelegramChannel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddHttpClient<DiscordChannel>(c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddTransient<INotificationChannel>(sp => sp.GetRequiredService<TelegramChannel>());
            services.AddTransient<INotificationChannel>(sp => sp.GetRequiredService<DiscordChannel>());
            services.AddSingleton<NotificationDispatcher>();
            services.AddSingleton<MonitoringService>();
            services.AddSingleton<SteamLiveMonitor>();
            services.AddSingleton<ExportService>();
            services.AddSingleton<WatchViewModel>();
            services.AddSingleton(sp => new TrayService(ShowMainWindow, ExitApp, () => sp.GetRequiredService<OverlayService>().HideAll()));
            services.AddSingleton<INotifier>(sp => sp.GetRequiredService<TrayService>());
            services.AddSingleton<OverlayService>();
            services.AddSingleton<IOverlaySink>(sp => sp.GetRequiredService<OverlayService>());
            services.AddSingleton<NotificationPrefs>();
            services.AddSingleton<NotificationPrefsRepository>();
            services.AddSingleton<LedgerRepository>();
            services.AddSingleton<IEventNotifier, NotificationRouter>();
            services.AddSingleton<ObservedRepository>();
            services.AddSingleton<ForecastService>();
            services.AddSingleton<BradleyService>();
            services.AddSingleton<ContainerService>();
            services.AddSingleton<DecayRepository>();
            services.AddSingleton<DecayService>();
            services.AddSingleton<MonumentRespawnService>();
            services.AddSingleton<PairedServerRepository>();
            services.AddSingleton<RpDeviceRepository>();
            services.AddSingleton<RpDevicePairingRepository>();
            services.AddSingleton<GameNotifyLog>();
            services.AddSingleton<IGameNotifier, GameNotifier>();
            services.AddSingleton<RustPlusDeviceEvents>();
            services.AddSingleton<RustPlusDeviceRuntime>();
            services.AddSingleton<RustPlusDeviceController>();
            services.AddSingleton<RustPlusHub>();
            services.AddSingleton<FcmEventRouter>();
            services.AddSingleton<RustPlusFcmService>();
            services.AddSingleton<DeviceAvailabilityService>();
            services.AddSingleton<RpDeviceCommandRepository>();
            services.AddSingleton<RustPlusDeviceCommandService>();
            services.AddSingleton(sp => new RustPlusDevicePairingService(() => new RustPlusLibGateway(), sp.GetRequiredService<RustPlusManager>(),
                sp.GetRequiredService<PairedServerRepository>(), sp.GetRequiredService<RpDeviceRepository>(), sp.GetRequiredService<RpDevicePairingRepository>(),
                sp.GetRequiredService<RpDeviceCommandRepository>(), sp.GetRequiredService<RustPlusHub>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<DiagnosticsService>()));
            services.AddSingleton<PlayerDirectoryRepository>();
            services.AddSingleton<ServerCrawler>();
            services.AddSingleton(sp => new RustPlusManager(() => new RustPlusLibGateway(), sp.GetRequiredService<PairedServerRepository>(), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IEventNotifier>(), sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<DiagnosticsService>()));
            services.AddSingleton<AppTimerSource>();
            services.AddSingleton<ITimerSource>(sp => sp.GetRequiredService<AppTimerSource>());
            services.AddSingleton(BotCommandEngine.CreateDefault());
            services.AddSingleton<RustPlusBotService>();
            services.AddSingleton(sp =>
            {
                var sec = sp.GetRequiredService<ISecretStore>();
                return new RustPlusPairing(() => sec.Get(SecretNames.RustPlusFcmCredentials),
                    json => { if (json is null) sec.Delete(SecretNames.RustPlusFcmCredentials); else sec.Set(SecretNames.RustPlusFcmCredentials, json); },
                    new SteamLoginService(), saveCompanionToken: token => sp.GetRequiredService<PairedServerRepository>().SetCompanionToken(token));
            });
            services.AddSingleton<RustPlusViewModel>();
            services.AddSingleton<RustPlusDevicesViewModel>();
            services.AddSingleton<HudDataService>();
            services.AddSingleton(sp => new Lazy<HudDataService>(() => sp.GetRequiredService<HudDataService>()));
            services.AddHttpClient<SteamServerBrowser>(c => c.Timeout = Timeout.InfiniteTimeSpan);
            services.AddSingleton<IUdpExchangeFactory, UdpExchangeFactory>();
            services.AddSingleton(sp => new A2sQueryClient(sp.GetRequiredService<IUdpExchangeFactory>(), sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<RateLimiter>()));
            services.AddSingleton(sp => new MasterServerClient(sp.GetRequiredService<IUdpExchangeFactory>(), sp.GetRequiredService<ISettingsProvider>(), sp.GetRequiredService<RateLimiter>()));
            services.AddSingleton<PresenceHistoryRepository>();
            services.AddSingleton(sp => new A2sServerSource(sp.GetRequiredService<A2sQueryClient>(), sp.GetRequiredService<SteamServerBrowser>(),
                sp.GetRequiredService<MasterServerClient>(), sp.GetRequiredService<PresenceHistoryRepository>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<DiagnosticsService>()));
            services.AddSingleton<IServerQuerySource>(sp => sp.GetRequiredService<A2sServerSource>());
            services.AddSingleton<IDiagnosableSource>(sp => sp.GetRequiredService<A2sServerSource>());
            services.AddSingleton(sp => new ServerSearchController(sp.GetRequiredService<IServerQuerySource>()));
            services.AddSingleton(sp => new ServerPlayersController(sp.GetRequiredService<IServerQuerySource>(), sp.GetRequiredService<IClock>(),
                () => TimeSpan.FromSeconds(Math.Max(15, sp.GetRequiredService<ISettingsProvider>().Current.ServerPanelRefreshSeconds) * 3)));
            services.AddSingleton<RecentServersRepository>();
            services.AddSingleton<IClipboardService, WpfClipboardService>();
            services.AddSingleton<ConnectCopier>();
            services.AddSingleton<HotkeyRepository>();
            services.AddSingleton<HotkeyManager>();
            services.AddSingleton<HotkeyService>();
            services.AddSingleton<ServerBrowserViewModel>();
            services.AddSingleton<HotkeysViewModel>();
            services.AddSingleton<OverlayViewModel>();
            services.AddSingleton<MonumentsViewModel>();
            services.AddSingleton<NotificationsViewModel>();
            services.AddSingleton<BradleyViewModel>();
            services.AddSingleton<ContainerViewModel>();
            services.AddSingleton<DecayViewModel>();
            services.AddSingleton(sp =>
            {
                ForecastViewModel Make(ObservedKind k) => new(k, sp.GetRequiredService<ServerRepository>(), sp.GetRequiredService<ObservedRepository>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<NotificationPrefs>());
                return new ForecastSet(Make(ObservedKind.Chinook), Make(ObservedKind.PatrolHelicopter), Make(ObservedKind.Airdrop), Make(ObservedKind.BradleyAppeared), Make(ObservedKind.CargoShip));
            });
            services.AddSingleton<TimerService>();
            services.AddSingleton<ServersViewModel>();
            services.AddSingleton<PlayersViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddSingleton<MainViewModel>();
            _sp = services.BuildServiceProvider();

            _sp.GetRequiredService<TrayService>(); // создаёт значок в трее
            _sp.GetRequiredService<RustPlusDeviceRuntime>(); // фоновая обработка событий устройств (тревога Smart Alarm) не зависит от открытой вкладки
            var main = _sp.GetRequiredService<MainViewModel>();
            _window = new MainWindow { DataContext = main };
            var settings = _sp.GetRequiredService<ISettingsProvider>();
            _window.Closing += (_, ev) =>
            {
                if (_exiting) return;
                if (settings.Current.CloseToTray) { ev.Cancel = true; _window.Hide(); }
                else ExitApp();
            };
            _window.Show();

            await main.InitializeAsync(_cts.Token);
            if (recovered)
                MessageBox.Show("База данных была повреждена. Она сохранена рядом с пометкой .corrupt, создана новая.", "Rust Companion");

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (_, _) => { try { await main.TickAsync(); } catch (Exception ex) { serilog.Error(ex, "Tick failed"); } };
            _timer.Start();
        }
        catch (Exception ex)
        {
            serilog.Error(ex, "Startup failed");
            try { File.WriteAllText(Path.Combine(dataDir, "startup-error.txt"), ex.ToString()); } catch (Exception) { }
            ShowError("Не удалось запустить приложение", "Причина: " + ex.Message + "\n\nПолный текст сохранён в файле:\n" + Path.Combine(dataDir, "startup-error.txt"));
            ExitApp();
        }
    }

    /// <summary>Сообщение об ошибке поверх всех окон и с кнопкой в панели задач: модальное окно без владельца раньше могло спрятаться за другими окнами и выглядеть как «зависание».</summary>
    private static void ShowError(string title, string text)
    {
        using var owner = new System.Windows.Forms.Form { TopMost = true, ShowInTaskbar = false, StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen };
        owner.Show();
        System.Windows.Forms.MessageBox.Show(owner, text.Length > 1500 ? text[..1500] + "…" : text, "Rust Companion — " + title,
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _cts.Cancel();
        _timer?.Stop();
        _sp?.GetService<HotkeyManager>()?.Dispose();
        _sp?.GetService<TrayService>()?.Dispose();
        Shutdown();
    }
}
