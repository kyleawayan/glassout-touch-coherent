using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GlassoutTouch;

/// <summary>
/// Translates GlassOut profile coordinates (scaled DIP units, matching Electron's per-monitor DIP
/// space) into physical pixels for WinForms placement. GlassOut records each monitor's DIP bounds in
/// the profile's `screens` block (e.g. a 4K panel at 150% shows up as 2560x1440); we pair those to
/// the real monitors by scaled size and map each placement into the matching monitor's physical space.
/// </summary>
public sealed class ScreenMapper
{
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmon, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private readonly record struct Mon(Rectangle Bounds, double Scale);
    private readonly List<(ScreenInfo Dip, Mon Mon)> _pairs = new();

    public bool Ready => _pairs.Count > 0;

    public ScreenMapper(List<ScreenInfo> dipScreens)
    {
        if (dipScreens.Count == 0) return;

        var mons = new List<Mon>();
        foreach (var s in Screen.AllScreens)
            mons.Add(new Mon(s.Bounds, ScaleOf(s.Bounds)));

        // pair each GlassOut DIP screen to the monitor whose implied DIP size (physical / scale)
        // is closest — this also tells us that monitor's scale factor.
        var used = new HashSet<int>();
        foreach (var ds in dipScreens)
        {
            int best = -1; double bestErr = double.MaxValue;
            for (int i = 0; i < mons.Count; i++)
            {
                if (used.Contains(i)) continue;
                double dw = mons[i].Bounds.Width / mons[i].Scale, dh = mons[i].Bounds.Height / mons[i].Scale;
                double err = Math.Abs(dw - ds.Width) + Math.Abs(dh - ds.Height);
                if (err < bestErr) { bestErr = err; best = i; }
            }
            if (best >= 0) { used.Add(best); _pairs.Add((ds, mons[best])); }
        }
    }

    private static double ScaleOf(Rectangle bounds)
    {
        try
        {
            var c = new POINT { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            if (GetDpiForMonitor(MonitorFromPoint(c, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>Map a DIP rect to physical pixels. Returns false (rect unchanged) if it lands outside every known screen.</summary>
    public bool TryMap(int x, int y, int w, int h, out Rectangle phys)
    {
        phys = new Rectangle(x, y, w, h);
        if (!Ready) return false;

        double cx = x + w / 2.0, cy = y + h / 2.0;
        foreach (var (ds, mon) in _pairs)
        {
            if (cx >= ds.X && cx < ds.X + ds.Width && cy >= ds.Y && cy < ds.Y + ds.Height)
            {
                phys = new Rectangle(
                    (int)Math.Round(mon.Bounds.X + (x - ds.X) * mon.Scale),
                    (int)Math.Round(mon.Bounds.Y + (y - ds.Y) * mon.Scale),
                    (int)Math.Round(w * mon.Scale),
                    (int)Math.Round(h * mon.Scale));
                return true;
            }
        }
        return false;
    }

    public string Describe() =>
        _pairs.Count == 0 ? "(no screen info — using raw coordinates)"
        : string.Join("; ", _pairs.Select(p => $"{p.Dip.Label ?? "?"}[{p.Dip.Width}x{p.Dip.Height} dip] -> {p.Mon.Bounds.Width}x{p.Mon.Bounds.Height} @{p.Mon.Scale:0.##}x"));
}
