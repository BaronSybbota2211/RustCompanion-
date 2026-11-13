using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RustCompanion.Core.Overlay;

namespace RustCompanion.App.Services;

/// <summary>Мониторы и окно клиента Rust в одной системе координат (пиксели Windows). Только чтение положения окон — без доступа к памяти игры.</summary>
internal static class MonitorTools
{
    private static RectD ToRect(Win32.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static IReadOnlyList<MonitorRect> GetMonitors()
    {
        var list = new List<MonitorRect>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data) =>
        {
            var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(hMon, ref mi)) list.Add(new MonitorRect(ToRect(mi.rcMonitor), ToRect(mi.rcWork), (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Прямоугольник видимого окна RustClient или null (игра не запущена, окно ещё не создано или свёрнуто).</summary>
    public static RectD? FindRustWindowRect()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("RustClient"); } catch (Exception) { return null; }
        try
        {
            foreach (var p in procs)
            {
                IntPtr h;
                try { h = p.MainWindowHandle; } catch (Exception) { continue; }
                if (h == IntPtr.Zero || !Win32.IsWindowVisible(h) || Win32.IsIconic(h)) continue;
                if (Win32.GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top) return ToRect(r);
            }
            return null;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
