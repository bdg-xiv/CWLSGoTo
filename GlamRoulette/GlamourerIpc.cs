using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;

namespace GlamRoulette;

/// <summary>
/// Glamourer's published IPC, subscribed by label rather than through its NuGet package so
/// this does not carry a second copy of the API around.
/// </summary>
internal sealed class GlamourerIpc
{
    // Values of Glamourer.Api.Enums.ApplyFlag.
    [Flags]
    public enum ApplyFlag : ulong
    {
        Once = 1,
        Equipment = 2,
        Customization = 4,
        Lock = 8,
    }

    // Glamourer.Api.Enums.GlamourerApiEc; only the ones worth reacting to are named.
    public enum Result
    {
        Success = 0,
        NothingDone = 1,
        ActorNotFound = 2,
        ActorNotHuman = 3,
        DesignNotFound = 4,
        Unavailable = -1,
    }

    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)>> designList;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> applyDesign;
    private readonly ICallGateSubscriber<int, uint, ulong, int> revertState;
    private readonly ICallGateSubscriber<Guid, Newtonsoft.Json.Linq.JObject?> designJObject;
    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> setItem;

    public GlamourerIpc()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        designList = Svc.PluginInterface
            .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended");
        applyDesign = Svc.PluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        revertState = Svc.PluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        designJObject = Svc.PluginInterface
            .GetIpcSubscriber<Guid, Newtonsoft.Json.Linq.JObject?>("Glamourer.GetDesignJObject");
        setItem = Svc.PluginInterface
            .GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
    }

    /// <summary>The design as stored, so its per-slot items can be read back.</summary>
    public Newtonsoft.Json.Linq.JObject? Design(Guid design)
    {
        try
        {
            return designJObject.InvokeFunc(design);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read design {design}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-sets a slot to the item it already has, but dyed. There is no "just change the dye"
    /// call - the item has to be named again or Glamourer has nothing to put the dye on.
    /// </summary>
    public Result Dye(int objectIndex, byte slot, ulong itemId, IReadOnlyList<byte> stains)
        => Call(() => setItem.InvokeFunc(objectIndex, slot, itemId, stains, 0, (ulong)ApplyFlag.Equipment));

    /// <summary>Glamourer is loaded and speaking a version we understand.</summary>
    public bool Available
    {
        get
        {
            try
            {
                return apiVersion.InvokeFunc().Major >= 1;
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyDictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)> Designs()
    {
        try
        {
            return designList.InvokeFunc();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not read Glamourer's design list: {ex.Message}");
            return new Dictionary<Guid, (string, string, uint, bool)>();
        }
    }

    /// <summary>
    /// Equipment only, deliberately. Glamourer turns these flags into a mask over what the
    /// design is allowed to touch, so Equipment on its own masks every customization out and
    /// a design cannot hand its face, race or colouring to the wearer whatever it has ticked.
    /// Equipment|Customization is not the stricter request it looks like - it means "restrict
    /// nothing" and leaves the outcome entirely up to the design.
    ///
    /// Applied without Once, so the design sticks rather than being dropped the next time
    /// Glamourer reapplies its own automation over the top.
    /// </summary>
    public Result Apply(Guid design, int objectIndex)
        => Call(() => applyDesign.InvokeFunc(design, objectIndex, 0, (ulong)ApplyFlag.Equipment));

    /// <summary>
    /// Reverting is the opposite case and does want everything: applying an equipment-only
    /// design over someone only sets equipment, it does not take a customization off that was
    /// applied before, so undoing has to be allowed to reach further than dressing.
    /// </summary>
    public Result Revert(int objectIndex)
        => Call(() => revertState.InvokeFunc(objectIndex, 0,
            (ulong)(ApplyFlag.Equipment | ApplyFlag.Customization)));

    private static Result Call(Func<int> call)
    {
        try
        {
            return (Result)call();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Glamourer call failed: {ex.Message}");
            return Result.Unavailable;
        }
    }
}
