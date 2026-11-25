namespace HuntHop;

/// <summary>
/// Hand-picked teleports that beat the straight-line nearest aetheryte. Kept apart from the
/// generated <see cref="ZoneTable"/> so regenerating it doesn't lose them. Each rule has a setting.
/// </summary>
internal static class RouteOverrides
{
    public const string AmhAraeng = "amh_araeng";

    /// <summary>
    /// "Use Trolley in Amh Araeng": from Twine, the trolley reaches these south-west spawn points
    /// faster than walking from The Inn at Journey's Head (Faloop spawn point id → aetheryte name).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> AmhAraengTrolley = new Dictionary<int, string>
    {
        [966] = "Twine", // (19.7, 29.0)
        [969] = "Twine", // (17.0, 31.7)
    };

    /// <summary>An exact reported position this close to a listed spawn point (map-texture pixels, about 0.6 map units) counts as that point.</summary>
    public const int SnapPixels = 30;
}
