using clib.Services;
using Dalamud.Plugin.Ipc;
using System.Threading.Tasks;

namespace clib.Internal;

internal class NavmeshIPC {
    private readonly ICallGateSubscriber<bool> _navIsReady;
    private readonly ICallGateSubscriber<float> _navBuildProgress;
    private readonly ICallGateSubscriber<bool> _navPathfindInProgress;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?> _pathfind;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, float, Task<List<Vector3>>?> _pathfindWithTolerance;

    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _nearestPoint;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _nearestPointReachable;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _pointOnFloor;
    private readonly ICallGateSubscriber<Vector3?> _flagToPoint;

    private readonly ICallGateSubscriber<object> _pathStop;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> _pathMoveTo;
    private readonly ICallGateSubscriber<bool> _pathIsRunning;
    private readonly ICallGateSubscriber<float> _pathGetTolerance;

    private readonly ICallGateSubscriber<Vector3, bool, bool> _pathfindAndMoveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _pathfindAndMoveCloseTo;
    private readonly ICallGateSubscriber<bool> _pathfindInProgress;

    public NavmeshIPC() {
        _navIsReady = Svc.Interface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _navBuildProgress = Svc.Interface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        _navPathfindInProgress = Svc.Interface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        _pathfind = Svc.Interface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>?>("vnavmesh.Nav.Pathfind");
        _pathfindWithTolerance = Svc.Interface.GetIpcSubscriber<Vector3, Vector3, bool, float, Task<List<Vector3>>?>("vnavmesh.Nav.PathfindWithTolerance");

        _nearestPoint = Svc.Interface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint");
        _nearestPointReachable = Svc.Interface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        _pointOnFloor = Svc.Interface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        _flagToPoint = Svc.Interface.GetIpcSubscriber<Vector3?>("vnavmesh.Query.Mesh.FlagToPoint");

        _pathStop = Svc.Interface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _pathMoveTo = Svc.Interface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        _pathIsRunning = Svc.Interface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _pathGetTolerance = Svc.Interface.GetIpcSubscriber<float>("vnavmesh.Path.GetTolerance");

        _pathfindAndMoveTo = Svc.Interface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        _pathfindAndMoveCloseTo = Svc.Interface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _pathfindInProgress = Svc.Interface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");

    }

    public bool IsAvailable => _navIsReady.HasFunction;

    public bool IsReady => _navIsReady.HasFunction && _navIsReady.InvokeFunc();
    public float BuildProgress => _navBuildProgress.HasFunction ? _navBuildProgress.InvokeFunc() : -1f;
    public Task<List<Vector3>>? Pathfind(Vector3 start, Vector3 end, bool fly)
        => _pathfind.HasFunction ? _pathfind.InvokeFunc(start, end, fly) : null;

    public Task<List<Vector3>>? PathfindWithTolerance(Vector3 start, Vector3 end, bool fly, float range)
        => _pathfindWithTolerance.HasFunction ? _pathfindWithTolerance.InvokeFunc(start, end, fly, range) : null;

    public bool PathfindInProgress => _navPathfindInProgress.HasFunction && _navPathfindInProgress.InvokeFunc();

    public bool MoveTo(List<Vector3> waypoints, bool fly) {
        if (!_pathMoveTo.HasAction)
            return false;
        _pathMoveTo.InvokeAction(waypoints, fly);
        return true;
    }

    public Vector3? NearestPoint(Vector3 position, float halfExtentXZ = 5, float halfExtentY = 5) => _nearestPoint.HasFunction ? _nearestPoint.InvokeFunc(position, halfExtentXZ, halfExtentY) : null;
    public Vector3? NearestPointReachable(Vector3 position, float halfExtentXZ = 5, float halfExtentY = 5) => _nearestPointReachable.HasFunction ? _nearestPointReachable.InvokeFunc(position, halfExtentXZ, halfExtentY) : null;
    // unlandable isn't (currently) used in any way so it doesn't matter
    public Vector3? PointOnFloor(Vector3 position, bool allowUnlandable = false, float halfExtentXZ = 5) => _pointOnFloor.HasFunction ? _pointOnFloor.InvokeFunc(position, allowUnlandable, halfExtentXZ) : null;
    public Vector3? FlagToPoint() => _flagToPoint.HasFunction ? _flagToPoint.InvokeFunc() : null;

    public void Stop() {
        if (!_pathStop.HasAction)
            return;
        _pathStop.InvokeAction();
    }
    public bool IsRunning() => _pathIsRunning.HasFunction && _pathIsRunning.InvokeFunc();
    public float GetTolerance() => _pathGetTolerance.HasFunction ? _pathGetTolerance.InvokeFunc() : 0f;

    public bool PathfindAndMoveTo(Vector3 destination, bool allowFlying = false) => _pathfindAndMoveTo.HasFunction && _pathfindAndMoveTo.InvokeFunc(destination, allowFlying);
    public bool PathfindAndMoveCloseTo(Vector3 destination, bool allowFlying, float range) => _pathfindAndMoveCloseTo.HasFunction && _pathfindAndMoveCloseTo.InvokeFunc(destination, allowFlying, range);
    public bool PathfindingInProgress => _pathfindInProgress.HasFunction && _pathfindInProgress.InvokeFunc();
}
