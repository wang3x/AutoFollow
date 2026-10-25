using System.Numerics;
using System.Runtime.InteropServices;
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
        /// <summary>目标消失，前往最后已知坐标</summary>
        ToLastKnown,
        /// <summary>被过图线挡住，强制向前推进穿线（期间忽略目标重现，防止跨图被打断）</summary>
        CrossLinePush,
        /// <summary>到达后待命，等目标重现</summary>
        Waiting,
    }

    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly FollowConfig _config;
    private readonly ConditionManager _conditionManager;
    private readonly SprintController _sprint;
    private readonly IPCService _ipc;
    private readonly VnavmeshFollow _vnavmesh;
    private readonly DebugLog _debugLog;

    private FollowState _state = FollowState.Idle;
    private string? _followTargetName;
    private ulong? _followTargetId;
    private Vector3? _lastSentPosition;
    private DateTime _lastUpdate;
    private DateTime _combatEndTime;
    private DateTime _followStartTime;
    private bool _wasInCombat;
    private Vector3? _lastKnownTargetPos;
    /// <summary>循环插件的输出权当前是否由本插件暂停（用于复位时只还原本插件动过的状态）</summary>
    private bool _loopPausedByMe;

    // ── 惯移状态（目标消失 → 去最后已知点 → 待命） ──
    private InertiaPhase _inertiaPhase = InertiaPhase.None;
    private Vector3? _inertiaLastKnownDest;   // 原最后坐标（到达基准）
    private Vector3? _inertiaMoveDest;        // 实际移动目标（沿移动方向延伸点）
    private Vector3? _prevKnownTargetPos;     // 目标上一帧位置（用于计算移动方向）
    private DateTime _pushStartTime;          // 穿线推进开始时间
    private DateTime _lastAutoRunPress;       // 上次自动奔跑键注入时间（节流）
    private bool _keyHeld;                    // 按住型穿线键当前是否处于按住状态
    private Vector3? _pushDirection;          // 穿线推进方向（水平）

    /// <summary>当前暂停原因的友好描述，供 UI 显示</summary>
    public string? PauseReason { get; private set; }

    private const double OutOfCombatDelay = 1.0;
    private const double StartupGracePeriod = 2.0;
    private const float LastKnownArriveRange = 3f;   // 视为到达最后已知点
    private const float InertiaCancelRange = 100f;  // 目标 ≤100y 取消待命恢复跟随
    private const double CrossLinePushSeconds = 2.0; // 穿线推进时长

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
        IObjectTable objectTable, IFramework framework,
        FollowConfig config, ConditionManager conditionManager, SprintController sprint,
        IPCService ipc, VnavmeshFollow vnavmesh, DebugLog debugLog,
        Func<ushort?> getTerritory)
    {
        _objectTable = objectTable; _framework = framework;
        _config = config; _conditionManager = conditionManager; _sprint = sprint;
        _ipc = ipc; _vnavmesh = vnavmesh; _debugLog = debugLog; _getTerritory = getTerritory;
    }

    public void Start() { _framework.Update += OnTick; _lastUpdate = DateTime.MinValue; _followStartTime = DateTime.UtcNow; _debugLog.Log("引擎", "启动帧监听"); }
    private void StopUpdate() { _framework.Update -= OnTick; _debugLog.Log("引擎", "停止帧监听"); }

    private void OnTick(IFramework _)
    {
        // ── 惯移（目标消失 → 去最后已知点 → 待命等重现） ──
        if (_inertiaPhase != InertiaPhase.None)
        {
            HandleInertia();
            return;
        }

        if (_state is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped)
            return;

        // 每帧距离检查（独立于扫描间隔，防贴脸）
        // Boss 战 → 按「离 Boss 目标圈外沿」判定；其余（小怪/无目标）→ 沿用原「离跟随目标 ≤5y」
        if (_state is FollowState.Following or FollowState.CatchingUp)
        {
            var p = _objectTable[0];
            var t = ResolveTarget();
            if (p != null && t != null)
            {
                var boss = ResolveBossAnchor();
                bool reached = boss != null
                    ? DistanceToBossCircle(p.Position, boss) <= _config.BossStopRange
                    : Vector3.Distance(p.Position, t.Position) <= 5f;

                if (reached)
                {
                    if (boss != null)
                        _debugLog.Log("state", $"距Boss圈{boss.HitboxRadius:F1}y外沿{DistanceToBossCircle(p.Position, boss):F1}y，进入战斗");
                    if (_vnavmesh.IsMoving) _vnavmesh.Stop();
                    // 进入战斗状态（SetState 内含 ResumeLoop，恢复循环插件攻击）
                    SetState(FollowState.Combat);
                    return;
                }
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

        // _combatEndTime 只在「战斗中 → 脱战」那一帧被赋值；仍是 MinValue 说明本次根本没进过战斗，
        // 不满足「脱战恢复」的前提（否则 Boss 圈停步把状态置 Combat 时会瞬间误触发）
        if (!inCombat && _state == FollowState.Combat &&
            _combatEndTime != DateTime.MinValue &&
            (DateTime.UtcNow - _combatEndTime).TotalSeconds >= OutOfCombatDelay)
        {
            // Boss 战进行中（锚点仍在）→ 保持停步，不恢复跟随。
            // 否则会与「Boss 圈停步」互相拉扯：恢复跟随→下一帧又被圈判定拉回 Combat→再恢复…（每帧刷屏）
            if (GetBossAnchor() != null)
            {
                _debugLog.Log("state", "脱战但Boss战进行中，保持停步");
                return;
            }

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
            Log.Print("[强效跟随] 脱战恢复跟随");
            _debugLog.Log("state", "脱战恢复跟随");
            ResumeFollow();
            return;
        }

        // ── 每帧：解析目标、刷新最后已知点、目标丢失立即检测 ──
        var player = _objectTable[0];
        if (player == null) return;
        var target = ResolveTarget();

        if (target == null)
        {
            // 目标丢失 → 立即用最后一帧坐标作为移动目标（比 1s 扫描更准确及时）
            if (_config.ContinueOnTargetLost)
            {
                // 目标传送后常从 ObjectTable 消失：去最后已知点，到了停下待命
                BeginInertia("目标丢失，前往最后已知点");
            }
            else
            {
                _debugLog.Log("状态", "目标丢失");
                if (_config.PauseOnTargetLost) SetTarget(null);
            }
            return;
        }

        var targetPos = target.Position;
        if (_lastKnownTargetPos != null)
            _prevKnownTargetPos = _lastKnownTargetPos;
        _lastKnownTargetPos = targetPos;   // 每帧刷新最后已知点，惯移起点始终最新
        var playerPos = player.Position;
        DistanceToTarget = Vector3.Distance(playerPos, targetPos);

        // ── 每秒扫描决策（暂停/恢复/疾跑/发送移动） ──
        var now = DateTime.UtcNow;
        if ((now - _lastUpdate).TotalSeconds < _config.ScanInterval) return;
        _lastUpdate = now;

        // 暂停条件：距离≤进入值 + 已过启动保护期
        // Boss 战跳过此判定（改由每帧的「Boss 圈外沿」判定接管），否则坦克站在圈心时会被 10y 规则提前叫停
        var graceRemaining = StartupGracePeriod - (DateTime.UtcNow - _followStartTime).TotalSeconds;
        var bossAnchor = ResolveBossAnchor();
        if (bossAnchor == null && DistanceToTarget <= _config.CombatEnterRange
            && _state != FollowState.Combat && graceRemaining <= 0)
        {
            Log.Print($"[强效跟随] 距离≤{_config.CombatEnterRange}y，暂停跟随");
            _debugLog.Log("state", $"暂停跟随 距离≤{_config.CombatEnterRange}y");
            _vnavmesh.Stop(); SetState(FollowState.Combat); return;
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
                Log.Print($"[强效跟随] 距离>{_config.CombatExitRange}y，恢复跟随");
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
        // Boss 战跳过：此时停步与否由「离 Boss 圈外沿」决定，否则坦克站圈心时又会贴脸
        if (bossAnchor == null && DistanceToTarget <= 5f)
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

    /// <summary>玩家到敌人「目标圈外沿」的距离（已扣除 HitboxRadius，可为负=站在圈内）</summary>
    private static float DistanceToBossCircle(Vector3 playerPos, IGameObject boss)
        => Vector3.Distance(playerPos, boss.Position) - boss.HitboxRadius;

    /// <summary>Boss 判定倍数：50 级及以上 / 50 级以下</summary>
    private const uint BossHpMultiplierHigh = 20;
    private const uint BossHpMultiplierLow = 10;

    /// <summary>玩家最大生命值低于此值视为读取异常（任何等级角色都远高于此），用于防止基准错乱导致全员误判</summary>
    private const uint MinPlausiblePlayerHp = 100;

    /// <summary>
    /// 玩家「当前实际」最大生命值 —— Boss 判定的唯一基准。
    /// 用 Dalamud 托管属性 ICharacter.MaxHp（内部已按正确偏移读取），
    /// 它反映等级同步后的实时值（进低级副本同步后大幅下降）、装备与属性修正。
    /// **不得**用等级换算表推算，也不要用原生指针偏移硬读（7.x 结构变动会读错）。
    /// </summary>
    private uint GetPlayerMaxHp()
    {
        // LocalPlayer 比 _objectTable[0] 更明确，避免索引 0 在加载瞬间不是玩家
        var lp = _objectTable.LocalPlayer as ICharacter;
        return lp?.MaxHp ?? 0u;
    }

    /// <summary>
    /// 单个敌人是否为 Boss 级：以玩家「同步后实际最大生命值」为基准，
    /// 敌人 50 级及以上 &gt; 20 倍、50 级以下 &gt; 10 倍。
    /// 全部走 Dalamud 托管 API（ICharacter.MaxHp / ICharacter.Level），不碰原生指针。
    /// </summary>
    private bool IsBossLike(IGameObject? obj, uint playerMaxHp)
    {
        if (obj == null) return false;
        if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc) return false;

        // 基准异常保护：读不到玩家血量或数值离谱时，一律判定非 Boss，宁可退回旧逻辑也不要全员误判
        if (playerMaxHp < MinPlausiblePlayerHp) return false;

        if (obj is not ICharacter ch) return false;

        var level = ch.Level;
        var maxHp = ch.MaxHp;
        if (maxHp == 0) return false;

        var multiplier = level >= 50 ? BossHpMultiplierHigh : BossHpMultiplierLow;
        var threshold = (ulong)playerMaxHp * multiplier;   // ulong 防溢出

        var isBoss = maxHp > threshold;
        if (isBoss)
        {
            // 留痕便于实机核对：进低级副本时这里的 playerMaxHp 应是同步后的低值
            _debugLog.Log("boss",
                $"Boss判定 Lv{level} 敌HP{maxHp} > 玩家HP{playerMaxHp}×{multiplier}={threshold}");
        }
        return isBoss;
    }

    /// <summary>扫描玩家周围（BossScanRange 内）是否有 Boss 级敌人</summary>
    private bool IsBossTarget()
    {
        var playerHp = GetPlayerMaxHp();
        if (playerHp < MinPlausiblePlayerHp) return false;

        var player = _objectTable.LocalPlayer as IGameObject;
        if (player == null) return false;

        var playerPos = player.Position;
        var range = _config.BossScanRange;

        foreach (var obj in _objectTable)
        {
            if (obj == null) continue;
            if (Vector3.Distance(playerPos, obj.Position) > range) continue;   // 范围外忽略
            if (IsBossLike(obj, playerHp)) return true;
        }
        return false;
    }

    // Boss 锚点缓存：避免每帧遍历 ObjectTable（250ms 足够，Boss 不会瞬移）
    private IGameObject? _bossAnchorCache;
    private DateTime _bossAnchorCacheTime = DateTime.MinValue;
    private const double BossAnchorCacheSeconds = 0.25;

    // 「进入 Boss 战」提示的防抖状态
    private bool _bossEngageNotified;
    private DateTime _lastBossNoticeTime = DateTime.MinValue;
    private const double BossNoticeCooldownSeconds = 10;

    /// <summary>
    /// 解析「Boss 战斗锚点」：仅当跟随目标（坦克）正在与一只 Boss 交战时返回该 Boss。
    /// 小怪、杂兵、目标已死 → 返回 null，调用方回落到原有按跟随目标距离判定的逻辑。
    ///
    /// 两条取法（按可靠性排序）：
    ///   1. 坦克当前选中的目标就是 Boss（最常见）；
    ///   2. 坦克没选目标时，取「正在攻击坦克」的那只 Boss（Boss 开怪后会自动锁定坦克）。
    /// </summary>
    private IGameObject? ResolveBossAnchor()
    {
        var anchor = GetBossAnchor();
        HandleBossEngageNotice(anchor);
        return anchor;
    }

    private IGameObject? GetBossAnchor()
    {
        if (!_config.BossCircleStop) return null;

        var now = DateTime.UtcNow;
        if ((now - _bossAnchorCacheTime).TotalSeconds < BossAnchorCacheSeconds)
        {
            // 缓存命中：仍需确认对象还活着（死亡/消失要立刻失效）
            var cached = _bossAnchorCache;
            if (cached == null) return null;
            if (cached.IsDead || !cached.IsTargetable) { _bossAnchorCache = null; return null; }
            return cached;
        }

        _bossAnchorCacheTime = now;
        _bossAnchorCache = ResolveBossAnchorCore();
        return _bossAnchorCache;
    }

    /// <summary>
    /// 「进入 Boss 战」提示：锚点由无 → 有时发一次（带 Boss 名与等级），
    /// 锚点消失（Boss 死亡/脱战）后复位，下次再进再提示。
    /// 10 秒冷却用于吸收「坦克短暂丢失目标」造成的一帧抖动，避免刷屏。
    /// </summary>
    private void HandleBossEngageNotice(IGameObject? anchor)
    {
        if (anchor == null)
        {
            _bossEngageNotified = false;   // 战斗结束/切回小怪 → 允许下次再提示
            return;
        }

        if (_bossEngageNotified) return;
        if ((DateTime.UtcNow - _lastBossNoticeTime).TotalSeconds < BossNoticeCooldownSeconds) return;

        _bossEngageNotified = true;
        _lastBossNoticeTime = DateTime.UtcNow;

        var name = anchor.Name.TextValue;
        var level = (anchor as ICharacter)?.Level ?? 0;
        var gap = _objectTable.LocalPlayer is IGameObject lp
            ? DistanceToBossCircle(lp.Position, anchor)
            : 0f;

        // 聊天提示：用户明确要求「明显的提示」，故用 Notice 强制输出（不受聊天开关影响）
        Log.Notice($"[强效跟随] 进入 Boss 战：{name} Lv{level}，将在圈外 {_config.BossStopRange:F0}y 停下开打");

        _debugLog.Log("boss",
            $"进入Boss战 {name} Lv{level} 圈R{anchor.HitboxRadius:F1} 当前圈外沿{gap:F1}y 停止阈值{_config.BossStopRange:F0}y");
    }

    private IGameObject? ResolveBossAnchorCore()
    {
        try
        {
            var tank = ResolveTarget();
            if (tank == null) return null;

            // 基准每帧重新读取：等级同步 / 换装 / 副本内属性变化都会实时反映
            var playerHp = GetPlayerMaxHp();
            if (playerHp < MinPlausiblePlayerHp) return null;

            var player = _objectTable.LocalPlayer as IGameObject;
            if (player == null) return null;

            var playerPos = player.Position;
            var range = _config.BossScanRange;

            // ① 坦克选中的目标（须在识别范围内 —— 坦克预选远处Boss时不应把玩家拉过去）
            var selected = tank.TargetObject;
            if (selected != null && !selected.IsDead && selected.IsTargetable
                && Vector3.Distance(playerPos, selected.Position) <= range
                && IsBossLike(selected, playerHp))
                return selected;

            // ② 兜底：正在攻击坦克的 Boss（仅在战斗中扫描，避免平时浪费）
            if (!_conditionManager.InCombat) return null;

            IGameObject? best = null;
            var bestDist = float.MaxValue;

            foreach (var obj in _objectTable)
            {
                if (obj == null || obj.IsDead || !obj.IsTargetable) continue;
                if (obj.TargetObjectId != tank.EntityId) continue;

                var d = Vector3.Distance(playerPos, obj.Position);
                if (d > range) continue;                       // 范围外忽略
                if (!IsBossLike(obj, playerHp)) continue;

                if (d < bestDist) { bestDist = d; best = obj; }
            }
            return best;
        }
        catch
        {
            return null;
        }
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

        SetState(FollowState.Following);   // 内含 PauseLoop
    }

    /// <summary>
    /// 目标消失 → 立即用最后一帧的目标坐标作为移动目标（一次刷新）。
    /// 有最后已知点且不在附近 → 前往该点；到达后停下待命等目标重现。
    /// </summary>
    private void BeginInertia(string reason)
    {
        var player = _objectTable[0];
        if (player == null) return;

        ClearInertiaState();
        _debugLog.Log("状态", reason);

        if (_lastKnownTargetPos != null)
        {
            var lastKnown = _lastKnownTargetPos.Value;

            // 计算穿线推进方向：优先目标移动方向（上一帧→最后一帧），
            // 目标静止时用 玩家出发位置→最后坐标 方向（即目标消失前的行进方向，通常指向过图线）
            _pushDirection = null;
            if (_prevKnownTargetPos != null)
            {
                var d = lastKnown - _prevKnownTargetPos.Value;
                d.Y = 0;
                if (d.LengthSquared() > 0.25f)
                    _pushDirection = Vector3.Normalize(d);
            }
            if (_pushDirection == null)
            {
                var d = lastKnown - player.Position;
                d.Y = 0;
                if (d.LengthSquared() > 0.25f)
                    _pushDirection = Vector3.Normalize(d);
            }

            // 已经在最后已知点附近 → 若目标仍未重现且有推进方向，直接穿线推进
            if (Vector3.Distance(player.Position, lastKnown) <= LastKnownArriveRange)
            {
                if (_pushDirection != null)
                {
                    BeginCrossLinePush(player);
                    return;
                }
                EnterWaiting("已在最后已知点附近，停下待命等目标重现");
                return;
            }

            // 前往最后坐标（vnav 寻路）；穿线由到达后的自动奔跑完成
            var moveDest = lastKnown;
            _inertiaPhase = InertiaPhase.ToLastKnown;
            _inertiaLastKnownDest = lastKnown;
            _inertiaMoveDest = moveDest;
            if (_vnavmesh.IsAvailable)
                _vnavmesh.MoveTo(player.Position, moveDest, lastKnown);
            _debugLog.Log("状态", $"前往最后坐标 ({moveDest.X:F1},{moveDest.Y:F1},{moveDest.Z:F1})");
        }
        else
        {
            // 无最后已知点 → 直接待命
            EnterWaiting("无最后已知点，停下待命等目标重现");
        }
    }

    /// <summary>
    /// 惯移每帧处理：
    /// - CrossLinePush：强制向前推进穿线（期间忽略目标重现，防止跨图步骤被打断）
    /// - 目标重现且距离≤100y → 立即恢复跟随（ToLastKnown/Waiting 阶段）
    /// - ToLastKnown：前往最后已知点；vnav 停止且未到达 → 判定过图线阻挡，开始穿线推进
    /// - Waiting：保持待命，只等目标重现
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

        // ── CrossLinePush 阶段：自动奔跑穿线（期间忽略目标重现） ──
        if (_inertiaPhase == InertiaPhase.CrossLinePush)
        {
            var now = DateTime.UtcNow;
            if ((now - _pushStartTime).TotalSeconds < CrossLinePushSeconds)
            {
                // 面向推进方向 + 启动自动奔跑（沿角色朝向直线跑，不受镜头/navmesh 约束）
                FacePushDirection(player);
                StartAutoRun();
                return;
            }

            // 推进超时结束
            StopAutoRun();
            _debugLog.Log("状态", "穿线推进结束");
            if (ResolveTarget() != null)
            {
                ClearInertiaState();
                _lastUpdate = DateTime.MinValue;
                ResumeFollow();
            }
            else
            {
                EnterWaiting("穿线推进结束，停下待命等目标重现");
            }
            return;
        }

        // ── 取消条件：目标重新出现 且 距离 ≤100y → 恢复跟随 ──
        var target = ResolveTarget();
        if (target != null)
        {
            var dist = Vector3.Distance(player.Position, target.Position);
            DistanceToTarget = dist;
            if (_lastKnownTargetPos != null)
                _prevKnownTargetPos = _lastKnownTargetPos;
            _lastKnownTargetPos = target.Position;

            if (dist <= InertiaCancelRange)
            {
                _debugLog.Log("状态", $"惯移中目标恢复(距离{dist:F0}y≤{InertiaCancelRange})，取消惯移恢复跟随");
                ClearInertiaState();
                _lastUpdate = DateTime.MinValue;
                ResumeFollow();
                return;
            }
            // 目标在表内但仍 >100y：继续前往/待命（瞬移后仍可见）
        }

        if (_inertiaPhase == InertiaPhase.ToLastKnown)
        {
            var lastKnown = _inertiaLastKnownDest;
            if (lastKnown == null)
            {
                EnterWaiting("无目的地，停下待命等目标重现");
                return;
            }

            var moveDest = _inertiaMoveDest ?? lastKnown.Value;

            // 到达延伸点或原坐标（或 vnav 已停且够近）
            var distToMove = Vector3.Distance(player.Position, moveDest);
            var distToLast = Vector3.Distance(player.Position, lastKnown.Value);
            if (distToMove <= LastKnownArriveRange || distToLast <= LastKnownArriveRange
                || (!_vnavmesh.IsMoving && (distToMove <= LastKnownArriveRange * 2f || distToLast <= LastKnownArriveRange * 2f)))
            {
                // 到达后目标仍未重现：若有推进方向（朝向目标消失前的行进方向）→ 穿线推进跨过过图线
                if (_pushDirection != null)
                {
                    BeginCrossLinePush(player);
                    return;
                }
                EnterWaiting($"到达移动点({distToMove:F1}y/原坐标{distToLast:F1}y)，停下待命等目标重现");
                return;
            }

            // vnav 已停止但离目标点仍远 → 判定被过图线/障碍挡住 → 强制向前推进穿线
            if (!_vnavmesh.IsMoving)
            {
                BeginCrossLinePush(player);
                return;
            }

            // 仍在移动中：等待 vnav 到达
            return;
        }

        // Waiting：什么都不做，等待目标重现（上方检测已处理接回）
    }

    /// <summary>开始穿线推进：记录推进方向，面向该方向后启动自动奔跑 2 秒跨越过图线</summary>
    private void BeginCrossLinePush(IGameObject player)
    {
        _inertiaPhase = InertiaPhase.CrossLinePush;
        _pushStartTime = DateTime.UtcNow;
        _lastAutoRunPress = DateTime.MinValue;
        _keyHeld = false;

        // 推进方向：BeginInertia 已算好的优先（目标移动方向/玩家→最后坐标方向），
        // 为空时才用 玩家→移动目标 方向兜底
        if (_pushDirection == null)
        {
            var dest = _inertiaMoveDest ?? _inertiaLastKnownDest;
            if (dest != null)
            {
                var d = dest.Value - player.Position;
                d.Y = 0;
                if (d.LengthSquared() > 0.25f)
                    _pushDirection = Vector3.Normalize(d);
            }
        }

        _vnavmesh.Stop();
        // 使挂起的 MoveTo 寻路结果失效，防止晚到的旧路径干扰自动奔跑
        _vnavmesh.InvalidatePaths();

        if (_pushDirection != null)
        {
            _debugLog.Log("状态", "vnav 停止但未到达，判定过图线阻挡，面向推进方向后自动奔跑穿线(2s)");
        }
        else
        {
            _debugLog.Log("状态", "vnav 停止但未到达，无推进方向，直接待命");
            EnterWaiting("无推进方向，停下待命等目标重现");
        }
    }

    /// <summary>每帧面向推进方向（自动奔跑沿角色朝向直线移动，必须先对准方向）</summary>
    private unsafe void FacePushDirection(IGameObject player)
    {
        if (_pushDirection == null) return;
        var yaw = (float)Math.Atan2(_pushDirection.Value.X, _pushDirection.Value.Z); // FFXIV 朝向角：0=北，顺时针
        var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        if (go != null)
            go->Rotation = yaw;
    }

    // ── Win32 原生按键注入（绕开卫月禁止模拟按键的限制，真实按键驱动角色） ──
    private const uint KEYEVENTF_KEYUP = 0x0002;
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    /// <summary>Win32 按键按下（KEYDOWN）</summary>
    private void PressKey()
    {
        try { keybd_event((byte)_config.CrossLineKey, 0, 0, 0); } catch { }
    }

    /// <summary>Win32 按键松开（KEYUP）</summary>
    private void ReleaseKey()
    {
        try { keybd_event((byte)_config.CrossLineKey, 0, KEYEVENTF_KEYUP, 0); } catch { }
    }

    /// <summary>启动穿线移动：按住型键按住；自动奔跑型键点按触发</summary>
    private void StartAutoRun()
    {
        if (_config.CrossLineKeyIsHold)
        {
            // 按住型（如 W）：持续按住直到推进结束
            if (_keyHeld) return;
            PressKey();
            _keyHeld = true;
            _debugLog.Log("move", $"穿线键按住（hold 型 {_config.CrossLineKey}）");
            return;
        }

        // 自动奔跑型（如 R）：游戏已开启则无需按，否则点按一次（500ms 节流）
        if (IsGameAutoRunning()) return;
        var now = DateTime.UtcNow;
        if ((now - _lastAutoRunPress).TotalMilliseconds < 500) return;
        _lastAutoRunPress = now;
        PressKey();
        ReleaseKey();
        _debugLog.Log("move", $"auto-run 键点按（{_config.CrossLineKey}）");
    }

    /// <summary>停止穿线移动：按住型松键；自动奔跑型点按取消</summary>
    private void StopAutoRun()
    {
        if (_config.CrossLineKeyIsHold)
        {
            // 按住型：直接松开
            if (_keyHeld)
            {
                ReleaseKey();
                _keyHeld = false;
                _debugLog.Log("move", "穿线键松开（hold 型）");
            }
            return;
        }

        // 自动奔跑型：游戏仍开着才点按取消
        if (!IsGameAutoRunning()) return;
        PressKey();
        ReleaseKey();
        _debugLog.Log("move", "auto-run 键取消");
    }

    /// <summary>读取游戏原生自动奔跑状态（InputManager.IsAutoRunning）</summary>
    private static unsafe bool IsGameAutoRunning()
    {
        try { return FFXIVClientStructs.FFXIV.Client.Game.Control.InputManager.IsAutoRunning(); }
        catch { return false; }
    }

    /// <summary>进入待命：停止移动，等目标重现（由 HandleInertia 自动接回跟随）</summary>
    private void EnterWaiting(string reason)
    {
        StopAutoRun();   // 保险：停止自动奔跑
        _inertiaPhase = InertiaPhase.Waiting;
        _inertiaLastKnownDest = null;
        _inertiaMoveDest = null;
        _debugLog.Log("状态", reason);
        if (_vnavmesh.IsMoving) _vnavmesh.Stop();
    }

    private void ClearInertiaState()
    {
        StopAutoRun();   // 保险：停止自动奔跑
        _inertiaPhase = InertiaPhase.None;
        _inertiaLastKnownDest = null;
        _inertiaMoveDest = null;
        _pushDirection = null;
    }

    private bool CheckBlacklistedMap()
    {
        var territory = _getTerritory();
        if (territory == null || _config.BlacklistedMaps.Count == 0) return false;
        if (!_config.BlacklistedMaps.Contains(territory.Value)) return false;

        if (_state != FollowState.Paused)
        {
            Log.Print("[强效跟随] 当前地图在黑名单中，暂停跟随");
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
            if (found != null) { _followTargetId = found.EntityId; return found; }
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
            Log.Print("[强效跟随] 已清除跟随目标");
            _debugLog.Log("命令", "清除跟随目标");
            return;
        }

        _followTargetName = playerName; _followTargetId = null; _lastSentPosition = null;
        ClearInertiaState();
        var target = ResolveTarget();
        if (target != null) _followTargetId = target.EntityId;
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

    /// <summary>
    /// 按引擎状态同步第三方循环插件的输出权 —— 状态即真相，所有入口共用这一处，
    /// 避免「SetTarget 进了 Following 却没暂停 RSR」这类不一致。
    ///
    ///   Following / CatchingUp   → 暂停输出（专心跟随）
    ///   Combat                   → 恢复输出（到位开打）
    ///   Paused / EmergencyStopped → 暂停输出
    ///   Idle（清目标/复位）       → 若本插件暂停过则还回去
    /// </summary>
    private void SyncLoopOutput(FollowState newState)
    {
        if (_ipc == null) return;

        switch (newState)
        {
            case FollowState.Combat:
                _ipc.ResumeLoop();
                _loopPausedByMe = false;
                break;

            case FollowState.Following:
            case FollowState.CatchingUp:
            case FollowState.Paused:
            case FollowState.EmergencyStopped:
                _ipc.PauseLoop();
                _loopPausedByMe = true;
                break;

            case FollowState.Idle:
                // 清目标/复位：只还原本插件真正暂停过的，别硬开用户原本关着的输出
                if (_loopPausedByMe)
                {
                    _ipc.ResumeLoop();
                    _loopPausedByMe = false;
                }
                break;
        }
    }

    /// <summary>
    /// 完全停止并复位到插件刚加载时的状态：
    /// 停寻路、清惯移/穿线、清目标、状态回 Idle。
    /// </summary>
    /// <param name="restoreLoopOutput">
    /// <c>true</c>  —— 把输出权交还给循环插件（清目标等常规场景，恢复插件接管前的状态）；
    /// <c>false</c> —— 反过来**暂停一次**循环输出（副本通关场景：出本后不希望 RSR 继续 auto）。
    /// </param>
    public void ResetToIdle(bool restoreLoopOutput = true)
    {
        _debugLog.Log("cmd", $"reset to idle (restoreLoopOutput={restoreLoopOutput})");

        ClearInertiaState();            // 清惯移/穿线阶段，松开穿线键
        _vnavmesh.Stop();               // 停 vnavmesh 寻路
        _sprint.Reset();                // 清疾跑标记

        _followTargetName = null;       // 清跟随目标 → 迷你窗目标名/距离归零
        _followTargetId = null;
        _lastSentPosition = null;
        _lastKnownTargetPos = null;
        _prevKnownTargetPos = null;
        DistanceToTarget = float.MaxValue;

        _conditionManager.ManualResume();
        IsEmergencyStopped = false;
        _bossEngageNotified = false;    // 顺手复位「进入Boss战」提示标记

        if (restoreLoopOutput)
        {
            // 只还原本插件动过的状态：没接管过就不要发恢复命令，否则会把用户原本关着的输出强行打开
            if (_loopPausedByMe)
            {
                _ipc.ResumeLoop();
                _loopPausedByMe = false;
            }

            SetState(FollowState.Idle); // PauseReason 清空 + StopUpdate() 取消帧订阅
            return;
        }

        // ── 副本通关：出本后不要让循环插件继续 auto ──
        // 先把归属标记摘掉，避免 SetState(Idle) 里的 SyncLoopOutput 又发一次恢复命令（否则会出现"恢复→再暂停"的闪烁）
        _loopPausedByMe = false;
        SetState(FollowState.Idle);
        _ipc.PauseLoop();               // 明确发一次「停止战斗循环」命令
        _loopPausedByMe = true;         // 记录：当前输出是由本插件关闭的
    }

    public void EmergencyStop()
    {
        Log.Print("[强效跟随] 紧急停止");
        _debugLog.Log("cmd", "emergency stop");
        ClearInertiaState();
        SetState(FollowState.EmergencyStopped);   // 内含 PauseLoop
        _vnavmesh.Stop(); _sprint.Reset();
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
        SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset();   // 内含 PauseLoop
        _conditionManager.ManualPause(reason);
    }

    public void Resume()
    {
        _conditionManager.ManualResume(); IsEmergencyStopped = false;
        if (string.IsNullOrEmpty(_followTargetName)) return;
        _lastSentPosition = null;
        ClearInertiaState();
        // 恢复的是「跟随」而不是「开打」：进入 Following 时输出应保持暂停，等靠近目标进 Combat 才恢复
        Start(); SetState(FollowState.Following);
    }

    private void SetState(FollowState newState)
    {
        if (_state == newState) return;
        var old = _state; _state = newState;
        SyncLoopOutput(newState);   // 状态即真相：输出权随状态一起切换
        PauseReason = _state switch
        {
            FollowState.Combat => "战斗姿态",
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
