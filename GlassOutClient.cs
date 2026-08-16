using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GlassoutTouch;

public sealed class GlassOutPanel
{
    public string Id = "";
    public string Name = "";
    public int Width;
    public int Height;
}

/// <summary>
/// One viewer connection to the GlassOut engine (ws://host:port/ws). Reads the panel list and
/// streams per-panel JPEG frames. connectionType=viewer, so it never keeps the engine alive.
///
/// Every await uses ConfigureAwait(false): the caller blocks on ConnectAsync/SubscribeAsync with
/// GetResult() on the UI thread, and SubscribeAsync in particular runs after the windows (and their
/// synchronization context) exist — marshaling a continuation back to that blocked thread deadlocks.
/// </summary>
public sealed class GlassOutClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Raised (on a background thread) whenever the engine sends a panel list.</summary>
    public event Action<List<GlassOutPanel>>? PanelsReceived;
    /// <summary>Raised (on a background thread) for each binary panel frame: (panelId, rawJpegBytes).</summary>
    public event Action<string, byte[]>? FrameReceived;

    public GlassOutClient(string host, int port)
    {
        _uri = new Uri($"ws://{host}:{port}/ws?name=glassout-touch&appKey=glassout-touch&connectionType=viewer");
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _ws.ConnectAsync(_uri, ct).ConfigureAwait(false);
        _ = Task.Run(() => ReceiveLoop(ct), ct);
    }

    public async Task SubscribeAsync(string panelId, int fps, CancellationToken ct)
    {
        await SendTextAsync(JsonSerializer.Serialize(new { type = "subscribe.panel", panelId, targetFps = fps }), ct)
            .ConfigureAwait(false);
    }

    private async Task SendTextAsync(string s, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                if (res.MessageType == WebSocketMessageType.Text)
                    HandleText(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                else
                    HandleBinary(ms.GetBuffer(), (int)ms.Length);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine("[glassout] receive error: " + ex.Message); }
    }

    private void HandleText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return;
            var type = t.GetString();
            if (type is not ("welcome" or "panels")) return;
            if (!root.TryGetProperty("panels", out var panels) || panels.ValueKind != JsonValueKind.Array) return;

            var list = new List<GlassOutPanel>();
            foreach (var p in panels.EnumerateArray())
            {
                list.Add(new GlassOutPanel
                {
                    Id = p.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Width = p.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                    Height = p.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                });
            }
            PanelsReceived?.Invoke(list);
        }
        catch { /* ignore malformed */ }
    }

    private void HandleBinary(byte[] data, int len)
    {
        // header: u8 idLen | idLen bytes panelId | u32 frameNumber | u16 w | u16 h | u64 ts | JPEG
        if (len < 1) return;
        int idLen = data[0];
        int jpegOffset = 1 + idLen + 16;
        if (len <= jpegOffset) return;
        string panelId = Encoding.UTF8.GetString(data, 1, idLen);
        int jpegLen = len - jpegOffset;
        var jpeg = new byte[jpegLen];
        Buffer.BlockCopy(data, jpegOffset, jpeg, 0, jpegLen);
        FrameReceived?.Invoke(panelId, jpeg);
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
