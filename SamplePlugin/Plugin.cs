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
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using SamplePlugin.Windows;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
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
    private const string BusyCheckThrottleName = "CWLSGoToLifestreamBusyCheck";
    private const string MountCheckThrottleName = "CWLSGoToMountCheck";
    private const string MountSummonThrottleName = "CWLSGoToMountSummon";
    private const string WorldHopStartThrottleName = "CWLSGoToWorldHopStart";

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("CWLSGoTo");
    private readonly ConfigWindow configWindow;
    private readonly HuntTrackerWindow huntTrackerWindow;

    internal sealed record HuntEntry(string Label, string WorldName, World? World, Aetheryte Aetheryte, MapLinkPayload MapLink, DateTime AddedAt);

    /// <summary>Hunts tracked from watched messages, newest first. Shown by <see cref="HuntTrackerWindow"/>.</summary>
    internal List<HuntEntry> Hunts { get; } = [];

    private readonly ICallGateSubscriber<uint, byte, bool> teleportIpc;
    private readonly ICallGateSubscriber<uint, byte, bool> lifestreamTeleportIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitSameDcIpc;
    private readonly ICallGateSubscriber<string, bool> lifestreamCanVisitCrossDcIpc;
    private readonly ICallGateSubscriber<string, bool, string?, bool, int?, bool?, bool?, object> lifestreamTpAndChangeWorldIpc;
    private readonly ICallGateSubscriber<bool> lifestreamIsBusyIpc;
    private readonly List<(uint Id, Aetheryte Aetheryte, MapLinkPayload MapLink, World? World)> goToLinks = [];
    private uint nextGoToLinkId;
    private sealed class WorldTeleportTask
    {
        public required Aetheryte Aetheryte;
        public required uint WorldId;
        public required string WorldName;
        public required bool CrossDc;
        public DateTime Deadline;
        public int StartAttempts;
        public bool Started; // a transfer visibly began; never re-issue after this
    }

    private WorldTeleportTask? pendingWorldTeleport;
    private DateTime nextWaitDiagnosticLog = DateTime.MinValue;

    // Tracks the "after the teleport landed" follow-up: open the Hunt Train
    // Assistant window and mount up using HTA's own mount settings.
    private sealed class ArrivalTask
    {
        public required uint TerritoryId;
        public DateTime Deadline;
        public bool SawTransition; // saw the teleport cast/loading actually happen
        public bool Arrived;
        public int MountId = -1;
    }

    private ArrivalTask? pendingArrival;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate())
            Configuration.Save();

        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(configWindow);
        huntTrackerWindow = new HuntTrackerWindow(this);
        WindowSystem.AddWindow(huntTrackerWindow);

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
            HelpMessage = "Opens the CWLS Go To channel settings. \"/cwlsgoto hunts\" toggles the hunt tracker."
        });
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("hunts", StringComparison.OrdinalIgnoreCase))
            huntTrackerWindow.Toggle();
        else
            ToggleConfigWindow();
    }

    private void ToggleConfigWindow() => configWindow.Toggle();

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (!Configuration.WatchedChannels.Contains(message.LogKind))
            return;

        // The hunt tracker is fed only by plugin reports (Faloop/Sonar print to Echo),
        // never by player chatter - [Go To] links still work on every watched channel.
        var isPluginReport = message.LogKind == XivChatType.Echo;

        // Death reports (e.g. Faloop's "... was killed") have nothing to travel to;
        // instead they clear the matching tracked hunt.
        if (message.Message.TextValue.Contains("killed", StringComparison.OrdinalIgnoreCase))
        {
            if (isPluginReport)
                RemoveKilledHunts(message.Message.TextValue);
            return;
        }

        var mapLink = message.Message.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (mapLink == null)
            return;

        var aetheryte = MapManager.GetNearestAetheryte(mapLink);
        if (aetheryte == null)
        {
            Svc.Log.Warning($"Could not find an aetheryte near map link {mapLink}");
            return;
        }

        // The destination world (if any) is named in the message text, not the sender's name.
        // Only look at the text AFTER the map link: hunt callouts and Faloop reports put the
        // mob name before the flag and the world after it, and mob names can contain world
        // names ("Kaiser Behemoth" must not send us to the Behemoth server).
        var payloads = message.Message.Payloads;
        var linkIndex = payloads.IndexOf(mapLink);
        var textAfterLink = string.Concat(payloads
            .Skip(linkIndex + 1)
            .OfType<TextPayload>()
            .Select(p => p.Text));

        // The link's own display text repeats the zone name, and zone names can
        // contain world names ("Ultima Thule" contains the JP world "Ultima") -
        // remove the zone name before scanning for the destination world.
        if (!string.IsNullOrEmpty(mapLink.PlaceName))
            textAfterLink = textAfterLink.Replace(mapLink.PlaceName, " ", StringComparison.OrdinalIgnoreCase);

        var targetWorld = MapManager.ParseWorldFromText(textAfterLink);

        if (isPluginReport)
            TrackHunt(payloads, linkIndex, targetWorld, aetheryte.Value, mapLink);

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

    // Decorations that alert sources (Sonar, Faloop, train callouts) prepend to the
    // mob name; stripped so the same hunt gets the same label regardless of source.
    private static readonly string[] RankPrefixTokens = ["rank", "s", "ss", "a", "b", "[s]", "(s)", "[a]", "(a)", "[b]", "(b)", "s:", "a:", "b:", "-", ":", "*"];

    private static string NormalizeHuntName(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && RankPrefixTokens.Contains(words[0].ToLowerInvariant()))
            words.RemoveAt(0);
        return string.Join(' ', words);
    }

    private void TrackHunt(List<Payload> payloads, int linkIndex, World? world, Aetheryte aetheryte, MapLinkPayload mapLink)
    {
        // The mob name is the text before the map link (minus game icon glyphs and
        // rank decorations, so Sonar and Faloop reports of one mob look identical).
        var label = NormalizeHuntName(CleanHuntLabel(string.Concat(payloads
            .Take(linkIndex)
            .OfType<TextPayload>()
            .Select(p => p.Text))));
        if (label.Length == 0)
            label = mapLink.PlaceName;

        var worldName = world?.Name.ExtractText() ?? "";

        // Any report of an already-tracked hunt is ignored outright - no age refresh,
        // no position update. Different sources (Sonar/Faloop) decorate the name
        // differently, so same world + same zone + one name containing the other
        // counts as the same hunt; the first report wins.
        if (Hunts.Any(h => h.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase)
            && h.MapLink.TerritoryType.RowId == mapLink.TerritoryType.RowId
            && (h.Label.Contains(label, StringComparison.OrdinalIgnoreCase)
                || label.Contains(h.Label, StringComparison.OrdinalIgnoreCase))))
        {
            Svc.Log.Information($"Ignoring repeat hunt report: {label} ({worldName})");
            return;
        }

        // Hunts never expire on their own - they leave the list only via a kill
        // report, the row's remove button, or Clear all.
        Hunts.Insert(0, new HuntEntry(label, worldName, world, aetheryte, mapLink, DateTime.UtcNow));

        if (Configuration.AutoOpenHuntWindow)
            huntTrackerWindow.IsOpen = true;
    }

    private void RemoveKilledHunts(string killText)
    {
        for (var i = Hunts.Count - 1; i >= 0; i--)
        {
            var hunt = Hunts[i];
            if (hunt.Label.Length == 0 || !killText.Contains(hunt.Label, StringComparison.OrdinalIgnoreCase))
                continue;

            // When the tracked hunt has a world, the kill report must name it too, so a
            // kill on one world doesn't clear the same mob tracked on another world.
            if (hunt.WorldName.Length > 0 && !killText.Contains(hunt.WorldName, StringComparison.OrdinalIgnoreCase))
                continue;

            Svc.Log.Information($"Removing killed hunt: {hunt.Label} ({hunt.WorldName})");
            Hunts.RemoveAt(i);
        }
    }

    private static string CleanHuntLabel(string raw)
    {
        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            // Strip SeIconChar glyphs (private use area) that Faloop prefixes messages with.
            if (c >= 0xE000 && c <= 0xF8FF)
                continue;
            builder.Append(c);
        }

        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 40 ? cleaned[..40] : cleaned;
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

        ExecuteGoTo(link.Aetheryte, link.World, link.MapLink);
    }

    /// <summary>The full Go To flow: map flag, then teleport (with a world hop first if needed).
    /// Used by both the chat [Go To] links and the hunt tracker window.</summary>
    internal void ExecuteGoTo(Aetheryte aetheryte, World? world, MapLinkPayload mapLink)
    {
        if (!Svc.PlayerState.IsLoaded)
            return;

        // Open the map and drop the flag right away, before teleporting.
        Svc.GameGui.OpenMapWithMapLink(mapLink);

        if (world == null || world.Value.RowId == Svc.PlayerState.CurrentWorld.RowId)
        {
            if (!TryTeleportToAetheryte(aetheryte))
                NotifyChat("Teleport request was not accepted - are the Teleporter/Lifestream plugins enabled?");
            // The arrival watcher waits until it has seen the teleport cast/loading
            // happen, so a declined teleport just times out quietly.
            BeginArrivalWatch(aetheryte, sawTransition: false);
            return;
        }

        GoToWorldThenAetheryte(world.Value, aetheryte);
    }

    private void BeginArrivalWatch(Aetheryte aetheryte, bool sawTransition)
    {
        pendingArrival = new ArrivalTask
        {
            TerritoryId = aetheryte.Territory.RowId,
            Deadline = DateTime.UtcNow.AddSeconds(60),
            SawTransition = sawTransition,
        };

        // Idempotent (re)subscribe: the world-hop path may already have us hooked.
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    private void GoToWorldThenAetheryte(World world, Aetheryte aetheryte)
    {
        try
        {
            var worldName = world.Name.ExtractText();
            var sameDc = lifestreamCanVisitSameDcIpc.InvokeFunc(worldName);
            if (!sameDc && !lifestreamCanVisitCrossDcIpc.InvokeFunc(worldName))
            {
                NotifyChat($"Lifestream reports it cannot visit {worldName} from here.");
                return;
            }

            // Don't drop the click when Lifestream happens to be busy right now - queue
            // the hop; the framework watcher issues (and retries) the transfer once
            // Lifestream is free and the player is ready.
            pendingWorldTeleport = new WorldTeleportTask
            {
                Aetheryte = aetheryte,
                WorldId = world.RowId,
                WorldName = worldName,
                CrossDc = !sameDc,
                Deadline = DateTime.UtcNow.AddSeconds(30),
            };
            EzThrottler.Reset(TeleportThrottleName);
            EzThrottler.Reset(WorldHopStartThrottleName);
            Svc.Framework.Update -= OnFrameworkUpdate;
            Svc.Framework.Update += OnFrameworkUpdate;
            Svc.Log.Information($"Queued world hop to {worldName} (world id {world.RowId}, crossDc={!sameDc}) then teleport to aetheryte {aetheryte.RowId} ({aetheryte.PlaceName.ValueNullable?.Name.ExtractText()})");
        }
        catch (Exception ex)
        {
            NotifyChat($"Failed to invoke Lifestream. Is the Lifestream plugin installed? ({ex.Message})");
            pendingWorldTeleport = null;
        }
    }

    private static void NotifyChat(string text)
    {
        Svc.Log.Warning(text);
        Svc.Chat.Print(text, "CWLS Go To");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingWorldTeleport == null && pendingArrival == null)
        {
            Svc.Framework.Update -= OnFrameworkUpdate;
            return;
        }

        if (pendingWorldTeleport != null)
            UpdateWorldTeleport();

        if (pendingArrival != null)
            UpdateArrival();
    }

    private void UpdateWorldTeleport()
    {
        var pending = pendingWorldTeleport!;

        var loggingNow = DateTime.UtcNow >= nextWaitDiagnosticLog;
        if (loggingNow)
            nextWaitDiagnosticLog = DateTime.UtcNow.AddSeconds(3);

        if (!Player.Available || Svc.PlayerState.CurrentWorld.RowId != pending.WorldId)
        {
            // World transfers routinely take well over 30 seconds (queueing, loading
            // screens, cross-DC transfers), so a fixed deadline from the moment of the
            // click is wrong. Keep pushing the deadline out for as long as the hop is
            // still visibly in progress; the deadline only counts down once nothing is
            // happening anymore.
            // The world-visit queue (Readying/WaitingToVisitOtherWorld) counts as in
            // progress: re-issuing the transfer while queued resets the queue position,
            // and giving up abandons a transfer that will still happen. This mirrors
            // Hunt Train Assistant, which issues the transfer once and then only waits.
            var hopInProgress = !IsScreenReady()
                || Svc.Condition[ConditionFlag.BetweenAreas]
                || Svc.Condition[ConditionFlag.BetweenAreas51]
                || Svc.Condition[ConditionFlag.ReadyingVisitOtherWorld]
                || Svc.Condition[ConditionFlag.WaitingToVisitOtherWorld]
                || (EzThrottler.Throttle(BusyCheckThrottleName, 1000) && LifestreamIsBusy());
            if (hopInProgress)
            {
                if (pending.StartAttempts > 0)
                    pending.Started = true;
                pending.Deadline = DateTime.UtcNow.AddSeconds(30);
            }
            else if (!pending.Started && Player.Available && !Player.IsBusy && !Svc.Condition[ConditionFlag.InCombat]
                && EzThrottler.Throttle(WorldHopStartThrottleName, 5000))
            {
                // Nothing is happening - issue (or re-issue) the world change instead
                // of waiting for a transfer that never started: Lifestream may have
                // been busy at click time, or the attempt was cancelled by combat,
                // movement, or a failed gateway teleport.
                if (pending.StartAttempts >= 3)
                {
                    NotifyChat($"Could not start the world transfer to {pending.WorldName} after {pending.StartAttempts} attempts, giving up.");
                    pendingWorldTeleport = null;
                    return;
                }

                pending.StartAttempts++;
                pending.Deadline = DateTime.UtcNow.AddSeconds(30);
                Svc.Log.Information($"Starting world transfer to {pending.WorldName} (attempt {pending.StartAttempts}, crossDc={pending.CrossDc})");
                try
                {
                    lifestreamTpAndChangeWorldIpc.InvokeAction(pending.WorldName, pending.CrossDc, null, false, null, null, null);
                }
                catch (Exception ex)
                {
                    NotifyChat($"Failed to invoke Lifestream for the transfer to {pending.WorldName}: {ex.Message}");
                    pendingWorldTeleport = null;
                    return;
                }
            }
            else if (DateTime.UtcNow > pending.Deadline)
            {
                NotifyChat($"Gave up waiting for the world transfer to {pending.WorldName}: still on world {Svc.PlayerState.CurrentWorld.RowId} and Lifestream reports no transfer in progress.");
                pendingWorldTeleport = null;
                return;
            }

            if (loggingNow)
                Svc.Log.Information($"Waiting for world hop to land: playerAvailable={Player.Available}, currentWorld={Svc.PlayerState.CurrentWorld.RowId}, targetWorld={pending.WorldId}, hopInProgress={hopInProgress}, attempts={pending.StartAttempts}");
            return;
        }

        // On the target world now; the remaining deadline covers the local teleport phase.
        if (DateTime.UtcNow > pending.Deadline)
        {
            NotifyChat($"Timed out waiting to teleport to the destination aetheryte after arriving on {pending.WorldName}.");
            pendingWorldTeleport = null;
            return;
        }

        // The world hop may have already dropped us in the target zone.
        if (Svc.ClientState.TerritoryType == pending.Aetheryte.Territory.RowId)
        {
            Svc.Log.Information($"Already in the target territory {Svc.ClientState.TerritoryType} after world hop");
            pendingWorldTeleport = null;
            // We just transitioned here, so the arrival follow-up can fire as
            // soon as the player is ready.
            pendingArrival = new ArrivalTask
            {
                TerritoryId = pending.Aetheryte.Territory.RowId,
                Deadline = DateTime.UtcNow.AddSeconds(60),
                SawTransition = true,
            };
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

    private void UpdateArrival()
    {
        var arrival = pendingArrival!;

        if (!arrival.Arrived && DateTime.UtcNow > arrival.Deadline)
        {
            Svc.Log.Information("Gave up waiting for the teleport to land; skipping the mount and Hunt Train Assistant window.");
            pendingArrival = null;
            return;
        }

        // Require having actually seen the teleport start (cast or loading screen)
        // so we don't fire instantly when the target zone is the current zone.
        if (!arrival.SawTransition)
        {
            if (Svc.Condition[ConditionFlag.Casting]
                || Svc.Condition[ConditionFlag.BetweenAreas]
                || Svc.Condition[ConditionFlag.BetweenAreas51]
                || !IsScreenReady())
                arrival.SawTransition = true;
            return;
        }

        if (Svc.ClientState.TerritoryType != arrival.TerritoryId)
            return;

        if (!IsScreenReady() || !Player.Interactable
            || Svc.Condition[ConditionFlag.Casting]
            || Svc.Condition[ConditionFlag.BetweenAreas]
            || Svc.Condition[ConditionFlag.BetweenAreas51])
            return;

        if (!arrival.Arrived)
        {
            arrival.Arrived = true;
            arrival.Deadline = DateTime.UtcNow.AddSeconds(20); // window for the mount attempts
            OpenHuntTrainAssistantUi();

            var (useMount, mountId) = ReadHtaMountSettings();
            if (!useMount)
            {
                pendingArrival = null;
                return;
            }

            arrival.MountId = mountId;
            Svc.Log.Information($"Arrived; mounting with Hunt Train Assistant settings (mount id {mountId}, 0 = roulette)");
        }

        if (TryMountWithHtaSettings(arrival.MountId) || DateTime.UtcNow > arrival.Deadline)
            pendingArrival = null;
    }

    /// <summary>Mirror of Hunt Train Assistant's TaskMount.MountIfCan, driven by HTA's own config.</summary>
    private static unsafe bool TryMountWithHtaSettings(int configuredMountId)
    {
        if (Player.Mounted)
            return true;
        if (configuredMountId == -1)
            return true;

        // Hold off while a mount transition or cast is in progress, and briefly after.
        if (Svc.Condition[ConditionFlag.MountOrOrnamentTransition] || Svc.Condition[ConditionFlag.Casting])
            EzThrottler.Throttle(MountCheckThrottleName, 2000, rethrottle: true);
        if (!EzThrottler.Check(MountCheckThrottleName))
            return false;

        // If mount roulette is unusable, mounting is impossible here (city, combat, ...).
        if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 9) != 0)
            return true;

        var mountId = configuredMountId;
        if (mountId != 0 && !PlayerState.Instance()->IsMountUnlocked((uint)mountId))
            mountId = 0;

        if (!Player.IsAnimationLocked && EzThrottler.Throttle(MountSummonThrottleName))
        {
            if (mountId != 0 && Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>().GetRowOrDefault((uint)mountId)?.Singular.ExtractText() is { Length: > 0 } mountName)
                Chat.ExecuteCommand($"/mount \"{mountName}\"");
            else
                Chat.ExecuteGeneralAction(9);
        }

        return false;
    }

    /// <summary>Reads UseMount/Mount from Hunt Train Assistant's config file so we follow its settings live.</summary>
    private (bool UseMount, int MountId) ReadHtaMountSettings()
    {
        try
        {
            var pluginConfigsDir = PluginInterface.ConfigDirectory.Parent;
            var path = pluginConfigsDir == null ? null : Path.Combine(pluginConfigsDir.FullName, "HuntTrainAssistant", "DefaultConfig.json");
            if (path == null || !File.Exists(path))
            {
                Svc.Log.Warning("Hunt Train Assistant config not found; skipping the mount after teleport.");
                return (false, -1);
            }

            var json = JObject.Parse(File.ReadAllText(path));
            return (json.Value<bool?>("UseMount") ?? false, json.Value<int?>("Mount") ?? 0);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to read Hunt Train Assistant mount settings: {ex.Message}");
            return (false, -1);
        }
    }

    private void OpenHuntTrainAssistantUi()
    {
        try
        {
            // HTA's /hta command toggles its window, so reach into its EzConfigGui
            // (in HTA's own ECommons copy) and force the window open instead.
            var opened = false;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "HuntTrainAssistant")
                    continue;

                var ecommons = AssemblyLoadContext.GetLoadContext(assembly)?.Assemblies
                    .FirstOrDefault(a => a.GetName().Name == "ECommons");
                if (ecommons?.GetType("ECommons.SimpleGui.EzConfigGui")?
                        .GetProperty("Window", BindingFlags.Public | BindingFlags.Static)?
                        .GetValue(null) is Window window)
                {
                    window.IsOpen = true;
                    opened = true;
                }
            }

            if (opened)
            {
                Svc.Log.Information("Opened the Hunt Train Assistant window.");
                return;
            }

            // Fallback: HTA's own toggle command (only reached when reflection found nothing).
            Svc.Commands.ProcessCommand("/hta");
            Svc.Log.Information("Hunt Train Assistant window not reachable via reflection; issued /hta instead.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"Failed to open the Hunt Train Assistant window: {ex.Message}");
        }
    }

    private bool LifestreamIsBusy()
    {
        try
        {
            return lifestreamIsBusyIpc.InvokeFunc();
        }
        catch
        {
            return false;
        }
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
