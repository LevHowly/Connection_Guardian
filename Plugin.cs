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
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly WindowSystem windowSystem = new("Connection Guardian");
    private readonly MainWindow mainWindow;
    private readonly Guardian guardian;
    private readonly ConcurrentQueue<string> pendingChat = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IChatGui chat,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.chat = chat;
        this.log = log;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        guardian = new Guardian(configuration, pluginInterface.GetPluginConfigDirectory(), OnGuardianEvent);
        mainWindow = new MainWindow(configuration, guardian, SaveConfiguration);
        windowSystem.AddWindow(mainWindow);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi += mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += mainWindow.Toggle;
        framework.Update += OnFrameworkUpdate;
        commandManager.AddHandler(CommandName, new CommandInfo((_, _) => mainWindow.Toggle())
        {
            HelpMessage = "Open Connection Guardian (connection monitor and outage log)",
        });
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi -= mainWindow.Toggle;
        commandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();
        guardian.Dispose();
    }

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
