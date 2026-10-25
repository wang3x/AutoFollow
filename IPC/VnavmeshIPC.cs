using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using AutoFollow.Movement;
using AutoFollow.Windows;

namespace AutoFollow.IPC;

/// <summary>All game/IPC reads and path publication happen on the framework thread.</summary>
public sealed class VnavmeshIPC : IDisposable
{
    private readonly IFramework _framework;
    private readonly DebugLog _log;
    private readonly Func<Vector3?> _player;
    private readonly Func<ushort?> _territory;
    private readonly ICallGateSubscriber<bool> _ready;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> _find;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> _move;
    private readonly ICallGateSubscriber<object> _stop;
    private readonly ICallGateSubscriber<bool> _running;
    private readonly PathRequestGuard _requests = new();
    private bool _disposed;
    private PathStatus _status;
    private Vector3 _destination;
    private DateTime _requestStarted;

    public VnavmeshIPC(IDalamudPluginInterface pi, DebugLog log, Func<Vector3?> player,
        IFramework framework, Func<ushort?> territory)
    {
        _framework = framework; _log = log; _player = player; _territory = territory;
        _ready = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _find = pi.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
        _move = pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        _stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _running = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    }

    public bool IsAvailable
    {
        get { try { return !_disposed && _ready.InvokeFunc(); } catch { return false; } }
    }

    public PathStatus Status
    {
        get
        {
            if (_status == PathStatus.Pending && (DateTime.UtcNow - _requestStarted).TotalSeconds > 10)
            {
                InvalidatePaths(); _status = PathStatus.Failed;
            }
            if (_status == PathStatus.Moving && !IsMoving())
                _status = _player() is Vector3 p && Vector3.Distance(p, _destination) <= 1.5f
                    ? PathStatus.Arrived : PathStatus.Failed;
            return _status;
        }
    }

    public bool IsMoving()
    {
        try { return !_disposed && _running.InvokeFunc(); } catch { return false; }
    }

    public void MoveToPositionAsync(Vector3 from, Vector3 destination, Vector3? fallbackDestination = null)
        => Request(from, destination, false);

    // A successful probe does not start movement; the engine explicitly resumes afterwards.
    public void Probe(Vector3 from, Vector3 destination) => Request(from, destination, true);

    private async void Request(Vector3 from, Vector3 destination, bool probe)
    {
        var version = _requests.Next();
        var territory = _territory();
        _requestStarted = DateTime.UtcNow;
        _status = PathStatus.Pending;
        if (!IsAvailable || territory == null) { _status = PathStatus.Failed; return; }
        try
        {
            var path = await _find.InvokeFunc(from, destination, false).ConfigureAwait(false);
            await _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed || !_requests.IsCurrent(version) || territory != _territory()) return;
                if (!PathRequestGuard.CompletePath(path, destination))
                {
                    Stop(); _status = PathStatus.Failed;
                    _log.Log("move", "No complete path to destination");
                    return;
                }
                var player = _player();
                // A stale start could cut a corner. Retry from the new location instead.
                if (player == null || Vector3.Distance(player.Value, from) > 3f)
                {
                    Stop(); _status = PathStatus.Failed; return;
                }
                if (probe) { _status = PathStatus.Reachable; return; }
                _destination = destination;
                _move.InvokeAction(path, false);
                _status = PathStatus.Moving;
            });
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            await _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed || !_requests.IsCurrent(version)) return;
                Stop(); _status = PathStatus.Failed; _log.Log("IPC", ex.Message);
            });
        }
    }

    public void MoveDirect(Vector3 destination)
    {
        Stop();
        try
        {
            if (!IsAvailable) { _status = PathStatus.Failed; return; }
            _destination = destination;
            _move.InvokeAction(new List<Vector3> { destination }, false);
            _status = PathStatus.Moving;
        }
        catch { _status = PathStatus.Failed; }
    }

    public void InvalidatePaths() => _requests.Next();

    public void Stop()
    {
        InvalidatePaths();
        _status = PathStatus.Stopped;
        try { _stop.InvokeAction(); } catch { }
    }

    public void Dispose() { Stop(); _disposed = true; }
}
