using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using AutoFollow.Windows;

namespace AutoFollow.IPC;

/// <summary>
/// vnavmesh IPC — 使用 Nav.Pathfind + Path.MoveTo 标准接口。
/// 国服/国际服通用，寻路在后台线程执行不阻塞主线程。
/// </summary>
public sealed class VnavmeshIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _logger;
    private readonly DebugLog _debugLog;

    private ICallGateSubscriber<bool>? _navIsReady;
    private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _navPathfind;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _pathMoveTo;
    private ICallGateSubscriber<object>? _pathStop;
    private ICallGateSubscriber<bool>? _pathIsRunning;

    private bool _connectionAttempted;
    private bool _isAvailable;

    public bool IsAvailable
    {
        get { if (!_connectionAttempted) TryConnect(); return _isAvailable; }
    }

    public VnavmeshIPC(IDalamudPluginInterface pi, IPluginLog logger, DebugLog debugLog)
    {
        _pi = pi;
        _logger = logger;
        _debugLog = debugLog;
    }

    private void TryConnect()
    {
        if (_connectionAttempted) return;
        _connectionAttempted = true;
        try
        {
            _navIsReady = _pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
            _navPathfind = _pi.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind");
            _pathMoveTo = _pi.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
            _pathStop = _pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
            _pathIsRunning = _pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
            _isAvailable = _navIsReady.InvokeFunc();
            if (_isAvailable)
            {
                _logger.Info("vnavmesh IPC ready");
                _debugLog.Log("IPC", "vnavmesh connected");
            }
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            _logger.Info("vnavmesh not available: {0}", ex.Message);
        }
    }

    /// <summary>后台寻路+移动</summary>
    public void MoveToPositionAsync(Vector3 from, Vector3 destination)
    {
        if (!IsAvailable) { _debugLog.Log("IPC", "vnavmesh unavailable"); return; }

        _debugLog.Log("IPC", $"pathfind ({from.X:F1},{from.Y:F1},{from.Z:F1}) -> ({destination.X:F1},{destination.Y:F1},{destination.Z:F1})");

        Task.Run(async () =>
        {
            try
            {
                var pathTask = _navPathfind!.InvokeFunc(from, destination, false);
                var path = await pathTask;
                if (path == null || path.Count == 0)
                {
                    _debugLog.Log("move", "no path found");
                    return;
                }
                _debugLog.Log("move", $"path found ({path.Count} nodes)");
                _pathMoveTo!.InvokeAction(path, false);
            }
            catch (Exception ex)
            {
                _debugLog.Log("IPC", $"move error: {ex.Message}");
            }
        });
    }

    public void Stop()
    {
        try { _pathStop?.InvokeAction(); _debugLog.Log("move", "stop"); } catch { }
    }

    public bool IsMoving()
    {
        try { return _pathIsRunning?.InvokeFunc() ?? false; } catch { return false; }
    }

    public void Dispose() { Stop(); }
}
