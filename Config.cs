using System.Text.Json;

namespace PcDash;

public sealed class DeckButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..10];
    public string Label { get; set; } = "";
    /// <summary>app | url | path | hotkey | text | command | system</summary>
    public string Type { get; set; } = "app";
    /// <summary>App path or shell:AppsFolder id, URL, file/folder, key combo, text, command, or system action.</summary>
    public string Target { get; set; } = "";
    public string? Args { get; set; }
    /// <summary>For websites: default | chrome | edge | firefox | brave</summary>
    public string? Browser { get; set; }
    public bool NewWindow { get; set; }
    /// <summary>For commands: cmd | powershell</summary>
    public string? Shell { get; set; }
    public bool Hidden { get; set; } = true;
    /// <summary>auto (app/site icon) | emoji</summary>
    public string Icon { get; set; } = "auto";
    public string? Emoji { get; set; }
    public string? Color { get; set; }
}

public sealed class DeckConfig
{
    public int Columns { get; set; } = 6;
    public List<DeckButton> Buttons { get; set; } = [];
}

public sealed class WeatherConfig
{
    public string? Name { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    /// <summary>c | f</summary>
    public string Units { get; set; } = "c";
}

public sealed class DashConfig
{
    public DeckConfig Deck { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
}

/// <summary>Deck and weather settings in %APPDATA%\PcDash\config.json.</summary>
public sealed class ConfigStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcDash", "config.json");

    readonly object _lock = new();
    DashConfig _current;

    public ConfigStore()
    {
        _current = Load() ?? Defaults();
        if (!File.Exists(FilePath)) Save(_current);
    }

    public DashConfig Current { get { lock (_lock) return _current; } }

    /// <summary>Bumped on every save so phones know to reload the deck.</summary>
    public int Version { get; private set; } = 1;

    public void Save(DashConfig cfg)
    {
        Sanitize(cfg);
        lock (_lock)
        {
            _current = cfg;
            Version++;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(cfg, Json));
            }
            catch { }
        }
    }

    static DashConfig? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var cfg = JsonSerializer.Deserialize<DashConfig>(File.ReadAllText(FilePath), Json);
            if (cfg != null) Sanitize(cfg);
            return cfg;
        }
        catch { return null; }
    }

    static void Sanitize(DashConfig cfg)
    {
        cfg.Deck ??= new();
        cfg.Weather ??= new();
        cfg.Deck.Buttons ??= [];
        cfg.Deck.Columns = Math.Clamp(cfg.Deck.Columns, 2, 10);
        cfg.Weather.Units = cfg.Weather.Units == "f" ? "f" : "c";
        var seen = new HashSet<string>();
        foreach (var b in cfg.Deck.Buttons)
        {
            if (string.IsNullOrWhiteSpace(b.Id) || !seen.Add(b.Id)) { b.Id = Guid.NewGuid().ToString("n")[..10]; seen.Add(b.Id); }
            b.Label ??= ""; b.Target ??= ""; b.Type ??= "app"; b.Icon ??= "auto";
        }
    }

    /// <summary>A useful starting deck; everything can be changed in setup.</summary>
    static DashConfig Defaults() => new()
    {
        Deck = new DeckConfig
        {
            Columns = 6,
            Buttons =
            [
                new() { Label = "Claude", Type = "url", Target = "https://claude.ai", Browser = "default" },
                new() { Label = "Explorer", Type = "app", Target = "explorer.exe" },
                new() { Label = "Task Manager", Type = "app", Target = "taskmgr.exe" },
                new() { Label = "Calculator", Type = "app", Target = "calc.exe" },
                new() { Label = "Screenshot", Type = "hotkey", Target = "win+shift+s", Icon = "emoji", Emoji = "✂️" },
                new() { Label = "Lock PC", Type = "system", Target = "lock", Icon = "emoji", Emoji = "🔒" },
                new() { Label = "Previous", Type = "hotkey", Target = "prev", Icon = "emoji", Emoji = "⏮️" },
                new() { Label = "Play / Pause", Type = "hotkey", Target = "playpause", Icon = "emoji", Emoji = "⏯️" },
                new() { Label = "Next", Type = "hotkey", Target = "next", Icon = "emoji", Emoji = "⏭️" },
                new() { Label = "Volume −", Type = "hotkey", Target = "voldown", Icon = "emoji", Emoji = "🔉" },
                new() { Label = "Volume +", Type = "hotkey", Target = "volup", Icon = "emoji", Emoji = "🔊" },
                new() { Label = "Mute", Type = "hotkey", Target = "mute", Icon = "emoji", Emoji = "🔇" },
            ],
        },
    };
}
