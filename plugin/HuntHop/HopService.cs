using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace HuntHop;

/// <summary>
/// Drains alerts from the listener on the game thread, filters them, posts them to chat with a
/// [Go] link, and (in Auto mode) travels through Lifestream when every guard passes.
/// </summary>
internal sealed class HopService : IDisposable
{
    private const int MaxLinks = 30;
    /// <summary>Fixed id for the [Cancel] link; far from the rolling [Go] ids so it's never evicted.</summary>
    private const uint CancelLinkId = uint.MaxValue - 1;
    private static readonly TimeSpan DedupWindow    = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PingSilentWarn = TimeSpan.FromMinutes(5);

    private static readonly (ConditionFlag Flag, string Why)[] BlockingConditions =
    [
        (ConditionFlag.InCombat,                "in combat"),
        (ConditionFlag.BoundByDuty,             "in a duty"),
        (ConditionFlag.BoundByDuty56,           "in a duty"),
        (ConditionFlag.BoundByDuty95,           "in a duty"),
        (ConditionFlag.WatchingCutscene,        "in a cutscene"),
        (ConditionFlag.WatchingCutscene78,      "in a cutscene"),
        (ConditionFlag.OccupiedInCutSceneEvent, "in a cutscene"),
        (ConditionFlag.BetweenAreas,            "zoning"),
        (ConditionFlag.BetweenAreas51,          "zoning"),
        (ConditionFlag.Crafting,                "crafting"),
        (ConditionFlag.Gathering,               "gathering"),
        (ConditionFlag.Casting,                 "casting"),
        (ConditionFlag.OccupiedInEvent,         "busy with an event"),
        (ConditionFlag.OccupiedInQuestEvent,    "busy with a quest event"),
    ];

    private readonly Configuration _config;
    private readonly HuntListener  _listener;
    private readonly LifestreamIpc _lifestream;

    /// <summary>lower-case world name → display name, public worlds only.</summary>
    private readonly Dictionary<string, string> _worlds;

    private readonly Dictionary<string, DateTime> _recent = [];
    private readonly Queue<uint> _linkOrder = new();
    private readonly Dictionary<uint, string> _linkWorlds = [];
    private uint _nextLinkId = 1;

    private bool _pingWarned;
    private readonly DalamudLinkPayload _cancelLink;

    /// <summary>An auto-hop waiting for its delay to pass. Guards are checked again when it fires.</summary>
    private (string World, HuntAlert Alert, DateTime DueUtc)? _pendingHop;

    public DateTime? LastHopUtc { get; private set; }

    /// <summary>For the settings window: world and seconds left of a pending auto-hop, if any.</summary>
    public (string World, double SecondsLeft)? PendingHop =>
        _pendingHop is { } p ? (p.World, Math.Max(0, (p.DueUtc - DateTime.UtcNow).TotalSeconds)) : null;
    public string?   LatestWorld { get; private set; }
    public string?   LatestSummary { get; private set; }

    public HopService(Configuration config, HuntListener listener, LifestreamIpc lifestream)
    {
        _config     = config;
        _listener   = listener;
        _lifestream = lifestream;

        _worlds = Plugin.DataManager.GetExcelSheet<World>()
            .Where(w => w.IsPublic)
            .Select(w => w.Name.ToString())
            .Where(n => n.Length > 0)
            .Distinct()
            .ToDictionary(n => n.ToLowerInvariant(), n => n);

        _cancelLink = Plugin.ChatGui.AddChatLinkHandler(CancelLinkId, (_, _) => CancelPending(fromLink: true));
    }

    public bool TryResolveWorld(string slugOrName, out string world) =>
        _worlds.TryGetValue(slugOrName.Trim().ToLowerInvariant(), out world!);

    // ── game thread ────────────────────────────────────────────────────────────

    public void OnFrameworkUpdate(IFramework _)
    {
        var handled = 0;
        while (handled++ < 8 && _listener.Alerts.TryDequeue(out var alert))
            HandleAlert(alert);

        RunPendingHop();
        CheckPingHealth();
    }

    private void RunPendingHop()
    {
        if (_pendingHop is not { } p || DateTime.UtcNow < p.DueUtc) return;
        _pendingHop = null;

        // Things may have changed during the delay (combat, moved away from the aetheryte, …).
        var reason = "auto mode turned off";
        if (_config.Mode == HopMode.Auto && CheckGuards(p.World, auto: true, p.Alert, out reason))
        {
            Hop(p.World);
            return;
        }
        Print(new SeStringBuilder().AddUiForeground(4)
            .AddText($"[HuntHop] Auto-hop to {p.World} cancelled: {reason}")
            .AddUiForegroundOff().Build());
    }

    private void HandleAlert(HuntAlert alert)
    {
        if (!TryResolveWorld(alert.World, out var world))
        {
            Plugin.Log.Warning($"Alert for unknown world '{alert.World}' (mob '{alert.Mob}') ignored");
            return;
        }

        var known = MobTable.TryGet(alert.Mob, out var mob);

        // Faloop sends one notification per spawn, but several tabs or a reconnect can repeat it.
        var key = $"{alert.World}.{alert.Mob}.{alert.Instance}";
        var now = DateTime.UtcNow;
        foreach (var stale in _recent.Where(kv => now - kv.Value > DedupWindow).Select(kv => kv.Key).ToList())
            _recent.Remove(stale);
        if (!alert.IsTest && _recent.ContainsKey(key))
        {
            Plugin.Log.Debug($"Duplicate alert {key} ignored");
            return;
        }
        _recent[key] = now;

        var filteredOut = known && !(_config.AllowsRank(mob.Rank) && _config.AllowsExpansion(mob.Expansion));
        var label = Describe(alert, world, known ? mob : null);

        Plugin.Log.Info($"Alert: {label}{(filteredOut ? " (filtered out)" : string.Empty)}{(alert.IsTest ? " [test]" : string.Empty)}");

        if (filteredOut)
        {
            if (_config.ShowFilteredAlerts)
                Print(new SeStringBuilder().AddUiForeground(4).AddText($"[HuntHop] {label} (filtered)").AddUiForegroundOff().Build());
            return;
        }

        LatestWorld   = world;
        LatestSummary = label;

        var link = RegisterLink(world);
        var msg = new SeStringBuilder()
            .AddUiForeground(52).AddText("[HuntHop] ").AddUiForegroundOff()
            .AddText(label + " ")
            .Add(link)
            .AddUiForeground(43).AddText("[Go]").AddUiForegroundOff()
            .Add(RawPayload.LinkTerminator);
        if (!known) msg.AddText(" (unknown mob: no auto-hop)");
        Print(msg.Build());

        if (_config.Mode != HopMode.Auto) return;

        if (!known)
            return;

        if (!CheckGuards(world, auto: true, alert, out var reason))
        {
            Print(new SeStringBuilder().AddUiForeground(4).AddText($"[HuntHop] Auto-hop skipped: {reason}").AddUiForegroundOff().Build());
            return;
        }

        if (_config.AutoHopDelaySeconds <= 0)
        {
            Hop(world);
            return;
        }

        _pendingHop = (world, alert, DateTime.UtcNow.AddSeconds(_config.AutoHopDelaySeconds));
        Print(new SeStringBuilder().AddUiForeground(45)
            .AddText($"[HuntHop] Auto-hop to {world} in {_config.AutoHopDelaySeconds}s ")
            .AddUiForegroundOff()
            .Add(_cancelLink)
            .AddUiForeground(17).AddText("[Cancel]").AddUiForegroundOff()
            .Add(RawPayload.LinkTerminator)
            .Build());
    }

    private static string Describe(HuntAlert alert, string world, MobInfo? mob)
    {
        var name = mob?.Name ?? alert.Mob;
        var inst = alert.Instance > 0 ? $" i{alert.Instance}" : string.Empty;
        var rank = mob == null ? string.Empty : mob.Rank == "F" ? " (FATE)" : $" ({mob.Rank})";
        var zone = mob is { Zones.Length: 1 } ? $" in {Pretty(mob.Zones[0])}" : string.Empty;
        return $"{name}{rank}{inst} @ {world}{zone}";
    }

    private static string Pretty(string slug) =>
        string.Join(' ', slug.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // ── travel ─────────────────────────────────────────────────────────────────

    /// <summary>Travel to the most recent alert's world (/hunthop go).</summary>
    public void GoLatest()
    {
        if (LatestWorld == null)
        {
            PrintError("No alert yet.");
            return;
        }
        GoManual(LatestWorld);
    }

    public void GoManual(string world)
    {
        if (CheckGuards(world, auto: false, null, out var reason))
            Hop(world);
        else
            PrintError($"Can't travel to {world}: {reason}");
    }

    /// <summary>Cancel an auto-hop that is still waiting out its delay ([Cancel] link or /hunthop stop).</summary>
    private bool CancelPending(bool fromLink)
    {
        if (_pendingHop is { } p)
        {
            _pendingHop = null;
            Print(new SeStringBuilder().AddText($"[HuntHop] Auto-hop to {p.World} cancelled.").Build());
            return true;
        }
        if (fromLink)
            PrintError("Nothing to cancel: the hop already started or finished. /hunthop stop aborts a running Lifestream trip.");
        return false;
    }

    public void Stop()
    {
        CancelPending(fromLink: false);
        if (_lifestream.Abort())
            Print(new SeStringBuilder().AddText("[HuntHop] Lifestream aborted.").Build());
    }

    private void Hop(string world)
    {
        var ok = _lifestream.ChangeWorld(world);
        if (ok == true)
        {
            LastHopUtc = DateTime.UtcNow;
            Print(new SeStringBuilder().AddUiForeground(45).AddText($"[HuntHop] Travelling to {world}…").AddUiForegroundOff().Build());
            Plugin.Log.Info($"Lifestream.ChangeWorld({world}) accepted");
        }
        else
        {
            PrintError(ok == null ? "Lifestream is not loaded." : $"Lifestream refused to travel to {world}.");
        }
    }

    /// <summary>
    /// Shared guards for click and auto. Auto adds: must stand at a city aetheryte, known fresh alert,
    /// cooldown elapsed. Click lets Lifestream teleport you to a city aetheryte if you aren't at one.
    /// </summary>
    public bool CheckGuards(string world, bool auto, HuntAlert? alert, out string reason)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (!Plugin.ClientState.IsLoggedIn || player == null) { reason = "not logged in"; return false; }

        var busy = _lifestream.IsBusy();
        if (busy == null) { reason = "Lifestream is not loaded"; return false; }
        if (busy == true) { reason = "Lifestream is busy"; return false; }

        foreach (var (flag, why) in BlockingConditions)
            if (Plugin.Condition[flag]) { reason = why; return false; }

        var current = player.CurrentWorld.ValueNullable?.Name.ToString();
        if (string.Equals(current, world, StringComparison.OrdinalIgnoreCase)) { reason = $"already on {world}"; return false; }

        var sameDc  = _lifestream.CanVisitSameDc(world) == true;
        var crossDc = _lifestream.CanVisitCrossDc(world) == true;
        if (!sameDc)
        {
            if (!crossDc) { reason = $"{world} isn't reachable from here"; return false; }
            if (!_config.AllowCrossDc) { reason = $"{world} needs Data Center Travel (off in settings)"; return false; }
        }

        // A cross-world party is always fine: World Visit keeps you in it, and
        // Lifestream only /leaves normal parties. A normal party blocks unless allowed, because
        // Lifestream leaves it before changing world.
        var crossWorldParty = Plugin.Condition[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance];
        if (!crossWorldParty && Plugin.PartyList.Length > 1 && !_config.AllowInParty)
        {
            reason = "in a party (Lifestream would leave it)";
            return false;
        }

        if (auto)
        {
            if (_pendingHop is { } pending) { reason = $"an auto-hop to {pending.World} is already pending"; return false; }

            var aetheryte = _lifestream.ActiveAetheryte() ?? 0;
            if (!LifestreamIpc.WorldChangeAetherytes.ContainsKey(aetheryte))
            {
                reason = "not at the Limsa, Gridania or Ul'dah aetheryte";
                return false;
            }

            if (alert is { Ts: > 0 })
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(alert.Ts);
                if (age.TotalSeconds > _config.MaxAlertAgeSeconds) { reason = $"alert is {age.TotalSeconds:0}s old"; return false; }
            }

            if (LastHopUtc is { } last)
            {
                var left = TimeSpan.FromSeconds(_config.CooldownSeconds) - (DateTime.UtcNow - last);
                if (left > TimeSpan.Zero) { reason = $"cooldown, {left.TotalSeconds:0}s left"; return false; }
            }
        }

        reason = string.Empty;
        return true;
    }

    // ── chat ───────────────────────────────────────────────────────────────────

    private DalamudLinkPayload RegisterLink(string world)
    {
        while (_linkOrder.Count >= MaxLinks)
        {
            var old = _linkOrder.Dequeue();
            _linkWorlds.Remove(old);
            Plugin.ChatGui.RemoveChatLinkHandler(old);
        }

        var id = _nextLinkId++;
        _linkWorlds[id] = world;
        _linkOrder.Enqueue(id);
        return Plugin.ChatGui.AddChatLinkHandler(id, OnLinkClicked);
    }

    private void OnLinkClicked(uint id, SeString _)
    {
        if (_linkWorlds.TryGetValue(id, out var world))
            GoManual(world);
        else
            PrintError("That alert has expired.");
    }

    private void Print(SeString message) =>
        Plugin.ChatGui.Print(new XivChatEntry { Type = _config.ChatType, Message = message });

    private void PrintError(string text) =>
        Print(new SeStringBuilder().AddUiForeground(17).AddText($"[HuntHop] {text}").AddUiForegroundOff().Build());

    private void CheckPingHealth()
    {
        if (_listener.LastPingUtc is not { } last) return;
        var silent = DateTime.UtcNow - last > PingSilentWarn;
        if (silent && !_pingWarned)
        {
            _pingWarned = true;
            PrintError("No contact from the Faloop userscript for 5 minutes. Is the faloop.app tab still open?");
        }
        else if (!silent && _pingWarned)
        {
            _pingWarned = false;
            Print(new SeStringBuilder().AddText("[HuntHop] Faloop userscript is back.").Build());
        }
    }

    public void Dispose() => Plugin.ChatGui.RemoveChatLinkHandler();
}
