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

    public GlamourerIpc()
    {
        apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        designList = Svc.PluginInterface
            .GetIpcSubscriber<Dictionary<Guid, (string, string, uint, bool)>>("Glamourer.GetDesignListExtended");
        applyDesign = Svc.PluginInterface.GetIpcSubscriber<Guid, int, uint, ulong, int>("Glamourer.ApplyDesign");
        revertState = Svc.PluginInterface.GetIpcSubscriber<int, uint, ulong, int>("Glamourer.RevertState");
    }

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
    /// Applied without Once, so the design sticks rather than being dropped the next time
    /// Glamourer reapplies its own automation over the top.
    /// </summary>
    public Result Apply(Guid design, int objectIndex)
        => Call(() => applyDesign.InvokeFunc(design, objectIndex, 0,
            (ulong)(ApplyFlag.Equipment | ApplyFlag.Customization)));

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
