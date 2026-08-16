using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace GlassoutTouch;

internal static class Program
{
    private const int ClickGapMs = 40;
    private const int SubscribeFps = 30;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("glassout-touch — borderless, click-fixed GlassOut pop-outs for MSFS.");
            Console.WriteLine("Usage: glassout-touch <profile.json | profileName> [--host 127.0.0.1] [--glassout-port 8787] [--cdp-port 19999]");
            Console.WriteLine("Start the GlassOut app first (it runs the engine and lends its license), then run this.");
            return;
        }

        // one instance only — two would fight over the one-per-page debugger sockets
        using var single = new Mutex(true, @"Local\glassout-touch-single-instance", out bool isNew);
        if (!isNew)
        {
            Console.Error.WriteLine("glassout-touch is already running — close that instance first (only one can drive the debugger).");
            Environment.Exit(1);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine("Fatal: " + ex.Message); Environment.Exit(1); }
    }

    private static void Run(string[] args)
    {
        string host = GetOpt(args, "--host") ?? "127.0.0.1";
        int goPort = int.Parse(GetOpt(args, "--glassout-port") ?? "8787");
        int cdpPort = int.Parse(GetOpt(args, "--cdp-port") ?? "19999");

        string profilePath = ResolveProfile(args[0]);
        var profile = Profile.Load(profilePath);
        Console.WriteLine($"Profile: {profile.Name}  ({profilePath})");

        var placements = (profile.Layout?.Placements ?? new())
            .Where(p => p.Type == "panel" && !string.IsNullOrEmpty(p.SourceId))
            .ToList();
        if (placements.Count == 0) { Console.WriteLine("No 'panel' placements in this profile."); return; }

        // clamp to a sane range: negatives would make Task.Delay hang/throw; the debugger path doesn't need a big hover delay
        int clickGap = profile.Settings?.TouchClickDelayMs is { } d && d >= 0 && d < 400 ? d : ClickGapMs;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            // hard self-kill — a graceful WinForms exit can deadlock if the UI thread is mid-blocking-call
            try { Process.GetCurrentProcess().Kill(); } catch { Environment.Exit(0); }
        };
        var ct = cts.Token;

        // needs a running GlassOut engine — start the GlassOut app first (it also lends its license)
        if (!IsEngineUp(host, goPort))
        {
            Console.Error.WriteLine($"No GlassOut engine on :{goPort}. Start the GlassOut app first, then run this.");
            Environment.Exit(1);
        }

        // --- all network setup runs synchronously before any UI is created (keeps WinForms on the STA thread) ---
        var pageMap = LoadPageMap(host, cdpPort, ct).GetAwaiter().GetResult();
        Console.WriteLine($"Debugger pages: {string.Join(", ", pageMap.Select(kv => $"{kv.Key}->{kv.Value}"))}");

        var go = new GlassOutClient(host, goPort);
        var panelsReady = new TaskCompletionSource<List<GlassOutPanel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        go.PanelsReceived += list => panelsReady.TrySetResult(list);
        go.ConnectAsync(ct).GetAwaiter().GetResult();
        // wait for the engine's panel list (confirms the connection is live) before subscribing
        panelsReady.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).GetAwaiter().GetResult();

        var cdps = new List<CdpClient>();

        // The engine identifies each panel by injecting a marker over the SAME debugger page we use
        // for clicks, and Coherent allows one client per page. If we grabbed the page first, the
        // engine could never inject that panel (no video). So: start VIDEO first, wait until each
        // panel is actually streaming (marker injected), and only THEN take the page for input.
        var mapper = new ScreenMapper(profile.Layout?.Screens ?? new());
        Console.WriteLine("Screen map: " + mapper.Describe());

        var windows = new Dictionary<string, PanelWindow>();
        foreach (var pl in placements)
        {
            var srcId = pl.SourceId!;
            var rect = new Rectangle(pl.X, pl.Y, pl.Width, pl.Height);
            var scaled = mapper.TryMap(pl.X, pl.Y, pl.Width, pl.Height, out var phys);
            if (scaled) rect = phys;
            windows[srcId] = new PanelWindow(srcId, rect.X, rect.Y, rect.Width, rect.Height, clickGap);
            Console.WriteLine($"  + {srcId}  {(scaled ? $"dip({pl.X},{pl.Y} {pl.Width}x{pl.Height}) -> " : "")}@ ({rect.X},{rect.Y}) {rect.Width}x{rect.Height}");
        }

        var streaming = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        go.FrameReceived += (panelId, jpeg) =>
        {
            streaming[panelId] = true;
            if (windows.TryGetValue(panelId, out var w)) w.ShowFrame(jpeg);
        };

        foreach (var w in windows.Values) w.Show();
        foreach (var srcId in windows.Keys) go.SubscribeAsync(srcId, SubscribeFps, ct).GetAwaiter().GetResult();

        // wait for the panels that will get input to start streaming (engine has injected their marker)
        var inputPanels = placements.Where(p => pageMap.ContainsKey(p.SourceId!)).Select(p => p.SourceId!).ToList();
        if (inputPanels.Count > 0)
        {
            Console.WriteLine("Waiting for video before taking the debugger for input...");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && inputPanels.Any(id => !streaming.ContainsKey(id)))
                Thread.Sleep(250);
        }

        // now take the debugger per panel and wire input
        foreach (var pl in placements)
        {
            var srcId = pl.SourceId!;
            if (!pageMap.TryGetValue(srcId, out var pageId)) { Console.WriteLine($"  · {srcId}: no debugger page — view only"); continue; }
            if (!streaming.ContainsKey(srcId)) Console.WriteLine($"  ! {srcId}: not streaming yet — taking the debugger may block its video");

            CdpClient? cdp = null;
            for (int attempt = 1; attempt <= 4 && !ct.IsCancellationRequested; attempt++)
            {
                try { var c = new CdpClient(host, cdpPort, pageId); c.ConnectAsync(ct).GetAwaiter().GetResult(); cdp = c; break; }
                catch (Exception ex)
                {
                    if (attempt == 4) Console.WriteLine($"  ! {srcId}: debugger connect failed ({ex.Message}) — view only");
                    else Thread.Sleep(700);
                }
            }
            if (cdp != null && windows.TryGetValue(srcId, out var win)) { cdps.Add(cdp); win.AttachCdp(cdp); Console.WriteLine($"  · {srcId}: input ready"); }
        }

        Console.WriteLine($"Running {windows.Count} window(s). Ctrl+C to quit.");
        Application.Run();

        go.Dispose();
        foreach (var c in cdps) c.Dispose();
    }

    /// <summary>Map GlassOut panel id (== profile sourceId) -> debugger page id, via normalized page title.</summary>
    private static async Task<Dictionary<string, string>> LoadPageMap(string host, int cdpPort, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var json = await http.GetStringAsync($"http://{host}:{cdpPort}/pagelist.json", ct);
        var map = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;
            var id = p.TryGetProperty("id", out var i) ? i.GetRawText() : null; // id may be number
            if (title == null || id == null) continue;
            var key = NormalizeTitle(title);
            if (key.Length > 0) map[key] = id.Trim('"');
        }
        return map;
    }

    /// <summary>"VCockpit04 - WTG3000_GTC_2" -> "wtg3000-gtc-2" (matches the GlassOut panel id).</summary>
    private static string NormalizeTitle(string title)
    {
        var last = title.Contains(" - ") ? title[(title.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..] : title;
        return last.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
    }

    private static string ResolveProfile(string arg)
    {
        if (File.Exists(arg)) return Path.GetFullPath(arg);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassOut", "profiles");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var p = Profile.Load(file);
                    if (string.Equals(p.Name, arg, StringComparison.OrdinalIgnoreCase)) return file;
                }
                catch { /* skip unreadable */ }
            }
        }
        throw new FileNotFoundException($"No profile file or profile named '{arg}' found (looked in {dir}).");
    }

    private static bool IsEngineUp(string host, int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync($"http://{host}:{port}/status").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string? GetOpt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
