using System;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>Adds "Re-roll outfit" to the context menu on a player's name.</summary>
internal sealed class PlayerContextMenu : IDisposable
{
    private readonly Configuration config;
    private readonly Wardrobe wardrobe;

    public PlayerContextMenu(Configuration config, Wardrobe wardrobe)
    {
        this.config = config;
        this.wardrobe = wardrobe;
        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (!config.Enabled)
                return;

            if (args.Target is not MenuTargetDefault { TargetName.Length: > 0 } target)
                return;

            // A retainer or an NPC has no world to be from, so the name alone is what they are
            // known by - but only when they are one of the ones being dealt to. Everything else
            // in a city has a context menu too, and none of them want this on it.
            var world = target.TargetHomeWorld.ValueNullable?.Name.ExtractText();
            var player = string.IsNullOrEmpty(world)
                ? target.TargetObject is ICharacter other && Dealt(other) ? Wardrobe.KeyOf(other) : null
                : $"{target.TargetName}@{world}";

            if (player == null)
                return;

            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText("Re-roll outfit").Build(),
                PrefixChar = 'G',
                OnClicked = _ => Reroll(player),
            });

            // Keeping is per outfit, so it needs the role they are on right now rather than
            // just their name. Without the object in front of us there is no role to read,
            // and pinning the player as a whole would keep more than was asked for.
            if (target.TargetObject is not ICharacter character)
                return;

            var key = wardrobe.KeyFor(character);
            var pinned = wardrobe.IsPinned(key);

            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder()
                    .AddText(pinned ? "Stop remembering this outfit" : "Remember this outfit").Build(),
                PrefixChar = 'G',
                OnClicked = _ => TogglePinned(key),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to build the context menu entry");
        }
    }

    /// <summary>Whether this one is being dealt to at all, for the worldless ones.</summary>
    private bool Dealt(ICharacter character)
        => character.ObjectKind switch
        {
            ObjectKind.Retainer => config.IncludeRetainers,
            ObjectKind.EventNpc or ObjectKind.BattleNpc => config.IncludeNpcs,
            _ => false,
        };

    private void TogglePinned(string key)
    {
        var pinned = wardrobe.TogglePinned(key);
        Svc.Chat.Print(pinned
            ? $"[Glam Roulette] Keeping this outfit on {Describe(key)} however long they are away."
            : $"[Glam Roulette] This outfit on {Describe(key)} will be forgotten like any other.");
    }

    private void Reroll(string key)
    {
        // Dropping the assignment is enough: the next framework pass sees no outfit for them
        // and hands out a new one.
        if (wardrobe.Reroll(key))
            Svc.Chat.Print($"[Glam Roulette] Re-rolling {key}.");
        else
            Svc.Chat.Print($"[Glam Roulette] {key} had nothing to re-roll - either no outfit from me, " +
                           "or the ones they have are being kept.");
    }

    /// <summary>"Name@World#Tank" reads better as "Name@World as a tank".</summary>
    private static string Describe(string key)
    {
        var hash = key.IndexOf('#');
        return hash < 0 ? key : $"{key[..hash]} as {key[(hash + 1)..].ToLowerInvariant()}";
    }
}
