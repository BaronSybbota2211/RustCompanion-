using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using RustCompanion.Core.Hotkeys;

namespace RustCompanion.App.Services;

public sealed record HotkeyRegistration(HotkeyEvent Event, HotkeyGesture Gesture, bool Success, int ErrorCode, string Message);

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey (сообщение WM_HOTKEY приходит в скрытое message-only окно). Права администратора не нужны.
/// MOD_NOREPEAT отключает автоповтор при удержании. Ввод в игре не перехватывается: регистрируется только заданное сочетание.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyEvent> _ids = new();
    public event Action<HotkeyEvent>? HotkeyPressed;

    private void EnsureWindow()
    {
        if (_source is not null) return;
        var p = new HwndSourceParameters("RustCompanionHotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var ev)) { handled = true; HotkeyPressed?.Invoke(ev); }
        return IntPtr.Zero;
    }

    public IReadOnlyList<HotkeyRegistration> Apply(IReadOnlyDictionary<HotkeyEvent, HotkeyGesture> assignments)
    {
        EnsureWindow();
        UnregisterAll();
        var results = new List<HotkeyRegistration>(); var id = 1;
        foreach (var (ev, g) in assignments)
        {
            var err = g.Validate();
            if (err is not null) { results.Add(new HotkeyRegistration(ev, g, false, 0, err)); continue; }
            var myId = id++;
            if (Win32.RegisterHotKey(_source!.Handle, myId, (int)g.Mods | Win32.MOD_NOREPEAT, g.VirtualKey))
            { _ids[myId] = ev; results.Add(new HotkeyRegistration(ev, g, true, 0, "Зарегистрировано")); }
            else
            {
                var code = Marshal.GetLastWin32Error();
                results.Add(new HotkeyRegistration(ev, g, false, code, code == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "Сочетание уже занято другим приложением или Windows. Выберите другое или используйте кнопку «Отметить»."
                    : $"Windows отказала в регистрации (код {code}). Выберите другое сочетание или используйте кнопку «Отметить»."));
            }
        }
        return results;
    }

    private void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _ids.Keys) Win32.UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc); _source?.Dispose(); _source = null;
    }
}

/// <summary>Присоединённое поведение: TextBox ловит нажатое сочетание и передаёт его команде (для назначения горячих клавиш).</summary>
public static class HotkeyCapture
{
    public static readonly System.Windows.DependencyProperty CommandProperty =
        System.Windows.DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(HotkeyCapture), new System.Windows.PropertyMetadata(null, OnChanged));
    public static ICommand? GetCommand(System.Windows.DependencyObject o) => (ICommand?)o.GetValue(CommandProperty);
    public static void SetCommand(System.Windows.DependencyObject o, ICommand? v) => o.SetValue(CommandProperty, v);

    private static void OnChanged(System.Windows.DependencyObject d, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.TextBox tb) return;
        tb.PreviewKeyDown -= OnKeyDown;
        if (e.NewValue is not null) { tb.IsReadOnly = true; tb.PreviewKeyDown += OnKeyDown; }
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        var tb = (System.Windows.Controls.TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None or Key.DeadCharProcessed) { e.Handled = true; return; }
        var mods = HotkeyMods.None; var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) mods |= HotkeyMods.Control;
        if (m.HasFlag(ModifierKeys.Alt)) mods |= HotkeyMods.Alt;
        if (m.HasFlag(ModifierKeys.Shift)) mods |= HotkeyMods.Shift;
        if (m.HasFlag(ModifierKeys.Windows)) mods |= HotkeyMods.Win;
        var gesture = new HotkeyGesture(mods, KeyInterop.VirtualKeyFromKey(key));
        e.Handled = true;
        var cmd = GetCommand(tb);
        if (cmd?.CanExecute(gesture) == true) cmd.Execute(gesture);
    }
}

public sealed class WpfClipboardService : RustCompanion.Core.Watch.IClipboardService
{
    public void SetText(string text) => System.Windows.Clipboard.SetText(text);
}
