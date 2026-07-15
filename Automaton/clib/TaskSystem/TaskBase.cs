using clib.Services;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Threading.Tasks;

namespace clib.TaskSystem;

[Flags]
public enum MovementOptions {
    None = 0,
    Mount = 1 << 0,
    Fly = 1 << 1,
    Dismount = 1 << 2,
}

public enum PathingStrategy {
    Auto = 0,
    Navmesh = 1,
    Direct = 2,
}

public static class MovementOptionsExtensions {
    extension(MovementOptions) {
        public static MovementOptions Current {
            get {
                if (Svc.Objects.LocalPlayer.InFlight)
                    return MovementOptions.Mount | MovementOptions.Fly | MovementOptions.Dismount;
                if (Svc.Objects.LocalPlayer.Mounted)
                    return MovementOptions.Mount | MovementOptions.Dismount;
                return MovementOptions.None;
            }
        }
    }
}

public readonly record struct MovementConfig(float? Tolerance, MovementOptions Movement, PathingStrategy Pathing) {
    public static MovementConfig Default => new(null, MovementOptions.None, PathingStrategy.Auto);
    public static MovementConfig Everything => new(null, MovementOptions.Mount | MovementOptions.Fly | MovementOptions.Dismount, PathingStrategy.Auto);
    public static MovementConfig GroundMove => new(null, MovementOptions.Mount | MovementOptions.Dismount, PathingStrategy.Auto);
    public static MovementConfig InteractRange => new(3, MovementOptions.None, PathingStrategy.Auto);

    public MovementConfig WithTolerance(float? tolerance) => this with { Tolerance = tolerance };
    public MovementConfig WithTolerance(InteractRange range) => this with { Tolerance = range.MaxDistance };
    public MovementConfig WithOptions(MovementOptions movement) => this with { Movement = movement };
    public MovementConfig WithStrategy(PathingStrategy pathing) => this with { Pathing = pathing };
}

public readonly record struct InteractRange(float MaxDistance, float MaxUpDistance) {
    public static InteractRange Aetheryte => new(8.5f, float.MaxValue);
    public static InteractRange GatheringPoint => new(3, 3);
    public static InteractRange EventObj => new(2.1f, float.MaxValue);
}

[Flags]
public enum UiSkipOptions {
    None = 0,
    Talk = 1 << 0,
    YesNo = 1 << 1,
    Request = 1 << 2,
}

public abstract class TaskBase : AutoTask {
    private readonly OverrideMovement movement = new();
    private static IPlayerCharacter? Player => Svc.Objects.LocalPlayer;

    protected TaskBase() {
        RegisterCleanup(movement);
    }

    private async Task NavmeshReady() {
        using var scope = BeginScope("WaitingForNavmesh");
        Status = "Waiting for Navmesh";
        await WaitUntil(() => Svc.Navmesh.IsReady || Svc.Navmesh.BuildProgress >= 0, "WaitForBuildStart");
        if (Svc.Navmesh.BuildProgress >= 0) {
            await WaitWhile(() => Svc.Navmesh.BuildProgress >= 0, "BuildMesh");
        }
        ErrorIf(!Svc.Navmesh.IsReady, "Failed to build navmesh for the zone");
    }

    protected async Task MoveToFlag(MovementConfig config, bool allowTeleportIfFaster = true, Func<bool>? stopCondition = null, Func<Task>? onStopReached = null) {
        using var scope = BeginScope("MoveToFlag");
        if (FlagMapMarker.Get() is not { } flag) {
            Error($"No flag set!");
            return;
        }
        var destination = flag.Position.ToVector3();
        var teleportTerritoryId = flag.TerritoryId;
        var teleportDestination = destination;
        if (flag.TerritoryId == 886) {
            teleportTerritoryId = 418;
            teleportDestination = Coords.AetherytePosition(70);
        }
        await TeleportTo(teleportTerritoryId, teleportDestination);
        await UseAethernet(flag.TerritoryId, destination);
        ErrorIf(Svc.ClientState.TerritoryType != flag.TerritoryId, $"Failed to reach flag territory (exp: {flag.TerritoryId}, act: {Svc.ClientState.TerritoryType})");
        await NavmeshReady();
        if (Svc.Navmesh.FlagToPoint() is not { } pof) {
            Error($"Unable to convert flag to point on floor");
            return;
        }
        await MoveTo(pof, config, allowTeleportIfFaster, stopCondition, onStopReached);
    }

    protected async Task MoveTo(uint territoryId, Vector3 dest, MovementConfig config, bool allowTeleportIfFaster = true, Func<bool>? stopCondition = null, Func<Task>? onStopReached = null, bool allowAethernetWithinTerritory = true) {
        using var scope = BeginScope("MoveToCmb");
        var teleportTerritoryId = territoryId;
        var teleportDestination = dest;
        if (territoryId == 886) {
            teleportTerritoryId = 418;
            teleportDestination = Coords.AetherytePosition(70);
        }
        await TeleportTo(teleportTerritoryId, teleportDestination);
        await UseAethernet(territoryId, dest);
        ErrorIf(Svc.ClientState.TerritoryType != territoryId, $"Failed to reach territory (exp: {territoryId}, act: {Svc.ClientState.TerritoryType})");
        await MoveTo(dest, config, allowTeleportIfFaster, stopCondition, onStopReached, allowAethernet: allowAethernetWithinTerritory);
        await NavmeshReady();
    }

    protected async Task MoveTo(Vector3 dest, MovementConfig config, bool allowTeleportIfFaster = true, Func<bool>? stopCondition = null, Func<Task>? onStopReached = null, bool allowAethernet = true) {
        using var scope = BeginScope("MoveTo");
        await WaitUntil(() => Player.Available, "WaitingForPlayer");
        var tolerance = Math.Max(config.Tolerance ?? 0, Svc.Navmesh.GetTolerance());
        if (Player.WithinRange(dest, tolerance))
            return;

        if (allowTeleportIfFaster && Coords.IsTeleportingFaster(dest)) {
            await TeleportTo(Svc.ClientState.TerritoryType, dest, allowSameZoneTeleport: true);
            await WaitWhile(() => Player.IsBusy, "WaitForAvailable");
        }

        if (allowAethernet)
            await UseAethernet(Svc.ClientState.TerritoryType, dest);

        if (config.Movement.HasFlag(MovementOptions.Mount) || config.Movement.HasFlag(MovementOptions.Fly))
            await Mount();

        if (config.Pathing == PathingStrategy.Direct)
            await MoveToDirectly(dest, tolerance);
        else {
            await NavmeshReady();
            await WaitWhile(() => Svc.Navmesh.PathfindInProgress, "WaitingForInProgressCalls");

            // TODO: revist this
            //var fly = Player.InFlight || config.Movement.HasFlag(MovementOptions.Fly) && Control.CanFly;
            //var pathTask = Svc.Navmesh.PathfindWithTolerance(Player!.Position, dest, fly, config.Tolerance ?? 3f);
            //ErrorIf(pathTask is null, "Failed to pathfind");

            //var waypoints = await pathTask!;
            //ErrorIf(waypoints is not { Count: > 0 }, "Failed to produce a path"); // TODO: teleport to nearest aetheryte on failure or something
            //ErrorIf(!Svc.Navmesh.MoveTo(waypoints!, fly), "Failed to MoveTo");

            ErrorIf(!Svc.Navmesh.PathfindAndMoveCloseTo(dest, Player.InFlight || config.Movement.HasFlag(MovementOptions.Fly) && Control.CanFly, config.Tolerance ?? 3f), "Failed to start pathfinding to destination");

            Status = $"Moving to {dest}";
            using var stop = new OnDispose(Svc.Navmesh.Stop);
            await NextFrame(); // tick so that vnav has a chance to flip to IsRunning

            if (stopCondition is null) {
                await WaitWhile(() => !Player.WithinRange(dest, tolerance) && (Svc.Navmesh.PathfindingInProgress || Svc.Navmesh.IsRunning()), "Navigate");
            }
            else {
                await WaitWhile(() => !Player.WithinRange(dest, tolerance) && !stopCondition() && (Svc.Navmesh.PathfindingInProgress || Svc.Navmesh.IsRunning()), "Navigate");
                if (stopCondition() && onStopReached is not null) {
                    Svc.Navmesh.Stop(); // must be stopped because onStopReached's MoveTo (if present) calls !PathfindingInProgress
                    await onStopReached();
                }
            }
        }

        if (config.Movement.HasFlag(MovementOptions.Dismount) && Player.WithinRange(dest, tolerance)) // only dismount if we're close
            await Dismount();
    }

    protected async Task MoveToDirectly(Vector3 dest, Func<bool> stopCondition) {
        using var scope = BeginScope("MoveDirectly");
        if (stopCondition())
            return;

        Status = $"Moving to {dest}";
        movement.DesiredPosition = dest;
        movement.Enabled = true;
        using var stop = new OnDispose(() => movement.Enabled = false);
        await WaitUntil(stopCondition, "WaitForCondition");
    }

    protected async Task MoveToDirectly(Vector3 dest, float tolerance) {
        using var scope = BeginScope("MoveDirectlyWithTolerance");
        await MoveToDirectly(dest, () => Player.WithinRange(dest, tolerance));
    }

    protected async Task TeleportTo(uint territoryId, FlagMapMarker flag, bool allowSameZoneTeleport = false)
        => await TeleportTo(territoryId, new Vector3(flag.XFloat, 0, flag.YFloat), allowSameZoneTeleport);

    protected async Task TeleportTo(uint territoryId, Vector3 destination, bool allowSameZoneTeleport = false) {
        using var scope = BeginScope("Teleport");
        if (!allowSameZoneTeleport && Svc.ClientState.TerritoryType == territoryId)
            return; // already in correct zone

        // must wait for ui or else a world travel (that fades ui) will conflict because teleport is called before it fades back in
        await WaitWhile(() => Player.IsUiFading, "WaitForUiUnfade");

        var closestAetheryteId = Coords.FindClosestAetheryte(territoryId, destination, includeAethernet: true) ?? 0;
        var teleportAetheryteId = Coords.FindPrimaryAetheryte(closestAetheryteId);
        ErrorIf(teleportAetheryteId == 0, $"Failed to find aetheryte in [{territoryId}] {Sheets.TerritoryType.GetRowRef(territoryId).Value.PlaceName.Value.Name}");
        if (Sheets.Aetheryte.GetRowRef(teleportAetheryteId) is { Value.Territory.RowId: var destinationId, Value.PlaceName.Value.Name: var destinationName } &&
            (Svc.ClientState.TerritoryType != destinationId || allowSameZoneTeleport)) {
            Status = $"Teleporting to {destinationName}";

            while (true) { // infinite loops are my passion
                var sawCast = false;
                var sawUiFade = false;
                ErrorIf(!ActionManager.Teleport(teleportAetheryteId), $"Failed to teleport to {teleportAetheryteId}");

                while (true) {
                    var isUiFading = Player.IsUiFading;
                    var isCasting = Player?.IsCasting ?? false;

                    if (isCasting)
                        sawCast = true;
                    if (isUiFading)
                        sawUiFade = true;

                    if (sawUiFade && !isUiFading) {
                        await WaitUntil(() => GameMain.IsTerritoryLoaded && Player.Interactable, "WaitTransportFinish");
                        return;
                    }

                    // cast ended, ui didn't fade, and cast didn't complete
                    // id resets after cast but elapsed doesn't until a new cast occurs. I'm assuming that it cannot be 5 and the teleport still gets cancelled
                    if (sawCast && !isCasting && !sawUiFade && ActionManager.GetCastAction() is not { Elapsed: 5 })
                        break;

                    await NextFrame();
                }
            }
        }
    }

    protected async Task UseAethernet(uint territoryId, Vector3 destination) {
        using var scope = BeginScope("UseAethernet");
        if (territoryId == 886) {
            // firmament special case
            Status = $"Interacting with aetheryte to get to the Firmament";
            var (firmamentObjId, firmamentObjPos) = Coords.FindAetheryte(70);
            if (firmamentObjId is 0)
                return;
            if (!Player.WithinRange(firmamentObjPos, InteractRange.Aetheryte.MaxDistance))
                await MoveTo(firmamentObjPos, MovementConfig.Default.WithTolerance(InteractRange.Aetheryte), allowTeleportIfFaster: false, allowAethernet: false);
            if (Player.Mounted)
                await Dismount();
            ErrorIf(!TargetSystem.InteractWith(firmamentObjId), "Failed to interact with aetheryte");
            await WaitUntilSkipping(() => AtkUnitBase.IsAddonReady("SelectString"), "WaitSelectFirmament", UiSkipOptions.Talk);
            PacketDispatcher.TeleportToFirmament(70);
            await WaitUntilTerritory(territoryId);
            return;
        }

        var sourceAetheryteId = Coords.FindClosestAetheryte(Svc.ClientState.TerritoryType, Player!.Position, includeAethernet: true) ?? 0;
        var destinationAetheryteId = Coords.FindClosestAetheryte(territoryId, destination, includeAethernet: true) ?? 0;
        if (sourceAetheryteId == 0 || destinationAetheryteId == 0 || sourceAetheryteId == destinationAetheryteId)
            return;

        var sourcePrimary = Coords.FindPrimaryAetheryte(sourceAetheryteId);
        var destinationPrimary = Coords.FindPrimaryAetheryte(destinationAetheryteId);
        if (sourcePrimary == 0 || sourcePrimary != destinationPrimary)
            return;

        var (aetheryteId, aetherytePos) = Coords.FindAetheryte(sourceAetheryteId) is var sourceObj && sourceObj.id != 0 ? sourceObj : Coords.FindAetheryte(sourcePrimary);
        if (aetheryteId == 0)
            return;

        Status = $"Interacting with aethernet to get to [{territoryId}]";
        if (!Player.WithinRange(aetherytePos, InteractRange.Aetheryte.MaxDistance))
            await MoveTo(aetherytePos, MovementConfig.Default.WithTolerance(InteractRange.Aetheryte), allowTeleportIfFaster: false, allowAethernet: false);
        if (Player.Mounted)
            await Dismount();
        ErrorIf(!TargetSystem.InteractWith(aetheryteId), "Failed to interact with aetheryte");

        if (Sheets.Aetheryte.GetRow(sourceAetheryteId).IsAetheryte)
            await WaitUntilSkipping(() => AtkUnitBase.IsAddonReady("SelectString"), "WaitSelectAethernet", UiSkipOptions.Talk);
        else
            await WaitUntil(() => AtkUnitBase.IsAddonReady("TelepotTown"), "WaitForAddon");
        PacketDispatcher.TeleportToAethernet(sourceAetheryteId, destinationAetheryteId);
        await WaitUntilThenFalse(() => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas], "TeleportStart");

        if (Svc.ClientState.TerritoryType != territoryId)
            await WaitUntil(() => Svc.ClientState.TerritoryType == territoryId && GameMain.IsTerritoryLoaded && Player.Interactable, "TeleportFinish");
        else
            await WaitUntil(() => GameMain.IsTerritoryLoaded && Player.Interactable, "TeleportFinishSameTerritory");
    }

    protected async Task Mount() {
        using var scope = BeginScope(nameof(Mount));
        if (!Player.CanMount) return; // early return if not in mounting territories

        Status = "Mounting";
        while (!Player.Mounted) {
            if (!Player.IsBusy && !ActionManager.IsActionInUse(ActionType.GeneralAction, 24))
                ActionManager.UseAction(ActionType.GeneralAction, 24);
            await NextFrame();
        }
    }

    protected async Task Dismount() {
        using var scope = BeginScope("Dismount");
        if (Player is null || !Player.Mounted) return;

        if (Player.InFlight) {
            if (Svc.Navmesh.NearestPointReachable(Player.Position) is { } nearestPoint)
                await MoveTo(nearestPoint, MovementConfig.Everything);
            else
                Warning($"No nearest landable point found from {Player.Position}. Dismounting may fail");
        }

        Status = "Dismounting";
        while (Player.Mounted) {
            // we are assuming from here on out that you cannot possibly be above ground that is unlandable
            if (Player.InFlight && !Player.IsAirDismountable) {
                Log($"Descending");
                ActionManager.UseAction(ActionType.GeneralAction, 23); // TODO: find a force ground function
                // await WaitWhile(() => Player.InFlight || !Player.IsAirDismountable, "WaitForGround");
            }
            else if (Player.InFlight && Player.IsAirDismountable) {
                Log($"Air Dismount");
                GameMain.ExecuteLocationCommand(LocationCommandFlag.Dismount, Player.Position, (int)Player.PackedRotation);
                //await WaitWhile(() => Player.Mounted, "WaitForDismount");
            }
            else if (Player.Mounted && !Player.InFlight) {
                Log($"Ground Dismount");
                GameMain.ExecuteCommand(CommandFlag.Dismount, 1);
                //await WaitWhile(() => Player.Mounted, "WaitForDismount");
            }
            await NextFrame();
        }
    }

    protected async Task WaitUntilSkipping(Func<bool> condition, string scopeName, UiSkipOptions skip) {
        using var scope = BeginScope(scopeName);
        while (!condition()) {
            if (skip.HasFlag(UiSkipOptions.Talk) && AtkUnitBase.IsAddonReady("Talk")) {
                Log("progressing talk...");
                AddonTalk.Progress();
            }
            if (skip.HasFlag(UiSkipOptions.YesNo) && AtkUnitBase.IsAddonReady("SelectYesno")) {
                Log("progressing yes/no...");
                AddonSelectYesno.Yes();
            }
            if (skip.HasFlag(UiSkipOptions.Request) && AtkUnitBase.IsAddonReady("Request")) {
                Log("progressing request...");
                AgentNpcTrade.TurnInRequests();
            }
            Log("waiting...");
            await NextFrame();
        }
    }

    protected async Task WaitUntilTerritory(uint territoryId) {
        using var scope = BeginScope("WaitUntilTerritory");
        await WaitUntil(() => Svc.ClientState.TerritoryType == territoryId && GameMain.IsTerritoryLoaded && Player.Interactable, "WaitingForTerritory");
    }

    protected async Task InteractWith(IGameObject obj, Func<bool>? waitUntil = null, int? selectStringIndex = null, UiSkipOptions skip = UiSkipOptions.None) {
        using var scope = BeginScope("InteractWith");

        if (!obj.IsInInteractRange()) {
            Log("Not in interact range, moving closer");
            await MoveToDirectly(obj.Position, obj.IsInInteractRange);
        }

        Status = $"Interacting with {obj.GameObjectId}";
        await WaitWhile(() => Player.IsJumping, "WaitForAbleToInteract");
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++) {
            if (TargetSystem.InteractWith(obj.GameObjectId)) {
                if (selectStringIndex is { } index) {
                    await WaitUntil(() => AtkUnitBase.IsAddonReady("SelectString"), "WaitingForSelectString");
                    AddonSelectString.Select(index);
                }
                if (waitUntil is { } condition) {
                    await WaitUntilSkipping(condition, "WaitingForNpcInteractionToFinish", skip);
                    return;
                }
                else return;
            }
            await NextFrame();
        }
        ErrorIf(true, $"Failed to interact with object after {maxAttempts} tries");
    }
}
