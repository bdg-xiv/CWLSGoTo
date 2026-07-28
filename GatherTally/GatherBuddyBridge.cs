using ECommons.DalamudServices;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace GatherTally;

/// <summary>Creates an auto-gather list inside GatherBuddy Reborn and makes it the one
/// being gathered.
///
/// GBR's IPC only covers starting and stopping auto-gather - there is nothing public for
/// building lists - so this reaches into its loaded assembly instead. Everything is
/// resolved by name at call time and any failure is reported rather than thrown, so a GBR
/// update that moves these internals disables the button instead of breaking the plugin.
/// The list itself is described purely by item ids: GBR's own FromConfig resolves those to
/// its gatherables and fish, so none of its types have to be constructed here.</summary>
public static class GatherBuddyBridge
{
    private const string AssemblyName = "GatherBuddyReborn";
    private const string PluginTypeName = "GatherBuddy.GatherBuddy";
    private const string ListTypeName = "GatherBuddy.AutoGather.Lists.AutoGatherList";

    /// <summary>Every list this plugin makes is named with this, so they can be told apart
    /// from the user's own and cleared out again.</summary>
    public const string ListPrefix = "GT: ";

    public static bool Available => FindAssembly() != null;

    /// <summary>Deletes every list this plugin created. Returns how many went, or -1 if
    /// GatherBuddy Reborn couldn't be reached.</summary>
    public static int DeleteOwnLists()
    {
        try
        {
            var assembly = FindAssembly();
            var listType = assembly?.GetType(ListTypeName);
            var manager = assembly == null ? null : FindListsManager(assembly);
            if (listType == null || manager == null)
                return -1;

            var nameProperty = listType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            var deleteList = manager.GetType().GetMethod("DeleteList", BindingFlags.Public | BindingFlags.Instance);
            if (nameProperty == null || deleteList == null)
                return -1;

            if (manager.GetType().GetProperty("Lists", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(manager) is not IEnumerable lists)
                return -1;

            // Materialise first - deleting walks the same collection.
            var doomed = lists.Cast<object>()
                .Where(l => l != null
                    && nameProperty.GetValue(l) is string name
                    && name.StartsWith(ListPrefix, StringComparison.Ordinal))
                .ToList();

            foreach (var list in doomed)
                deleteList.Invoke(manager, [list]);

            return doomed.Count;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to delete GatherBuddy Reborn lists");
            return -1;
        }
    }

    private static Assembly? FindAssembly()
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

    /// <summary>Builds a one-item list, enables it, and disables every other active list
    /// so it is the only thing GBR gathers. Returns an error message, or null on success;
    /// <paramref name="deactivated"/> lists the lists that were switched off.</summary>
    public static string? CreateAndActivate(string name, string description, uint itemId, uint quantity, out List<string> deactivated)
    {
        deactivated = [];

        try
        {
            var assembly = FindAssembly();
            if (assembly == null)
                return "GatherBuddy Reborn isn't loaded.";

            var listType = assembly.GetType(ListTypeName);
            var configType = listType?.GetNestedType("Config");
            var fromConfig = listType?.GetMethod("FromConfig", BindingFlags.Public | BindingFlags.Static);
            if (listType == null || configType == null || fromConfig == null)
                return "GatherBuddy Reborn's list format has changed; this button needs updating.";

            var manager = FindListsManager(assembly);
            if (manager == null)
                return "Couldn't reach GatherBuddy Reborn's auto-gather lists.";

            var config = Activator.CreateInstance(configType)!;
            if (!SetField(configType, config, "ItemIds", new[] { itemId })
                || !SetField(configType, config, "Quantities", new Dictionary<uint, uint> { [itemId] = quantity })
                || !SetField(configType, config, "PrefferedLocations", new Dictionary<uint, uint>())
                || !SetField(configType, config, "EnabledItems", new Dictionary<uint, bool> { [itemId] = true })
                || !SetField(configType, config, "Name", name)
                || !SetField(configType, config, "Description", description)
                || !SetField(configType, config, "FolderPath", string.Empty)
                || !SetField(configType, config, "Order", 0)
                || !SetField(configType, config, "Enabled", true)
                || !SetField(configType, config, "Fallback", false)
                || !SetField(configType, config, "RemoveCompletedItems", false))
                return "GatherBuddy Reborn's list format has changed; this button needs updating.";

            var args = new object?[] { config, null };
            fromConfig.Invoke(null, args);
            if (args[1] is not { } list)
                return "GatherBuddy Reborn rejected the list - is the item gatherable?";

            // Anything else still enabled would be gathered alongside this, so the new
            // list would not be "the" active one. Fallback lists are a separate mechanism
            // and are left alone.
            deactivated = DisableOtherLists(manager, listType);

            var addList = manager.GetType().GetMethod("AddList", BindingFlags.Public | BindingFlags.Instance);
            if (addList == null)
                return "Couldn't add the list to GatherBuddy Reborn.";
            addList.Invoke(manager, [list, null]);

            manager.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance)?.Invoke(manager, null);
            manager.GetType().GetMethod("SetActiveItems", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(manager, [false]);

            return null;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to create a GatherBuddy Reborn list");
            return $"GatherBuddy Reborn refused the list: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>GBR keeps its lists manager on the plugin instance, which is not exposed
    /// statically - but the static AutoGather holds an ActiveItemList that references it.
    /// (GBR itself reads _listsManager the same way.)</summary>
    private static object? FindListsManager(Assembly assembly)
    {
        var pluginType = assembly.GetType(PluginTypeName);
        var autoGather = pluginType?.GetProperty("AutoGather", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (autoGather == null)
            return null;

        var activeItemList = autoGather.GetType()
            .GetField("_activeItemList", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(autoGather);
        if (activeItemList == null)
            return null;

        return activeItemList.GetType()
            .GetField("_listsManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(activeItemList);
    }

    private static List<string> DisableOtherLists(object manager, Type listType)
    {
        var disabled = new List<string>();

        var lists = manager.GetType().GetProperty("Lists", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager);
        if (lists is not IEnumerable enumerable)
            return disabled;

        var enabledProperty = listType.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
        var fallbackProperty = listType.GetProperty("Fallback", BindingFlags.Public | BindingFlags.Instance);
        var nameProperty = listType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        if (enabledProperty == null || fallbackProperty == null)
            return disabled;

        foreach (var other in enumerable)
        {
            if (other == null)
                continue;
            if (enabledProperty.GetValue(other) is not true || fallbackProperty.GetValue(other) is true)
                continue;

            enabledProperty.SetValue(other, false);
            disabled.Add(nameProperty?.GetValue(other) as string ?? "?");
        }

        return disabled;
    }

    private static bool SetField(Type type, object target, string field, object value)
    {
        var info = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
        if (info == null)
        {
            Svc.Log.Warning($"GatherBuddy Reborn list config has no field '{field}'");
            return false;
        }

        info.SetValue(target, value);
        return true;
    }
}
