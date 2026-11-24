using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PcDash;

/// <summary>Runs what a deck button is configured to do.</summary>
public static class DeckActions
{
    public static void Run(DeckButton b)
    {
        // Let the app we launch come to the front (Windows otherwise may open it behind the current window).
        AllowSetForegroundWindow(-1);
        switch (b.Type)
        {
            case "app":
            case "path": Launch(b.Target, b.Args); break;
            case "url": OpenUrl(b); break;
            case "hotkey": Keys.SendSequence(b.Target); break;
            case "text": Keys.TypeText(b.Target); break;
            case "command": RunCommand(b); break;
            case "system": RunSystem(b.Target); break;
            default: throw new InvalidOperationException("Unknown button type: " + b.Type);
        }
    }

    static void Launch(string target, string? args)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true })?.Dispose();
            return;
        }
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
        if (Path.IsPathRooted(target) && File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target)!;
        Process.Start(psi)?.Dispose();
    }

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        return Regex.IsMatch(url, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase) ? url : "https://" + url;
    }

    static void OpenUrl(DeckButton b)
    {
        var url = NormalizeUrl(b.Target);
        var exe = Browsers.Find(b.Browser);
        if (exe == null)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return;
        }
        bool firefox = exe.Contains("firefox", StringComparison.OrdinalIgnoreCase);
        var args = (b.NewWindow ? (firefox ? "-new-window " : "--new-window ") : "") + "\"" + url.Replace("\"", "%22") + "\"";
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
    }

    static void RunCommand(DeckButton b)
    {
        bool ps = b.Shell == "powershell";
        string exe = ps ? "powershell.exe" : "cmd.exe";
        string args = ps
            ? (b.Hidden ? "-NoProfile -WindowStyle Hidden -Command " : "-NoProfile -NoExit -Command ") + b.Target
            : (b.Hidden ? "/c " : "/k ") + b.Target;
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = !b.Hidden,
            CreateNoWindow = b.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        Process.Start(psi)?.Dispose();
    }

    static void RunSystem(string action)
    {
        switch (action)
        {
            case "lock": LockWorkStation(); break;
            case "sleep": SetSuspendState(false, false, false); break;
            case "monitoroff": PostMessage(new IntPtr(0xFFFF), 0x0112, new IntPtr(0xF170), new IntPtr(2)); break;
            case "showdesktop": Keys.SendSequence("win+d"); break;
            default: throw new InvalidOperationException("Unknown system action: " + action);
        }
    }

    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}

/// <summary>Keyboard shortcuts, media keys and text typing via SendInput.</summary>
public static class Keys
{
    static readonly Dictionary<string, ushort> Map = BuildMap();

    static Dictionary<string, ushort> BuildMap()
    {
        var m = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12,
            ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["tab"] = 0x09, ["space"] = 0x20,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pageup"] = 0x21, ["pgdn"] = 0x22, ["pagedown"] = 0x22,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["arrowup"] = 0x26, ["arrowdown"] = 0x28, ["arrowleft"] = 0x25, ["arrowright"] = 0x27,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["capslock"] = 0x14, ["menu"] = 0x5D,
            ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1, ["previous"] = 0xB1, ["stop"] = 0xB2,
            ["volup"] = 0xAF, ["voldown"] = 0xAE, ["mute"] = 0xAD,
            ["-"] = 0xBD, ["minus"] = 0xBD, ["="] = 0xBB, ["plus"] = 0xBB, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
            [";"] = 0xBA, ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["`"] = 0xC0,
        };
        for (char c = 'a'; c <= 'z'; c++) m[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (char c = '0'; c <= '9'; c++) m[c.ToString()] = c;
        for (int i = 1; i <= 24; i++) m["f" + i] = (ushort)(0x6F + i);
        return m;
    }

    static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D, 0x2C];

    /// <summary>"ctrl+shift+m" or several separated by commas: "ctrl+c, ctrl+v".</summary>
    public static void SendSequence(string spec)
    {
        foreach (var combo in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var keys = new List<ushort>();
            foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Map.TryGetValue(part, out var vk)) throw new ArgumentException($"Unknown key \"{part}\"");
                keys.Add(vk);
            }
            if (keys.Count == 0) continue;
            var inputs = new List<INPUT>();
            foreach (var k in keys) inputs.Add(Key(k, up: false));
            for (int i = keys.Count - 1; i >= 0; i--) inputs.Add(Key(keys[i], up: true));
            Send(inputs);
            Thread.Sleep(40);
        }
    }

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        Send(inputs);
    }

    public static bool IsValid(string spec)
    {
        try
        {
            return spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(c => c.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(Map.ContainsKey));
        }
        catch { return false; }
    }

    static INPUT Key(ushort vk, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? 2u : 0u) | (Extended.Contains(vk) ? 1u : 0u) } },
    };

    static INPUT Unicode(char c, bool up) => new()
    {
        type = 1,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 4u | (up ? 2u : 0u) } },
    };

    static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        if (SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>()) != arr.Length)
            throw new InvalidOperationException("Windows refused the key presses (an elevated app may be in front).");
    }

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
}

/// <summary>Installed browsers, found through Windows' App Paths registry.</summary>
public static class Browsers
{
    static readonly (string id, string name, string exe)[] Known =
    [
        ("chrome", "Google Chrome", "chrome.exe"),
        ("edge", "Microsoft Edge", "msedge.exe"),
        ("firefox", "Firefox", "firefox.exe"),
        ("brave", "Brave", "brave.exe"),
    ];

    public static string? Find(string? id)
    {
        if (string.IsNullOrEmpty(id) || id == "default") return null;
        var k = Known.FirstOrDefault(b => b.id == id);
        return k.exe == null ? null : AppPath(k.exe);
    }

    public static object[] Installed() =>
        Known.Where(b => AppPath(b.exe) != null).Select(b => (object)new { b.id, b.name }).ToArray();

    public static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        return null;
    }
}

/// <summary>Start menu apps (desktop and Store), via PowerShell's Get-StartApps.</summary>
public static class StartApps
{
    public sealed record App(string Name, string Target);
    static (DateTime at, App[] list)? _cache;

    public static async Task<App[]> GetAsync()
    {
        if (_cache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(10)) return c.list;
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var list = new List<App>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            var junk = new Regex(@"uninstall|readme|help|documentation|release notes|license|website|manual", RegexOptions.IgnoreCase);
            foreach (var e in items)
            {
                var name = e.GetProperty("Name").GetString();
                var id = e.GetProperty("AppID").GetString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || junk.IsMatch(name)) continue;
                if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new App(name, @"shell:AppsFolder\" + id));
            }
        }
        catch { }
        var arr = list.DistinctBy(a => a.Target).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        _cache = (DateTime.UtcNow, arr);
        return arr;
    }
}

/// <summary>Button icons: app/file icons from the Windows shell, website icons from the site itself. Cached on disk.</summary>
public static class DeckIcons
{
    const int Size = 96;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcDash", "icons");
    static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> Mem = new();
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(6) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PcDash/1.0");
        return h;
    }

    /// <summary>Whether this kind of button can have an automatic icon.</summary>
    public static bool HasAuto(DeckButton b) => b.Type is "app" or "path" or "url" && !string.IsNullOrWhiteSpace(b.Target);

    public static string CacheKey(DeckButton b) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{b.Type}|{b.Target.Trim()}")))[..16];

    public static Task<byte[]?> GetAsync(DeckButton b)
    {
        if (!HasAuto(b)) return Task.FromResult<byte[]?>(null);
        var key = CacheKey(b);
        return Mem.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() => Task.Run(() => Produce(b, key)))).Value;
    }

    static async Task<byte[]?> Produce(DeckButton b, string key)
    {
        var file = Path.Combine(Dir, key + ".png");
        try { if (File.Exists(file)) return await File.ReadAllBytesAsync(file); } catch { }

        byte[]? png = b.Type == "url"
            ? await Favicon(DeckActions.NormalizeUrl(b.Target))
            : ShellIcon(ResolveShellPath(b.Target));
        if (png != null)
        {
            try { Directory.CreateDirectory(Dir); await File.WriteAllBytesAsync(file, png); } catch { }
        }
        return png;
    }

    static string ResolveShellPath(string target)
    {
        target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(target)) return target;
        // bare exe name like "calc.exe": look in App Paths, System32, then PATH
        var fromReg = Browsers.AppPath(target);
        if (fromReg != null) return fromReg;
        var candidates = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in candidates)
        {
            try { var p = Path.Combine(dir, target); if (File.Exists(p)) return p; } catch { }
        }
        return target;
    }

    // ---- shell icons (works for exe, lnk, files, folders and shell:AppsFolder\ Store apps) ----

    static byte[]? ShellIcon(string parsingName) => RunSta(() =>
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, 0x1 | 0x4, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try
            {
                using var bmp = HBitmapToArgb(hbmp);
                return bmp == null ? null : ToPng(bmp);
            }
            finally { DeleteObject(hbmp); }
        }
        catch { return null; }
    });

    static Bitmap? HBitmapToArgb(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) return Image.FromHbitmap(hbmp);
        int h = Math.Abs(bm.bmHeight);
        using var view = new Bitmap(bm.bmWidth, h, bm.bmWidthBytes, PixelFormat.Format32bppPArgb, bm.bmBits);
        var copy = new Bitmap(bm.bmWidth, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy)) g.DrawImage(view, 0, 0, bm.bmWidth, h);
        if (bm.bmHeight > 0) copy.RotateFlip(RotateFlipType.RotateNoneFlipY); // bottom-up DIB
        return copy;
    }

    static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        var t = new Thread(() => result = f()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(TimeSpan.FromSeconds(10));
        return result;
    }

    // ---- website icons ----

    static async Task<byte[]?> Favicon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null;
        var candidates = new List<Uri>();
        try
        {
            using var resp = await Http.GetAsync(new Uri(uri.GetLeftPart(UriPartial.Authority) + "/"), HttpCompletionOption.ResponseHeadersRead);
            var baseUri = resp.RequestMessage?.RequestUri ?? uri;
            var html = await ReadLimited(resp, 512 * 1024);
            var links = Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase)
                .Select(m => m.Value)
                .Select(tag => (rel: Attr(tag, "rel"), href: Attr(tag, "href"), sizes: Attr(tag, "sizes")))
                .Where(l => l.rel != null && l.href != null && l.rel.Contains("icon", StringComparison.OrdinalIgnoreCase)
                            && !l.href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.rel!.Contains("apple-touch", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(l => int.TryParse((l.sizes ?? "").Split('x')[0], out var s) ? s : 0);
            foreach (var l in links)
                if (Uri.TryCreate(baseUri, System.Net.WebUtility.HtmlDecode(l.href!), out var u)) candidates.Add(u);
            candidates.Add(new Uri(baseUri, "/favicon.ico"));
        }
        catch { candidates.Add(new Uri(uri, "/favicon.ico")); }

        foreach (var c in candidates.Distinct().Take(5))
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(c);
                var png = DecodeToPng(bytes);
                if (png != null) return png;
            }
            catch { }
        }
        return null;
    }

    static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    static async Task<string> ReadLimited(HttpResponseMessage resp, int max)
    {
        await using var s = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[max];
        int total = 0, n;
        while (total < max && (n = await s.ReadAsync(buf.AsMemory(total, max - total))) > 0) total += n;
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    static byte[]? DecodeToPng(byte[] bytes)
    {
        if (bytes.Length < 16) return null;
        try
        {
            Bitmap src;
            if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
                using (var ico = new Icon(new MemoryStream(bytes), 256, 256)) src = ico.ToBitmap();
            else
                src = new Bitmap(new MemoryStream(bytes));
            using (src)
            {
                if (src.Width < 16) return null;
                using var scaled = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, 0, 0, Size, Size);
                }
                return ToPng(scaled);
            }
        }
        catch { return null; }
    }

    static byte[] ToPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
}

/// <summary>File/folder picker for the setup page, shown on top of everything.</summary>
public static class PathPicker
{
    public static Task<string?> PickAsync(string kind)
    {
        var tcs = new TaskCompletionSource<string?>();
        var t = new Thread(() =>
        {
            try
            {
                using var owner = new Form
                {
                    TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.CenterScreen, Size = new Size(1, 1), Opacity = 0,
                };
                owner.Show();
                owner.Activate();
                if (kind == "folder")
                {
                    using var d = new FolderBrowserDialog { Description = "Choose a folder for the deck button", UseDescriptionForTitle = true };
                    tcs.SetResult(d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null);
                }
                else
                {
                    using var d = new OpenFileDialog
                    {
                        Title = "Choose what the deck button opens",
                        Filter = "Programs and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.url;*.appref-ms|All files|*.*",
                        DereferenceLinks = false,
                    };
                    if (kind == "any") d.FilterIndex = 2;
                    tcs.SetResult(d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null);
                }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}
