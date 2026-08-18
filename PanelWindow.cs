using System.Drawing;
using System.Windows.Forms;

namespace GlassoutTouch;

/// <summary>
/// A borderless, always-on-top, position-locked window that renders one GlassOut panel and
/// forwards taps/drags to its CDP page. WS_EX_NOACTIVATE keeps MSFS focused when you tap the GTC.
/// </summary>
public sealed class PanelWindow : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int DragThresholdPx = 6;

    private readonly PictureBox _pic = new()
    {
        Dock = DockStyle.Fill,
        // Zoom letterboxes the frame (aspect-preserving), matching GlassOut's own viewer. StretchImage
        // filled the whole rect, so a placement whose aspect differed from the panel's native ratio
        // scaled the content up and clipped it. ToPage compensates for the letterbox margins.
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = Color.Black,
    };
    private CdpClient? _cdp;
    private readonly int _clickGapMs;

    private bool _down;
    private bool _dragging;
    private bool _wired;
    private Point _start;

    public PanelWindow(string title, int x, int y, int w, int h, int clickGapMs)
    {
        _clickGapMs = clickGapMs;

        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Bounds = new Rectangle(x, y, w, h);

        Controls.Add(_pic);
    }

    /// <summary>Wire up input once this panel's debugger connection is ready (after video is streaming).</summary>
    public void AttachCdp(CdpClient cdp)
    {
        _cdp = cdp;
        if (_wired) return;
        _wired = true;
        _pic.MouseDown += OnDown;
        _pic.MouseMove += OnMove;
        _pic.MouseUp += OnUp;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE; return cp; }
    }

    /// <summary>Decode + display a JPEG frame. Safe to call from any thread.</summary>
    public void ShowFrame(byte[] jpeg)
    {
        if (!IsHandleCreated) return;
        Bitmap bmp;
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var tmp = Image.FromStream(ms);
            bmp = new Bitmap(tmp); // independent copy so the stream can close
        }
        catch { return; }

        try
        {
            BeginInvoke(() =>
            {
                var old = _pic.Image;
                _pic.Image = bmp;
                old?.Dispose();
            });
        }
        catch { bmp.Dispose(); }
    }

    private (int x, int y) ToPage(Point p)
    {
        if (_cdp == null) return (0, 0);
        int cw = _pic.ClientSize.Width, ch = _pic.ClientSize.Height;
        if (cw <= 0 || ch <= 0) return (0, 0);

        // Zoom centers the frame and preserves its aspect ratio, so the image occupies only part of
        // the client area with letterbox bars on two sides. Map the tap through that displayed rect,
        // not the full client, or clicks land off-target. Frame aspect == page aspect (it's a capture
        // of the page), so displayed -> page is a straight linear scale.
        var img = _pic.Image;
        double iw = img?.Width ?? _cdp.PageW, ih = img?.Height ?? _cdp.PageH;
        double scale = Math.Min(cw / iw, ch / ih);
        double dw = iw * scale, dh = ih * scale;
        double ox = (cw - dw) / 2.0, oy = (ch - dh) / 2.0;

        // clamp into the displayed rect so taps on the bars resolve to the nearest edge
        double lx = Math.Clamp(p.X - ox, 0, dw), ly = Math.Clamp(p.Y - oy, 0, dh);
        int px = (int)Math.Round(lx / dw * _cdp.PageW);
        int py = (int)Math.Round(ly / dh * _cdp.PageH);
        return (px, py);
    }

    private void OnDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _down = true; _dragging = false; _start = e.Location;
    }

    private async void OnMove(object? sender, MouseEventArgs e)
    {
        if (!_down || _cdp == null) return;
        if (!_dragging &&
            (Math.Abs(e.X - _start.X) > DragThresholdPx || Math.Abs(e.Y - _start.Y) > DragThresholdPx))
        {
            _dragging = true;
            var (sx, sy) = ToPage(_start);
            await _cdp.BeginDrag(sx, sy);   // press + un-prime atomically so a mid-handler move can't reorder them
        }
        if (_dragging)
        {
            var (mx, my) = ToPage(e.Location);
            await _cdp.PointerMove(mx, my);
        }
    }

    private async void OnUp(object? sender, MouseEventArgs e)
    {
        if (!_down || _cdp == null) return;
        _down = false;
        if (_dragging)
        {
            var (ux, uy) = ToPage(e.Location);
            await _cdp.PointerUp(ux, uy);
            _dragging = false;
        }
        else
        {
            var (sx, sy) = ToPage(_start);
            await _cdp.Click(sx, sy, _clickGapMs);
        }
    }
}
