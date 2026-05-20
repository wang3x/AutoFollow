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

    /// <summary>当前暂停原因的友好描述，供 UI 显示</summary>
    public string? PauseReason { get; private set; }

    private const double OutOfCombatDelay = 1.0;
    private const double StartupGracePeriod = 2.0;

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
            _debugLog.Log("状态", "目标丢失");
            if (_config.PauseOnTargetLost) SetTarget(null);
            return;
        }

        var player = _objectTable[0];
        if (player == null) return;

        var targetPos = target.Position;
        var playerPos = player.Position;
        DistanceToTarget = Vector3.Distance(playerPos, targetPos);

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
                o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player);
            if (found != null) { _followTargetId = found.ObjectIndex; return found; }
        }
        return null;
    }

    public void SetTarget(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            _followTargetName = null; _followTargetId = null; _lastSentPosition = null;
            SetState(FollowState.Idle); _vnavmesh.Stop();
            PrintMsg("[强效跟随] target cleared");
            _debugLog.Log("命令", "清除跟随目标");
            return;
        }

        _followTargetName = playerName; _followTargetId = null; _lastSentPosition = null;
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
                _debugLog.Log("cmd", "toggle -> start"); Start(); SetState(FollowState.Following);
            }
        }
        else { _debugLog.Log("cmd", "toggle -> pause"); SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset(); }
    }

    public void Pause(string? reason = null)
    {
        _debugLog.Log("cmd", $"pause: {reason ?? ""}");
        PauseReason = reason ?? "手动暂停";
        SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset();
        _conditionManager.ManualPause(reason); _ipc.PauseLoop();
    }

    public void Resume()
    {
        _conditionManager.ManualResume(); IsEmergencyStopped = false;
        if (string.IsNullOrEmpty(_followTargetName)) return;
        _lastSentPosition = null; Start(); SetState(FollowState.Following);
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

    public void Dispose() { StopUpdate(); _vnavmesh.Stop(); _sprint.Reset(); }
}
