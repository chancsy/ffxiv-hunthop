using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuntHop;

/// <summary>An alert forwarded by the userscript. Slugs are Faloop ids (e.g. world "aegis", mob "sansheya").</summary>
public sealed class HuntAlert
{
    [JsonPropertyName("world")]    public string World    { get; set; } = string.Empty;
    [JsonPropertyName("mob")]      public string Mob      { get; set; } = string.Empty;
    [JsonPropertyName("instance")] public int    Instance { get; set; }
    [JsonPropertyName("title")]    public string Title    { get; set; } = string.Empty;
    [JsonPropertyName("body")]     public string Body     { get; set; } = string.Empty;
    /// <summary>Browser time (Unix ms) when Faloop raised the notification. 0 = unknown.</summary>
    [JsonPropertyName("ts")]       public long   Ts       { get; set; }
    /// <summary>Spawn point, when the userscript already knows it at alert time (world/mob/instance are filled in from the alert).</summary>
    [JsonPropertyName("spot")]     public SpotUpdate? Spot { get; set; }
    /// <summary>True for alerts made by the plugin's own test button / command.</summary>
    [JsonIgnore]                   public bool   IsTest   { get; set; }
}

/// <summary>
/// Where an alerted spawn is, from Faloop's live data (socket "spawn" / "spawn_location" reports).
/// Either an exact <see cref="Location"/> ("x,y" map-texture pixels, needs a zone) or Faloop
/// spawn-point ids in <see cref="Pois"/>.
/// </summary>
public sealed class SpotUpdate
{
    [JsonPropertyName("world")]    public string  World    { get; set; } = string.Empty;
    [JsonPropertyName("mob")]      public string  Mob      { get; set; } = string.Empty;
    [JsonPropertyName("instance")] public int     Instance { get; set; }
    [JsonPropertyName("zone")]     public string? Zone     { get; set; }
    [JsonPropertyName("pois")]     public int[]?  Pois     { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
}

/// <summary>
/// Minimal localhost HTTP endpoint for the userscript. No Dalamud dependencies, so it can be
/// exercised outside the game. Requests are handled on a background task; alerts are queued
/// for the game thread to drain.
///
///   POST /hunt  (JSON HuntAlert)  → 204, queued
///   POST /spot  (JSON SpotUpdate) → 204, queued
///   POST /ping                    → 204, updates LastPingUtc
/// Every request needs the header  X-HuntHop-Token: &lt;token&gt;.
/// </summary>
public sealed class HuntListener : IDisposable
{
    public const string TokenHeader = "X-HuntHop-Token";
    private const int MaxBodyBytes = 8 * 1024;
    private const int MaxQueue = 64;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly System.Text.RegularExpressions.Regex LocationRe = new(@"^\d{1,4}(\.\d+)?,\s?\d{1,4}(\.\d+)?$");

    private readonly Func<string> _token;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public ConcurrentQueue<HuntAlert> Alerts { get; } = new();
    public ConcurrentQueue<SpotUpdate> Spots { get; } = new();
    public DateTime? LastPingUtc  { get; private set; }
    public DateTime? LastAlertUtc { get; private set; }
    public string?   LastError    { get; private set; }
    public int       Port         { get; private set; }
    public bool      IsRunning    => _listener?.IsListening == true;

    public HuntListener(Func<string> token, Action<string> info, Action<string> warn)
    {
        _token = token;
        _info  = info;
        _warn  = warn;
    }

    public void Start(int port)
    {
        Stop();
        Port = port;
        try
        {
            var listener = new HttpListener();
            // "localhost" prefixes don't need an admin URL reservation and only accept
            // requests whose Host header is localhost.
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(listener, ct));
            LastError = null;
            _info($"Listening on http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _listener = null;
            _warn($"Listener failed to start on port {port}: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Close(); } catch { /* already closed */ }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                _warn($"Listener error: {ex.Message}");
                continue;
            }

            try
            {
                Respond(ctx, Handle(ctx.Request));
            }
            catch (Exception ex)
            {
                _warn($"Request failed: {ex.Message}");
                try { Respond(ctx, 500); } catch { /* client gone */ }
            }
        }
    }

    private int Handle(HttpListenerRequest req)
    {
        if (req.HttpMethod != "POST") return 405;
        if (!TokenMatches(req.Headers[TokenHeader])) return 401;

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        switch (path)
        {
            case "/ping":
                LastPingUtc = DateTime.UtcNow;
                return 204;

            case "/hunt":
                if (req.ContentLength64 > MaxBodyBytes) return 413;
                var body = ReadBody(req);
                if (body == null) return 413;

                HuntAlert? alert;
                try { alert = JsonSerializer.Deserialize<HuntAlert>(body, JsonOpts); }
                catch (JsonException) { return 400; }
                if (alert == null || !IsSlug(alert.World) || !IsSlug(alert.Mob)) return 400;
                if (alert.Spot != null)
                {
                    alert.Spot.World = alert.World;
                    alert.Spot.Mob = alert.Mob;
                    alert.Spot.Instance = alert.Instance;
                    if (!IsValidSpot(alert.Spot)) return 400;
                }

                alert.Title = Clip(alert.Title, 120);
                alert.Body  = Clip(alert.Body, 300);
                alert.IsTest = false;
                Enqueue(alert);
                LastPingUtc = DateTime.UtcNow; // an alert also proves the userscript is alive
                return 204;

            case "/spot":
                if (req.ContentLength64 > MaxBodyBytes) return 413;
                var spotBody = ReadBody(req);
                if (spotBody == null) return 413;

                SpotUpdate? spot;
                try { spot = JsonSerializer.Deserialize<SpotUpdate>(spotBody, JsonOpts); }
                catch (JsonException) { return 400; }
                if (spot == null || !IsValidSpot(spot)) return 400;

                while (Spots.Count >= MaxQueue && Spots.TryDequeue(out _)) { }
                Spots.Enqueue(spot);
                LastPingUtc = DateTime.UtcNow;
                return 204;

            default:
                return 404;
        }
    }

    /// <summary>Queues an alert as if it had arrived over HTTP (used by the test button).</summary>
    public void Enqueue(HuntAlert alert)
    {
        while (Alerts.Count >= MaxQueue && Alerts.TryDequeue(out _)) { }
        Alerts.Enqueue(alert);
        LastAlertUtc = DateTime.UtcNow;
    }

    private bool TokenMatches(string? given)
    {
        var expected = _token();
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }

    private static string? ReadBody(HttpListenerRequest req)
    {
        using var stream = req.InputStream;
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while ((read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
            if (total > MaxBodyBytes) return null;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool IsValidSpot(SpotUpdate s) =>
        IsSlug(s.World) && IsSlug(s.Mob)
        && (string.IsNullOrEmpty(s.Zone) || IsSlug(s.Zone))
        && s.Pois is not { Length: > 64 }
        && (string.IsNullOrEmpty(s.Location) || LocationRe.IsMatch(s.Location))
        && (s.Pois is { Length: > 0 } || !string.IsNullOrEmpty(s.Location));

    private static bool IsSlug(string? s) =>
        !string.IsNullOrEmpty(s) && s.Length <= 64 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    private static string Clip(string? s, int max)
    {
        s ??= string.Empty;
        s = new string(s.Where(c => !char.IsControl(c) || c == '\n').ToArray());
        return s.Length <= max ? s : s[..max];
    }

    private static void Respond(HttpListenerContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentLength64 = 0;
        ctx.Response.Close();
    }
}
