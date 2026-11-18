using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace HuntHop;

/// <summary>
/// Thin wrapper over Lifestream's IPC (NightmareXIV/Lifestream, IPC/IPCProvider.cs, checked 2026-11-18).
/// Every call fails soft: if Lifestream isn't loaded, the subscriber throws and we report "unavailable".
/// </summary>
internal sealed class LifestreamIpc
{
    /// <summary>Aetheryte sheet rows Lifestream treats as world-change aetherytes (IsWorldChangeAetheryte).</summary>
    public static readonly IReadOnlyDictionary<uint, string> WorldChangeAetherytes = new Dictionary<uint, string>
    {
        [2] = "Gridania",
        [8] = "Limsa Lominsa",
        [9] = "Ul'dah",
    };

    private readonly ICallGateSubscriber<bool>         _isBusy;
    private readonly ICallGateSubscriber<uint>         _getActiveAetheryte;
    private readonly ICallGateSubscriber<string, bool> _canVisitSameDc;
    private readonly ICallGateSubscriber<string, bool> _canVisitCrossDc;
    private readonly ICallGateSubscriber<string, bool> _changeWorld;
    private readonly ICallGateSubscriber<object>       _abort;
    private readonly ICallGateSubscriber<uint, byte, bool> _teleport;

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        _isBusy             = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _getActiveAetheryte = pi.GetIpcSubscriber<uint>("Lifestream.GetActiveAetheryte");
        _canVisitSameDc     = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        _canVisitCrossDc    = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        _changeWorld        = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        _abort              = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        _teleport           = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
    }

    /// <summary>Teleport to an Aetheryte sheet row (sub-index 0 = the aetheryte itself). null = Lifestream not loaded.</summary>
    public bool? Teleport(uint aetheryteRowId) => Try(() => _teleport.InvokeFunc(aetheryteRowId, 0));

    /// <summary>null = Lifestream not loaded.</summary>
    public bool? IsBusy() => Try(() => _isBusy.InvokeFunc());

    /// <summary>Aetheryte sheet row of the aetheryte you're standing at, 0 if none. null = Lifestream not loaded.</summary>
    public uint? ActiveAetheryte() => Try(() => _getActiveAetheryte.InvokeFunc());

    public bool? CanVisitSameDc(string world)  => Try(() => _canVisitSameDc.InvokeFunc(world));
    public bool? CanVisitCrossDc(string world) => Try(() => _canVisitCrossDc.InvokeFunc(world));

    /// <summary>
    /// Lifestream picks DC travel if the world is on another DC, otherwise world visit. It teleports
    /// to a city aetheryte first if you aren't at one. Returns false if it refused (busy / unknown world).
    /// </summary>
    public bool? ChangeWorld(string world) => Try(() => _changeWorld.InvokeFunc(world));

    public bool Abort()
    {
        try { _abort.InvokeAction(); return true; }
        catch { return false; }
    }

    public bool IsAvailable => IsBusy() != null;

    private static T? Try<T>(Func<T> f) where T : struct
    {
        try { return f(); }
        catch { return null; }
    }
}
