using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RustCompanion.Core;
using RustCompanion.Core.Data;
using RustCompanion.Core.Models;
using RustCompanion.Core.Notifications;
using RustCompanion.Core.Overlay;

namespace RustCompanion.App.Services;

/// <summary>
/// Оверлей: постоянная компактная панель (HUD) с выбранными таймерами/прогнозами + временные плашки (подтверждения горячих клавиш, уведомления).
/// Это обычное внешнее окно Windows (не внедрение в игру). Поверх эксклюзивного полноэкранного режима Direct3D не отображается — это диагностируется.
/// </summary>
public sealed class OverlayService : IOverlaySink
{
    private readonly ISettingsProvider _settings; private readonly SettingsRepository _repo; private readonly IClock _clock; private readonly Lazy<HudDataService> _hudLazy; private readonly ILogger<OverlayService> _log;
    // Ленивая зависимость: HudDataService -> BradleyService -> IEventNotifier -> NotificationRouter -> OverlayService образовали бы цикл при создании.
    private HudDataService _hud => _hudLazy.Value;
    private OverlayWindow? _win;
    private OverlayQueue _queue = new(3);
    private int _queueMax = 3;
    private DispatcherTimer? _timer, _saveTimer;
    private bool _needsPlace = true, _lastTopmostOkLogged = true;
    private int _lastMonitor = -2; private string _lastPlacementReason = "";
    private DateTimeOffset _monitorCheckedAt = DateTimeOffset.MinValue;
    public string? LastError { get; private set; }

    public OverlayService(ISettingsProvider settings, SettingsRepository repo, IClock clock, Lazy<HudDataService> hud, ILogger<OverlayService> log)
    { _settings = settings; _repo = repo; _clock = clock; _hudLazy = hud; _log = log; }

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _log.LogInformation("Оверлей: сервис запущен (HUD={Hud}, плашки={Toasts}, закреплён={Locked})", _settings.Current.OverlayHudVisible, _settings.Current.OverlayEnabled, _settings.Current.OverlayLocked);
    }

    public void Show(string eventId, string text)
    {
        var (title, body) = OverlayToastKinds.Split(eventId, text);
        ShowCore(eventId, text, title, body);
    }

    /// <summary>Уведомление с отдельным заголовком (из NotificationRouter): карточка показывает заголовок и текст раздельно.</summary>
    public void Show(string eventId, string title, string text) => ShowCore(eventId, OverlayToastKinds.ProductPrefix + $"{title}: {text}", title, text);

    private void ShowCore(string eventId, string text, string title, string body)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) { _log.LogWarning("Оверлей: приложение не готово (нет Dispatcher), плашка «{Event}» не показана", eventId); return; }
        _log.LogInformation("Оверлей: запрос показа «{Event}»", eventId);
        d.BeginInvoke(new Action(() =>
        {
            var s = _settings.Current;
            if (!s.OverlayEnabled && eventId != "test") { _log.LogWarning("Оверлей: плашки выключены в настройках, «{Event}» не показана", eventId); return; }
            if (s.OverlayMaxVisible != _queueMax) { _queueMax = s.OverlayMaxVisible; _queue = new OverlayQueue(_queueMax); }
            _queue.Add(text, _clock.UtcNow, TimeSpan.FromSeconds(s.OverlayDurationSeconds), eventId, title, body);
            Render();
            if (LastError is null) _log.LogInformation("Оверлей: плашка «{Event}» показана", eventId);
            else _log.LogError("Оверлей: плашка «{Event}» не показана: {Error}", eventId, LastError);
        }));
    }

    public void HideAll() => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { _queue.Clear(); Render(); }));
    public void ShowTest() => Show("test", "Rust Companion — тест оверлея: плашка поверх окон, без перехвата мыши и клавиатуры.");
    public void Invalidate() => _hud.Invalidate();

    /// <summary>Применить изменённые настройки (видимость, режим, позиция).</summary>
    public void Apply()
    {
        _needsPlace = true; _hud.Invalidate();
        Application.Current?.Dispatcher.BeginInvoke(new Action(Render));
    }

    public void ResetPosition()
    {
        var s = Clone(); s.OverlayLeft = null; s.OverlayTop = null; _settings.Update(s); _ = _repo.SaveAsync(s);
        Apply();
    }

    private void Tick()
    {
        try
        {
            if (_hud.NeedsRefresh && _settings.Current.OverlayHudVisible) _ = _hud.RefreshAsync();
            Render();
        }
        catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; _log.LogWarning("Оверлей: ошибка обновления: {Error}", LastError); }
    }

    private AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings.Current))!;

    private void Render()
    {
        var s = _settings.Current; var now = _clock.UtcNow;
        var toasts = s.OverlayEnabled || _queue.Visible(now).Count > 0 ? _queue.VisibleItems(now) : Array.Empty<OverlayToastData>();
        var editing = s.OverlayHudVisible && !s.OverlayLocked;
        IReadOnlyList<string> hud = Array.Empty<string>();
        if (s.OverlayHudVisible)
            hud = OverlayHud.ParseSelection(s.OverlayHudItems).Count == 0 ? new[] { "Выберите элементы оверлея на вкладке «Оверлей»" } : _hud.Lines();
        var visible = (s.OverlayHudVisible && (hud.Count > 0 || editing)) || toasts.Count > 0 || (_win?.HasExitAnimations ?? false);   // пока идёт анимация исчезновения, окно не скрывается
        if (!visible) { _win?.Hide(); return; }
        try
        {
            if (_win is null)
            {
                _win = new OverlayWindow();
                _win.BoundsCommitted += ScheduleSave;
                _needsPlace = true;
            }
            _win.SetContent(hud, toasts);
            if (!_win.IsVisible) { _win.Show(); _needsPlace = true; }   // ShowActivated=False: фокус игры не отбирается
            _win.ApplyLock(!editing);
            if (!editing && s.OverlayFollowRust) CheckRustMonitor();      // Rust перенесли на другой монитор — оверлей следует за ним
            if (_needsPlace) { PlaceWindow(); _needsPlace = false; }
            _win.ReassertTopmost();
            if (!_win.LastTopmostOk && _lastTopmostOkLogged) _log.LogWarning("Оверлей: SetWindowPos(TOPMOST) не удалась, код Windows {Code}", _win.LastTopmostError);
            _lastTopmostOkLogged = _win.LastTopmostOk;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            _log.LogError("Оверлей: не удалось создать/показать окно: {Error}", LastError);
        }
    }

    private void CheckRustMonitor()
    {
        var now = _clock.UtcNow;
        if (now - _monitorCheckedAt < TimeSpan.FromSeconds(1)) return;
        _monitorCheckedAt = now;
        var idx = OverlayTarget.PickMonitor(MonitorTools.GetMonitors(), MonitorTools.FindRustWindowRect());
        if (idx != _lastMonitor) { _lastMonitor = idx; _needsPlace = true; }
    }

    private void PlaceWindow()
    {
        if (_win is null) return;
        var s = _settings.Current; _win.UpdateLayout();
        var monitors = MonitorTools.GetMonitors();
        if (monitors.Count == 0)                                              // на случай сбоя Win32: прежний расчёт по рабочей области WPF
        {
            var wa0 = SystemParameters.WorkArea; var h0 = _win.ActualHeight > 0 ? _win.ActualHeight : 80;
            var virt = new RectD(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var r0 = OverlayLayout.Resolve(s.OverlayLeft, s.OverlayTop, s.OverlayWidth, h0, virt, new RectD(wa0.Left, wa0.Top, wa0.Width, wa0.Height), (OverlayCorner)s.OverlayCorner, s.OverlayMargin);
            _win.Place(r0.X, r0.Y, r0.Width); _lastPlacementReason = "Мониторы не определены — рабочая область WPF"; return;
        }
        var dpi = _win.CurrentDpi;
        var widthDip = OverlayLayout.ClampWidth(s.OverlayWidth);
        var heightPx = (_win.ActualHeight > 0 ? _win.ActualHeight : 80) * dpi.DpiScaleY;
        var rust = s.OverlayFollowRust ? MonitorTools.FindRustWindowRect() : null;
        var r = OverlayTarget.Resolve(s.OverlayLeft, s.OverlayTop, widthDip * dpi.DpiScaleX, heightPx, monitors, rust, (OverlayCorner)s.OverlayCorner, s.OverlayMargin * dpi.DpiScaleX);
        _win.MovePx((int)Math.Round(r.X), (int)Math.Round(r.Y), widthDip);
        _lastMonitor = s.OverlayFollowRust ? OverlayTarget.PickMonitor(monitors, rust) : _lastMonitor;
        if (r.Reason != _lastPlacementReason) _log.LogInformation("Оверлей: {Reason} (монитор {Monitor} из {Count})", r.Reason, r.MonitorIndex + 1, monitors.Count);
        _lastPlacementReason = r.Reason;
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Stop(); _saveTimer.Tick -= SaveBounds; _saveTimer.Tick += SaveBounds; _saveTimer.Start();
    }

    private void SaveBounds(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        if (_win is null) return;
        var s = Clone(); var pos = _win.GetPositionPx();
        if (pos is { } p) { s.OverlayLeft = p.X; s.OverlayTop = p.Y; }   // пиксели Windows (как у мониторов)
        s.OverlayWidth = OverlayLayout.ClampWidth(_win.Width);
        _settings.Update(s); _ = _repo.SaveAsync(s);
    }

    /// <summary>Отчёт для пользователя: что включено, что получилось создать/показать, режим экрана, Rust, DPI. Секретов не содержит.</summary>
    public IReadOnlyList<string> Diagnose()
    {
        var s = _settings.Current; var lines = new List<string>();
        lines.Add($"HUD включён: {(s.OverlayHudVisible ? "да" : "нет")}; плашки разрешены: {(s.OverlayEnabled ? "да" : "нет")}; режим: {(s.OverlayLocked ? "закреплён (клики проходят сквозь)" : "перемещение")}");
        lines.Add($"Выбрано элементов HUD: {OverlayHud.ParseSelection(s.OverlayHudItems).Count}; сервер для отметок: {(s.HotkeyServerId > 0 ? "выбран" : "НЕ выбран")}");
        if (_win is null) lines.Add("Окно оверлея: ещё не создавалось (оно создаётся, когда есть что показать — включите HUD или нажмите «Тестовая плашка»).");
        else
        {
            var dpi = _win.CurrentDpi;
            lines.Add($"Окно создано: да; видимо: {(_win.IsVisible ? "да" : "нет")}; дескриптор: 0x{_win.Handle.ToInt64():X}");
            var ex = _win.ExStyle;
            lines.Add($"Стили окна: NOACTIVATE={(ex & OverlayWindowStyles.WS_EX_NOACTIVATE) != 0}, TOOLWINDOW={(ex & OverlayWindowStyles.WS_EX_TOOLWINDOW) != 0}, TRANSPARENT(клики сквозь)={(ex & OverlayWindowStyles.WS_EX_TRANSPARENT) != 0}");
            lines.Add($"Положение: {_win.Left:0}, {_win.Top:0}; размер: {_win.ActualWidth:0}×{_win.ActualHeight:0}; DPI-масштаб: {dpi.DpiScaleX:P0}");
            lines.Add($"Topmost (SetWindowPos): {(_win.LastTopmostOk ? "успешно" : "ошибка Windows " + _win.LastTopmostError)}");
        }
        var mons = MonitorTools.GetMonitors(); var rustRect = MonitorTools.FindRustWindowRect(); var rustIdx = OverlayTarget.PickMonitor(mons, rustRect);
        lines.Add($"Следовать за монитором Rust: {(s.OverlayFollowRust ? "да" : "нет")}; окно Rust: {(rustRect is null ? "не найдено или свёрнуто" : $"на мониторе {rustIdx + 1} из {mons.Count}")}");
        if (_lastPlacementReason.Length > 0) lines.Add("Размещение: " + _lastPlacementReason);
        lines.Add($"Мониторов: {System.Windows.Forms.Screen.AllScreens.Length}; виртуальный экран: {SystemParameters.VirtualScreenWidth:0}×{SystemParameters.VirtualScreenHeight:0}");
        if (LastError is not null) lines.Add("Последняя ошибка: " + LastError);
        try
        {
            var rust = Process.GetProcessesByName("RustClient");
            lines.Add(rust.Length > 0 ? "Rust (RustClient.exe) запущен." : "Rust (RustClient.exe) не найден среди запущенных процессов.");
            var fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && Win32.GetWindowThreadProcessId(fg, out var pid) != 0)
                lines.Add("Активное окно принадлежит процессу: " + SafeProcessName((int)pid));
        }
        catch (Exception ex) { lines.Add("Список процессов недоступен: " + ex.GetType().Name); }
        if (Win32.SHQueryUserNotificationState(out var quns) == 0)
        {
            var (state, advice) = OverlayDiagnostics.FromNotificationState(quns);
            lines.Add($"Режим экрана (Windows): {state}. {advice}");
        }
        else lines.Add("Режим экрана определить не удалось.");
        lines.Add("Рекомендация: запускайте Rust в режиме Borderless/Windowed; в эксклюзивном полноэкранном режиме обычное окно поверх игры не отображается.");
        foreach (var l in lines) _log.LogInformation("Диагностика оверлея: {Line}", l);
        return lines;
    }

    private static string SafeProcessName(int pid) { try { return Process.GetProcessById(pid).ProcessName; } catch (Exception) { return "недоступно"; } }
}
