using System.Collections.Concurrent;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace ConnectionGuardian;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/cguard";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("Connection Guardian");
    private readonly MainWindow mainWindow;
    private readonly SessionLog sessionLog;
    private readonly Guardian guardian;
    private readonly ConcurrentQueue<string> pendingChat = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IChatGui chat,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.clientState = clientState;
        this.chat = chat;
        this.log = log;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        sessionLog = new SessionLog(pluginInterface.GetPluginConfigDirectory());
        List<(string, string)> environment =
        [
            ("Plugin", $"Connection Guardian {typeof(Plugin).Assembly.GetName().Version}"),
            ("Dalamud", $"{typeof(IDalamudPluginInterface).Assembly.GetName().Version}"),
            ("Game state", clientState.IsLoggedIn ? $"logged in, territory {clientState.TerritoryType}" : "not logged in"),
            ("Runtime", $"{Environment.Version}, {Environment.OSVersion}"),
        ];
        guardian = new Guardian(configuration, sessionLog, environment, OnGuardianEvent);
        mainWindow = new MainWindow(configuration, guardian, sessionLog, SaveConfiguration);
        windowSystem.AddWindow(mainWindow);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        framework.Update += OnFrameworkUpdate;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        clientState.TerritoryChanged += OnTerritoryChanged;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Connection Guardian. \"/cguard note <text>\" adds a note to the log (e.g. \"/cguard note got logged out\").",
        });
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        commandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        guardian.Dispose();
        sessionLog.Dispose();
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();
        if (args.StartsWith("note", StringComparison.OrdinalIgnoreCase))
        {
            guardian.AddNote(args[4..]);
            chat.Print("[Connection Guardian] Note added to the log.");
            return;
        }

        mainWindow.Toggle();
    }

    private void OnLogin() => guardian.GameEvent($"character logged in (territory {clientState.TerritoryType})", true);

    private void OnLogout(int type, int code) =>
        guardian.GameEvent($"character LOGGED OUT (logout type {type}, code {code})", true);

    private void OnTerritoryChanged(uint territory) => guardian.GameEvent($"zone changed to territory {territory}", false);

    private void SaveConfiguration()
    {
        pluginInterface.SavePluginConfig(configuration);
        guardian.SettingsChanged();
    }

    // Called from the monitor thread; chat must be printed from the framework thread.
    private void OnGuardianEvent(string message, bool important)
    {
        log.Information(message);
        if (important && configuration.ChatNotifications)
            pendingChat.Enqueue(message);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        while (pendingChat.TryDequeue(out var message))
            chat.Print($"[Connection Guardian] {message}");
    }
}
