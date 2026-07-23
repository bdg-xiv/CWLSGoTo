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

            Status = $"Moving to {dest}";
            using var stop = new OnDispose(Svc.Navmesh.Stop);

            // patched from upstream: manual player input can cancel the navmesh path,
            // which previously made this method return silently and left the caller
            // idle. Keep re-pathing until arrival, the stop condition fires, or the
            // interruptions don't stop.
            var repathAttempts = 0;
            while (true) {
                // patched from upstream: vnavmesh rejects a new SimpleMove request
                // while a previous pathfind task is still winding down (a one-frame
                // race — "Pathfinding complete" lands milliseconds after the
                // rejection). Wait it out and retry instead of failing the task.
                var startAttempts = 0;
                while (!Svc.Navmesh.PathfindAndMoveCloseTo(dest, Player.InFlight || config.Movement.HasFlag(MovementOptions.Fly) && Control.CanFly, config.Tolerance ?? 3f)) {
                    ErrorIf(++startAttempts > 20, "Failed to start pathfinding to destination");
                    Log($"vnavmesh rejected pathfind request, retrying (attempt {startAttempts})");
                    if (!await WaitWhile(() => Svc.Navmesh.PathfindInProgress || Svc.Navmesh.PathfindingInProgress, "WaitForPathfinderIdle"))
                        return; // user skip
                    await NextFrame(5);
                }
                await NextFrame(); // tick so that vnav has a chance to flip to IsRunning

                bool navCompleted;
                if (stopCondition is null) {
                    navCompleted = await WaitWhile(() => !Player.WithinRange(dest, tolerance) && (Svc.Navmesh.PathfindingInProgress || Svc.Navmesh.IsRunning()), "Navigate");
                }
                else {
                    navCompleted = await WaitWhile(() => !Player.WithinRange(dest, tolerance) && !stopCondition() && (Svc.Navmesh.PathfindingInProgress || Svc.Navmesh.IsRunning()), "Navigate");
                    if (stopCondition()) {
                        if (onStopReached is not null) {
                            Svc.Navmesh.Stop(); // must be stopped because onStopReached's MoveTo (if present) calls !PathfindingInProgress
                            await onStopReached();
                        }
                        break;
                    }
                }

                // patched from upstream: the user force-completed this move from the UI
                // (already at the destination); stop and proceed to the next step.
                if (!navCompleted) {
                    Svc.Navmesh.Stop();
                    break;
                }

                if (Player.WithinRange(dest, tolerance))
                    break;

                if (++repathAttempts > 20) {
                    Warning($"Navigation to {dest} keeps getting interrupted, giving up for now");
                    break;
                }

                Log($"Path was interrupted, re-pathing to {dest} (attempt {repathAttempts})");
                // patched: a user skip during this wait ends the whole move instead of
                // re-pathing; navigation is stopped by the OnDispose on exit.
                if (!await WaitWhile(() => Player.IsMoving, "WaitForManualMovementStop"))
                    break;
                if (Player.WithinRange(dest, tolerance))
                    break;
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

            var teleportAttempts = 0;
            while (true) { // infinite loops are my passion
                // patched from upstream: user force-skipped this step from the UI.
                if (ConsumeSkip()) {
                    Warning($"Teleport to {destinationName} skipped by user");
                    return;
                }

                // patched from upstream: cancel any active auto-movement before casting.
                // A running navmesh path (or override movement) cancels the teleport cast
                // instantly, which made this loop spam teleport attempts until the mover
                // reached its destination on its own.
                Svc.Navmesh.Stop();
                movement.Enabled = false;
                // patched: bounded — a stuck IsMoving flag must not hang the teleport
                // state; past the deadline just attempt the cast and let the retry
                // counter deal with a cancelled cast.
                var stopMoveBy = DateTime.UtcNow.AddSeconds(15);
                if (!await WaitWhile(() => Player.IsMoving && DateTime.UtcNow <= stopMoveBy, "WaitStopMoving")) {
                    Warning($"Teleport to {destinationName} skipped by user");
                    return;
                }

                // patched from upstream: teleporting is impossible while in combat (e.g.
                // the chocobo pulled aggro) - wait for combat to drop instead of burning
                // retry attempts on requests the game is guaranteed to refuse.
                if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]) {
                    Status = $"Waiting for combat to end before teleporting to {destinationName}";
                    var combatEnded = await WaitWhile(() => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat], "WaitOutCombat");
                    Status = $"Teleporting to {destinationName}";
                    var stopMoveAfterCombatBy = DateTime.UtcNow.AddSeconds(15);
                    if (!combatEnded || !await WaitWhile(() => Player.IsMoving && DateTime.UtcNow <= stopMoveAfterCombatBy, "WaitStopMovingAfterCombat")) {
                        Warning($"Teleport to {destinationName} skipped by user");
                        return;
                    }
                }

                // patched: the teleport cast is silently dropped while mounted or flying,
                // which used to burn every retry on casts that could never start. Land
                // and dismount first; Dismount is bounded, so a failed landing still
                // falls through to the retry counter instead of hanging here.
                if (Player.Mounted) {
                    Status = $"Dismounting before teleporting to {destinationName}";
                    await Dismount();
                    Status = $"Teleporting to {destinationName}";
                    if (Player.Mounted)
                        Warning($"Still mounted going into teleport attempt {teleportAttempts + 1}; the cast will likely not start");
                }

                // patched from upstream: never stay stuck in the teleporting state. If it
                // keeps failing (e.g. the game reports another teleport already underway),
                // give up and let the caller re-evaluate and try again later.
                if (++teleportAttempts > 6) {
                    Warning($"Teleport to {destinationName} did not go through after {teleportAttempts - 1} attempts, giving up for now");
                    return;
                }

                var sawCast = false;
                var sawUiFade = false;
                if (!ActionManager.Teleport(teleportAetheryteId)) {
                    // patched from upstream: a rejected request ("another teleport is
                    // already underway") used to hard-error; back off and retry instead.
                    Warning($"Teleport request to {destinationName} was rejected (attempt {teleportAttempts}), retrying shortly");
                    await NextFrame(120);
                    continue;
                }

                var castDeadline = DateTime.UtcNow.AddSeconds(10);
                while (true) {
                    // patched from upstream: user force-skipped this step from the UI.
                    if (ConsumeSkip()) {
                        Warning($"Teleport to {destinationName} skipped by user");
                        return;
                    }

                    var isUiFading = Player.IsUiFading;
                    var isCasting = Player?.IsCasting ?? false;

                    if (isCasting)
                        sawCast = true;
                    if (isUiFading)
                        sawUiFade = true;

                    // patched from upstream: while the cast or the zone transfer is
                    // actually running, keep pushing the deadline out; it only expires
                    // once nothing has been happening for a while.
                    if (isCasting || isUiFading)
                        castDeadline = DateTime.UtcNow.AddSeconds(10);

                    if (sawUiFade && !isUiFading) {
                        await WaitUntil(() => GameMain.IsTerritoryLoaded && Player.Interactable, "WaitTransportFinish");
                        return;
                    }

                    // cast ended, ui didn't fade, and cast didn't complete
                    // id resets after cast but elapsed doesn't until a new cast occurs. I'm assuming that it cannot be 5 and the teleport still gets cancelled
                    if (sawCast && !isCasting && !sawUiFade && ActionManager.GetCastAction() is not { Elapsed: 5 })
                        break;

                    // patched from upstream: no cast and no transfer in progress for
                    // 10 seconds - whatever the game did with the request, it is not
                    // teleporting. Cancel this attempt and retry instead of waiting
                    // forever (covers both a cast that never starts and a completed
                    // cast whose transfer silently never happens).
                    if (DateTime.UtcNow > castDeadline) {
                        Warning(sawCast
                            ? $"Teleport cast to {destinationName} ended but no transfer happened, retrying"
                            : $"Teleport cast to {destinationName} never started, retrying");
                        break;
                    }

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

        // patched from upstream: don't clobber the caller's status when already
        // mounted, and give up instead of hanging forever when mounting keeps
        // failing (e.g. a lingering combat flag after a fate).
        if (Player.Mounted) return;

        Status = "Mounting";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Player.Mounted) {
            // patched from upstream: user force-skipped this step from the UI.
            if (ConsumeSkip()) {
                Warning("Mounting skipped by user");
                return;
            }

            // patched from upstream: mounting is impossible while in combat (e.g. the
            // chocobo pulled aggro) - hold the timeout until combat ends so we mount
            // as soon as it's actually possible instead of giving up mid-fight.
            if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
                deadline = DateTime.UtcNow.AddSeconds(10);

            if (DateTime.UtcNow > deadline) {
                Warning("Mounting did not complete in time, continuing unmounted");
                return;
            }
            if (!Player.IsBusy && !ActionManager.IsActionInUse(ActionType.GeneralAction, 24))
                ActionManager.UseAction(ActionType.GeneralAction, 24);
            await NextFrame();
        }
    }

    protected async Task Dismount() {
        using var scope = BeginScope("Dismount");
        if (Player is null || !Player.Mounted) return;

        if (Player.InFlight) {
            // patched: hovering at (or just above) the landable point needs no landing
            // flight - the descend/dismount loop below touches down. Flying a
            // sub-tolerance hop makes vnav and the arrival check disagree and spam
            // "keeps getting interrupted" re-paths until the move gives up.
            if (Svc.Navmesh.NearestPointReachable(Player.Position) is { } nearestPoint) {
                if (!Player.WithinRange(nearestPoint, 5f))
                    await MoveTo(nearestPoint, MovementConfig.Everything);
            }
            else
                Warning($"No nearest landable point found from {Player.Position}. Dismounting may fail");
        }

        Status = "Dismounting";
        // patched from upstream: bound the dismount so a spot where landing never
        // completes can't hang this forever - give up with a warning and let the
        // caller retry (callers verify Player.Mounted afterwards).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Player.Mounted) {
            if (ConsumeSkip()) {
                Log("Dismount skipped by user");
                return;
            }
            if (DateTime.UtcNow > deadline) {
                Warning("Dismounting did not complete in time");
                return;
            }
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
