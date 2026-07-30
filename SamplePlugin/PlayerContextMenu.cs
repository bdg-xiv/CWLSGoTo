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

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.Target is not MenuTargetDefault target || target.TargetCharacter is not { } character)
            return;

        var territoryId = character.Location.RowId;
        if (territoryId == 0)
            return;

        // Null for anywhere there is nothing to teleport to - a duty, a housing ward, or
        // a zone the game is not reporting.
        var aetheryte = MapManager.GetTerritoryAetheryte(territoryId);
        if (aetheryte == null)
            return;

        var zone = character.Location.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(zone))
            return;

        var world = character.CurrentWorld.ValueNullable;

        // Already standing in the same zone on the same world - nothing to offer.
        if (territoryId == Svc.ClientState.TerritoryType
            && (world == null || world.Value.RowId == Svc.PlayerState.CurrentWorld.RowId))
            return;

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
