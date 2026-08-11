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
        ItemInvalid = 5,

        /// <summary>Somebody else is holding this actor's state with a key of their own, and
        /// nothing we ask for will be honoured until they let go. In practice that is Mare,
        /// which locks the characters it is syncing so they look the way their owner says.</summary>
        InvalidKey = 6,

        InvalidState = 7,
        CouldNotParse = 8,
        Unavailable = -1,
    }

    /// <summary>What a refusal means, for saying out loud rather than swallowing.</summary>
    public static string Explain(Result result) => result switch
    {
        Result.InvalidKey => "their state is locked by another plugin - Mare does this to the "
                             + "characters it syncs, and nothing of ours will take while it holds them",
        Result.ActorNotHuman => "they are not being drawn as a human",
        Result.ActorNotFound => "the game had nobody at that index by the time we asked",
        _ => "no reason given",
    };

    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<Dictionary<Guid, (string DisplayName, string FullPath, uint DisplayColor, bool ShownInQdb)>> designList;
    private readonly ICallGateSubscriber<Guid, int, uint, ulong, int> applyDesign;
    private readonly ICallGateSubscriber<int, uint, ulong, int> revertState;
    private readonly ICallGateSubscriber<int, uint, ulong, int> revertToAutomation;
    private readonly ICallGateSubscriber<Guid, Newtonsoft.Json.Linq.JObject?> designJObject;
    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> setItem;
    private readonly ICallGateSubscriber<int, uint, (int Result, Newtonsoft.Json.Linq.JObject? State)> getState;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> applyState;

    public GlamourerIpc()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        designList = Svc.PluginInterface
            .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended");
        applyDesign = Svc.PluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        revertState = Svc.PluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
        revertToAutomation = Svc.PluginInterface
            .GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertToAutomation.V2");
        designJObject = Svc.PluginInterface
            .GetIpcSubscriber<Guid, Newtonsoft.Json.Linq.JObject?>("Glamourer.GetDesignJObject");
        setItem = Svc.PluginInterface
            .GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");
        getState = Svc.PluginInterface
            .GetIpcSubscriber<int, uint, (int, Newtonsoft.Json.Linq.JObject?)>("Glamourer.GetState");
        applyState = Svc.PluginInterface
            .GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");
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

    /// <summary>
    /// Puts someone back the way Glamourer would have them rather than the way the game would:
    /// their automated design if they have one, their own gear if they do not. A plain revert
    /// undoes our outfit and the automation with it, which on yourself means taking your
    /// glamour off rather than giving it back.
    /// </summary>
    public Result RevertToAutomation(int objectIndex)
        => Call(() => revertToAutomation.InvokeFunc(objectIndex, 0,
            (ulong)(ApplyFlag.Equipment | ApplyFlag.Customization)));

    /// <summary>
    /// Copies one actor's whole look onto another. The duty cards at a duty's start are
    /// separate actors built from the server's own gear snapshot - Penumbra routes them
    /// through the right collections on its own, but Glamourer never touches them, so the
    /// outfit somebody is really shown in has to be carried across by hand. Equipment lands
    /// on a standing actor in place; customization needs a rebuild cards never get, so a
    /// race swap or a bust stays as the server drew it.
    /// </summary>
    public Result Mirror(int fromIndex, int toIndex)
    {
        try
        {
            var (result, state) = getState.InvokeFunc(fromIndex, 0);
            if ((Result)result != Result.Success)
                return (Result)result;
            if (state == null)
                return Result.ActorNotFound;

            return Call(() => applyState.InvokeFunc(state, toIndex, 0,
                (ulong)(ApplyFlag.Equipment | ApplyFlag.Customization)));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not mirror object {fromIndex} onto {toIndex}: {ex.Message}");
            return Result.Unavailable;
        }
    }

    /// <summary>
    /// Moves someone to another clan or gender, and nothing else. Their state is read back and
    /// handed straight to Glamourer with every customization switched off bar the ones named, so
    /// the only thing being asked for is what was asked for.
    ///
    /// Deliberately not spelling out the face, hair or anything else: Glamourer runs its own
    /// fix-up on either change, and it is the one that knows which of those the new clan or
    /// gender actually has. It even knows Hrothgar faces are numbered four higher than everyone
    /// else's, which is exactly the trap here. Whatever carries over sensibly - colouring,
    /// build - carries over, and the rest lands on something valid.
    /// </summary>
    public Result SetLook(int objectIndex, byte? race, byte? clan, byte? gender, byte? bust)
    {
        if (race == null && clan == null && gender == null && bust == null)
            return Result.NothingDone;

        try
        {
            var (result, state) = getState.InvokeFunc(objectIndex, 0);
            if ((Result)result != Result.Success)
                return (Result)result;

            if (state?["Customize"] is not Newtonsoft.Json.Linq.JObject customize)
                return Result.ActorNotHuman;

            foreach (var property in customize.Properties())
                if (property.Value is Newtonsoft.Json.Linq.JObject entry && entry["Apply"] != null)
                    entry["Apply"] = false;

            // Race and clan have to agree or the state is rejected as nonsense before it gets
            // anywhere near being applied. Gender stands on its own, and both of them are asked
            // for together when both apply, so a man who is also a Hrothgar costs one redraw
            // rather than one for each.
            if (race != null && !Set(customize, "Race", race.Value))
                return Result.ActorNotHuman;
            if (clan != null && !Set(customize, "Clan", clan.Value))
                return Result.ActorNotHuman;
            if (gender != null && !Set(customize, "Gender", gender.Value))
                return Result.ActorNotHuman;

            // Out of range for a race that has a shorter slider is not a failure worth having:
            // Glamourer validates every customization it is handed and pulls it back to
            // something the wearer's set actually offers.
            if (bust != null && !Set(customize, "BustSize", bust.Value))
                return Result.ActorNotHuman;

            return Call(() => applyState.InvokeFunc(state, objectIndex, 0, (ulong)ApplyFlag.Customization));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[GlamRoulette] Could not change the look of object {objectIndex}: {ex.Message}");
            return Result.Unavailable;
        }
    }

    private static bool Set(Newtonsoft.Json.Linq.JObject customize, string name, byte value)
    {
        if (customize[name] is not Newtonsoft.Json.Linq.JObject entry)
            return false;

        entry["Value"] = value;
        entry["Apply"] = true;
        return true;
    }

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
