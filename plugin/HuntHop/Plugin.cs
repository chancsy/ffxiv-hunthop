using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HuntHop.Windows;

namespace HuntHop;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/hunthop";

    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState    ClientState    { get; private set; } = null!;
    [PluginService] internal static IDataManager    DataManager    { get; private set; } = null!;
    [PluginService] internal static IFramework      Framework      { get; private set; } = null!;
    [PluginService] internal static IPluginLog      Log            { get; private set; } = null!;
    [PluginService] internal static IObjectTable    ObjectTable    { get; private set; } = null!;
    [PluginService] internal static IChatGui        ChatGui        { get; private set; } = null!;
    [PluginService] internal static ICondition      Condition      { get; private set; } = null!;
    [PluginService] internal static IPartyList      PartyList      { get; private set; } = null!;

    internal static Configuration Config { get; private set; } = null!;

    internal HuntListener  Listener   { get; }
    internal LifestreamIpc Lifestream { get; }
    internal HopService    Hops       { get; }
    internal ZoneLookup    Zones      { get; }

    private readonly WindowSystem _windows = new("HuntHop");
    private readonly ConfigWindow _configWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        PluginInterface = pluginInterface;
        pluginInterface.Inject(this);

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.EnsureToken()) Config.Save();

        Listener   = new HuntListener(() => Config.Token, m => Log.Info(m), m => Log.Warning(m));
        Lifestream = new LifestreamIpc(pluginInterface);
        Zones      = new ZoneLookup();
        Hops       = new HopService(Config, Listener, Lifestream, new SpawnSpots(Zones));

        if (Config.ListenerEnabled)
            Listener.Start(Config.Port);

        _configWindow = new ConfigWindow(this);
        _windows.AddWindow(_configWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Settings. Subcommands: go (travel to the latest alert's world), auto on|off, stop (abort Lifestream), test <world>.",
        });

        pluginInterface.UiBuilder.Draw         += _windows.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi   += _configWindow.Toggle;
        Framework.Update += Hops.OnFrameworkUpdate;
    }

    /// <summary>Restart the listener after the port or the enabled flag changed.</summary>
    internal void ApplyListenerSettings()
    {
        Listener.Stop();
        if (Config.ListenerEnabled)
            Listener.Start(Config.Port);
    }

    /// <summary>Queue a fake alert, as if it came from the userscript.</summary>
    internal void SendTestAlert(string world, string mob = "sansheya")
    {
        Listener.Enqueue(new HuntAlert
        {
            World    = world.Trim().ToLowerInvariant(),
            Mob      = mob,
            Instance = 0,
            Title    = world,
            Body     = "Test alert",
            Ts       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsTest   = true,
        });
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (sub)
        {
            case "":
            case "config":
                _configWindow.Toggle();
                break;

            case "go":
                Hops.GoLatest();
                break;

            case "auto":
                if (rest is "on" or "off")
                {
                    Config.Mode = rest == "on" ? HopMode.Auto : HopMode.Click;
                    Config.Save();
                }
                ChatGui.Print($"[HuntHop] Mode: {(Config.Mode == HopMode.Auto ? "automatic" : "click to go")}");
                break;

            case "stop":
                Hops.Stop();
                break;

            case "test":
                if (rest.Length == 0) ChatGui.PrintError("[HuntHop] Usage: /hunthop test <world>");
                else SendTestAlert(rest);
                break;

            default:
                ChatGui.PrintError($"[HuntHop] Unknown subcommand '{sub}'. Try: go, auto on|off, stop, test <world>.");
                break;
        }
    }

    public void Dispose()
    {
        Framework.Update -= Hops.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi   -= _configWindow.Toggle;

        CommandManager.RemoveHandler(Command);
        _windows.RemoveAllWindows();
        Hops.Dispose();
        Listener.Dispose();
    }
}
