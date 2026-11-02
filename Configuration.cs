using Dalamud.Configuration;

namespace ConnectionGuardian;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool TuneSockets { get; set; } = true;
    public int RtoMaxMs { get; set; } = 1000;
    public int UserTimeoutSeconds { get; set; } = 300;
    public int PortMin { get; set; } = 54992;
    public int PortMax { get; set; } = 55551;
    public int StallAckMs { get; set; } = 2000;
    public bool ChatNotifications { get; set; } = true;
}
