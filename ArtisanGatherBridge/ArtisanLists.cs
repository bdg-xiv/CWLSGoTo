using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.Reflection;
using Lumina.Excel.Sheets;
using System;
using System.Collections;
using System.Reflection;

namespace ArtisanGatherBridge;

/// <summary>Traces an item name back to the Artisan crafting list it was clicked in.
///
/// Artisan sends nothing but the name, so the rest is read out of its open List Editor
/// windows: each holds an ingredient table, and every ingredient knows both the list it
/// belongs to and how much of it is still missing. Everything is resolved by name at call
/// time - if Artisan reshapes these internals the lookup just comes back empty and the
/// command is handed to GatherBuddy Reborn as usual.</summary>
internal static class ArtisanLists
{
    private const string Artisan = "Artisan";
    private const string EditorTypeName = "ListEditor";

    internal sealed record Request(string ListName, uint ItemId, int Remaining, int Required);

    public static bool Available
        => DalamudReflector.TryGetDalamudPlugin(Artisan, out _, suppressErrors: true, ignoreCache: true);

    /// <summary>Finds the open crafting list this item was asked for, or null when the
    /// request didn't come from one.</summary>
    public static Request? Find(string itemName)
    {
        try
        {
            if (!DalamudReflector.TryGetDalamudPlugin(Artisan, out var plugin, suppressErrors: true, ignoreCache: true))
                return null;

            var windowSystem = plugin.GetType()
                .GetField("ws", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(plugin) as WindowSystem;
            if (windowSystem == null)
                return null;

            Request? fallback = null;
            foreach (var window in windowSystem.Windows)
            {
                if (!window.IsOpen || window.GetType().Name != EditorTypeName)
                    continue;

                var found = FindInEditor(window, itemName);
                if (found == null)
                    continue;

                // The focused editor is the one that was just clicked in. Any other is a
                // guess, so it is only used when nothing focused matched.
                if (window.IsFocused)
                    return found;

                fallback ??= found;
            }

            return fallback;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to read Artisan's crafting lists");
            return null;
        }
    }

    private static Request? FindInEditor(object editor, string itemName)
    {
        if (editor.GetType().GetField("Table", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(editor) is not { } table)
            return null;

        if (table.GetType().GetField("ListItems", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(table) is not IEnumerable ingredients)
            return null;

        foreach (var ingredient in ingredients)
        {
            if (ingredient == null)
                continue;

            var type = ingredient.GetType();

            // Artisan sends the item name straight off this row - the same ToString on the
            // same sheet row - so an exact match is what a click produces.
            if (type.GetField("Data", BindingFlags.Instance | BindingFlags.Public)?.GetValue(ingredient) is not Item data)
                continue;
            if (data.RowId == 0 || !data.Name.ToString().Equals(itemName, StringComparison.OrdinalIgnoreCase))
                continue;

            var listName = type.GetField("OriginList", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(ingredient) is { } originList
                ? originList.GetType().GetProperty("Name")?.GetValue(originList) as string
                : null;

            var remaining = type.GetProperty("Remaining", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(ingredient) as int? ?? 0;
            var required = type.GetField("Required", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(ingredient) as int? ?? 0;

            return new Request(listName ?? string.Empty, data.RowId, remaining, required);
        }

        return null;
    }
}
