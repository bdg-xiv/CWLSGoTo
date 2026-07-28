using Dalamud.Bindings.ImGui;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using System;
using System.Numerics;

namespace ArtisanGatherBridge;

/// <summary>Makes Artisan's "Gather Item" work with GatherBuddy Reborn.
///
/// Artisan's integration is thinner than it looks: it enables the menu entry when a plugin
/// with the internal name "GatherBuddy" is loaded, and clicking it types /gather &lt;item&gt;
/// into the chat box. So this plugin carries that internal name - which is the whole of
/// the impersonation - and picks the command up again on the other side. If the item can
/// be traced to an open Artisan crafting list it goes into a GatherBuddy Reborn
/// auto-gather list named after that crafting list; anything else is handed straight back
/// to GatherBuddy Reborn.</summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    /// <summary>Used when a crafting list has never been given a name.</summary>
    private const string UnnamedList = "Artisan";

    private const uint MaxQuantity = 999999;

    private readonly Configuration config;
    private readonly CommandRelay relay;

    private bool windowOpen;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this, Module.DalamudReflector);

        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        relay = new CommandRelay(Route);

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= Draw;

        relay.Dispose();
        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => windowOpen = !windowOpen;

    /// <summary>Returns false for anything that didn't come from an Artisan crafting list,
    /// which sends it back to GatherBuddy Reborn untouched.</summary>
    private bool Route(string command, string arguments)
    {
        if (arguments.Length == 0)
            return false;

        var request = ArtisanLists.Find(arguments);
        if (request == null)
            return false;

        var listName = string.IsNullOrWhiteSpace(request.ListName) ? UnnamedList : request.ListName;
        var quantity = (uint)Math.Clamp(Wanted(request), 1, MaxQuantity);

        var outcome = GatherBuddyLists.Add(listName, request.ItemId, quantity, config.EnableList, out var error);
        if (outcome == null)
        {
            Svc.Chat.PrintError($"[Artisan GBR Bridge] {arguments}: {error}");
            return true;
        }

        var verb = outcome.AddedItem ? "added to" : "updated in";
        var kind = outcome.CreatedList ? "new list" : "list";
        var woken = outcome.EnabledList && !outcome.CreatedList ? " (list switched on)" : "";
        Svc.Chat.Print($"[Artisan GBR Bridge] {arguments} x{quantity:N0} {verb} {kind} \"{listName}\".{woken}");

        if (config.StartAutoGather && GatherBuddyIpc.IsAutoGatherEnabled() == false)
            GatherBuddyIpc.SetAutoGatherEnabled(true);

        return true;
    }

    /// <summary>How much to ask GatherBuddy Reborn for. What's still missing already
    /// accounts for inventory, retainers and sub-crafts; when nothing is missing the click
    /// was deliberate, so fall back to the recipe's requirement rather than nothing.</summary>
    private int Wanted(ArtisanLists.Request request)
    {
        if (!config.UseRemaining)
            return request.Required;

        return request.Remaining > 0 ? request.Remaining : request.Required;
    }

    private void Draw()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(470, 290), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Artisan GBR Bridge###ArtisanGatherBridge", ref windowOpen))
        {
            DrawStatus();
            ImGui.Separator();
            DrawSettings();
        }

        ImGui.End();
    }

    private static void DrawStatus()
    {
        Requirement("Artisan", ArtisanLists.Available);
        Requirement("GatherBuddy Reborn", GatherBuddyLists.Available);

        static void Requirement(string name, bool present)
            => ImGui.TextColored(
                present ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
                present ? $"{name}: found" : $"{name}: missing");
    }

    private void DrawSettings()
    {
        if (ImGui.Checkbox("Gather what's missing", ref config.UseRemaining))
            config.Save();
        Hint("Uses the list's remaining count instead of the recipe's full requirement.");

        if (ImGui.Checkbox("Turn the list on", ref config.EnableList))
            config.Save();
        Hint("Ticks the auto-gather list so its items count towards what GatherBuddy Reborn gathers.");

        if (ImGui.Checkbox("Start gathering right away", ref config.StartAutoGather))
            config.Save();
        Hint("Also flips GatherBuddy Reborn's auto-gather switch, instead of waiting for you.");

        ImGui.Separator();
        ImGui.TextWrapped("Right-click an ingredient in Artisan's list editor and pick \"Gather Item\". "
            + "It lands in a GatherBuddy Reborn list named after the crafting list, and further "
            + "ingredients join the same one. Typing /gather or /gatherfish yourself is unaffected.");

        static void Hint(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }
    }
}
