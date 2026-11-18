using System.Security.Cryptography;
using Dalamud.Configuration;
using Dalamud.Game.Text;

namespace HuntHop;

public enum HopMode
{
    /// <summary>Post the alert with a [Go] link; travel only when you click it (or run /hunthop go).</summary>
    Click,

    /// <summary>Travel automatically when every guard passes (at a city aetheryte, idle, cooldown elapsed).</summary>
    Auto,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Listener (userscript → plugin)
    public bool   ListenerEnabled { get; set; } = true;
    public int    Port            { get; set; } = 47821;
    public string Token           { get; set; } = string.Empty;

    // Travel
    public HopMode Mode               { get; set; } = HopMode.Click;
    public int     CooldownSeconds    { get; set; } = 300;
    /// <summary>Auto mode: wait this long after an alert before travelling, so it doesn't react instantly. 0 = immediate.</summary>
    public int     AutoHopDelaySeconds { get; set; } = 3;
    public int     MaxAlertAgeSeconds { get; set; } = 60;
    public bool    AllowCrossDc       { get; set; } = false;
    public bool    AllowInParty       { get; set; } = false;

    // Filters (second pass after Faloop's own filters). Bools rather than sets so that
    // unticked defaults don't come back when the JSON is merged into a fresh instance.
    public bool RankS    { get; set; } = true;
    public bool RankSS   { get; set; } = true;
    public bool RankFate { get; set; } = false;

    public bool ExpArr { get; set; } = false;
    public bool ExpHw  { get; set; } = false;
    public bool ExpSb  { get; set; } = false;
    public bool ExpShb { get; set; } = true;
    public bool ExpEw  { get; set; } = true;
    public bool ExpDt  { get; set; } = true;

    // Chat
    public XivChatType ChatType          { get; set; } = XivChatType.Echo;
    public bool        ShowFilteredAlerts { get; set; } = false;

    public bool AllowsRank(string rank) => rank switch
    {
        "S"  => RankS,
        "SS" => RankSS,
        "F"  => RankFate,
        _    => false,
    };

    public bool AllowsExpansion(string expansion) => expansion switch
    {
        "a_realm_reborn" => ExpArr,
        "heavensward"    => ExpHw,
        "stormblood"     => ExpSb,
        "shadowbringers" => ExpShb,
        "endwalker"      => ExpEw,
        "dawntrail"      => ExpDt,
        _                => true, // a future expansion: let Faloop's filter decide
    };

    /// <summary>Creates a token on first run. Returns true if the config changed.</summary>
    public bool EnsureToken()
    {
        if (Token.Length >= 16) return false;
        RegenerateToken();
        return true;
    }

    public void RegenerateToken() => Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
