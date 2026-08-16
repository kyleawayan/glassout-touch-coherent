using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GlassoutTouch;

/// <summary>
/// One Chrome DevTools Protocol connection to a Coherent GT page inside MSFS
/// (ws://host:port/devtools/page/{id}). A native ClientWebSocket sends no Origin header, so the
/// debugger — which declines browser connections that include one — accepts it. Input is sent as a
/// mousedown+mouseup on the element under the point, the sequence WT G3000 touch-buttons respond to.
///
/// Every await uses ConfigureAwait(false) so continuations never marshal back to the WinForms UI
/// thread — the caller blocks on these with GetResult() during startup, which would otherwise
/// deadlock once a window (and its synchronization context) exists.
/// </summary>
public sealed class CdpClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _id;
    private CancellationToken _ct;

    // serial executor so down -> move -> up never reorder
    private readonly object _chainLock = new();
    private Task _tail = Task.CompletedTask;

    // coalesced drag: newest position wins, so a fast swipe never backs up a queue
    private readonly object _moveLock = new();
    private (int X, int Y)? _pendingMove;
    private bool _movePumpRunning;

    public int PageW { get; private set; } = 1280;
    public int PageH { get; private set; } = 768;

    public CdpClient(string host, int port, string pageId)
    {
        _uri = new Uri($"ws://{host}:{port}/devtools/page/{pageId}");
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ct = ct;
        await _ws.ConnectAsync(_uri, ct).ConfigureAwait(false);
        _ = Task.Run(() => ReceiveLoop(ct), ct);

        // don't hang forever if a page never answers the handshake
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(8));

        await SendAsync("Runtime.enable", new { }, handshake.Token).ConfigureAwait(false);

        var res = await SendAsync("Runtime.evaluate",
            new { expression = "JSON.stringify({w:window.innerWidth,h:window.innerHeight})", returnByValue = true },
            handshake.Token).ConfigureAwait(false);
        if (res.TryGetProperty("result", out var inner) && inner.TryGetProperty("value", out var val) &&
            val.GetString() is { } s)
        {
            try
            {
                using var d = JsonDocument.Parse(s);
                PageW = d.RootElement.GetProperty("w").GetInt32();
                PageH = d.RootElement.GetProperty("h").GetInt32();
            }
            catch { /* keep defaults */ }
        }
    }

    // ---- public input API (all ordered via the serial executor) ----

    public Task Click(int x, int y, int gapMs) => Enqueue(async () =>
    {
        await Evaluate(Dispatch("mousedown", 1, x, y)).ConfigureAwait(false);
        await Task.Delay(gapMs, _ct).ConfigureAwait(false);
        await Evaluate(Dispatch("mouseup", 0, x, y)).ConfigureAwait(false);
    });

    public Task PointerUp(int x, int y) => Enqueue(() => Evaluate(Dispatch("mouseup", 0, x, y)));

    // Drag start: press, then immediately un-prime the pressed touch-button. WT clears the blue
    // "primed" highlight only on mouseleave, which a synthetic drag never produces — so we send one
    // at the press point, leaving the drag itself to the parent scroll container. Both dispatches sit
    // in one queued step so a move dispatched mid-handler can't slip between them.
    public Task BeginDrag(int x, int y) => Enqueue(async () =>
    {
        await Evaluate(Dispatch("mousedown", 1, x, y)).ConfigureAwait(false);
        await Evaluate(LeaveDispatch(x, y)).ConfigureAwait(false);
    });

    private static string LeaveDispatch(int x, int y) =>
        "(function(){var el=document.elementFromPoint(" + x + "," + y + ");if(!el)return;" +
        "var b=el;while(b&&(!b.classList||!b.classList.contains('touch-button')))b=b.parentElement;" +
        "if(!b)return;" + // only un-prime an actual touch-button; leave touchpads/other drag targets untouched
        "b.dispatchEvent(new MouseEvent('mouseout',{bubbles:true,cancelable:true,view:window,clientX:" + x + ",clientY:" + y + "}));" +
        "b.dispatchEvent(new MouseEvent('mouseleave',{bubbles:false,cancelable:true,view:window,clientX:" + x + ",clientY:" + y + "}));})()";

    // Moves coalesce: while one mousemove round-trips, newer positions just overwrite the pending
    // target instead of queuing, so a fast drag tracks the finger live. Enqueued once so it still
    // runs after mousedown and before mouseup in the serial chain.
    public Task PointerMove(int x, int y)
    {
        lock (_moveLock)
        {
            _pendingMove = (x, y);
            if (_movePumpRunning) return Task.CompletedTask;
            _movePumpRunning = true;
        }
        return Enqueue(MovePump);
    }

    private async Task MovePump()
    {
        while (true)
        {
            (int X, int Y) m;
            lock (_moveLock)
            {
                if (_pendingMove is null) { _movePumpRunning = false; return; }
                m = _pendingMove.Value;
                _pendingMove = null;
            }
            await Evaluate(Dispatch("mousemove", 1, m.X, m.Y)).ConfigureAwait(false);
        }
    }

    // ---- internals ----

    private Task Enqueue(Func<Task> work)
    {
        lock (_chainLock)
        {
            var next = _tail.ContinueWith(async _ =>
            {
                try { await work().ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine("[cdp] " + ex.Message); }
            }, TaskScheduler.Default).Unwrap();
            _tail = next;
            return next;
        }
    }

    // ES5-only: this runs in Coherent GT, which targets an older ECMAScript baseline (no optional chaining, no Object.fromEntries).
    private static string Dispatch(string type, int buttons, int x, int y) =>
        "(function(){var el=document.elementFromPoint(" + x + "," + y + ");if(!el)return;var t='" + type + "';var ev;" +
        "if(t.indexOf('touch')===0){try{ev=new TouchEvent(t,{bubbles:true,cancelable:true});}catch(e){return;}}" +
        "else{ev=new MouseEvent(t,{bubbles:true,cancelable:true,view:window,clientX:" + x + ",clientY:" + y +
        ",button:0,buttons:" + buttons + "});}el.dispatchEvent(ev);})()";

    private Task Evaluate(string expr) =>
        SendAsync("Runtime.evaluate", new { expression = expr, returnByValue = true }, _ct);

    private async Task<JsonElement> SendAsync(string method, object @params, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.Serialize(new { id, method, @params });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
        await using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task.ConfigureAwait(false);
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

                if (res.MessageType != WebSocketMessageType.Text) continue;
                HandleText(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.Error.WriteLine("[cdp] receive error: " + ex.Message); }
    }

    private void HandleText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl)) return; // ignore CDP events
            if (!_pending.TryRemove(idEl.GetInt32(), out var tcs)) return;
            tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
        _sendLock.Dispose();
    }
}
