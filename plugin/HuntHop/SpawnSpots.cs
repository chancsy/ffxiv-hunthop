namespace HuntHop;

/// <summary>A clickable map position: game TerritoryType + Map and in-game map coordinates.</summary>
internal sealed record MapPosition(uint Territory, uint Map, float X, float Y);

/// <summary>
/// Where an alerted spawn is, resolved to a teleport target. <see cref="Map"/> is set when there
/// is a single exact point (for a chat map link); <see cref="Where"/> is the plain-text fallback.
/// </summary>
internal sealed record ResolvedSpot(AetheryteTarget Aetheryte, string Where, MapPosition? Map);

/// <summary>
/// Latest known spawn spot per alert (key "world.mob.instance"), from the userscript's /spot
/// updates. A spot is either an exact location or a list of Faloop spawn-point ids; with several
/// candidates it only resolves when they all share the same nearest aetheryte.
/// </summary>
internal sealed class SpawnSpots
{
    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    private sealed record Spot(string? Zone, int[] Pois, string? Location, DateTime AtUtc);

    private readonly ZoneLookup _zones;
    private readonly Func<bool> _useTrolley;
    private readonly Dictionary<string, Spot> _spots = [];

    /// <param name="useTrolley">The "Use Trolley in Amh Araeng" setting (see <see cref="RouteOverrides"/>).</param>
    public SpawnSpots(ZoneLookup zones, Func<bool> useTrolley)
    {
        _zones = zones;
        _useTrolley = useTrolley;
    }

    public static string Key(string world, string mob, int instance) => $"{world}.{mob}.{instance}";

    /// <summary>Stores an update; returns the key it was stored under.</summary>
    public string Update(SpotUpdate u)
    {
        var now = DateTime.UtcNow;
        foreach (var stale in _spots.Where(kv => now - kv.Value.AtUtc > Keep).Select(kv => kv.Key).ToList())
            _spots.Remove(stale);

        var key = Key(u.World, u.Mob, u.Instance);
        _spots[key] = new Spot(string.IsNullOrEmpty(u.Zone) ? null : u.Zone, u.Pois ?? [], string.IsNullOrEmpty(u.Location) ? null : u.Location, now);
        return key;
    }

    public bool TryResolve(string key, string mobId, out ResolvedSpot? spot, out string reason)
    {
        spot = null;
        if (!_spots.TryGetValue(key, out var s)) { reason = "spawn point not reported yet"; return false; }

        // Candidate positions: an exact location (needs the zone) or Faloop spawn-point ids.
        var candidates = new List<(ZoneInfo Zone, int? PoiId, int X, int Y)>();
        if (s.Location is { } loc && TryParsePoint(loc, out var lx, out var ly))
        {
            var zoneId = s.Zone ?? (MobTable.TryGet(mobId, out var mob) && mob.Zones.Length == 1 ? mob.Zones[0] : null);
            if (zoneId != null && _zones.TryGetZone(zoneId, out var zone))
                candidates.Add((zone, null, lx, ly));
        }
        if (candidates.Count == 0)
            foreach (var poi in s.Pois)
                if (_zones.TryGetSpawnPoint(poi, out var zone, out var x, out var y))
                    candidates.Add((zone, poi, x, y));

        if (candidates.Count == 0) { reason = "spawn point not reported yet"; return false; }

        var targets = candidates
            .Select(c => (c, Target: PickAetheryte(c.Zone, c.PoiId, c.X, c.Y)))
            .ToList();
        if (targets.Any(t => t.Target == null)) { reason = "no known aetheryte in that zone"; return false; }

        var distinct = targets.Select(t => t.Target!.RowId).Distinct().Count();
        if (distinct > 1) { reason = $"spawn point not certain ({candidates.Count} possible spots, different aetherytes)"; return false; }

        var first = targets[0];
        var where = candidates.Count == 1
            ? $"{first.c.Zone.Name} {ZoneLookup.MapCoords(first.c.Zone, first.c.X, first.c.Y)}"
            : $"{first.c.Zone.Name} ({candidates.Count} possible spots, same aetheryte)";
        MapPosition? map = null;
        if (candidates.Count == 1 && _zones.TryGetMap(first.c.Zone.Id, out var territory, out var mapId))
        {
            var (mx, my) = ZoneLookup.MapPoint(first.c.Zone, first.c.X, first.c.Y);
            map = new MapPosition(territory, mapId, mx, my);
        }
        spot = new ResolvedSpot(first.Target!, where, map);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// The teleport for one spawn position: a hand-picked route when its setting is on (an exact
    /// position counts as the spawn point it sits on), otherwise the straight-line nearest aetheryte.
    /// </summary>
    private AetheryteTarget? PickAetheryte(ZoneInfo zone, int? poiId, int x, int y)
    {
        if (zone.Id == RouteOverrides.AmhAraeng && _useTrolley())
        {
            var id = poiId ?? ZoneLookup.NearestSpawnPoint(zone, x, y, RouteOverrides.SnapPixels);
            if (id is { } p && RouteOverrides.AmhAraengTrolley.TryGetValue(p, out var name)
                && _zones.AetheryteByName(zone, name) is { } target)
                return target;
        }
        return _zones.NearestAetheryte(zone, x, y);
    }

    /// <summary>Faloop's "x,y" map-texture pixels (same space as spawn points); decimals allowed.</summary>
    private static bool TryParsePoint(string s, out int x, out int y)
    {
        x = y = 0;
        var parts = s.Split(',');
        if (parts.Length != 2) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
        if (!double.TryParse(parts[0].Trim(), ns, ci, out var dx) || !double.TryParse(parts[1].Trim(), ns, ci, out var dy)) return false;
        if (dx is < 0 or > 4096 || dy is < 0 or > 4096) return false;
        x = (int)Math.Round(dx);
        y = (int)Math.Round(dy);
        return true;
    }
}
