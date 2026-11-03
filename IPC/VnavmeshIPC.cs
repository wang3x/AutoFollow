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
    /// <summary>聊天输出必须回主线程（IChatGui 非线程安全）；可能为 null（旧调用方）</summary>
    private readonly IFramework? _framework;

    private ICallGateSubscriber<bool>? _navIsReady;
    private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _navPathfind;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _pathMoveTo;
    private ICallGateSubscriber<object>? _pathStop;
    private ICallGateSubscriber<bool>? _pathIsRunning;

    private bool _connectionAttempted;
    private bool _isAvailable;
    private int _pathfindVersion;

    // ── 诊断（测试版临时）──
    private DateTime _lastErrorNotice = DateTime.MinValue;
    private string? _lastErrorText;

    // ── 主线程断言绕行 ──
    // 国际服 goatcorp 与新版国服 ottercorp(>=15.x) 的 Dalamud 在 ObjectTable 等成员上加了
    // ThreadSafety.AssertMainThread，非框架线程访问会抛 InvalidOperationException("Not on main thread!")。
    // 断言只读 [ThreadStatic] threadStaticIsMainThread 这个字段，而把当前线程标记为"主线程"
    // 的方法 MarkMainThread 是 internal —— 通过反射调用即可（vnavmesh 自己也是这么做的）。
    private static System.Reflection.MethodInfo? _markMainThread;
    private static bool _markMainThreadResolved;

    /// <summary>
    /// 把「当前线程」标记为 Dalamud 认定的主线程，使本线程后续访问 ObjectTable 等成员不再被断言拦截。
    /// 字段是 [ThreadStatic]，因此只影响调用它的那一个线程；对没有断言的旧版 Dalamud 调用也无害。
    /// </summary>
    private static void MarkCurrentThreadAsMainThread()
    {
        if (!_markMainThreadResolved)
        {
            _markMainThreadResolved = true;
            try
            {
                var ts = typeof(IDalamudPluginInterface).Assembly
                    .GetType("Dalamud.Utility.ThreadSafety", throwOnError: false);
                // NonPublic(32) | Public(16) | Static(8) = 56
                _markMainThread = ts?.GetMethod("MarkMainThread",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Static);
            }
            catch
            {
                _markMainThread = null;
            }
        }

        try { _markMainThread?.Invoke(null, null); } catch { }
    }

    /// <summary>
    /// 诊断用：把寻路异常同时输出到聊天框（10 秒内同内容不重复）。
    /// 本方法在寻路线程（线程池）上被调用，而 IChatGui 非线程安全 ——
    /// 必须切回框架主线程再输出，否则可能抛异常或写坏聊天队列。
    /// </summary>
    private void ReportError(string msg)
    {
        _debugLog.Log("IPC", msg);
        var now = DateTime.UtcNow;
        if (msg == _lastErrorText && (now - _lastErrorNotice).TotalSeconds < 10) return;
        _lastErrorText = msg;
        _lastErrorNotice = now;

        var text = $"[强效跟随] 寻路异常: {msg}";
        if (_framework != null)
        {
            try { _framework.RunOnFrameworkThread(() => Log.Notice(text)); }
            catch { /* 框架线程不可用（卸载中）→ 放弃聊天输出 */ }
        }
        else
        {
            // 没有 framework 引用时不能安全打印，只留日志
            Log.Info(text);
        }
    }

    /// <summary>
    /// 诊断用：探测 Dalamud 是否在后台线程拦截 ObjectTable 访问。
    /// 在后台线程尝试读一次玩家对象，抛 InvalidOperationException 说明有主线程断言。
    /// </summary>
    /// <summary>
    /// 环境探测：确认当前 Dalamud 是否在后台线程拦截 ObjectTable 访问，以及标记绕行是否生效。
    /// <b>必须 await 调用</b>（不要同步 Wait）—— 内部最多等 3s，同步等待会卡住游戏主线程。
    /// 结果输出请用 <c>RunOnFrameworkThread</c> 切回主线程。
    /// </summary>
    public static async Task<(string Repo, string Version, bool? Blocked, bool? AfterMark, string? Error)>
        ProbeEnvironmentAsync(IObjectTable objectTable)
    {
        string repo = "?", version = "?";
        try
        {
            var asm = typeof(IDalamudPluginInterface).Assembly;
            version = asm.GetName().Version?.ToString() ?? "?";
            foreach (var a in asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                var m = (System.Reflection.AssemblyMetadataAttribute)a;
                if (string.Equals(m.Key, "RepositoryUrl", StringComparison.Ordinal)) repo = m.Value ?? "?";
            }
        }
        catch { }

        // blocked：后台线程「未标记」读取 → 是否被断言拦截
        // after ：同一线程「标记后」再读 → 绕行是否生效
        bool? blocked = null, after = null;
        string? error = null;
        try
        {
            // 关键：必须在线程池线程上探测，主线程上永远不抛
            var task = Task.Run<(bool blocked, bool after, string? err)>(() =>
            {
                string? err = null;
                bool b;
                try { _ = objectTable[0]; b = false; }
                catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; b = true; }

                MarkCurrentThreadAsMainThread();

                bool a;
                try { _ = objectTable[0]; a = false; }
                catch (Exception ex) { err = (err ?? "") + " | 标记后: " + ex.GetType().Name + ": " + ex.Message; a = true; }

                return (b, a, err);
            });

            // 异步等待：不阻塞调用线程（框架主线程上绝不能 Wait）
            var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            if (done == task && task.IsCompletedSuccessfully)
            {
                var res = await task.ConfigureAwait(false);
                blocked = res.blocked;
                after   = res.after;
                error   = res.err;
            }
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }

        return (repo, version, blocked, after, error);
    }

    /// <summary>vnavmesh 插件是否已连接（连接成功一次后不再变化，不代表 NavMesh 已就绪）</summary>
    public bool IsAvailable
    {
        get { if (!_connectionAttempted) TryConnect(); return _isAvailable; }
    }

    /// <summary>内部用：安全读取 Nav.IsReady（异常/未连接一律 false）</summary>
    private bool SafeNavReady()
    {
        try { return _navIsReady?.InvokeFunc() ?? false; }
        catch { return false; }
    }

    /// <summary>
    /// NavMesh 当前是否已加载就绪（实时查询，每次调用都会问 vnavmesh）。
    /// 过图后 NavMesh 需要时间重建，期间为 false；此时发的 moveflag/move 会被丢弃，
    /// 所以换图后要等它为 true 再发移动指令。
    /// </summary>
    public bool IsNavReady
    {
        get
        {
            if (!IsAvailable) return false;
            return SafeNavReady();
        }
    }

    public VnavmeshIPC(IDalamudPluginInterface pi, DebugLog debugLog, Func<Vector3?> getPlayerPos,
        IFramework? framework = null)
    {
        _pi = pi;
        _debugLog = debugLog;
        _getPlayerPos = getPlayerPos;
        _framework = framework;
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

            // 可用性判定：能成功建立并调用任一 IPC 即视为已连接。
            // 注意**不能**用 Nav.IsReady 判断 —— 它表示"NavMesh 是否已生成"，
            // 刚进图/过图重建期间为 false，若据此判可用会导致 _connectionAttempted 已置位、
            // 永不重连，整个寻路永久失效。这里改问 Path.IsRunning（任何区域都可安全调用）。
            var probe = _pathIsRunning.InvokeFunc();
            _isAvailable = true;
            _debugLog.Log("IPC", $"vnavmesh connected (Path.IsRunning={probe}, Nav.IsReady={SafeNavReady()})");
        }
        catch (Exception ex)
        {
            _isAvailable = false;
            Log.Info($"vnavmesh not available: {ex.Message}");
        }
    }

    /// <summary>后台寻路+移动（带版本号防重叠，只应用最新结果）。主目标寻路失败时自动改用 fallbackDestination（如穿线延伸点不可达时退回原最后坐标）。</summary>
    public void MoveToPositionAsync(Vector3 from, Vector3 destination, Vector3? fallbackDestination = null)
    {
        if (!IsAvailable) { _debugLog.Log("IPC", "vnavmesh unavailable"); return; }

        var version = Interlocked.Increment(ref _pathfindVersion);
        _debugLog.Log("IPC", $"pathfind v{version} ({from.X:F1},{from.Y:F1},{from.Z:F1}) -> ({destination.X:F1},{destination.Y:F1},{destination.Z:F1})");

        Task.Run(async () =>
        {
            try
            {
                // 先把自己标记为 Dalamud 认定的主线程，之后再访问玩家数据才不会被断言拦截。
                // 必须在 await 之后重新标记：await 可能把续体切到另一个线程池线程，
                // 而 threadStaticIsMainThread 是 [ThreadStatic]，换线程就丢了。
                MarkCurrentThreadAsMainThread();

                var pathTask = _navPathfind!.InvokeFunc(from, destination, false);
                var path = await pathTask;

                MarkCurrentThreadAsMainThread();   // 续体线程可能已变，重新标记
                // 主目标寻路失败 → fallback 备用目标（如延伸点越界时退回原最后坐标）
                if (path == null || path.Count == 0)
                {
                    if (fallbackDestination != null)
                    {
                        _debugLog.Log("move", $"primary path not found, fallback to ({fallbackDestination.Value.X:F1},{fallbackDestination.Value.Y:F1},{fallbackDestination.Value.Z:F1})");
                        pathTask = _navPathfind!.InvokeFunc(from, fallbackDestination.Value, false);
                        path = await pathTask;
                        MarkCurrentThreadAsMainThread();   // 再次 await → 再标记
                    }
                    if (path == null || path.Count == 0)
                    {
                        _debugLog.Log("move", "no path found");
                        return;
                    }
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
                try
                {
                    _pathMoveTo!.InvokeAction(path, false);
                }
                catch (Exception mvEx)
                {
                    ReportError($"Path.MoveTo: {mvEx.GetType().Name}: {mvEx.Message}");
                }
            }
            catch (Exception ex)
            {
                ReportError($"{ex.GetType().Name}: {ex.Message}");
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

    // ── 其他 ──

    /// <summary>使所有挂起的寻路结果失效（防止旧 MoveTo 结果晚到覆盖后续移动）</summary>
    public void InvalidatePaths()
    {
        Interlocked.Increment(ref _pathfindVersion);
        _debugLog.Log("move", "invalidate pending paths");
    }

    public void Dispose() { Stop(); }
}
