using ECommons.DalamudServices;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace ArtisanGatherBridge;

/// <summary>Files an item into a GatherBuddy Reborn auto-gather list.
///
/// GBR's IPC only covers starting and stopping auto-gather - there is nothing public for
/// building lists - so this reaches into its loaded assembly instead. Everything is
/// resolved by name at call time and failures are reported rather than thrown, so a GBR
/// update that moves these internals turns the bridge off instead of breaking anything.</summary>
internal static class GatherBuddyLists
{
    private const string AssemblyName = "GatherBuddyReborn";
    private const string PluginTypeName = "GatherBuddy.GatherBuddy";
    private const string ListTypeName = "GatherBuddy.AutoGather.Lists.AutoGatherList";

    private const string Description = "Ingredients sent over from Artisan.";

    internal sealed record Outcome(bool CreatedList, bool AddedItem, bool EnabledList);

    public static bool Available => FindAssembly() != null;

    /// <summary>Adds the item to the list of that name, creating the list if it isn't
    /// there yet, and sets the wanted quantity. Returns null with a reason on failure.</summary>
    public static Outcome? Add(string listName, uint itemId, uint quantity, bool enableList, out string error)
    {
        error = string.Empty;

        try
        {
            var assembly = FindAssembly();
            if (assembly == null)
            {
                error = "GatherBuddy Reborn isn't loaded.";
                return null;
            }

            var listType = assembly.GetType(ListTypeName);
            var manager = FindListsManager(assembly);
            if (listType == null || manager == null)
            {
                error = "GatherBuddy Reborn's auto-gather lists have moved; this plugin needs updating.";
                return null;
            }

            var item = ResolveItem(assembly, itemId);
            if (item == null)
            {
                error = "GatherBuddy Reborn has no node or fishing spot for that item.";
                return null;
            }

            var enabledProperty = listType.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            var list = FindList(manager, listType, listName);
            var created = list == null;

            if (list == null)
            {
                list = Activator.CreateInstance(listType);
                if (list == null)
                {
                    error = "Couldn't create a GatherBuddy Reborn list.";
                    return null;
                }

                listType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.SetValue(list, listName);
                listType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance)?.SetValue(list, Description);
                enabledProperty?.SetValue(list, enableList);

                var addList = manager.GetType().GetMethod("AddList", BindingFlags.Public | BindingFlags.Instance);
                if (addList == null)
                {
                    error = "Couldn't add the list to GatherBuddy Reborn.";
                    return null;
                }

                addList.Invoke(manager, [list, null]);
            }

            // AddItem is a no-op for something already on the list, so the quantity is set
            // afterwards either way - a second click just refreshes the amount.
            var present = listType.GetProperty("Items", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(list) is IEnumerable items
                && items.Cast<object>().Any(i => ReferenceEquals(i, item));

            if (!present)
                Invoke(manager, "AddItem", [list, item]);

            Invoke(manager, "ChangeQuantity", [list, item, quantity]);

            var enabledNow = false;
            if (enableList && !created && enabledProperty?.GetValue(list) is false)
            {
                enabledProperty.SetValue(list, true);
                enabledNow = true;
            }

            Invoke(manager, "Save", []);
            Invoke(manager, "SetActiveItems", [false]);

            return new Outcome(created, !present, enabledNow || (created && enableList));
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Failed to add an item to a GatherBuddy Reborn list");
            error = ex.InnerException?.Message ?? ex.Message;
            return null;
        }
    }

    private static Assembly? FindAssembly()
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);

    /// <summary>GBR keeps its lists manager on the plugin instance, which is not exposed
    /// statically - but the static AutoGather holds an ActiveItemList that references it.
    /// (GBR itself reads _listsManager the same way.)</summary>
    private static object? FindListsManager(Assembly assembly)
    {
        var autoGather = assembly.GetType(PluginTypeName)
            ?.GetProperty("AutoGather", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (autoGather == null)
            return null;

        var activeItemList = autoGather.GetType()
            .GetField("_activeItemList", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(autoGather);

        return activeItemList?.GetType()
            .GetField("_listsManager", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(activeItemList);
    }

    private static object? FindList(object manager, Type listType, string listName)
    {
        if (manager.GetType().GetProperty("Lists", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(manager) is not IEnumerable lists)
            return null;

        var nameProperty = listType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        if (nameProperty == null)
            return null;

        return lists.Cast<object>().FirstOrDefault(l => l != null
            && nameProperty.GetValue(l) is string name
            && name.Equals(listName, StringComparison.Ordinal));
    }

    private static object? ResolveItem(Assembly assembly, uint itemId)
    {
        var gameData = assembly.GetType(PluginTypeName)
            ?.GetProperty("GameData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (gameData == null)
            return null;

        return Lookup(gameData, "Gatherables", itemId) ?? Lookup(gameData, "Fishes", itemId);
    }

    private static object? Lookup(object gameData, string property, uint itemId)
    {
        if (gameData.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(gameData) is not { } dictionary)
            return null;

        // Picked by shape rather than signature: the value type is internal to GBR, and
        // FrozenDictionary declares TryGetValue more than once across its hierarchy.
        var tryGetValue = dictionary.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TryGetValue" && m.GetParameters().Length == 2);
        if (tryGetValue == null)
            return null;

        var args = new object?[] { itemId, null };
        return tryGetValue.Invoke(dictionary, args) is true ? args[1] : null;
    }

    private static void Invoke(object target, string method, object?[] arguments)
        => target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(target, arguments);
}

/// <summary>The parts of GatherBuddy Reborn that do have an IPC.</summary>
internal static class GatherBuddyIpc
{
    /// <summary>Null when GatherBuddy Reborn isn't there to ask - which is a different
    /// thing from it answering "no".</summary>
    public static bool? IsAutoGatherEnabled() => Call<bool>("GatherBuddyReborn.IsAutoGatherEnabled");

    public static void SetAutoGatherEnabled(bool enabled)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled")
                .InvokeAction(enabled);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"GatherBuddy Reborn auto-gather toggle failed: {ex.Message}");
        }
    }

    private static T? Call<T>(string name) where T : struct
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc();
        }
        catch
        {
            return null;
        }
    }
}
