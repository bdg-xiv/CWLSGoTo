using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;

namespace MountMimic;

/// <summary>
/// "Switch to mount" on the context menu of anybody currently riding: right-click them in
/// the world, in the party list, or through the target bar, and you get on the same mount.
/// If something else is under you it dismounts first and mounts as soon as the game lets
/// it. Mounts you have not unlocked are refused by name, and places that refuse mounting
/// answer with the game's own reason. /mountmimic does the same for the current target.
/// </summary>
public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/mountmimic";

    /// <summary>GeneralAction sheet row for Dismount.</summary>
    private const uint DismountAction = 23;

    /// <summary>A mount waiting for the dismount before it to finish, and when to give up.</summary>
    private (uint Id, string Name, DateTime Until)? pending;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.Framework.Update += OnFrameworkUpdate;
        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Mounts you on whatever your current target is riding.",
        });
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(CommandName);
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        ECommonsMain.Dispose();
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.Target is not MenuTargetDefault { TargetName.Length: > 0 } target)
                return;

            // The rider has to be close enough to exist as an object - a party member two
            // maps away has no model and no mount to read.
            if (target.TargetObject is not ICharacter rider || rider.CurrentMount is not { } mount)
                return;

            var me = Svc.Objects.LocalPlayer;
            if (me == null || rider.GameObjectId == me.GameObjectId)
                return;

            // Already on the same one - nothing to switch to.
            if (me.CurrentMount is { } mine && mine.RowId == mount.RowId)
                return;

            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText("Switch to mount").Build(),
                PrefixChar = 'M',
                OnClicked = _ => Ride(mount.RowId, NameOf(mount.RowId)),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "[MountMimic] Failed to build the context menu entry");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (Svc.Targets.Target is not ICharacter rider || rider.CurrentMount is not { } mount)
        {
            Svc.Chat.Print("[Mount Mimic] Target somebody who is on a mount first.");
            return;
        }

        Ride(mount.RowId, NameOf(mount.RowId));
    }

    private static string NameOf(uint mountId)
        => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Mount>().TryGetRow(mountId, out var row)
            ? row.Singular.ExtractText()
            : $"mount {mountId}";

    private void Ride(uint mountId, string name)
    {
        if (!PlayerState.Instance()->IsMountUnlocked(mountId))
        {
            Svc.Chat.Print($"[Mount Mimic] You don't have the {name} unlocked.");
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            // Off the current one first; theirs follows the moment the dismount settles.
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, DismountAction);
            pending = (mountId, name, DateTime.UtcNow + TimeSpan.FromSeconds(4));
            return;
        }

        Mount(mountId, name);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (pending is not { } wish)
            return;

        // Dismounting never finished - hovering mid-air is the usual way.
        if (DateTime.UtcNow > wish.Until)
        {
            pending = null;
            Svc.Chat.Print($"[Mount Mimic] The dismount never settled - try the {wish.Name} again from the ground.");
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounted]
            || Svc.Condition[ConditionFlag.MountOrOrnamentTransition]
            || Svc.Condition[ConditionFlag.Mounting]
            || Svc.Condition[ConditionFlag.Mounting71])
            return;

        pending = null;
        Mount(wish.Id, wish.Name);
    }

    private static void Mount(uint mountId, string name)
    {
        // Zero means usable; anything else is the id of the game's own excuse, which makes
        // a better chat line than any guess.
        var status = ActionManager.Instance()->GetActionStatus(ActionType.Mount, mountId);
        if (status != 0)
        {
            var reason = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().TryGetRow(status, out var row)
                ? row.Text.ExtractText()
                : "";
            Svc.Chat.Print($"[Mount Mimic] Can't get on the {name} here"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $" - {reason}"));
            return;
        }

        ActionManager.Instance()->UseAction(ActionType.Mount, mountId);
    }
}
