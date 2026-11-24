using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PcDash;

/// <summary>
/// Reads Claude Code's stored OAuth token and calls the (undocumented) usage endpoint.
/// The token is re-read from disk on each fetch, so it picks up refreshes done by Claude Code.
/// This app never refreshes or writes the token itself.
/// </summary>
public sealed class ClaudeUsageService
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    static readonly string CredPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly SemaphoreSlim _gate = new(1, 1);
    JsonObject? _last;
    DateTime _fetchedUtc = DateTime.MinValue;
    DateTime _notBeforeUtc = DateTime.MinValue;
    DateTime _lastClientUtc = DateTime.MinValue;
    TimeSpan _backoff = TimeSpan.Zero;

    /// <summary>Reads the usage every minute while a dashboard is open (paused when nobody has looked for 5 minutes).</summary>
    public void StartBackgroundRefresh() => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                if (DateTime.UtcNow - _lastClientUtc < IdleAfter) await RefreshAsync();
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    });

    /// <summary>Returns the latest reading; fetches right away if there is none yet or it is stale (e.g. after idling).</summary>
    public async Task<JsonObject> GetAsync()
    {
        _lastClientUtc = DateTime.UtcNow;
        if (_last == null || (DateTime.UtcNow - _fetchedUtc >= CacheFor && DateTime.UtcNow >= _notBeforeUtc)) await RefreshAsync();
        return _last!;
    }

    async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (_last != null && (now - _fetchedUtc < CacheFor || now < _notBeforeUtc))
                return;

            var result = await FetchAsync();
            // Keep showing the last good numbers if a refresh fails.
            if (result["ok"]!.GetValue<bool>() || _last == null || !_last["ok"]!.GetValue<bool>())
                _last = result;
            else
                _last["error"] = result["error"]?.GetValue<string>();
            _fetchedUtc = now;
        }
        finally { _gate.Release(); }
    }

    async Task<JsonObject> FetchAsync()
    {
        JsonObject Fail(string msg) => new() { ["ok"] = false, ["error"] = msg, ["fetchedAt"] = DateTimeOffset.UtcNow };

        if (!File.Exists(CredPath))
            return Fail("Claude Code isn't logged in on this PC (no ~/.claude/.credentials.json).");

        JsonNode? oauth;
        try
        {
            using var fs = new FileStream(CredPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            oauth = (await JsonNode.ParseAsync(fs))?["claudeAiOauth"];
        }
        catch (Exception ex) { return Fail("Couldn't read Claude credentials: " + ex.Message); }

        var token = oauth?["accessToken"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token)) return Fail("No Claude access token found.");

        var expires = oauth?["expiresAt"]?.GetValue<long>();
        if (expires is long ms && DateTimeOffset.FromUnixTimeMilliseconds(ms) < DateTimeOffset.UtcNow)
            return Fail("Claude token expired. Run Claude Code once on this PC to refresh it.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("pcdash/1.0");
            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // back off 2 → 4 → 8 → 15 min while rate limited; Retry-After wins if it says longer
                _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(Math.Min(15, _backoff.TotalMinutes * 2));
                var wait = resp.Headers.RetryAfter?.Delta is { } d && d > _backoff ? d : _backoff;
                _notBeforeUtc = DateTime.UtcNow + wait;
                return Fail($"Usage endpoint is rate limiting, retrying at {DateTime.Now + wait:HH:mm}.");
            }
            _backoff = TimeSpan.Zero;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return Fail("Token rejected (401). Run Claude Code once on this PC to refresh it.");
            if (!resp.IsSuccessStatusCode)
                return Fail($"Usage endpoint returned {(int)resp.StatusCode}.");

            var usage = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            return new JsonObject
            {
                ["ok"] = true,
                ["fetchedAt"] = DateTimeOffset.UtcNow,
                ["plan"] = oauth?["subscriptionType"]?.GetValue<string>(),
                ["tier"] = oauth?["rateLimitTier"]?.GetValue<string>(),
                ["usage"] = usage,
            };
        }
        catch (Exception ex) { return Fail("Couldn't reach api.anthropic.com: " + ex.Message); }
    }
}
