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

    private static readonly TimeSpan TripTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ArrivalSettle = TimeSpan.FromSeconds(2);

    private readonly Configuration _config;
    private readonly HuntListener  _listener;
    private readonly LifestreamIpc _lifestream;
    private readonly SpawnSpots    _spots;

    /// <summary>lower-case world name → display name, public worlds only.</summary>
    private readonly Dictionary<string, string> _worlds;

    private readonly Dictionary<string, DateTime> _recent = [];
    private readonly Queue<uint> _linkOrder = new();
    private readonly Dictionary<uint, Target> _links = [];
    private uint _nextLinkId = 1;

    /// <summary>What a [Go] travels to: the world, plus the alert key/mob for the spawn-point teleport.</summary>
    private sealed record Target(string World, string Key, string MobId, string Label, string Short, int Instance);

    /// <summary>Alerts shown in chat (key → target), so later spawn-point updates can be announced.</summary>
    private readonly Dictionary<string, (Target Target, DateTime AtUtc, uint? AnnouncedAetheryte)> _alerted = [];

    /// <summary>A world visit in progress; on arrival, teleport to the aetheryte nearest the spawn if known.</summary>
    private sealed class Trip(Target target)
    {
        public Target Target { get; } = target;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public DateTime? SettledSinceUtc { get; set; }
    }
    private Trip? _trip;

    private bool _pingWarned;
    private readonly DalamudLinkPayload _cancelLink;

    /// <summary>An auto-hop waiting for its delay to pass. Guards are checked again when it fires.</summary>
    private (Target Target, HuntAlert Alert, DateTime DueUtc)? _pendingHop;

    public DateTime? LastHopUtc { get; private set; }

    /// <summary>For the settings window: world and seconds left of a pending auto-hop, if any.</summary>
    public (string World, double SecondsLeft)? PendingHop =>
        _pendingHop is { } p ? (p.Target.World, Math.Max(0, (p.DueUtc - DateTime.UtcNow).TotalSeconds)) : null;
    private Target? _latest;
    public string?   LatestSummary => _latest?.Label;
    /// <summary>For the settings window: world of a trip waiting to arrive before teleporting.</summary>
    public string?   TripWorld => _trip?.Target.World;

    public HopService(Configuration config, HuntListener listener, LifestreamIpc lifestream, SpawnSpots spots)
    {
        _config     = config;
        _listener   = listener;
        _lifestream = lifestream;
        _spots      = spots;

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
        // Spot updates first, so an alert in the same frame already knows its spawn point.
        var handled = 0;
        while (handled++ < 16 && _listener.Spots.TryDequeue(out var spot))
            HandleSpot(spot);
        handled = 0;
        while (handled++ < 8 && _listener.Alerts.TryDequeue(out var alert))
            HandleAlert(alert);

        RunPendingHop();
        RunTrip();
        CheckPingHealth();
    }

    private void RunPendingHop()
    {
        if (_pendingHop is not { } p || DateTime.UtcNow < p.DueUtc) return;
        _pendingHop = null;

        // Things may have changed during the delay (combat, moved away from the aetheryte, …).
        var reason = "auto mode turned off";
        if (_config.Mode == HopMode.Auto && CheckGuards(p.Target.World, auto: true, p.Alert, out reason))
        {
            Hop(p.Target);
            return;
        }
        Print(new SeStringBuilder().AddUiForeground(4)
            .AddText($"[HuntHop] Auto-hop to {p.Target.World} cancelled: {reason}")
            .AddUiForegroundOff().Build());
    }

    // ── spawn points ───────────────────────────────────────────────────────────

    private void HandleSpot(SpotUpdate update)
    {
        var key = _spots.Update(update);
        Plugin.Log.Debug($"Spot update {key}: zone={update.Zone} pois=[{string.Join(",", update.Pois ?? [])}] location={update.Location}");

        // Announce it for alerts shown in chat, once per resulting aetheryte.
        if (!_alerted.TryGetValue(key, out var a)) return;
        if (!_spots.TryResolve(key, a.Target.MobId, out var spot, out _) || spot == null) return;
        if (a.AnnouncedAetheryte == spot.Aetheryte.RowId) return;
        _alerted[key] = a with { AnnouncedAetheryte = spot.Aetheryte.RowId };
        var line = new SeStringBuilder()
            .AddUiForeground(52).AddText("[HuntHop] ").AddUiForegroundOff()
            .AddText($"{a.Target.Short}: spawn point ");
        AppendWhere(line, spot, a.Target.Instance);
        line.AddText($", nearest aetheryte {spot.Aetheryte.Name}");
        Print(line.Build());
    }

    /// <summary>A clickable map link when the spot is one exact point, otherwise plain text.</summary>
    private static void AppendWhere(SeStringBuilder b, ResolvedSpot spot, int instance)
    {
        if (spot.Map is { } m)
            b.Append(SeString.CreateMapLinkWithInstance(m.Territory, m.Map, instance > 0 ? instance : null, m.X, m.Y));
        else
            b.AddText(spot.Where);
    }

    /// <summary>
    /// After a world visit: once you're on the target world, not zoning and Lifestream is idle
    /// (settled for a moment), teleport to the aetheryte nearest the spawn, only if the spawn
    /// point is known by then.
    /// </summary>
    private void RunTrip()
    {
        if (_trip is not { } t) return;
        var now = DateTime.UtcNow;
        if (now - t.StartedUtc > TripTimeout)
        {
            _trip = null;
            PrintError($"Didn't arrive on {t.Target.World} within 5 minutes; no teleport.");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var arrived = player != null
            && string.Equals(player.CurrentWorld.ValueNullable?.Name.ToString(), t.Target.World, StringComparison.OrdinalIgnoreCase)
            && _lifestream.IsBusy() == false
            && !BlockingConditions.Any(c => Plugin.Condition[c.Flag]);
        if (!arrived) { t.SettledSinceUtc = null; return; }
        if (t.SettledSinceUtc is not { } since) { t.SettledSinceUtc = now; return; }
        if (now - since < ArrivalSettle) return;

        _trip = null;
        TeleportToSpawn(t.Target);
    }

    private void TeleportToSpawn(Target target)
    {
        if (!_spots.TryResolve(target.Key, target.MobId, out var spot, out var reason) || spot == null)
        {
            Print(new SeStringBuilder().AddUiForeground(4)
                .AddText($"[HuntHop] Arrived on {target.World}; no teleport: {reason}.")
                .AddUiForegroundOff().Build());
            return;
        }

        var ok = _lifestream.Teleport(spot.Aetheryte.RowId);
        if (ok == true)
        {
            Print(new SeStringBuilder().AddUiForeground(45)
                .AddText($"[HuntHop] Teleporting to {spot.Aetheryte.Name}…")
                .AddUiForegroundOff().Build());
            Plugin.Log.Info($"Lifestream.Teleport({spot.Aetheryte.RowId} {spot.Aetheryte.Name}) accepted for {target.Key}");
        }
        else
        {
            PrintError(ok == null ? "Lifestream is not loaded." : $"Couldn't teleport to {spot.Aetheryte.Name} (not attuned, or not allowed right now).");
        }
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

        var target = new Target(world, key, alert.Mob, label, Describe(alert, world, known ? mob : null, withZone: false), alert.Instance);
        _latest = target;
        foreach (var stale in _alerted.Where(kv => now - kv.Value.AtUtc > TimeSpan.FromHours(1)).Select(kv => kv.Key).ToList())
            _alerted.Remove(stale);

        // The spawn point usually comes with the alert (or arrived just before it).
        if (alert.Spot != null)
            _spots.Update(alert.Spot);
        _spots.TryResolve(key, alert.Mob, out var spot, out _);
        _alerted[key] = (target, now, spot?.Aetheryte.RowId);

        var link = RegisterLink(target);
        var msg = new SeStringBuilder()
            .AddUiForeground(52).AddText("[HuntHop] ").AddUiForegroundOff();
        if (spot == null)
        {
            msg.AddText(label + " ");
        }
        else
        {
            // e.g. "Ruminator (S) @ Mandragora [map link: Mare Lamentorum (20.9, 34.7)] near Sinus Lacrimarum"
            msg.AddText(target.Short + " ");
            AppendWhere(msg, spot, alert.Instance);
            msg.AddText($" near {spot.Aetheryte.Name} ");
        }
        msg.Add(link)
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
            Hop(target);
            return;
        }

        _pendingHop = (target, alert, DateTime.UtcNow.AddSeconds(_config.AutoHopDelaySeconds));
        Print(new SeStringBuilder().AddUiForeground(45)
            .AddText($"[HuntHop] Auto-hop to {world} in {_config.AutoHopDelaySeconds}s ")
            .AddUiForegroundOff()
            .Add(_cancelLink)
            .AddUiForeground(17).AddText("[Cancel]").AddUiForegroundOff()
            .Add(RawPayload.LinkTerminator)
            .Build());
    }

    private static string Describe(HuntAlert alert, string world, MobInfo? mob, bool withZone = true)
    {
        var name = mob?.Name ?? alert.Mob;
        var inst = alert.Instance > 0 ? $" i{alert.Instance}" : string.Empty;
        var rank = mob == null ? string.Empty : mob.Rank == "F" ? " (FATE)" : $" ({mob.Rank})";
        var zone = withZone && mob is { Zones.Length: 1 } ? $" in {Pretty(mob.Zones[0])}" : string.Empty;
        return $"{name}{rank}{inst} @ {world}{zone}";
    }

    private static string Pretty(string slug) =>
        string.Join(' ', slug.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // ── travel ─────────────────────────────────────────────────────────────────

    /// <summary>Travel to the most recent alert's world (/hunthop go).</summary>
    public void GoLatest()
    {
        if (_latest == null)
        {
            PrintError("No alert yet.");
            return;
        }
        GoManual(_latest);
    }

    private void GoManual(Target target)
    {
        // Already on that world: just do the spawn-point teleport (if enabled and known).
        if (_config.TeleportAfterHop && IsOnWorld(target.World))
        {
            if (_lifestream.IsBusy() != false) { PrintError("Lifestream is busy or not loaded."); return; }
            foreach (var (flag, why) in BlockingConditions)
                if (Plugin.Condition[flag]) { PrintError($"Can't teleport: {why}"); return; }
            TeleportToSpawn(target);
            return;
        }

        if (CheckGuards(target.World, auto: false, null, out var reason))
            Hop(target);
        else
            PrintError($"Can't travel to {target.World}: {reason}");
    }

    private static bool IsOnWorld(string world) =>
        string.Equals(Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString(), world, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cancel an auto-hop that is still waiting out its delay ([Cancel] link or /hunthop stop).</summary>
    private bool CancelPending(bool fromLink)
    {
        if (_pendingHop is { } p)
        {
            _pendingHop = null;
            Print(new SeStringBuilder().AddText($"[HuntHop] Auto-hop to {p.Target.World} cancelled.").Build());
            return true;
        }
        if (fromLink)
            PrintError("Nothing to cancel: the hop already started or finished. /hunthop stop aborts a running Lifestream trip.");
        return false;
    }

    public void Stop()
    {
        CancelPending(fromLink: false);
        if (_trip != null)
        {
            _trip = null;
            Print(new SeStringBuilder().AddText("[HuntHop] Spawn-point teleport after arrival cancelled.").Build());
        }
        if (_lifestream.Abort())
            Print(new SeStringBuilder().AddText("[HuntHop] Lifestream aborted.").Build());
    }

    private void Hop(Target target)
    {
        var world = target.World;
        var ok = _lifestream.ChangeWorld(world);
        if (ok == true)
        {
            LastHopUtc = DateTime.UtcNow;
            _trip = _config.TeleportAfterHop ? new Trip(target) : null;
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
            if (_pendingHop is { } pending) { reason = $"an auto-hop to {pending.Target.World} is already pending"; return false; }

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

    private DalamudLinkPayload RegisterLink(Target target)
    {
        while (_linkOrder.Count >= MaxLinks)
        {
            var old = _linkOrder.Dequeue();
            _links.Remove(old);
            Plugin.ChatGui.RemoveChatLinkHandler(old);
        }

        var id = _nextLinkId++;
        _links[id] = target;
        _linkOrder.Enqueue(id);
        return Plugin.ChatGui.AddChatLinkHandler(id, OnLinkClicked);
    }

    private void OnLinkClicked(uint id, SeString _)
    {
        if (_links.TryGetValue(id, out var target))
            GoManual(target);
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
