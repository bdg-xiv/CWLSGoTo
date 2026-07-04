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
using ECommons;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;
using SamplePlugin.Windows;
using System.Collections.Generic;
using System.Linq;
using System;
using static ECommons.GenericHelpers;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const ushort GoToLinkColor = 45;
    private const string CommandName = "/cwlsgoto";
    private const string TeleportThrottleName = "CWLSGoToTeleport";

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
    private DateTime nextWaitDiagnosticLog = DateTime.MinValue;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

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
        Svc.Chat.CheckMessageHandled += OnChatMessage;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
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
            Svc.Log.Warning($"Could not find an aetheryte near map link {mapLink}");
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
        var payload = Svc.Chat.AddChatLinkHandler(id, OnGoToLinkClicked);
        goToLinks.Add((id, aetheryte, mapLink, world));

        // Cap stored links so we don't leak handler registrations for very old messages.
        if (goToLinks.Count > 100)
        {
            Svc.Chat.RemoveChatLinkHandler(goToLinks[0].Id);
            goToLinks.RemoveAt(0);
        }

        return payload;
    }

    private void OnGoToLinkClicked(uint commandId, SeString _)
    {
        var link = goToLinks.FirstOrDefault(l => l.Id == commandId);
        if (link.MapLink == null)
            return;

        if (!Svc.PlayerState.IsLoaded)
            return;

        // Open the map and drop the flag right away, before teleporting.
        Svc.GameGui.OpenMapWithMapLink(link.MapLink);

        if (link.World == null || link.World.Value.RowId == Svc.PlayerState.CurrentWorld.RowId)
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
                Svc.Log.Warning("Lifestream is busy, ignoring Go To click.");
                return;
            }

            var worldName = world.Name.ExtractText();
            var sameDc = lifestreamCanVisitSameDcIpc.InvokeFunc(worldName);
            if (!sameDc && !lifestreamCanVisitCrossDcIpc.InvokeFunc(worldName))
            {
                Svc.Log.Warning($"Lifestream cannot visit {worldName} from here.");
                return;
            }

            pendingWorldTeleport = (aetheryte, world.RowId, DateTime.UtcNow.AddSeconds(30));
            EzThrottler.Reset(TeleportThrottleName);
            Svc.Framework.Update += OnFrameworkUpdate;
            Svc.Log.Information($"Starting world hop to {worldName} (world id {world.RowId}, crossDc={!sameDc}) then teleport to aetheryte {aetheryte.RowId} ({aetheryte.PlaceName.ValueNullable?.Name.ExtractText()})");
            lifestreamTpAndChangeWorldIpc.InvokeAction(worldName, !sameDc, null, false, null, null, null);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to invoke Lifestream IPC. Is the Lifestream plugin installed? {ex.Message}");
            pendingWorldTeleport = null;
            Svc.Framework.Update -= OnFrameworkUpdate;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingWorldTeleport == null)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            return;
        }

        var pending = pendingWorldTeleport.Value;

        if (DateTime.UtcNow > pending.Deadline)
        {
            Svc.Log.Warning($"Timed out waiting to teleport to aetheryte {pending.Aetheryte.RowId} after a world hop.");
            pendingWorldTeleport = null;
            Svc.Framework.Update -= OnFrameworkUpdate;
            return;
        }

        var loggingNow = DateTime.UtcNow >= nextWaitDiagnosticLog;
        if (loggingNow)
            nextWaitDiagnosticLog = DateTime.UtcNow.AddSeconds(3);

        if (!Player.Available || Svc.PlayerState.CurrentWorld.RowId != pending.WorldId)
        {
            if (loggingNow)
                Svc.Log.Information($"Waiting for world hop to land: playerAvailable={Player.Available}, currentWorld={Svc.PlayerState.CurrentWorld.RowId}, targetWorld={pending.WorldId}");
            return;
        }

        // The world hop may have already dropped us in the target zone.
        if (Svc.ClientState.TerritoryType == pending.Aetheryte.Territory.RowId)
        {
            Svc.Log.Information($"Already in the target territory {Svc.ClientState.TerritoryType} after world hop");
            pendingWorldTeleport = null;
            Svc.Framework.Update -= OnFrameworkUpdate;
            return;
        }

        // Mirror Hunt Train Assistant's readiness gate using the same ECommons helpers it
        // relies on: Player.Interactable/IsBusy cover occupied/casting/moving/animation-lock/
        // combat/territory-load-state in one call, plus the "NowLoading" addon and mount check.
        var interactable = Player.Interactable;
        var busy = Player.IsBusy;
        var screenReady = IsScreenReady();
        var mounting = Svc.Condition[ConditionFlag.MountOrOrnamentTransition];
        if (!interactable || busy || !screenReady || mounting)
        {
            if (loggingNow)
                Svc.Log.Information($"Waiting for readiness: interactable={interactable}, busy={busy}, screenReady={screenReady}, mounting={mounting}");
            return;
        }

        if (!EzThrottler.Throttle(TeleportThrottleName, 2000))
            return;

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
                Svc.Log.Information($"Teleported to aetheryte {aetheryte.RowId} via Teleporter plugin");
                return true;
            }

            Svc.Log.Information($"Teleporter plugin declined to teleport to aetheryte {aetheryte.RowId} (returned false)");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to invoke Teleporter IPC. Is the Teleporter plugin installed? {ex.Message}");
        }

        try
        {
            if (lifestreamTeleportIpc.InvokeFunc(aetheryte.RowId, 0))
            {
                Svc.Log.Information($"Teleported to aetheryte {aetheryte.RowId} via Lifestream plugin");
                return true;
            }

            Svc.Log.Information($"Lifestream plugin declined to teleport to aetheryte {aetheryte.RowId} (returned false)");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to invoke Lifestream teleport IPC. Is the Lifestream plugin installed? {ex.Message}");
        }

        return false;
    }

    public void Dispose()
    {
        Svc.Chat.CheckMessageHandled -= OnChatMessage;
        Svc.Chat.RemoveChatLinkHandler();
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigWindow;
        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();

        Svc.Commands.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }
}
