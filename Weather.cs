using System.Globalization;
using System.Text.Json.Nodes;

namespace PcDash;

/// <summary>Weather from Open-Meteo (free, no API key). Cached for 15 minutes.</summary>
public sealed class WeatherService(ConfigStore config)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    readonly SemaphoreSlim _gate = new(1, 1);
    JsonObject? _last;
    string? _lastKey;
    DateTime _fetchedUtc;

    public async Task<JsonObject> GetAsync()
    {
        var w = config.Current.Weather;
        if (w.Lat is null || w.Lon is null) return new JsonObject { ["configured"] = false };

        string key = string.Create(CultureInfo.InvariantCulture, $"{w.Lat:0.####},{w.Lon:0.####},{w.Units}");
        await _gate.WaitAsync();
        try
        {
            if (_last != null && _lastKey == key && DateTime.UtcNow - _fetchedUtc < CacheFor) return _last;
            try
            {
                var result = await FetchAsync(w);
                _last = result; _lastKey = key; _fetchedUtc = DateTime.UtcNow;
                return result;
            }
            catch (Exception ex)
            {
                if (_last != null && _lastKey == key) { _last["error"] = "Weather update failed: " + ex.Message; return _last; }
                return new JsonObject { ["configured"] = true, ["name"] = w.Name, ["error"] = "Weather unavailable: " + ex.Message };
            }
        }
        finally { _gate.Release(); }
    }

    static async Task<JsonObject> FetchAsync(WeatherConfig w)
    {
        bool f = w.Units == "f";
        var url = FormattableString.Invariant(
            $"https://api.open-meteo.com/v1/forecast?latitude={w.Lat:0.####}&longitude={w.Lon:0.####}") +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max" +
            "&hourly=temperature_2m,weather_code,precipitation_probability,is_day&forecast_hours=12" +
            "&forecast_days=4&timezone=auto" + (f ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "");
        var root = JsonNode.Parse(await Http.GetStringAsync(url))!;
        var cur = root["current"]!;
        var d = root["daily"]!;

        // Hourly times come back as local wall-clock time without an offset; attach the location's
        // UTC offset so the phone can compare them with "now" exactly (the data is cached 15 min).
        var offset = TimeSpan.FromSeconds(root["utc_offset_seconds"]?.GetValue<int>() ?? 0);
        var hours = new JsonArray();
        if (root["hourly"] is JsonNode h && h["time"] is JsonArray ht)
            for (int i = 0; i < ht.Count; i++)
            {
                var local = DateTime.Parse(ht[i]!.GetValue<string>(), CultureInfo.InvariantCulture);
                hours.Add(new JsonObject
                {
                    ["time"] = new DateTimeOffset(local, offset).ToString("o", CultureInfo.InvariantCulture),
                    ["code"] = h["weather_code"]?[i]?.GetValue<int>(),
                    ["temp"] = h["temperature_2m"]?[i]?.GetValue<double>(),
                    ["rain"] = h["precipitation_probability"]?[i]?.GetValue<int>(),
                    ["isDay"] = h["is_day"]?[i]?.GetValue<int>() == 1,
                });
            }
        var days = new JsonArray();
        for (int i = 0; i < (d["time"] as JsonArray)!.Count; i++)
            days.Add(new JsonObject
            {
                ["date"] = d["time"]![i]!.GetValue<string>(),
                ["code"] = d["weather_code"]![i]?.GetValue<int>(),
                ["max"] = d["temperature_2m_max"]![i]?.GetValue<double>(),
                ["min"] = d["temperature_2m_min"]![i]?.GetValue<double>(),
                ["rain"] = d["precipitation_probability_max"]?[i]?.GetValue<int>(),
                ["sunrise"] = d["sunrise"]![i]?.GetValue<string>(),
                ["sunset"] = d["sunset"]![i]?.GetValue<string>(),
            });
        return new JsonObject
        {
            ["configured"] = true,
            ["name"] = w.Name,
            ["units"] = f ? "f" : "c",
            ["windUnit"] = f ? "mph" : "km/h",
            ["fetchedAt"] = DateTimeOffset.UtcNow,
            ["current"] = new JsonObject
            {
                ["temp"] = cur["temperature_2m"]?.GetValue<double>(),
                ["feels"] = cur["apparent_temperature"]?.GetValue<double>(),
                ["humidity"] = cur["relative_humidity_2m"]?.GetValue<int>(),
                ["wind"] = cur["wind_speed_10m"]?.GetValue<double>(),
                ["code"] = cur["weather_code"]?.GetValue<int>(),
                ["isDay"] = cur["is_day"]?.GetValue<int>() == 1,
            },
            ["hourly"] = hours,
            ["daily"] = days,
        };
    }

    public static async Task<JsonNode> GeocodeAsync(string q)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name=" + Uri.EscapeDataString(q);
        var root = JsonNode.Parse(await Http.GetStringAsync(url))!;
        var list = new JsonArray();
        if (root["results"] is JsonArray results)
            foreach (var r in results)
                list.Add(new JsonObject
                {
                    ["name"] = r!["name"]?.GetValue<string>(),
                    ["region"] = r["admin1"]?.GetValue<string>(),
                    ["country"] = r["country"]?.GetValue<string>(),
                    ["lat"] = r["latitude"]?.GetValue<double>(),
                    ["lon"] = r["longitude"]?.GetValue<double>(),
                });
        return list;
    }
}
