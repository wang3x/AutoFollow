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
    private readonly DebugLog _debugLog;
    private readonly Func<Vector3?> _getPlayerPos;

    private ICallGateSubscriber<bool>? _navIsReady;
    private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _navPathfind;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _pathMoveTo;
    private ICallGateSubscriber<object>? _pathStop;
    private ICallGateSubscriber<bool>? _pathIsRunning;

    private bool _connectionAttempted;
    private bool _isAvailable;
    private int _pathfindVersion;

    public bool IsAvailable
    {
        get { if (!_connectionAttempted) TryConnect(); return _isAvailable; }
    }

    public VnavmeshIPC(IDalamudPluginInterface pi, DebugLog debugLog, Func<Vector3?> getPlayerPos)
    {
        _pi = pi;
        _debugLog = debugLog;
        _getPlayerPos = getPlayerPos;
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
                Log.Info("vnavmesh IPC ready");
                _debugLog.Log("IPC", "vnavmesh connected");
            }
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            Log.Info($"vnavmesh not available: {ex.Message}");
        }
    }

    /// <summary>后台寻路+移动（带版本号防重叠，只应用最新结果）</summary>
    public void MoveToPositionAsync(Vector3 from, Vector3 destination)
    {
        if (!IsAvailable) { _debugLog.Log("IPC", "vnavmesh unavailable"); return; }

        var version = Interlocked.Increment(ref _pathfindVersion);
        _debugLog.Log("IPC", $"pathfind v{version} ({from.X:F1},{from.Y:F1},{from.Z:F1}) -> ({destination.X:F1},{destination.Y:F1},{destination.Z:F1})");

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
                // 如果在此期间有更新的版本，丢弃当前结果防重叠
                if (version != _pathfindVersion)
                {
                    _debugLog.Log("move", $"skip stale path (v{version} != v{_pathfindVersion})");
                    return;
                }

                // ── A方案：寻路完成后用玩家实时位置替换路径起点 ──
                // 寻路计算慢，Pathfind 传入的 from 是发起时刻的位置，已与实际坐标有误差。
                // 实时读取当前坐标覆盖 path[0]，并清掉过近的冗余节点，避免角色转头回退。
                var playerPos = _getPlayerPos?.Invoke();
                if (playerPos != null)
                {
                    path[0] = playerPos.Value;
                    while (path.Count > 1 && Vector3.Distance(path[1], playerPos.Value) < 2f)
                        path.RemoveAt(1);
                    _debugLog.Log("move", $"A方案: 起点替换为实时坐标, 路径剩余 {path.Count} 节点");
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
