using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using AutoFollow.Conditions;
using AutoFollow.IPC;
using AutoFollow.Models;
using AutoFollow.Movement;
using AutoFollow.Windows;

namespace AutoFollow;

public sealed class FollowEngine : IDisposable
{
    private enum InertiaPhase
    {
        None,
        /// <summary>先跑向最后已知目标坐标</summary>
        ToLastKnown,
        /// <summary>到达后沿面朝方向惯移 1.5s / 总窗口 2s</summary>
        FaceForward,
    }

    private readonly IObjectTable _objectTable;
    private readonly IChatGui _chatGui;
    private readonly IPluginLog _logger;
    private readonly IFramework _framework;
    private readonly FollowConfig _config;
    private readonly ConditionManager _conditionManager;
    private readonly SprintController _sprint;
    private readonly IPCService _ipc;
    private readonly VnavmeshFollow _vnavmesh;
    private readonly DebugLog _debugLog;

    private FollowState _state = FollowState.Idle;
    private FollowState _lastMsgState = FollowState.Idle;
    private string? _followTargetName;
    private ulong? _followTargetId;
    private Vector3? _lastSentPosition;
    private DateTime _lastUpdate;
    private DateTime _combatEndTime;
    private DateTime _followStartTime;
    private bool _wasInCombat;
    private Vector3? _lastKnownTargetPos;
    private float _lastScanDistance = float.MaxValue;

    // ── 两段式惯移状态 ──
    private InertiaPhase _inertiaPhase = InertiaPhase.None;
    private Vector3? _inertiaLastKnownDest;
    private DateTime? _faceMoveStopTime;   // 面朝惯移截止（1.5s）
    private DateTime? _facePhaseExpiry;    // 面朝阶段总窗口（2s）
    private DateTime _inertiaStartTime;    // 整段惯移开始，用于超时兜底

    /// <summary>当前暂停原因的友好描述，供 UI 显示</summary>
    public string? PauseReason { get; private set; }

    private const double OutOfCombatDelay = 1.0;
    private const double StartupGracePeriod = 2.0;
    private const float LastKnownArriveRange = 3f;   // 视为到达最后已知点
    private const float InertiaCancelRange = 100f;  // 目标 ≤100y 取消惯移
    private const double FaceMoveSeconds = 1.5;
    private const double FacePhaseSeconds = 2.0;
    private const double ToLastKnownTimeoutSeconds = 30.0; // 第一阶段超时
    private const float FaceForwardDistance = 30f;

    public FollowState State => _state;
    public string? TargetName => _followTargetName;
    public float DistanceToTarget { get; private set; } = float.MaxValue;
    public Vector3? LastSentPosition => _lastSentPosition;
    public ConditionManager Conditions => _conditionManager;
    public SprintController Sprint => _sprint;
    public bool IsAvailable => _vnavmesh.IsAvailable;
    public bool IsMoving => _vnavmesh.IsMoving;
    public bool IsEmergencyStopped { get; private set; }

    public event Action<FollowState, FollowState>? OnStateChanged;

    private readonly Func<ushort?> _getTerritory;

    public FollowEngine(
        IObjectTable objectTable, IChatGui chatGui, IPluginLog logger, IFramework framework,
        FollowConfig config, ConditionManager conditionManager, SprintController sprint,
        IPCService ipc, VnavmeshFollow vnavmesh, DebugLog debugLog,
        Func<ushort?> getTerritory)
    {
        _objectTable = objectTable; _chatGui = chatGui; _logger = logger; _framework = framework;
        _config = config; _conditionManager = conditionManager; _sprint = sprint;
        _ipc = ipc; _vnavmesh = vnavmesh; _debugLog = debugLog; _getTerritory = getTerritory;
    }

    private void PrintMsg(string msg)
    {
        if (!_config.ChatOutput) return;
        if (_state == _lastMsgState) return;
        _lastMsgState = _state;
        _chatGui.Print(msg);
    }

    public void Start() { _framework.Update += OnTick; _lastUpdate = DateTime.MinValue; _followStartTime = DateTime.UtcNow; _debugLog.Log("引擎", "启动帧监听"); }
    private void StopUpdate() { _framework.Update -= OnTick; _debugLog.Log("引擎", "停止帧监听"); }

    private void OnTick(IFramework _)
    {
        // ── 两段式惯移（先最后已知点 → 再面朝惯移） ──
        if (_inertiaPhase != InertiaPhase.None)
        {
            HandleInertia();
            return;
        }

        if (_state is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped)
            return;

        // 每帧距离检查 — 距离≤5y停止移动（独立于扫描间隔，防贴脸）
        if (_state is FollowState.Following or FollowState.CatchingUp)
        {
            var p = _objectTable[0];
            var t = ResolveTarget();
            if (p != null && t != null && Vector3.Distance(p.Position, t.Position) <= 5f)
            {
                if (_vnavmesh.IsMoving) _vnavmesh.Stop();
                // 进入战斗状态，恢复循环插件攻击（与 CombatEnterRange 检测相同职责）
                _ipc.ResumeLoop();
                SetState(FollowState.Combat);
                return;
            }
        }

        // 地图黑名单检测
        if (CheckBlacklistedMap()) return;

        // 脱战检测 — 每帧都检查，不跟随扫描间隔
        _conditionManager.Update();
        var inCombat = _conditionManager.InCombat;
        if (!inCombat && _wasInCombat)
        {
            _combatEndTime = DateTime.UtcNow;
            _wasInCombat = false;
        }
        if (inCombat) _wasInCombat = true;

        if (!inCombat && _state == FollowState.Combat &&
            (DateTime.UtcNow - _combatEndTime).TotalSeconds >= OutOfCombatDelay)
        {
            // 脱战了但距离还很近的话，不恢复跟随（防反复横跳）
            if (_followTargetId != null || !string.IsNullOrEmpty(_followTargetName))
            {
                var p = _objectTable[0];
                if (p != null)
                {
                    var t = ResolveTarget();
                    if (t != null && Vector3.Distance(p.Position, t.Position) <= _config.CombatEnterRange)
                    {
                        _debugLog.Log("state", "脱战但距离近，跳过恢复");
                        return;
                    }
                }
            }
            PrintMsg("[强效跟随] 脱战恢复跟随");
            _debugLog.Log("state", "脱战恢复跟随");
            ResumeFollow();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastUpdate).TotalSeconds < _config.ScanInterval) return;
        _lastUpdate = now;

        var target = ResolveTarget();
        if (target == null)
        {
            if (_config.ContinueOnTargetLost)
            {
                // 目标传送后常从 ObjectTable 消失：先去最后已知点，到了再面朝惯移
                BeginTwoPhaseInertia("目标丢失，先去最后已知点");
            }
            else
            {
                _debugLog.Log("状态", "目标丢失");
                if (_config.PauseOnTargetLost) SetTarget(null);
            }
            return;
        }

        var player = _objectTable[0];
        if (player == null) return;

        var targetPos = target.Position;
        _lastKnownTargetPos = targetPos;
        var playerPos = player.Position;
        DistanceToTarget = Vector3.Distance(playerPos, targetPos);

        // 远距离目标检测：目标突然从近距跳到 >100y（仍在表内但已瞬移）
        if (DistanceToTarget > InertiaCancelRange && _lastScanDistance <= InertiaCancelRange
            && _inertiaPhase == InertiaPhase.None)
        {
            BeginTwoPhaseInertia($"目标突然远距({DistanceToTarget:F0}y，上次{_lastScanDistance:F0}y)，先去最后已知点");
            _lastScanDistance = DistanceToTarget;
            return;
        }

        // 目标在 ≤100y → 更新距离缓存
        _lastScanDistance = DistanceToTarget;

        // 暂停条件：距离≤进入值 + 已过启动保护期
        var graceRemaining = StartupGracePeriod - (DateTime.UtcNow - _followStartTime).TotalSeconds;
        if (DistanceToTarget <= _config.CombatEnterRange && _state != FollowState.Combat && graceRemaining <= 0)
        {
            PrintMsg($"[强效跟随] 距离≤{_config.CombatEnterRange}y，暂停跟随");
            _debugLog.Log("state", $"暂停跟随 距离≤{_config.CombatEnterRange}y");
            _vnavmesh.Stop(); _ipc.ResumeLoop(); SetState(FollowState.Combat); return;
        }
        // >30y 恢复跟随+暂停循环（Boss战不恢复）
        if (DistanceToTarget > _config.CombatExitRange && _state == FollowState.Combat)
        {
            if (IsBossTarget())
            {
                _debugLog.Log("state", "Boss战距离>30y但Boss仍在，不恢复");
            }
            else
            {
                PrintMsg($"[强效跟随] 距离>{_config.CombatExitRange}y，恢复跟随");
                _debugLog.Log("state", $"恢复跟随 距离>{_config.CombatExitRange}y");
                ResumeFollow();
            }
        }

        if (_state == FollowState.Combat) return;

        if (_config.UseMount)
        {
            if (_conditionManager.InCombat)
            {
                // 战斗中→放弃坐骑，改用疾跑
                _sprint.TryForceSprint();
            }
            else
            {
                // 脱战→直接上坐骑
                _sprint.TryMount();
            }
        }
        else if (_config.SprintEnabled)
        {
            if (_config.SprintAlwaysOn)
            {
                // 无脑疾跑
                _sprint.TryForceSprint();
            }
            else
            {
                // 目标在疾跑或距离>阈值 → 开疾跑
                var targetSprinting = SprintController.TargetIsSprinting(target);
                if (targetSprinting || DistanceToTarget > _config.SprintThreshold)
                    _sprint.TryForceSprint();
                else
                    _sprint.Update(DistanceToTarget, _conditionManager.InCombat);
            }
        }

        // 距离≤5y → 停止移动（不贴脸）
        if (DistanceToTarget <= 5f)
        {
            if (_vnavmesh.IsMoving) _vnavmesh.Stop();
            return;
        }

        // 目标移动超过阈值 → 立刻发新路径（vnavmesh会自动中断当前路径）
        if (_lastSentPosition != null && Vector3.Distance(targetPos, _lastSentPosition.Value) < _config.MoveThreshold)
            return;

        _debugLog.Log("move", $"target ({targetPos.X:F1},{targetPos.Y:F1},{targetPos.Z:F1})");
        _lastSentPosition = targetPos;
        if (_vnavmesh.IsAvailable) _vnavmesh.MoveTo(playerPos, targetPos);
        SetState(FollowState.Following);
    }

    /// <summary>扫描周围是否有Boss级敌人</summary>
    private unsafe bool IsBossTarget()
    {
        var player = _objectTable[0];
        if (player == null) return false;
        var pc = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
        if (pc == null) return false;
        var playerHp = pc->MaxHealth;

        foreach (var obj in _objectTable)
        {
            if (obj == null || obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
                continue;

            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
            if (chara == null) continue;

            var level = chara->Level;
            var hp = chara->MaxHealth;

            if (level >= 80 && hp > playerHp * 20) return true;
            if (level >= 50 && level < 80 && hp > playerHp * 15) return true;
            if (level >= 1 && level < 50 && hp > playerHp * 10) return true;
        }
        return false;
    }

    /// <summary>恢复跟随：开疾跑、立即扫描坐标、发送移动</summary>
    private void ResumeFollow()
    {
        // Boss 战中不恢复
        if (_conditionManager.InCombat && IsBossTarget())
        {
            _debugLog.Log("state", "Boss战中，跳过恢复");
            return;
        }

        // 重置扫描计时器，让下一帧立即扫描坐标
        _lastUpdate = DateTime.MinValue;

        // 清除上次发送的坐标缓存，强制重新发送
        _lastSentPosition = null;

        _ipc.PauseLoop();
        SetState(FollowState.Following);
    }

    /// <summary>
    /// 启动两段式惯移：
    /// 1) 跑向最后已知目标坐标
    /// 2) 到达后沿面朝方向再惯移 1.5s（总窗口 2s）
    /// 无最后已知点时直接进入面朝阶段。
    /// </summary>
    private void BeginTwoPhaseInertia(string reason)
    {
        var player = _objectTable[0];
        if (player == null) return;

        ClearInertiaState();
        _inertiaStartTime = DateTime.UtcNow;
        _debugLog.Log("状态", reason);

        if (_lastKnownTargetPos != null)
        {
            var dest = _lastKnownTargetPos.Value;
            // 已经在最后已知点附近 → 直接面朝惯移
            if (Vector3.Distance(player.Position, dest) <= LastKnownArriveRange)
            {
                StartFaceForwardPhase(player, "已在最后已知点附近，开始面朝惯移");
                return;
            }

            _inertiaPhase = InertiaPhase.ToLastKnown;
            _inertiaLastKnownDest = dest;
            if (_vnavmesh.IsAvailable)
                _vnavmesh.MoveTo(player.Position, dest);
            _debugLog.Log("状态", $"惯移阶段1：前往最后已知点 ({dest.X:F1},{dest.Y:F1},{dest.Z:F1})");
        }
        else
        {
            StartFaceForwardPhase(player, "无最后已知点，直接面朝惯移");
        }
    }

    private void StartFaceForwardPhase(IGameObject player, string reason)
    {
        var now = DateTime.UtcNow;
        var rot = player.Rotation;
        var forward = new Vector3((float)Math.Sin(rot), 0, (float)Math.Cos(rot));
        if (forward.LengthSquared() > 0) forward = Vector3.Normalize(forward);
        else forward = Vector3.UnitZ;
        var ahead = player.Position + forward * FaceForwardDistance;

        _inertiaPhase = InertiaPhase.FaceForward;
        _faceMoveStopTime = now + TimeSpan.FromSeconds(FaceMoveSeconds);
        _facePhaseExpiry = now + TimeSpan.FromSeconds(FacePhaseSeconds);
        _debugLog.Log("状态", reason);
        if (_vnavmesh.IsAvailable)
            _vnavmesh.MoveTo(player.Position, ahead);
    }

    /// <summary>
    /// 惯移每帧处理。中途若目标重现且距离≤100y，立即取消并恢复跟随。
    /// </summary>
    private void HandleInertia()
    {
        // 手动暂停 / 紧急停止 → 清惯移
        if (_state is FollowState.Paused or FollowState.EmergencyStopped or FollowState.Idle)
        {
            ClearInertiaState();
            return;
        }

        var player = _objectTable[0];
        if (player == null) return;

        // ── 取消条件：目标重新出现 且 距离 ≤100y → 恢复跟随 ──
        var target = ResolveTarget();
        if (target != null)
        {
            var dist = Vector3.Distance(player.Position, target.Position);
            DistanceToTarget = dist;
            _lastKnownTargetPos = target.Position;
            _lastScanDistance = dist;

            if (dist <= InertiaCancelRange)
            {
                _debugLog.Log("状态", $"惯移中目标恢复(距离{dist:F0}y≤{InertiaCancelRange})，取消惯移恢复跟随");
                ClearInertiaState();
                _lastUpdate = DateTime.MinValue;
                ResumeFollow();
                return;
            }
            // 目标在表内但仍 >100y：继续惯移（可能是瞬移后仍可见）
        }

        var now = DateTime.UtcNow;

        if (_inertiaPhase == InertiaPhase.ToLastKnown)
        {
            // 超时兜底：太久到不了最后已知点
            if ((now - _inertiaStartTime).TotalSeconds >= ToLastKnownTimeoutSeconds)
            {
                _debugLog.Log("状态", "前往最后已知点超时，改面朝惯移");
                StartFaceForwardPhase(player, "超时后开始面朝惯移");
                return;
            }

            var dest = _inertiaLastKnownDest;
            if (dest == null)
            {
                StartFaceForwardPhase(player, "无最后已知点，开始面朝惯移");
                return;
            }

            // 到达（或 vnav 已停且够近）→ 进入面朝阶段
            var distToLast = Vector3.Distance(player.Position, dest.Value);
            if (distToLast <= LastKnownArriveRange || (!_vnavmesh.IsMoving && distToLast <= LastKnownArriveRange * 2f))
            {
                StartFaceForwardPhase(player, $"到达最后已知点({distToLast:F1}y)，开始面朝惯移");
                return;
            }

            // 仍在路上：若 vnav 停了则补发一次路径
            if (!_vnavmesh.IsMoving && _vnavmesh.IsAvailable)
                _vnavmesh.MoveTo(player.Position, dest.Value);
            return;
        }

        if (_inertiaPhase == InertiaPhase.FaceForward)
        {
            if (_faceMoveStopTime != null && now < _faceMoveStopTime.Value)
            {
                // 1.5s 内保持面朝移动
                return;
            }

            if (_facePhaseExpiry != null && now < _facePhaseExpiry.Value)
            {
                // 1.5s~2s：停下等待窗口结束
                if (_vnavmesh.IsMoving) _vnavmesh.Stop();
                return;
            }

            // 2s 窗口结束
            FinishInertia();
        }
    }

    /// <summary>惯移完整结束：有目标则恢复跟随，无目标则按丢失策略收尾</summary>
    private void FinishInertia()
    {
        ClearInertiaState();
        _lastUpdate = DateTime.MinValue;

        if (ResolveTarget() != null)
        {
            _debugLog.Log("状态", "惯移结束，恢复跟随");
            ResumeFollow();
            return;
        }

        _debugLog.Log("状态", "惯移结束仍无目标，停止移动");
        if (_vnavmesh.IsMoving) _vnavmesh.Stop();
        if (_config.PauseOnTargetLost)
            SetTarget(null);
        else
            SetState(FollowState.Idle);
    }

    private void ClearInertiaState()
    {
        _inertiaPhase = InertiaPhase.None;
        _inertiaLastKnownDest = null;
        _faceMoveStopTime = null;
        _facePhaseExpiry = null;
    }

    private bool CheckBlacklistedMap()
    {
        var territory = _getTerritory();
        if (territory == null || _config.BlacklistedMaps.Count == 0) return false;
        if (!_config.BlacklistedMaps.Contains(territory.Value)) return false;

        if (_state != FollowState.Paused)
        {
            PrintMsg("[强效跟随] 当前地图在黑名单中，暂停跟随");
            _debugLog.Log("状态", $"地图{territory.Value}在黑名单中");
            PauseReason = "地图黑名单";
            ClearInertiaState();
            _vnavmesh.Stop(); SetState(FollowState.Paused);
        }
        return true;
    }

    private IGameObject? ResolveTarget()
    {
        if (_followTargetId != null)
        {
            var obj = _objectTable.SearchById((uint)_followTargetId.Value);
            if (obj != null) return obj;
        }
        if (!string.IsNullOrEmpty(_followTargetName))
        {
            var found = _objectTable.FirstOrDefault(o =>
                o.Name.TextValue == _followTargetName &&
                o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc);
            if (found != null) { _followTargetId = found.ObjectIndex; return found; }
        }
        return null;
    }

    public void SetTarget(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            _followTargetName = null; _followTargetId = null; _lastSentPosition = null;
            ClearInertiaState();
            SetState(FollowState.Idle); _vnavmesh.Stop();
            PrintMsg("[强效跟随] target cleared");
            _debugLog.Log("命令", "清除跟随目标");
            return;
        }

        _followTargetName = playerName; _followTargetId = null; _lastSentPosition = null;
        ClearInertiaState();
        var target = ResolveTarget();
        if (target != null) _followTargetId = target.ObjectIndex;
        else _debugLog.Log("命令", $"设目标{playerName}但未找到");

        _debugLog.Log("命令", $"设置跟随目标: {playerName}");
        Start(); SetState(FollowState.Following);
    }

    public void ManualMoveTo(Vector3 dest)
    {
        var player = _objectTable[0];
        if (player == null) return;
        _debugLog.Log("cmd", $"manual move ({dest.X:F1},{dest.Y:F1},{dest.Z:F1})");
        if (_vnavmesh.IsAvailable) _vnavmesh.MoveTo(player.Position, dest);
    }

    public void EmergencyStop()
    {
        PrintMsg("[强效跟随] 紧急停止");
        _debugLog.Log("cmd", "emergency stop");
        ClearInertiaState();
        SetState(FollowState.EmergencyStopped);
        _vnavmesh.Stop(); _sprint.Reset(); _ipc.PauseLoop();
        IsEmergencyStopped = true;
    }

    public void Toggle()
    {
        if (_state is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped)
        {
            if (!string.IsNullOrEmpty(_followTargetName))
            {
                IsEmergencyStopped = false; _lastSentPosition = null;
                ClearInertiaState();
                _debugLog.Log("cmd", "toggle -> start"); Start(); SetState(FollowState.Following);
            }
        }
        else
        {
            _debugLog.Log("cmd", "toggle -> pause");
            ClearInertiaState();
            SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset();
        }
    }

    public void Pause(string? reason = null)
    {
        _debugLog.Log("cmd", $"pause: {reason ?? ""}");
        PauseReason = reason ?? "手动暂停";
        ClearInertiaState();
        SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset();
        _conditionManager.ManualPause(reason); _ipc.PauseLoop();
    }

    public void Resume()
    {
        _conditionManager.ManualResume(); IsEmergencyStopped = false;
        if (string.IsNullOrEmpty(_followTargetName)) return;
        _lastSentPosition = null;
        ClearInertiaState();
        Start(); SetState(FollowState.Following);
        _ipc.ResumeLoop();
    }

    private void SetState(FollowState newState)
    {
        if (_state == newState) return;
        var old = _state; _state = newState;
        PauseReason = _state switch
        {
            FollowState.Combat => "战斗中",
            FollowState.Paused => "手动暂停",
            FollowState.EmergencyStopped => "紧急停止",
            FollowState.TargetLost => "目标丢失",
            _ => null,
        };
        OnStateChanged?.Invoke(old, newState);
        if (_state is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped) StopUpdate();
    }

    public void Dispose() { StopUpdate(); ClearInertiaState(); _vnavmesh.Stop(); _sprint.Reset(); }
}
