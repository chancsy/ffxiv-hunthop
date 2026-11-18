using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace HuntHop.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly Vector4 Good  = new(0.5f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Bad   = new(1f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    private static readonly Vector4 Head  = new(0.7f, 0.85f, 1f, 1f);

    private readonly Plugin _plugin;
    private int _portEdit;
    private bool _showToken;
    private string _testWorld = string.Empty;

    public ConfigWindow(Plugin plugin) : base("HuntHop###HuntHopConfig")
    {
        _plugin = plugin;
        _portEdit = Plugin.Config.Port;
        Size = new Vector2(460, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = Plugin.Config;
        var changed = false;

        // ── Status ──
        SubHeader("Status");
        var listener = _plugin.Listener;
        if (listener.IsRunning)
            ImGui.TextColored(Good, $"Listening on http://localhost:{listener.Port}/");
        else if (listener.LastError != null)
            ImGui.TextColored(Bad, $"Listener error: {listener.LastError}");
        else
            ImGui.TextColored(Muted, "Listener off");

        ImGui.TextColored(listener.LastPingUtc != null ? Good : Muted, $"Userscript: {Ago(listener.LastPingUtc, "never heard from")}");
        var lsOk = _plugin.Lifestream.IsAvailable;
        ImGui.TextColored(lsOk ? Good : Bad, lsOk ? "Lifestream: loaded" : "Lifestream: not loaded (required for travel)");
        if (lsOk)
        {
            var ae = _plugin.Lifestream.ActiveAetheryte() ?? 0;
            ImGui.TextColored(Muted, LifestreamIpc.WorldChangeAetherytes.TryGetValue(ae, out var city)
                ? $"At the {city} aetheryte: auto-hop possible"
                : "Not at a Limsa / Gridania / Ul'dah aetheryte");
        }
        if (_plugin.Hops.TripWorld is { } tripWorld)
            ImGui.TextColored(Good, $"Travelling to {tripWorld}; spawn-point teleport after arrival");
        if (_plugin.Zones.UnresolvedAetherytes > 0)
            ImGui.TextColored(Muted, $"{_plugin.Zones.UnresolvedAetherytes} Faloop aetheryte(s) without a game match (see /xllog)");
        if (_plugin.Hops.PendingHop is { } pending)
            ImGui.TextColored(Good, $"Auto-hop to {pending.World} in {pending.SecondsLeft:0}s");
        if (_plugin.Hops.LatestSummary is { } latest)
            ImGui.TextColored(Muted, $"Latest: {latest}");

        // ── Travel ──
        SubHeader("Travel");
        var mode = (int)cfg.Mode;
        if (ImGui.RadioButton("Click [Go] to travel", ref mode, (int)HopMode.Click)) { cfg.Mode = HopMode.Click; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Travel automatically", ref mode, (int)HopMode.Auto)) { cfg.Mode = HopMode.Auto; changed = true; }
        ImGui.TextColored(Muted, cfg.Mode == HopMode.Auto
            ? "Auto: only when idle at the Limsa, Gridania or Ul'dah aetheryte."
            : "Click: Lifestream teleports you to a city aetheryte first if needed.");

        var cooldown = cfg.CooldownSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto-hop cooldown (s)", ref cooldown, 30))
        {
            cfg.CooldownSeconds = Math.Clamp(cooldown, 0, 3600);
            changed = true;
        }
        Tip("Minimum time between automatic hops, so a burst of spawns doesn't bounce you between worlds. 0 = off. Click mode ignores it.");

        var delay = cfg.AutoHopDelaySeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Auto-hop delay (s)", ref delay, 1))
        {
            cfg.AutoHopDelaySeconds = Math.Clamp(delay, 0, 30);
            changed = true;
        }
        Tip("Auto mode waits this long after an alert before travelling, so it doesn't react instantly. Safety checks run again when it fires; /hunthop stop cancels. 0 = immediate.");

        var maxAge = cfg.MaxAlertAgeSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Ignore alerts older than (s)", ref maxAge, 10))
        {
            cfg.MaxAlertAgeSeconds = Math.Clamp(maxAge, 10, 600);
            changed = true;
        }
        Tip("Auto mode only. Stops a hop for an alert that sat in the queue (e.g. while you were in a duty).");

        changed |= Check("Teleport to the aetheryte nearest the spawn after arriving", cfg.TeleportAfterHop, v => cfg.TeleportAfterHop = v);
        Tip("Only when Faloop has reported the spawn point by the time you arrive; otherwise you stay in the city. If you're already on that world, [Go] just teleports.");
        changed |= Check("Allow Data Center Travel (slow; off = World Visit only)", cfg.AllowCrossDc, v => cfg.AllowCrossDc = v);
        changed |= Check("Allow while in a normal party (Lifestream leaves the party)", cfg.AllowInParty, v => cfg.AllowInParty = v);
        Tip("Cross-world parties are always allowed: World Visit keeps you in them.");

        // ── Filters ──
        SubHeader("Filters (after Faloop's own filters)");
        changed |= Check("S", cfg.RankS, v => cfg.RankS = v); ImGui.SameLine();
        changed |= Check("SS", cfg.RankSS, v => cfg.RankSS = v); ImGui.SameLine();
        changed |= Check("Hunt FATEs", cfg.RankFate, v => cfg.RankFate = v);
        changed |= Check("ARR", cfg.ExpArr, v => cfg.ExpArr = v); ImGui.SameLine();
        changed |= Check("HW", cfg.ExpHw, v => cfg.ExpHw = v); ImGui.SameLine();
        changed |= Check("SB", cfg.ExpSb, v => cfg.ExpSb = v); ImGui.SameLine();
        changed |= Check("ShB", cfg.ExpShb, v => cfg.ExpShb = v); ImGui.SameLine();
        changed |= Check("EW", cfg.ExpEw, v => cfg.ExpEw = v); ImGui.SameLine();
        changed |= Check("DT", cfg.ExpDt, v => cfg.ExpDt = v);
        changed |= Check("Show filtered-out alerts in grey", cfg.ShowFilteredAlerts, v => cfg.ShowFilteredAlerts = v);

        // ── Listener ──
        SubHeader("Userscript connection");
        changed |= Check("Listen for the userscript", cfg.ListenerEnabled, v => { cfg.ListenerEnabled = v; _plugin.ApplyListenerSettings(); });
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Port", ref _portEdit, 0);
        ImGui.SameLine();
        if (ImGui.Button("Apply port") && _portEdit is >= 1024 and <= 65535 && _portEdit != cfg.Port)
        {
            cfg.Port = _portEdit;
            changed = true;
            _plugin.ApplyListenerSettings();
        }

        ImGui.Text("Token:");
        ImGui.SameLine();
        ImGui.TextColored(Muted, _showToken ? cfg.Token : new string('•', 12));
        if (ImGui.Button(_showToken ? "Hide" : "Show")) _showToken = !_showToken;
        ImGui.SameLine();
        if (ImGui.Button("Copy token")) ImGui.SetClipboardText(cfg.Token);
        ImGui.SameLine();
        if (ImGui.Button("New token")) { cfg.RegenerateToken(); changed = true; }
        Tip("Paste the port and token into the userscript (Tampermonkey menu → HuntHop: set token).");

        // ── Test ──
        SubHeader("Test");
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("World##testworld", ref _testWorld, 32);
        ImGui.SameLine();
        if (ImGui.Button("Send test alert"))
        {
            if (_plugin.Hops.TryResolveWorld(_testWorld, out var world))
                _plugin.SendTestAlert(world);
            else
                Plugin.ChatGui.PrintError($"[HuntHop] Unknown world '{_testWorld}'.");
        }
        Tip("Posts a fake Sansheya (DT S) alert for that world. In auto mode it really travels if the guards pass.");

        if (changed) cfg.Save();
    }

    private static void SubHeader(string label)
    {
        ImGui.Spacing();
        ImGui.TextColored(Head, label);
        ImGui.Separator();
    }

    private static bool Check(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    private static void Tip(string text)
    {
        ImGui.SameLine();
        ImGui.TextColored(Muted, "(?)");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    private static string Ago(DateTime? utc, string never)
    {
        if (utc is not { } t) return never;
        var s = (DateTime.UtcNow - t).TotalSeconds;
        return s < 90 ? $"last contact {s:0}s ago" : $"last contact {s / 60:0} min ago";
    }
}
