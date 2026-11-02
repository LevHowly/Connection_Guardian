using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ConnectionGuardian;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.4f, 0.9f, 0.4f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.4f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly Configuration configuration;
    private readonly Guardian guardian;
    private readonly Action save;

    public MainWindow(Configuration configuration, Guardian guardian, Action save)
        : base("Connection Guardian###ConnectionGuardianMain")
    {
        this.configuration = configuration;
        this.guardian = guardian;
        this.save = save;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(1600, 1200),
        };
    }

    public override void Draw()
    {
        ImGui.TextColored(guardian.Available ? Good : Bad, guardian.Status);

        if (ImGui.CollapsingHeader("Settings"))
            DrawSettings();

        ImGui.Separator();
        ImGui.Text("Game connections");
        ImGui.SameLine();
        if (ImGui.SmallButton("Rescan"))
            guardian.Rescan();
        DrawConnections();

        ImGui.Separator();
        ImGui.Text("Events");
        ImGui.SameLine();
        ImGui.TextColored(Dim, $"(also written to {guardian.LogPath})");
        DrawEvents();
    }

    private void DrawSettings()
    {
        var changed = false;
        var tune = configuration.TuneSockets;
        if (ImGui.Checkbox("Keep connections retrying quickly during outages", ref tune))
        {
            configuration.TuneSockets = tune;
            changed = true;
        }

        var rto = configuration.RtoMaxMs;
        if (ImGui.SliderInt("Max retry interval (ms)", ref rto, 1000, 10000))
        {
            configuration.RtoMaxMs = rto;
            changed = true;
        }

        var userTimeout = configuration.UserTimeoutSeconds;
        if (ImGui.SliderInt("Give up after (s)", ref userTimeout, 30, 900))
        {
            configuration.UserTimeoutSeconds = userTimeout;
            changed = true;
        }

        var stall = configuration.StallAckMs;
        if (ImGui.SliderInt("Report outage after no ACK for (ms)", ref stall, 500, 10000))
        {
            configuration.StallAckMs = stall;
            changed = true;
        }

        var chat = configuration.ChatNotifications;
        if (ImGui.Checkbox("Print outages/recoveries in chat", ref chat))
        {
            configuration.ChatNotifications = chat;
            changed = true;
        }

        var min = configuration.PortMin;
        var max = configuration.PortMax;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("##portmin", ref min))
        {
            configuration.PortMin = Math.Clamp(min, 1, 65535);
            changed = true;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Server port range", ref max))
        {
            configuration.PortMax = Math.Clamp(max, 1, 65535);
            changed = true;
        }

        if (changed)
            save();
    }

    private void DrawConnections()
    {
        var connections = guardian.GetConnections();
        if (connections.Count == 0)
        {
            ImGui.TextColored(Dim, "No game server connection (not logged in yet?).");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("connections", 8, flags))
            return;

        ImGui.TableSetupColumn("Server");
        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Tuned");
        ImGui.TableSetupColumn("RTT");
        ImGui.TableSetupColumn("RTO");
        ImGui.TableSetupColumn("Last recv");
        ImGui.TableSetupColumn("Retrans");
        ImGui.TableSetupColumn("Outages");
        ImGui.TableHeadersRow();

        foreach (var c in connections)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(c.Remote);
            ImGui.TableNextColumn();
            if (c.Info is { } info)
            {
                var stalled = c.StallStart != null;
                var state = stalled
                    ? $"OUTAGE {(DateTime.Now - c.StallStart!.Value).TotalSeconds:F0}s"
                    : $"{Guardian.StateName(info.State)} {info.CaStateName}";
                ImGui.TextColored(stalled || info.State != 1 ? Bad : Good, state);
                ImGui.TableNextColumn();
                ImGui.TextColored(c.Tuned ? Good : Bad, c.Tuned ? "yes" : "no");
                if (c.TuneError != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(c.TuneError);
                ImGui.TableNextColumn();
                ImGui.Text($"{info.RttUs / 1000.0:F0} ms");
                ImGui.TableNextColumn();
                ImGui.Text($"{info.RtoUs / 1000} ms" + (info.Backoff > 0 ? $" (x{1 << Math.Min((int)info.Backoff, 16)})" : string.Empty));
                ImGui.TableNextColumn();
                ImGui.Text($"{info.LastDataRecvMs / 1000.0:F1} s ago");
                ImGui.TableNextColumn();
                ImGui.Text(info.TotalRetrans.ToString());
            }
            else
            {
                for (var i = 0; i < 6; i++)
                    ImGui.TableNextColumn();
            }

            ImGui.TableNextColumn();
            ImGui.Text(c.Outages.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawEvents()
    {
        if (!ImGui.BeginChild("events", Vector2.Zero, true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var e in guardian.GetEvents())
        {
            ImGui.TextColored(Dim, e.Time.ToString("HH:mm:ss"));
            ImGui.SameLine();
            if (e.Important)
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), e.Message);
            else
                ImGui.TextWrapped(e.Message);
        }

        ImGui.EndChild();
    }
}
