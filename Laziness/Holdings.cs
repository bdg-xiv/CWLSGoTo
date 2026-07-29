using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;

namespace Laziness;

/// <summary>
/// How much of an item you are already sitting on, counting the places it could still be
/// sold from: bags, saddlebag, retainer inventories and - the one that matters most -
/// whatever is already listed on the market board.
///
/// This is the "Owned" figure the item tooltip shows, and it comes from Allagan Tools,
/// which is the only thing that can see inside retainers while you are stood at a vendor.
/// Without it only the character's own bags can be counted, which is better than assuming
/// you hold nothing.
/// </summary>
internal static class Holdings
{
    private const string OwnedIpc = "AllaganTools.ItemCountOwned";
    private const string InitializedIpc = "AllaganTools.IsInitialized";

    // Allagan Tools reports containers using the game's own InventoryType values.
    private static readonly uint[] SellableContainers =
    [
        0, 1, 2, 3,                                       // character bags
        4000, 4001, 4002, 4003,                           // saddlebag and premium saddlebag
        10000, 10001, 10002, 10003, 10004, 10005, 10006,  // retainer bags
        10007,                                            // already listed on the market
    ];

    /// <summary>Whether the retainer-aware count is available, as opposed to bags only.</summary>
    internal static bool Complete => Ask(InitializedIpc) is true;

    /// <summary>Units already held. Falls back to the character's bags when Allagan Tools
    /// isn't answering.</summary>
    internal static unsafe int Owned(uint itemId)
    {
        try
        {
            if (Ask(InitializedIpc) is true)
                return (int)Svc.PluginInterface
                    .GetIpcSubscriber<uint, bool, uint[], uint>(OwnedIpc)
                    .InvokeFunc(itemId, true, SellableContainers);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[Laziness] Allagan Tools item count failed: {ex.Message}");
        }

        return InventoryManager.Instance()->GetInventoryItemCount(itemId);
    }

    private static bool? Ask(string name)
    {
        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>(name).InvokeFunc();
        }
        catch
        {
            return null;
        }
    }
}
