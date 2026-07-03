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
using System.Numerics;
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
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

    private const ushort GoToLinkColor = 45;
    private const string CommandName = "/cwlsgoto";

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("CWLSGoTo");
    private readonly ConfigWindow configWindow;

    private readonly ICallGateSubscriber<uint, byte, bool> teleportIpc;
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleportIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitSameDcIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitCrossDcIpc;
    private readonly ICallGateSubscriber<string, bool, string?, bool, int?, bool?, bool?, object> lifestreamTpAndChangeWorldIpc;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusyIpc;
    private readonly List<(uint Id, Aetheryte Aetheryte, MapLinkPayload MapLink, World? World)> goToLinks = [];
    private uint nextGoToLinkId;
    private (Aetheryte Aetheryte, uint WorldId, DateTime Deadline)? pendingWorldTeleport;
    private Vector3 lastPlayerPosition;
    private DateTime nextTeleportAttempt = DateTime.MinValue;
    private DateTime nextWaitDiagnosticLog = DateTime.MinValue;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);

        // The Teleporter plugin exposes a "Teleport" IPC (aetheryteId, subIndex) -> bool
        teleportIpc = PluginInterface.GetIpcSubscriber<uint, byte, bool>("Teleport");

        // The Lifestream plugin exposes IPC to change worlds/data centers before we teleport locally,
        // and also has its own local aetheryte-teleport IPC we use as a fallback for teleportIpc.
        lifestreamTeleportIpc = PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
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

        // Open the map and drop the flag right away, before teleporting.
        GameGui.OpenMapWithMapLink(link.MapLink);

        if (link.World == null || link.World.Value.RowId == PlayerState.CurrentWorld.RowId)
        {
            TryTeleportToAetheryte(link.Aetheryte);
            return;
        }

        GoToWorldThenAetheryte(link.World.Value, link.Aetheryte);
    }

    private void GoToWorldThenAetheryte(World world, Aetheryte aetheryte)
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

            pendingWorldTeleport = (aetheryte, world.RowId, DateTime.UtcNow.AddSeconds(30));
            lastPlayerPosition = ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            nextTeleportAttempt = DateTime.MinValue;
            Framework.Update += OnFrameworkUpdate;
            Log.Information($"Starting world hop to {worldName} (world id {world.RowId}, crossDc={!sameDc}) then teleport to aetheryte {aetheryte.RowId} ({aetheryte.PlaceName.ValueNullable?.Name.ExtractText()})");
            lifestreamTpAndChangeWorldIpc.InvokeAction(worldName, !sameDc, null, false, null, null, null);
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to invoke Lifestream IPC. Is the Lifestream plugin installed? {ex.Message}");
            pendingWorldTeleport = null;
            Framework.Update -= OnFrameworkUpdate;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingWorldTeleport == null)
        {
            Framework.Update -= OnFrameworkUpdate;
            return;
        }

        var pending = pendingWorldTeleport.Value;

        if (DateTime.UtcNow > pending.Deadline)
        {
            Log.Warning($"Timed out waiting to teleport to aetheryte {pending.Aetheryte.RowId} after a world hop.");
            pendingWorldTeleport = null;
            Framework.Update -= OnFrameworkUpdate;
            return;
        }

        var player = ObjectTable.LocalPlayer;
        var loggingNow = DateTime.UtcNow >= nextWaitDiagnosticLog;
        if (loggingNow)
            nextWaitDiagnosticLog = DateTime.UtcNow.AddSeconds(3);

        if (player == null || player.CurrentHp == 0)
        {
            if (loggingNow)
                Log.Information($"Waiting for local player (player null: {player == null})");
            return;
        }

        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded || PlayerState.CurrentWorld.RowId != pending.WorldId)
        {
            if (loggingNow)
                Log.Information($"Waiting for world hop to land: loggedIn={ClientState.IsLoggedIn}, playerStateLoaded={PlayerState.IsLoaded}, currentWorld={PlayerState.CurrentWorld.RowId}, targetWorld={pending.WorldId}");
            return;
        }

        // The world hop may have already dropped us in the target zone.
        if (ClientState.TerritoryType == pending.Aetheryte.Territory.RowId)
        {
            Log.Information($"Already in the target territory {ClientState.TerritoryType} after world hop");
            pendingWorldTeleport = null;
            Framework.Update -= OnFrameworkUpdate;
            return;
        }

        // Track movement across frames so we don't try to teleport while the character
        // is still sliding into place right after the world hop finishes loading.
        var isMoving = player.Position != lastPlayerPosition;
        lastPlayerPosition = player.Position;

        // Mirror Hunt Train Assistant's readiness gate: the zone transition clearing
        // (BetweenAreas) alone isn't enough - Teleporter's IPC also silently no-ops
        // while the character is still mid-animation/casting/mounting/moving.
        var betweenAreas = Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51];
        var inCombat = Condition[ConditionFlag.InCombat];
        var casting = Condition[ConditionFlag.Casting];
        var mounting = Condition[ConditionFlag.MountOrOrnamentTransition];
        if (betweenAreas || inCombat || casting || mounting || isMoving)
        {
            if (loggingNow)
                Log.Information($"Waiting for readiness: betweenAreas={betweenAreas}, inCombat={inCombat}, casting={casting}, mounting={mounting}, isMoving={isMoving}");
            return;
        }

        if (DateTime.UtcNow < nextTeleportAttempt)
            return;
        nextTeleportAttempt = DateTime.UtcNow.AddSeconds(2);

        // Don't treat a "true" return as confirmation: the in-game Teleport action has a
        // cast time and can be silently interrupted, and Teleporter/Lifestream's IPC only
        // reports whether the request was accepted, not whether the character actually
        // arrived. Keep retrying (throttled) until we observe the territory actually change.
        TryTeleportToAetheryte(pending.Aetheryte);
    }

    private bool TryTeleportToAetheryte(Aetheryte aetheryte)
    {
        try
        {
            if (teleportIpc.InvokeFunc(aetheryte.RowId, 0))
            {
                Log.Information($"Teleported to aetheryte {aetheryte.RowId} via Teleporter plugin");
                return true;
            }

            Log.Information($"Teleporter plugin declined to teleport to aetheryte {aetheryte.RowId} (returned false)");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to invoke Teleporter IPC. Is the Teleporter plugin installed? {ex.Message}");
        }

        try
        {
            if (lifestreamTeleportIpc.InvokeFunc(aetheryte.RowId, 0))
            {
                Log.Information($"Teleported to aetheryte {aetheryte.RowId} via Lifestream plugin");
                return true;
            }

            Log.Information($"Lifestream plugin declined to teleport to aetheryte {aetheryte.RowId} (returned false)");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to invoke Lifestream teleport IPC. Is the Lifestream plugin installed? {ex.Message}");
        }

        return false;
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
