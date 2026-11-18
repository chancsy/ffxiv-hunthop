// Stand-in for the HuntHop plugin: listens on localhost exactly like the plugin does and prints
// every alert / ping the Faloop userscript sends. Use it to check the browser side without the game.
//
//   run.ps1 [-Port 47821] [-Token <token>]
//
// Token order: -Token argument → the plugin's saved token (%APPDATA%\XIVLauncher\pluginConfigs\HuntHop.json)
// → tools\ListenerCheck\token.txt (created on first run).
using System.Security.Cryptography;
using System.Text.Json;
using HuntHop;

var port = 47821;
string? token = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] is "--port" or "-p") port = int.Parse(args[i + 1]);
    if (args[i] is "--token" or "-t") token = args[i + 1];
}

var source = "argument";
if (string.IsNullOrWhiteSpace(token))
{
    var pluginCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "HuntHop.json");
    if (File.Exists(pluginCfg))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pluginCfg));
        if (doc.RootElement.TryGetProperty("Token", out var t) && t.GetString() is { Length: >= 16 } s)
        {
            token = s;
            source = "plugin config";
        }
    }
}
if (string.IsNullOrWhiteSpace(token))
{
    var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "token.txt");
    file = Path.GetFullPath(file);
    if (!File.Exists(file))
        File.WriteAllText(file, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    token = File.ReadAllText(file).Trim();
    source = file;
}

void Say(ConsoleColor color, string text)
{
    Console.ForegroundColor = color;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {text}");
    Console.ResetColor();
}

var listener = new HuntListener(() => token!, m => Say(ConsoleColor.Gray, m), m => Say(ConsoleColor.Yellow, m));
listener.Start(port);
if (!listener.IsRunning)
{
    Say(ConsoleColor.Red, $"Could not listen on port {port}. Is the game running with HuntHop loaded? Then the plugin already owns the port: check /hunthop instead, or use --port with another number.");
    return 1;
}

Say(ConsoleColor.Cyan, $"Token ({source}): {token}");
Say(ConsoleColor.Cyan, "Paste it in Tampermonkey → HuntHop: set token, on the faloop.app tab. Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

DateTime? lastPing = null;
while (!cts.IsCancellationRequested)
{
    while (listener.Alerts.TryDequeue(out var a))
    {
        var inst = a.Instance > 0 ? $" i{a.Instance}" : "";
        var age = a.Ts > 0 ? $"{(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(a.Ts)).TotalSeconds:0.0}s old" : "no ts";
        if (MobTable.TryGet(a.Mob, out var mob))
        {
            var passes = mob.Rank is "S" or "SS" && mob.Expansion is "shadowbringers" or "endwalker" or "dawntrail";
            Say(passes ? ConsoleColor.Green : ConsoleColor.DarkGray,
                $"ALERT  {mob.Name} ({mob.Rank}, {mob.Expansion}){inst} @ {a.World}  [{age}]  default filter: {(passes ? "PASS" : "filtered out")}");
        }
        else
        {
            Say(ConsoleColor.Magenta, $"ALERT  unknown mob '{a.Mob}'{inst} @ {a.World}  [{age}]  (not in MobTable: regenerate it?)");
        }
        Say(ConsoleColor.DarkGray, $"       title='{a.Title}' body='{a.Body.Replace('\n', ' ')}'");
    }

    while (listener.Spots.TryDequeue(out var sp))
        Say(ConsoleColor.Cyan, $"SPOT   {sp.Mob}{(sp.Instance > 0 ? $" i{sp.Instance}" : "")} @ {sp.World}  zone={sp.Zone ?? "?"} pois=[{string.Join(",", sp.Pois ?? [])}] location={sp.Location ?? "-"}");

    if (listener.LastPingUtc != lastPing)
    {
        lastPing = listener.LastPingUtc;
        Say(ConsoleColor.DarkCyan, "contact from userscript (ping or alert)");
    }

    try { await Task.Delay(250, cts.Token); } catch (TaskCanceledException) { }
}

listener.Dispose();
return 0;
