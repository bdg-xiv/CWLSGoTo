using System;
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

            var world = target.TargetHomeWorld.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(world))
                return;

            var key = $"{target.TargetName}@{world}";

            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText("Re-roll outfit").Build(),
                PrefixChar = 'G',
                OnClicked = _ => Reroll(key),
            });

            var pinned = wardrobe.IsPinned(key);
            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText(pinned ? "Stop remembering outfit" : "Remember outfit").Build(),
                PrefixChar = 'G',
                OnClicked = _ => TogglePinned(key),
            });
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to build the context menu entry");
        }
    }

    private void TogglePinned(string key)
    {
        var pinned = wardrobe.TogglePinned(key);
        Svc.Chat.Print(pinned
            ? $"[Glam Roulette] Keeping {key}'s outfit however long they are away."
            : $"[Glam Roulette] {key}'s outfit will be forgotten like anyone else's.");
    }

    private void Reroll(string key)
    {
        // Dropping the assignment is enough: the next framework pass sees no outfit for them
        // and hands out a new one.
        if (wardrobe.Reroll(key))
            Svc.Chat.Print($"[Glam Roulette] Re-rolling {key}.");
        else
            Svc.Chat.Print($"[Glam Roulette] {key} had no outfit from me to re-roll.");
    }
}
