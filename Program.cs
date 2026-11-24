using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PcDash;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool startInTray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        using var mutex = new Mutex(true, @"Local\PcDash-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("PC Dash is already running (check the tray).", "PC Dash");
            return;
        }

        var settings = AppSettings.Load();
        var stats = new StatsSampler();
        var claude = new ClaudeUsageService();
        claude.StartBackgroundRefresh();
        var state = new ServerState(settings);
        var config = new ConfigStore();
        var weather = new WeatherService(config);

        WebApplication? app = null;
        Exception? lastError = null;
        foreach (int port in Enumerable.Range(settings.Port, 10))
        {
            try
            {
                app = BuildServer(port, stats, claude, state, config, weather);
                app.StartAsync().GetAwaiter().GetResult();
                state.Port = port;
                break;
            }
            catch (Exception ex) { lastError = ex; app = null; }
        }
        if (app == null)
        {
            MessageBox.Show("Couldn't start the web server:\n" + lastError?.Message, "PC Dash", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new MainForm(state, startInTray));

        app.StopAsync().GetAwaiter().GetResult();
        stats.Dispose();
    }

    static bool IsLocal(HttpContext c) => IPAddress.IsLoopback(c.Connection.RemoteIpAddress ?? IPAddress.None);
    static readonly IResult PcOnly = Results.Text("The setup page only works on the PC itself.", "text/plain", statusCode: 403);

    static string DefaultEmoji(DeckButton b) => b.Type switch
    {
        "hotkey" => b.Target.Trim().ToLowerInvariant() switch
        {
            "playpause" => "⏯️", "next" => "⏭️", "prev" or "previous" => "⏮️", "stop" => "⏹️",
            "volup" => "🔊", "voldown" => "🔉", "mute" => "🔇", "win+shift+s" => "✂️", "win+d" => "🖥️",
            _ => "⌨️",
        },
        "system" => b.Target switch { "lock" => "🔒", "sleep" => "🌙", "monitoroff" => "🖥️", "showdesktop" => "🗔", _ => "⚙️" },
        "text" => "📝",
        "command" => "▶️",
        "url" => "🌐",
        "path" => "📁",
        _ => "",
    };

    static WebApplication BuildServer(int port, StatsSampler stats, ClaudeUsageService claude, ServerState state, ConfigStore config, WeatherService weather)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Any, port));
        var app = builder.Build();

        // Every request except the keep-awake script needs the access key from the QR code.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path != "/NoSleep.min.js" && ctx.Request.Path != "/icon.png")
            {
                var k = ctx.Request.Query["k"].ToString();
                if (!state.KeyMatches(k))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync("Access key missing or wrong. Scan the QR code shown by PC Dash on the PC.");
                    return;
                }
                if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback))
                    state.LastRemoteHit = (DateTime.Now, ctx.Connection.RemoteIpAddress!.MapToIPv4().ToString());
            }
            ctx.Response.Headers.CacheControl = "no-store";
            await next();
        });

        app.MapGet("/", () => Embedded("index.html", "text/html; charset=utf-8"));
        app.MapGet("/NoSleep.min.js", () => Embedded("NoSleep.min.js", "text/javascript; charset=utf-8"));
        app.MapGet("/api/stats", () => stats.Snapshot is { } s ? Results.Json(s) : Results.StatusCode(503));
        app.MapGet("/icon.png", () => Results.Bytes(AppIcon.Png, "image/png"));
        app.MapGet("/manifest.webmanifest", () => Results.Json(new
        {
            name = "PC Dash",
            short_name = "PC Dash",
            start_url = "/?k=" + state.Settings.Key,
            scope = "/",
            display = "fullscreen",
            orientation = "landscape",
            background_color = "#0f1116",
            theme_color = "#0f1116",
            icons = new[] { new { src = "/icon.png", sizes = "180x180", type = "image/png" } },
        }, contentType: "application/manifest+json"));
        app.MapGet("/api/claude",async () => Results.Text((await claude.GetAsync()).ToJsonString(), "application/json"));

        // ---- deck, as the phone sees it: labels and icons only, never paths or commands ----
        app.MapGet("/api/deck", () =>
        {
            var d = config.Current.Deck;
            return Results.Json(new
            {
                version = config.Version,
                columns = d.Columns,
                buttons = d.Buttons.Select(b => new
                {
                    id = b.Id,
                    label = b.Label,
                    color = b.Color,
                    emoji = b.Icon == "emoji" && !string.IsNullOrWhiteSpace(b.Emoji) ? b.Emoji : null,
                    image = b.Icon != "emoji" && DeckIcons.HasAuto(b) ? DeckIcons.CacheKey(b) : null,
                    fallback = DefaultEmoji(b),
                }),
            });
        });
        app.MapPost("/api/deck/press/{id}", (string id) =>
        {
            var b = config.Current.Deck.Buttons.FirstOrDefault(x => x.Id == id);
            if (b == null) return Results.Json(new { ok = false, error = "Button not found (deck changed?)" }, statusCode: 404);
            try { DeckActions.Run(b); return Results.Json(new { ok = true }); }
            catch (Exception ex) { return Results.Json(new { ok = false, error = ex.Message }, statusCode: 500); }
        });
        app.MapGet("/api/deck/icon/{id}", async (string id, HttpContext ctx) =>
        {
            var b = config.Current.Deck.Buttons.FirstOrDefault(x => x.Id == id);
            var png = b == null ? null : await DeckIcons.GetAsync(b);
            if (png == null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=604800"; // URL carries a content hash
            return Results.Bytes(png, "image/png");
        });

        // ---- weather ----
        app.MapGet("/api/weather", async () => Results.Text((await weather.GetAsync()).ToJsonString(), "application/json"));

        // ---- setup page and its API: only from the PC itself ----
        app.MapGet("/setup", (HttpContext c) => IsLocal(c) ? Embedded("setup.html", "text/html; charset=utf-8") : PcOnly);
        app.MapGet("/api/config", (HttpContext c) => IsLocal(c) ? Results.Json(config.Current) : PcOnly);
        app.MapPut("/api/config", async (HttpContext c) =>
        {
            if (!IsLocal(c)) return PcOnly;
            var cfg = await c.Request.ReadFromJsonAsync<DashConfig>();
            if (cfg == null) return Results.BadRequest();
            config.Save(cfg);
            return Results.Json(new { ok = true, version = config.Version });
        });
        app.MapGet("/api/setup/apps", async (HttpContext c) => IsLocal(c) ? Results.Json(await StartApps.GetAsync()) : PcOnly);
        app.MapGet("/api/setup/browsers", (HttpContext c) => IsLocal(c) ? Results.Json(Browsers.Installed()) : PcOnly);
        app.MapPost("/api/setup/pick/{kind}", async (string kind, HttpContext c) =>
            IsLocal(c) ? Results.Json(new { path = await PathPicker.PickAsync(kind) }) : PcOnly);
        app.MapGet("/api/setup/checkkeys", (string spec, HttpContext c) =>
            IsLocal(c) ? Results.Json(new { ok = Keys.IsValid(spec) }) : PcOnly);
        app.MapGet("/api/setup/geocode", async (string q, HttpContext c) =>
        {
            if (!IsLocal(c)) return PcOnly;
            try { return Results.Text((await WeatherService.GeocodeAsync(q)).ToJsonString(), "application/json"); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });
        return app;
    }

    static IResult Embedded(string name, string contentType)
    {
        var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("web/" + name);
        return s == null ? Results.NotFound() : Results.Stream(s, contentType);
    }
}

/// <summary>"Start with Windows": a per-user Run entry (no admin needed), launching quietly in the tray.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PcDash";
    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(ValueName, Command);
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Home-screen icon, drawn once at startup.</summary>
public static class AppIcon
{
    public static readonly byte[] Png = Draw(180);

    static byte[] Draw(int s)
    {
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(15, 17, 22));
            using var accent = new SolidBrush(Color.FromArgb(217, 119, 87));
            using var white = new SolidBrush(Color.FromArgb(232, 234, 240));
            float w = s * 0.14f, gap = s * 0.07f, x0 = (s - (3 * w + 2 * gap)) / 2, bottom = s * 0.76f;
            float[] heights = [0.26f, 0.42f, 0.56f];
            for (int i = 0; i < 3; i++)
            {
                float h = s * heights[i];
                g.FillRectangle(i == 2 ? accent : white, x0 + i * (w + gap), bottom - h, w, h);
            }
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}

public sealed class ServerState(AppSettings settings)
{
    public AppSettings Settings { get; } = settings;
    public int Port { get; set; }
    public (DateTime time, string ip)? LastRemoteHit { get; set; }

    public bool KeyMatches(string k) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(Settings.Key));

    public string UrlFor(string host) => $"http://{host}:{Port}/?k={Settings.Key}";
}

public sealed class AppSettings
{
    public string Key { get; set; } = NewKey();
    public int Port { get; set; } = 8765;
    public string? PreferredIp { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcDash");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null && !string.IsNullOrWhiteSpace(s.Key)) return s;
            }
        }
        catch { }
        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public static class LanAddresses
{
    public record Candidate(string Ip, string Adapter, int Score)
    {
        public override string ToString() => $"{Ip}   ({Adapter})";
    }

    static readonly string[] VirtualHints =
        ["virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vpn", "tap-", "tunnel", "wireguard", "zerotier", "tailscale", "loopback", "docker", "wsl", "fortinet", "cisco anyconnect"];

    /// <summary>All IPv4 addresses, best guess for "the real LAN address" first.</summary>
    public static List<Candidate> Get()
    {
        var list = new List<Candidate>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            string text = (ni.Name + " " + ni.Description).ToLowerInvariant();
            bool isVirtual = VirtualHints.Any(text.Contains);
            bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;
                int score = 0;
                if (!isVirtual) score += 100;
                if (hasGateway) score += 50;
                if (ip.StartsWith("192.168.")) score += 30;
                else if (ip.StartsWith("10.")) score += 10;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet) score += 10;
                list.Add(new Candidate(ip, ni.Name, score));
            }
        }
        return list.OrderByDescending(c => c.Score).ToList();
    }
}
