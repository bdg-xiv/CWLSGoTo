using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using TerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace ComplexTweaks.Tweaks;

internal sealed class FateGrind(FateToolKit tweak) : TaskBase {
    private const string _presetName = "CBT - DwD";
    private const string _presetCompressed = "GwwhAORUXTtl2E+e+WjPVqrAATn5Vo7kAFPsV3yB7j9/70DoI3U+9gNJLIvWvQ+obKxFY4OEmQ5LM14Tx7oq1h1/UtrJk9/GAwBR4oGEDyIKa3iC+BSLybsB0rb9npzw4dspAURonZO29YKebWcZWVvjSe2aoHg+pMZuTSx8+PzJgL9NCMPrDpud8EGkQZ3QwcEQTdWNFT6IxOByT6341fL41D2aVRGTJSCWCPYnvLgJN+ZvXIKQ6ndG48VlJJCcwn61crTbpGd7QNbNTgZMj2mPF2IriLSt8EFUG73iCUTc7SePgrnkBySYJwxDpX3VZi0DDoLHZ0x5FxYZ5Z22cuqrsildhctZ5GgfouzrTBLj2hg+mvDXxoQTZWZtKG0rPS0Di6i2Rxh4MUrLWvSl7MmwPhBzuOEq4mNlFjsEwlIFUt61Zcm28jvIwMtCaRsG3sy8dpo32qwrg/8SGqbQrENNwGSafVkpi7ITlqwM7ds7i9TrshKraXcvUA4zgJn2y4PlNfc/y0pQ1fL+aAlV2HzfEinuZTiz07eRtmm2F7qY10AJYXH3bFNttNtE9rC0nR8sKnYR5SRWWaeneLcuRaZn1nunALFUF9NwgARvhesSntF4ETqmTvjwqUfaICfcUxcUfxRE0p5oQuSTEvi4azS77/UsBfNZ2u6Ae2mPdCCTrcAmJsfaUKRIeDUL5M1Km8gyu/EA4DEePAA=";
    private static readonly string _preset = _presetCompressed.FromBase64();

    private int PullSize => Player.ClassJob.Value switch {
        var cj when cj.IsTank => 0, // unlimited
        var cj when cj.IsDps => 3,
        var cj when cj.IsHealer => 5,
        _ => 1,
    };

    protected override async Task Execute() {
        using var stop = new OnDispose(() => Svc.TextAdvance.DisableExternalControl(Name));
        if (Service.BossMod.Get(_presetName) is not null) // one time overwrite in case I update the preset
            Service.BossMod.Create(_preset, true);
        try {
            while (!CancelToken.IsCancellationRequested && tweak.Running) {
                tweak.StopIfNoRemaining();
                if (tweak.PendingStopWhenSafe && PublicEvent.CurrentFate is null && !Svc.Condition[ConditionFlag.InCombat]) {
                    tweak.PendingStopWhenSafe = false;
                    tweak.Running = false;
                }
                if (!tweak.Running)
                    break;

                var state = State;
                tweak.CurrentState = state.ToString();

                // patched from upstream: the no-fates grace timer only runs while the
                // zone is actually dry; any other state resets it.
                if (state != GrindState.WaitingForFates) {
                    _noFatesSince = null;
                    _consecutiveDrySwaps = 0;
                }

                HandleIntegrations();

                switch (state) {
                    case GrindState.Unconscious:
                        await Revive();
                        break;
                    case GrindState.Engaging:
                        // this should only ever happen during hot reloading vbm during a fate
                        if (PublicEvent.CurrentFate is { IsOnMap: true } current && !Svc.BossMod.HasTempMap())
                            await GenerateObstacleMap(current);
                        await NextFrame();
                        break;
                    case GrindState.WaitingForFollowUp:
                        await NextFrame(100);
                        break;
                    case GrindState.BetweenFates:
                        await MoveToFate();
                        break;
                    case GrindState.WaitingForFates:
                        await HandleNoFates();
                        break;
                    case GrindState.SwapZones:
                        await SwapNewItemTarget();
                        break;
                    default:
                        await NextFrame();
                        break;
                }
            }
        }
        catch (OperationCanceledException) {
            throw; // expected, don't log
        }
        catch (Exception ex) {
            Error($"Error: {ex}");
            tweak.Running = false;
        }
    }

    public PublicEvent? NextFate { get; set; }
    private uint? ReturnToFateId { get; set; } // when we die, if the fate we were in progressed enough to not qualify, we want to return to it anyway
    private uint? LastStuckFateId { get; set; }
    private int ConsecutiveStuckRetries { get; set; }
    private uint? FollowUpFateId { get; set; } // id to store to check if NextFate is a follow up to this
    private long FollowUpWatchUntilMs { get; set; }
    private uint? WaitForExpiryFateId { get; set; } // id for when we leave a collect fate. Stay in zone until fate is null
    private DateTime? _noFatesSince; // patched: when the current zone first reported no fates
    private int _consecutiveDrySwaps; // patched: zone swaps in a row without finding any fate

    public IOrderedEnumerable<PublicEvent> AvailableFates => FateToolKit.ApplySortOrder(PublicEvent.Fates.Where(tweak.FateConditions), tweak.Config.SortOrder);
    private bool HasTwistOfFate => Player.Status.HasTwistOfFate();

    private GrindState State {
        get {
            if (WaitForExpiryFateId is { } waitId && PublicEvent.GetFateById(waitId) is null)
                WaitForExpiryFateId = null;

            if (Svc.Condition[ConditionFlag.Unconscious]) {
                if (PublicEvent.CurrentFate is { Id: var id, Progress: < 100 })
                    ReturnToFateId = id;
                FollowUpFateId = null;
                return GrindState.Unconscious;
            }

            if (PublicEvent.CurrentFate is { } current) {
                if (current.Progress >= 100)
                    StartFollowUpWatch(current.Id);
                else if (FollowUpFateId == current.Id)
                    FollowUpFateId = null;

                // treat completed collect fates as done and wait for out of combat/not busy before trying to move away
                if (current is { Rule: PublicEvent.FateRule.Collect, Progress: >= 100, Id: var id } && !Player.IsBusy) {
                    WaitForExpiryFateId = id;
                    return AvailableFates.FirstOrDefault(f => f.Id != id) is { } ? GrindState.BetweenFates : GrindState.WaitingForFates;
                }
                Status = "Engaging";
                return GrindState.Engaging;
            }

            if (ShouldWaitForFollowUp())
                return GrindState.WaitingForFollowUp;

            if (!HasTwistOfFate && !Svc.Condition[ConditionFlag.InCombat] && tweak.IsZoneItemTargetComplete(Player.Territory.RowId, out _))
                return GrindState.SwapZones;

            if (AvailableFates.FirstOrDefault() is { })
                return GrindState.BetweenFates;

            if (!AvailableFates.Any())
                return GrindState.WaitingForFates;

            return GrindState.Idle;
        }
    }
    private enum GrindState {
        Idle,
        WaitingForFates,
        WaitingForFollowUp,
        BetweenFates,
        SwapZones,
        Engaging,
        Unconscious,
    }

    private enum MoveStopReason {
        None,
        FateInvalid,
        FatePending,
        HigherPriority,
        NpcLoaded,
        StuckRetry,
        StuckTeleport,
    }

    private sealed class MoveTracker(Vector3 initialPosition, long initialTick) {
        private Vector3 LastProgressPosition { get; set; } = initialPosition;
        private long LastProgressAt { get; set; } = initialTick;
        private long LastPathActivityAt { get; set; } = initialTick;
        private Vector3 RetryPosition { get; set; }
        private bool RetriedOnce { get; set; }
        private bool WasRunning { get; set; }

        public MoveStopReason CheckStuck(Vector3 currentPosition) {
            var now = Environment.TickCount64;
            var isRunning = Svc.Navmesh.IsRunning();
            var isPathfinding = Svc.Navmesh.PathfindInProgress();

            if (isRunning || isPathfinding)
                LastPathActivityAt = now;

            if (!isRunning) {
                WasRunning = false;
                LastProgressPosition = currentPosition;
                LastProgressAt = now;

                // if vnav hard fails then it'll go back to being idle while MoveTo is waiting for it
                if (!isPathfinding && now - LastPathActivityAt >= 1500) {
                    if (RetriedOnce && Vector3.Distance(currentPosition, RetryPosition) <= 3f)
                        return MoveStopReason.StuckTeleport;

                    RetryPosition = currentPosition;
                    RetriedOnce = true;
                    return MoveStopReason.StuckRetry;
                }

                return MoveStopReason.None;
            }

            if (!WasRunning) {
                WasRunning = true;
                LastProgressPosition = currentPosition;
                LastProgressAt = now;
                return MoveStopReason.None;
            }

            if (Vector3.Distance(currentPosition, LastProgressPosition) > 1.5f) {
                LastProgressPosition = currentPosition;
                LastProgressAt = now;
                return MoveStopReason.None;
            }

            if (now - LastProgressAt < 2000)
                return MoveStopReason.None;

            if (RetriedOnce && Vector3.Distance(currentPosition, RetryPosition) <= 3f)
                return MoveStopReason.StuckTeleport;

            RetryPosition = currentPosition;
            RetriedOnce = true;
            return MoveStopReason.StuckRetry;
        }
    }

    private async Task Revive() {
        using var scope = BeginScope(nameof(Revive));
        await WaitUntil(() => Player.Revivable, "WaitForRevivable");
        (var lastZone, var lastPos) = (Player.Territory, Player.Position);
        if (Svc.Party.Length is 0) {
            Status = "Reviving";
            GameMain.ExecuteCommand(CommandFlag.Revive.Value, AgentReviveOp.Return.Value);
        }
        else {
            Status = "Waiting For Raise";
            await WaitUntil(() => Player.ReviveState is 2, "WaitingForRaise"); // 1 = return, 2 = raise
            GameMain.ExecuteCommand(CommandFlag.Revive.Value, AgentReviveOp.AcceptRevive.Value); // a1=5 for raises
        }
        await WaitWhile(() => Svc.Condition[ConditionFlag.Unconscious], "WaitForAlive");

        // if the zone we were in was an instanced zone, we might end up in a different one when tp'ing back
        // if the way back involves taking a city route, we don't be near an aetheryte to swap instances
        // TODO: figure out instance swapping, and bypass city routes and go directly back to zone
        if (Player.Territory.RowId != lastZone.RowId) {
            await TeleportTo(lastZone.RowId, lastPos);
            await UseAethernet(lastZone.RowId, lastPos);
        }
    }

    private async Task MoveToFate() {
        using var scope = BeginScope(nameof(MoveToFate));

        IEnumerable<PublicEvent> GetAvailableFates() {
            // If current is a collect at 100% we're leaving it; pick a different fate.
            if (PublicEvent.CurrentFate is { Rule: PublicEvent.FateRule.Collect, Progress: >= 100, Id: var currentId })
                return AvailableFates.Where(f => f.Id != currentId);
            return AvailableFates;
        }

        bool TrySelectNextFate(out PublicEvent selected) {
            if (ReturnToFateId is { } returnFateId) {
                if (PublicEvent.GetFateById(returnFateId) is { Progress: < 100 } returnFate) {
                    selected = returnFate;
                    return true;
                }

                ReturnToFateId = null;
            }

            if (FollowUpFateId is { } parentId && Environment.TickCount64 < FollowUpWatchUntilMs) {
                var parent = Fate.GetRow(parentId);
                // allow even if pending
                if (PublicEvent.Fates.Where(f => f.Id > parentId && Fate.GetRow(f.Id).Location == parent.Location).OrderBy(f => Player.DistanceTo(f.Position)).FirstOrDefault() is { } followUp) {
                    selected = followUp;
                    return true;
                }
            }

            if (GetAvailableFates().FirstOrDefault() is { } candidate) {
                selected = candidate;
                return true;
            }

            selected = null!;
            return false;
        }

        if (!TrySelectNextFate(out var nextFate))
            return;

        NextFate = nextFate;
        if (!NextFate.IsOnMap) {
            Status = "Waiting for fate to appear";
            await Mount();
            await NextFrame(30);
            return;
        }

        // TODO: if rnd=msh, retry?
        var rnd = NextFate.Position.RandomPoint(NextFate.Radius * 0.5f);
        var msh = rnd.OnMesh();
        WarningIf(rnd == msh, "Failed to find a random point on mesh. Destination might not land.");
        Log($"[NextFate={NextFate.Position}] -> [rnd={rnd}] -> [mesh={msh}]");

        var progress = new MoveTracker(Player.Position, Environment.TickCount64);
        var stopReason = MoveStopReason.None;

        bool IsCurrentFateInvalid() {
            if (NextFate is null)
                return true;
            if (PublicEvent.GetFateById(NextFate.Id) is not { } current)
                return true;

            NextFate = current; // keep nextfate fresh in case an unactivated fate disappears while pathing to it
            if (!current.IsOnMap)
                return false;

            return ReturnToFateId == current.Id ? current.Progress >= 100 : !tweak.FateConditions(current);
        }

        bool TrySwitchToHigherPriorityFate() {
            // don't check if we're returning to a previous fate
            if (ReturnToFateId is not null || NextFate is null)
                return false;

            if (GetAvailableFates().FirstOrDefault() is not { } higherPrio || higherPrio.Id == NextFate.Id)
                return false;

            Log($"Switching target fate {NextFate.Id} -> {higherPrio.Id} (higher priority)");
            NextFate = higherPrio;
            return true;
        }

        bool ShouldSwitchToNpc() => NextFate is { State: FateState.Preparing } fate && TryGetValidMotivationNpc(fate, out _);

        bool ShouldStopMove() {
            // preserve the first reason so it can't be overwritten by a later check.
            if (stopReason != MoveStopReason.None)
                return true;

            stopReason = MoveStopReason.None;

            if (IsCurrentFateInvalid()) {
                stopReason = MoveStopReason.FateInvalid;
                return true;
            }

            if (TrySwitchToHigherPriorityFate()) {
                stopReason = MoveStopReason.HigherPriority;
                return true;
            }

            if (NextFate is { IsOnMap: false }) {
                stopReason = MoveStopReason.FatePending;
                return true;
            }

            if (ShouldSwitchToNpc()) {
                stopReason = MoveStopReason.NpcLoaded;
                return true;
            }

            if (progress.CheckStuck(Player.Position) is not MoveStopReason.None and var reason) {
                if (reason == MoveStopReason.StuckTeleport)
                    Warning("Stuck again; teleporting instead");
                else
                    Warning("Stuck on the way to fate. Retrying from current position");

                stopReason = reason;
                return true;
            }

            return false;
        }

        await GenerateObstacleMap(nextFate);
        await MoveTo(msh, MovementConfig.Everything.WithTolerance(3),
            // patched from upstream: always prefer teleporting between fates when it's
            // faster, not only when the fate is already in progress or without the xp
            // buff. Still prohibited while waiting to collect fate rewards.
            allowTeleportIfFaster: WaitForExpiryFateId is null,
            stopCondition: ShouldStopMove,
            onStopReached: async () => {
                if (stopReason == MoveStopReason.NpcLoaded)
                    await ActivateFate();
            });

        Log($"{nameof(MoveToFate)} finished with stopReason={stopReason} fate={NextFate?.Id}");

        if (stopReason == MoveStopReason.StuckRetry && NextFate is { Id: var stuckFateId }) {
            if (LastStuckFateId == stuckFateId)
                ConsecutiveStuckRetries++;
            else {
                LastStuckFateId = stuckFateId;
                ConsecutiveStuckRetries = 1;
            }

            if (ConsecutiveStuckRetries >= 2) {
                Warning($"Escalating repeated stuck retries to teleport for fate {stuckFateId}");
                stopReason = MoveStopReason.StuckTeleport;
            }
        }
        else if (stopReason != MoveStopReason.StuckTeleport) {
            LastStuckFateId = null;
            ConsecutiveStuckRetries = 0;
        }

        if (stopReason == MoveStopReason.HigherPriority)
            return;

        if (stopReason == MoveStopReason.FatePending) {
            Status = "Waiting for fate to appear";
            await Mount();
            await NextFrame(30);
            return;
        }

        if (stopReason == MoveStopReason.StuckTeleport && WaitForExpiryFateId is null && NextFate is { Id: var fateId } && PublicEvent.GetFateById(fateId) is { } currentFate) {
            NextFate = currentFate;
            LastStuckFateId = null;
            ConsecutiveStuckRetries = 0;
            Status = "Teleporting to fate";
            var fateTerritoryId = Player.Territory.RowId;
            await TeleportTo(fateTerritoryId, currentFate.Position, allowSameZoneTeleport: true);
            await UseAethernet(fateTerritoryId, currentFate.Position);
            return;
        }

        // only activate after a normal arrival; if we explicitly stopped (e.g. npcloaded), let the loop re-handle
        if (stopReason == MoveStopReason.None && NextFate is { State: FateState.Preparing, MotivationNpcId: not 0xE0000000 } && PublicEvent.Fates.Any(f => f.Id == NextFate.Id))
            await ActivateFate();
    }

    // some are just so bad it's not worth it having them. I don't really have a better solution than this.
    private readonly List<uint> _obstacleMapBlacklist = [1831, 1832, 1914, 1915];
    private async Task GenerateObstacleMap(PublicEvent evt) {
        if (_obstacleMapBlacklist.Contains(evt.Id)) {
            return;
        }

        using var scope = BeginScope(nameof(GenerateObstacleMap));

        // sometimes the center of a fate is unreachable (tower fate in amh araeng), so generate from a reachable point then compensate for being off center
        var safe = Svc.Navmesh.NearestPointReachable(evt.Position, 5, 5);
        float? margin = safe is { } ? Vector3.Distance(evt.Position, safe.Value) : null;
        Svc.BossMod.Generate(safe ?? evt.Position, evt.Radius + margin ?? 10, false);
        await WaitUntil(() => {
            var status = Svc.BossMod.GetGenerationStatus();
            if (status is TaskStatus.RanToCompletion) {
                Log($"Obstacle map generated for fate {evt.Id}");
                return true;
            }
            if (status is TaskStatus.Faulted) {
                Warning($"Obstacle map generation failed for fate {evt.Id}");
                return true; // allow moving without the map rather than getting stuck in an infinite wait
            }
            return false;
        }, "WaitForObstacleMap");

        if (Svc.BossMod.EvaluateTempMapQuality() is { } quality) {
            Log($"Generated obstacle map quality for fate {evt.Id}: {quality}");
            if (quality.IsBad) {
                Log($"Obstacle map quality too poor. Clearing obstacle map. BossMod won't navigate in case of obstacles. Consider blacklisting this fate if it's problematic.");
                _obstacleMapBlacklist.Add(evt.Id);
                Svc.BossMod.ClearTempMap();
            }
        }
    }

    private async Task ActivateFate() {
        using var scope = BeginScope(nameof(ActivateFate));
        if (NextFate is not { } fate)
            return;
        if (!fate.IsOnMap)
            return;

        // sometimes fates are in prep for a very long time before they're on the map. Wait until the npc is actually ready before returning/attempting anything
        await WaitUntil(() => TryGetValidMotivationNpc(fate, out _) || fate.State is FateState.Running, "WaitForNpcSpawn");

        if (fate.State is FateState.Running) return; // someone beat us to activating

        if (TryGetValidMotivationNpc(fate, out var npc)) {
            Log($"ActivateFate start: fate={NextFate.Id} npc={npc.EntityId} npcPos={npc.Position} playerPos={Player.Position} dist={Player.DistanceTo(npc.Position):F2} inRange={npc.IsInInteractRange()}");
            await MoveTo(npc.Position, MovementConfig.InteractRange.WithOptions(MovementOptions.Current));
            Log($"ActivateFate after MoveTo: npc={npc.EntityId} playerPos={Player.Position} dist={Player.DistanceTo(npc.Position):F2} inRange={npc.IsInInteractRange()}");
            try {
                await InteractWith(npc, () => NextFate?.State == FateState.Running || !TryGetValidMotivationNpc(fate, out _), skip: UiSkipOptions.Talk | UiSkipOptions.YesNo);
            }
            catch (Exception ex) {
                // will crash if we don't catch and it's fine if interact fails because the npc/fate disappeared before we could start
                if (NextFate is null || !TryGetValidMotivationNpc(NextFate, out _) || NextFate.State != FateState.Preparing) {
                    Warning($"Skipping fate activation: npc/fate vanished before interact ({ex.Message})");
                    return;
                }
                throw;
            }
        }
        else
            Error($"Something weird happened with the npc activation [{fate}]");
    }

    private async Task HandleNoFates() {
        if (WaitForExpiryFateId is not null) {
            using var scope = BeginScope("WaitForFateRewards");
            Status = "Waiting for fate rewards";
            await Mount();
            await NextFrame(60);
            return;
        }

        // patched from upstream: give the current zone 30 seconds to spawn a new fate
        // before swapping to another zone.
        _noFatesSince ??= DateTime.UtcNow;
        var waitedForFates = DateTime.UtcNow - _noFatesSince.Value;
        if (waitedForFates < TimeSpan.FromSeconds(30)) {
            using var scope = BeginScope("WaitForFates");
            // mount first: Mount() sets its own status while actually mounting, so the
            // countdown must be written afterwards to stay visible during the wait.
            await Mount();
            Status = $"Waiting for fates to spawn ({30 - (int)waitedForFates.TotalSeconds}s until zone swap)";
            await NextFrame(60);
            return;
        }

        var hasEffectiveZones = tweak.GetEffectiveSwapZones() is { Count: > 0 } || tweak.HasSelectedSwapZones;
        // patched from upstream: swap zones even while Twist of Fate is up - when the
        // current zone has no fates left, move on to another allowed zone instead of
        // waiting in place to preserve the buff.
        if (hasEffectiveZones || tweak.Config.SwapZones) {
            using var scope = BeginScope("SwapZones");
            var destination = tweak.GetNextPreferredSwapZone(Player.Territory.RowId) ?? GetNextAchievementZone() ?? GetRandomSameExpacZone();
            if (destination == Player.Territory.RowId) {
                Status = "Waiting for fates in selected zones";
                await Mount();
                await NextFrame(60);
                return;
            }

            var fromTerritoryId = Player.Territory.RowId;
            await Mount();
            await TeleportTo(destination, Vector3.Zero);
            await tweak.GetCurrentMode().OnSwapZone(fromTerritoryId, destination, CancelToken);

            // patched: don't restart the 30s grace in every dry zone - hop through the
            // rotation checking for fates immediately, and only pause for another 30s
            // once every zone has been checked without finding anything.
            _consecutiveDrySwaps++;
            var rotationSize = tweak.GetEffectiveSwapZones()?.Count ?? 3;
            if (_consecutiveDrySwaps >= rotationSize) {
                _consecutiveDrySwaps = 0;
                _noFatesSince = DateTime.UtcNow;
            }
        }
        else {
            using var scope = BeginScope("WaitForFates");
            Status = HasTwistOfFate ? "Waiting for fates (preserving Twist of Fate)" : "Waiting for fates to spawn";
            await Mount();
            await NextFrame(60);
        }
    }

    private async Task SwapNewItemTarget() {
        if (!tweak.IsZoneItemTargetComplete(Player.Territory.RowId, out var destination))
            return;
        using var scope = BeginScope("SwapNewItemTarget");
        Status = "ZoneItemTarget complete. Swapping zones.";
        await Mount();
        await TeleportTo(destination, Vector3.Zero);
        await tweak.GetCurrentMode().OnSwapZone(Player.Territory.RowId, destination, CancelToken);
    }

    private void HandleIntegrations() {
        if (PublicEvent.CurrentFate is { } fate) {
            // when we leave collect fates early, it's still CurrentFate, so we need to ignore that and deactivate anyway
            if (fate is { Rule: PublicEvent.FateRule.Collect, Progress: >= 100 } && (NextFate is null || NextFate.Id != fate.Id)) {
                // don't deactivate before we're out of combat
                if (Svc.Condition[ConditionFlag.InCombat])
                    return;
                DeactivateIntegrations(clearNextFate: false);
                return;
            }

            // only activate for the fate we're pathfinding to (or any if NextFate is null)
            if (NextFate is { } next && fate.Id != next.Id
                && !(fate is { Rule: PublicEvent.FateRule.Collect, Progress: >= 100 } && Svc.Condition[ConditionFlag.InCombat])) {
                DeactivateIntegrations(clearNextFate: false);
                return;
            }

            if (Player.Mounted) {
                DeactivateIntegrations(clearNextFate: false);
                return;
            }

            if (Service.BossMod.GetActive() != _presetName) {
                if (Service.BossMod.Get(_presetName) is null)
                    Service.BossMod.Create(_preset, true);
                else
                    Service.BossMod.SetActive(_presetName);
            }
            Svc.BossMod.AddTransientStrategy(_presetName, "BossMod.Autorotation.MiscAI.AutoTarget", "MaxTargets", PullSize.ToString());

            if (PublicEvent.CurrentFate is { Rule: PublicEvent.FateRule.Collect } && !Svc.TextAdvance.IsInExternalControl())
                Svc.TextAdvance.EnableExternalControl(Name, new() { EnableTalkSkip = true, EnableRequestFill = true, EnableRequestHandin = true });
        }
        else {
            // Fate ended; clear NextFate so routing is correct. Only turn off combat preset once out of combat,
            // so we don't get stuck if a non-fate mob is still aggroed when the fate completes.
            NextFate = null;
            if (!Svc.Condition[ConditionFlag.InCombat])
                DeactivateIntegrations(clearNextFate: false);
        }
    }

    private void DeactivateIntegrations(bool clearNextFate) {
        if (clearNextFate)
            NextFate = null;

        Service.BossMod.ClearActive();
        Svc.Targets.Target = null; // avoid preset trying to go to the mob and interfering with casts
        if (Svc.TextAdvance.IsInExternalControl())
            Svc.TextAdvance.DisableExternalControl(Name);
    }

    private bool TryGetValidMotivationNpc(PublicEvent fate, [NotNullWhen(true)] out IGameObject? npc) {
        npc = null;
        if (Player.DistanceTo(fate.Position) > 50) // half the object table range
            return false;

        if (fate.MotivationNpc is not { IsTargetable: true } target)
            return false;

        npc = target;
        return true;
    }

    // TODO: find better shit for this
    private const int FollowUpWaitLimit = 15_000;
    private void StartFollowUpWatch(uint completedFateId) {
        if (!Fate.GetRow(completedFateId).HasFollowUp)
            return;

        if (FollowUpFateId != completedFateId)
            Log($"Watching for follow-up fate after {completedFateId} for {FollowUpWaitLimit / 1000}s");

        FollowUpFateId = completedFateId;
        FollowUpWatchUntilMs = Environment.TickCount64 + FollowUpWaitLimit;
    }

    private bool ShouldWaitForFollowUp() {
        if (FollowUpFateId is not { } fateId)
            return false;

        var row = Fate.GetRow(fateId);
        if (PublicEvent.Fates.Any(f => f.Id > fateId && Fate.GetRow(f.Id).Location == row.Location)) {
            Log($"Detected follow-up fate for {fateId}, resuming routing");
            FollowUpFateId = null;
            return false;
        }

        if (Environment.TickCount64 >= FollowUpWatchUntilMs) {
            FollowUpFateId = null;
            return false;
        }

        Status = $"Waiting for follow-up fate ({(FollowUpWatchUntilMs - Environment.TickCount64) / 1000 + 1}s)";
        return true;
    }

    private unsafe uint? GetNextAchievementZone() {
        var agent = AgentFateProgress.Instance();
        if (agent == null) return null;

        // prioritise zones in the same expac as current area
        var currentTabIndex = Array.FindIndex(agent->Tabs.ToArray(), tab => tab.Zones.ToArray().Any(zone => Player.Territory.RowId == zone.TerritoryTypeId));
        var zones = (currentTabIndex != -1 && currentTabIndex < agent->Tabs.Length - 1)
            ? agent->Tabs[currentTabIndex].Zones.ToArray()
            : agent->Tabs.ToArray().SelectMany(tab => tab.Zones.ToArray());

        // patched from upstream: the fallback pickers must also respect the Dawntrail
        // restriction to the last three zones, otherwise a null preferred zone could
        // still teleport into Urqopacha/Kozama'uka/Yak T'el.
        return zones.FirstOrNull(zone => zone.NeededFates - zone.FateProgress > 0 && !FateToolKit.DawntrailExcludedZones.Contains(zone.TerritoryTypeId))?.TerritoryTypeId;
    }

    private uint GetRandomSameExpacZone() {
        var rows = TerritoryType.Where(x => x.IsInUse && x.TerritoryIntendedUse.Value.StructsEnum is TerritoryIntendedUse.Overworld && x.ExVersion.RowId == Player.Territory.Value.ExVersion.RowId && !x.IsPvpZone
            // patched from upstream: see GetNextAchievementZone
            && !FateToolKit.DawntrailExcludedZones.Contains(x.RowId));
        return rows[new Random().Next(rows.Length)].RowId;
    }
}
