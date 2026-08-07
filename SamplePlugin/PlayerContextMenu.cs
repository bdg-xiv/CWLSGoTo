using Dalamud.Game.Gui.ContextMenu;
using ECommons.DalamudServices;
using System;

namespace SamplePlugin;

/// <summary>
/// Adds a Go To entry to the context menu of anyone the game will tell us the whereabouts
/// of - party finder listings, party members, friends, free company, linkshell and
/// cross-world linkshell - which teleports to that zone's aetheryte, hopping worlds first
/// when they are on another one.
///
/// The zone is read from the character data the game itself hangs off the context menu, so
/// one handler covers every list that shows a location without having to know which list
/// was clicked. Right-clicking someone stood in front of you carries no such data and gets
/// no entry, which costs nothing: you are already there.
/// </summary>
internal sealed class PlayerContextMenu : IDisposable
{
    private readonly Plugin plugin;

    internal PlayerContextMenu(Plugin plugin)
    {
        this.plugin = plugin;
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    /// <summary>
    /// The party finder is the one list that shows a location in its own tooltip - "Current
    /// Location: Garlemald" - so it is the one place where offering nothing looks like a fault
    /// rather than the game simply not saying. Said out loud there, and nowhere else, since
    /// every other list is right far more often than not.
    /// </summary>
    private const string PartyFinderAddon = "LookingForGroup";

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target)
            return;

        var partyFinder = args.AddonName == PartyFinderAddon;

        if (target.TargetCharacter is not { } character)
        {
            if (partyFinder)
                Svc.Log.Information($"[CWLSGoTo] The party finder handed over no character for " +
                                    $"{target.TargetName}, so there is no zone to go to");

            return;
        }

        var territoryId = character.Location.RowId;
        if (territoryId == 0)
        {
            if (partyFinder)
                Svc.Log.Information($"[CWLSGoTo] The party finder gave {target.TargetName} no location, " +
                                    "though its own tooltip has one");

            return;
        }

        // Null for anywhere there is nothing to teleport to - a duty, a housing ward, or
        // a zone the game is not reporting.
        var aetheryte = MapManager.GetTerritoryAetheryte(territoryId);
        if (aetheryte == null)
        {
            if (partyFinder)
                Svc.Log.Information($"[CWLSGoTo] {target.TargetName} is in territory {territoryId}, " +
                                    "which has no aetheryte to teleport to");

            return;
        }

        var zone = character.Location.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(zone))
        {
            if (partyFinder)
                Svc.Log.Information($"[CWLSGoTo] Territory {territoryId} has no name to show, " +
                                    $"so {target.TargetName} gets no entry");

            return;
        }

        var world = character.CurrentWorld.ValueNullable;

        // Already standing in the same zone on the same world - nothing to offer.
        if (territoryId == Svc.ClientState.TerritoryType
            && (world == null || world.Value.RowId == Svc.PlayerState.CurrentWorld.RowId))
        {
            if (partyFinder)
                Svc.Log.Information($"[CWLSGoTo] {target.TargetName} is in {zone} on your own world, " +
                                    "which is where you are");

            return;
        }

        var elsewhere = world != null && world.Value.RowId != Svc.PlayerState.CurrentWorld.RowId
            ? $" ({world.Value.Name.ExtractText()})"
            : string.Empty;

        args.AddMenuItem(new MenuItem
        {
            Name = $"Go To {zone}{elsewhere}",
            PrefixChar = 'G',
            OnClicked = _ => plugin.ExecuteGoTo(aetheryte.Value, world, null),
        });
    }
}
