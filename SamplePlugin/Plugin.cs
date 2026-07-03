using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using SamplePlugin.Windows;
using System.Collections.Generic;
using System.Linq;
using System;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const ushort GoToLinkColor = 45;
    private const string CommandName = "/cwlsgoto";

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("CWLSGoTo");
    private readonly ConfigWindow configWindow;

    private readonly ICallGateSubscriber<uint, byte, bool> teleportIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitSameDcIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitCrossDcIpc;
    private readonly ICallGateSubscriber<string, bool, string?, bool, int?, bool?, bool?, object> lifestreamTpAndChangeWorldIpc;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusyIpc;
    private readonly List<(uint Id, Aetheryte Aetheryte, MapLinkPayload MapLink, World? World)> goToLinks = [];
    private uint nextGoToLinkId;
    private (Aetheryte Aetheryte, MapLinkPayload MapLink, uint WorldId)? pendingWorldTeleport;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        // The Teleporter plugin exposes a "Teleport" IPC (aetheryteId, subIndex) -> bool
        teleportIpc = PluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport");

        // The Lifestream plugin exposes IPC to change worlds/data centers before we teleport locally
        lifestreamCanVisitSameDcIpc = PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        lifestreamCanVisitCrossDcIpc = PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        lifestreamTpAndChangeWorldIpc = PluginInterface.GetIpcSubscriber<string, bool, string?, bool, int?, bool?, bool?, object>("Lifestream.TPAndChangeWorld");
        lifestreamIsBusyIpc = PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");

        // Subscribe to the handleable chat message event (matches IChatGui.OnHandleableChatMessageDelegate)
        ChatGui.CheckMessageHandled += OnChatMessage;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the CWLS Go To channel settings."
        });
    }

    private void OnCommand(string command, string args) => ToggleConfigWindow();

    private void ToggleConfigWindow() => configWindow.Toggle();

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Configuration.WatchedChannels.Contains(message.LogKind))
            return;

        var mapLink = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null)
            return;

        var aetheryte = MapManager.GetNearestAetheryte(mapLink);
        if (aetheryte == null)
        {
            Log.Warning($"Could not find an aetheryte near map link {mapLink}");
            return;
        }

        // The destination world (if any) is named in the message text, not the sender's name
        var targetWorld = MapManager.ParseWorldFromText(message.Message.TextValue);

        var linkPayload = CreateGoToLink(aetheryte.Value, mapLink, targetWorld);
        message.Message = new SeStringBuilder()
            .Append(message.Message)
            .AddText(" ")
            .Add(linkPayload)
            .AddUiForeground(GoToLinkColor)
            .AddText("[Go To]")
            .AddUiForegroundOff()
            .Add(RawPayload.LinkTerminator)
            .Build();
    }

    private DalamudLinkPayload CreateGoToLink(Aetheryte aetheryte, MapLinkPayload mapLink, World? world)
    {
        var id = nextGoToLinkId++;
        var payload = ChatGui.AddChatLinkHandler(id, OnGoToLinkClicked);
        goToLinks.Add((id, aetheryte, mapLink, world));

        // Cap stored links so we don't leak handler registrations for very old messages.
        if (goToLinks.Count > 100)
        {
            ChatGui.RemoveChatLinkHandler(goToLinks[0].Id);
            goToLinks.RemoveAt(0);
        }

        return payload;
    }

    private void OnGoToLinkClicked(uint commandId, SeString _)
    {
        var link = goToLinks.FirstOrDefault(l => l.Id == commandId);
        if (link.MapLink == null)
            return;

        if (!PlayerState.IsLoaded)
            return;

        if (link.World == null || link.World.Value.RowId == PlayerState.CurrentWorld.RowId)
        {
            TeleportToAetheryte(link.Aetheryte, link.MapLink);
            return;
        }

        GoToWorldThenAetheryte(link.World.Value, link.Aetheryte, link.MapLink);
    }

    private void GoToWorldThenAetheryte(World world, Aetheryte aetheryte, MapLinkPayload mapLink)
    {
        try
        {
            if (lifestreamIsBusyIpc.InvokeFunc())
            {
                Log.Warning("Lifestream is busy, ignoring Go To click.");
                return;
            }

            var worldName = world.Name.ExtractText();
            var sameDc = lifestreamCanVisitSameDcIpc.InvokeFunc(worldName);
            if (!sameDc && !lifestreamCanVisitCrossDcIpc.InvokeFunc(worldName))
            {
                Log.Warning($"Lifestream cannot visit {worldName} from here.");
                return;
            }

            pendingWorldTeleport = (aetheryte, mapLink, world.RowId);
            Framework.Update += OnFrameworkUpdate;
            lifestreamTpAndChangeWorldIpc.InvokeAction(worldName, !sameDc, null, false, null, null, null);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to invoke Lifestream IPC. Is the Lifestream plugin installed? {ex.Message}");
            pendingWorldTeleport = null;
            Framework.Update -= OnFrameworkUpdate;
            GameGui.OpenMapWithMapLink(mapLink);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingWorldTeleport == null)
        {
            Framework.Update -= OnFrameworkUpdate;
            return;
        }

        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded)
            return;

        // Wait for the zone transition caused by the world hop to fully finish before
        // trying to teleport locally, otherwise Teleporter's IPC silently no-ops.
        if (Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51])
            return;

        if (PlayerState.CurrentWorld.RowId != pendingWorldTeleport.Value.WorldId)
            return;

        var pending = pendingWorldTeleport.Value;
        pendingWorldTeleport = null;
        Framework.Update -= OnFrameworkUpdate;
        TeleportToAetheryte(pending.Aetheryte, pending.MapLink);
    }

    private void TeleportToAetheryte(Aetheryte aetheryte, MapLinkPayload mapLink)
    {
        try
        {
            teleportIpc.InvokeFunc(aetheryte.RowId, 0);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to invoke Teleporter IPC. Is the Teleporter plugin installed? {ex.Message}");
        }

        GameGui.OpenMapWithMapLink(mapLink);
    }

    public void Dispose()
    {
        ChatGui.CheckMessageHandled -= OnChatMessage;
        ChatGui.RemoveChatLinkHandler();
        Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigWindow;
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }
}
