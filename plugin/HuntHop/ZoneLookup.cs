using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace HuntHop;

/// <summary>A teleport target: game Aetheryte row + display name.</summary>
internal sealed record AetheryteTarget(uint RowId, string Name);

/// <summary>
/// Lookups over the generated <see cref="ZoneTable"/>: spawn point → position, nearest aetheryte,
/// and Faloop aetheryte name → game Aetheryte row (matched on English names, so it works with any
/// client language).
/// </summary>
internal sealed class ZoneLookup
{
    private readonly Dictionary<string, ZoneInfo> _zones = ZoneTable.All.ToDictionary(z => z.Id);
    private readonly Dictionary<int, (ZoneInfo Zone, int X, int Y)> _spawnPoints = [];
    private readonly Dictionary<(string Zone, int PoiId), AetheryteTarget> _targets = [];
    /// <summary>Faloop zone → game TerritoryType + Map (taken from that zone's matched aetherytes), for map links.</summary>
    private readonly Dictionary<string, (uint Territory, uint Map)> _maps = [];

    public int UnresolvedAetherytes { get; }

    public ZoneLookup()
    {
        foreach (var z in ZoneTable.All)
            for (var i = 0; i + 2 < z.SpawnPoints.Length; i += 3)
                _spawnPoints[z.SpawnPoints[i]] = (z, z.SpawnPoints[i + 1], z.SpawnPoints[i + 2]);

        // (zone name, aetheryte name) → Aetheryte row, from the English sheets.
        var places = Plugin.DataManager.GetExcelSheet<PlaceName>(ClientLanguage.English);
        var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English);
        var byName = new Dictionary<(string, string), (uint Row, uint Territory, uint Map)>();
        foreach (var a in Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English))
        {
            if (!a.IsAetheryte || a.Territory.RowId == 0) continue;
            var name = places.GetRowOrDefault(a.PlaceName.RowId)?.Name.ToString();
            var terr = territories.GetRowOrDefault(a.Territory.RowId);
            var zone = terr is { } t ? places.GetRowOrDefault(t.PlaceName.RowId)?.Name.ToString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(zone) || terr is not { } tt) continue;
            byName.TryAdd((Norm(zone), Norm(name)), (a.RowId, a.Territory.RowId, tt.Map.RowId));
        }

        var unresolved = new List<string>();
        foreach (var z in ZoneTable.All)
            foreach (var ae in z.Aetherytes)
            {
                if (byName.TryGetValue((Norm(z.Name), Norm(ae.Name)), out var hit))
                {
                    _targets[(z.Id, ae.PoiId)] = new AetheryteTarget(hit.Row, ae.Name);
                    _maps.TryAdd(z.Id, (hit.Territory, hit.Map));
                }
                else
                {
                    unresolved.Add($"{z.Name}/{ae.Name}");
                }
            }

        UnresolvedAetherytes = unresolved.Count;
        Plugin.Log.Info($"ZoneLookup: {_targets.Count} aetherytes mapped to teleport ids, {_spawnPoints.Count} spawn points");
        if (unresolved.Count > 0)
            Plugin.Log.Warning($"ZoneLookup: no game aetheryte for {string.Join(", ", unresolved)}");
    }

    public bool TryGetZone(string zoneId, out ZoneInfo zone) => _zones.TryGetValue(zoneId, out zone!);

    public bool TryGetSpawnPoint(int poiId, out ZoneInfo zone, out int x, out int y)
    {
        if (_spawnPoints.TryGetValue(poiId, out var p)) { (zone, x, y) = p; return true; }
        zone = null!; x = y = 0;
        return false;
    }

    /// <summary>Nearest aetheryte in the same zone that has a game teleport id.</summary>
    public AetheryteTarget? NearestAetheryte(ZoneInfo zone, int x, int y)
    {
        AetheryteTarget? best = null;
        var bestDist = double.MaxValue;
        foreach (var ae in zone.Aetherytes)
        {
            if (!_targets.TryGetValue((zone.Id, ae.PoiId), out var target)) continue;
            var d = Math.Pow(ae.X - x, 2) + Math.Pow(ae.Y - y, 2);
            if (d < bestDist) { bestDist = d; best = target; }
        }
        return best;
    }

    /// <summary>Game TerritoryType + Map of a Faloop zone, for a map link.</summary>
    public bool TryGetMap(string zoneId, out uint territory, out uint map)
    {
        if (_maps.TryGetValue(zoneId, out var m)) { (territory, map) = m; return true; }
        territory = map = 0;
        return false;
    }

    /// <summary>In-game map coordinates of a map-texture pixel position (same formula as Faloop's site).</summary>
    public static (float X, float Y) MapPoint(ZoneInfo zone, int x, int y)
    {
        var scale = 41.0 / (zone.SizeFactor / 100.0);
        return ((float)Math.Round(x / 2048.0 * scale + 1, 1), (float)Math.Round(y / 2048.0 * scale + 1, 1));
    }

    /// <summary>Faloop/in-game map coordinates text for a pixel position, e.g. "(12.3, 25.6)".</summary>
    public static string MapCoords(ZoneInfo zone, int x, int y)
    {
        var (mx, my) = MapPoint(zone, x, y);
        return $"({mx:0.0}, {my:0.0})";
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();
}
