using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;

namespace DesynthAllCommand;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/desynthall";
    private const string PandoraFeatureName = "Desynth All";

    private readonly ICallGateSubscriber<string, bool?> pandoraGetFeatureEnabledIpc;
    private readonly ICallGateSubscriber<string, bool, object> pandoraSetFeatureEnabledIpc;

    public Plugin()
    {
        pandoraGetFeatureEnabledIpc = PluginInterface.GetIpcSubscriber<string, bool?>("PandorasBox.GetFeatureEnabled");
        pandoraSetFeatureEnabledIpc = PluginInterface.GetIpcSubscriber<string, bool, object>("PandorasBox.SetFeatureEnabled");

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the desynthesis window and arms PandorasBox's Desynth All feature."
        });
    }

    private void OnCommand(string command, string args)
    {
        try
        {
            var enabled = pandoraGetFeatureEnabledIpc.InvokeFunc(PandoraFeatureName);
            if (enabled == null)
            {
                ChatGui.PrintError("PandorasBox's \"Desynth All\" feature was not found. Is PandorasBox installed?");
                return;
            }

            if (enabled == false)
                pandoraSetFeatureEnabledIpc.InvokeAction(PandoraFeatureName, true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to reach PandorasBox's IPC. Is it installed and up to date? {ex.Message}");
            ChatGui.PrintError("Could not reach PandorasBox. Is it installed?");
            return;
        }

        OpenDesynthesisWindow();
    }

    private unsafe void OpenDesynthesisWindow()
    {
        var agent = AgentModule.Instance()->GetAgentSalvage();
        if (agent == null)
        {
            Log.Warning("Could not find the Salvage agent to open the desynthesis window.");
            return;
        }

        ((AgentInterface*)agent)->Show();
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandName);
    }
}
