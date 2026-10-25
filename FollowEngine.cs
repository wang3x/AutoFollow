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
    public ulong? TargetContentId { get; private set; }
    private readonly Func<ulong, IGameObject?> _resolveContentTarget;
    public event Action? ManualTargetSelected;

    private Vector3? _lastSentPosition;
    private DateTime _lastUpdate;
    private DateTime _combatEndTime;
    private DateTime _followStartTime;
    private bool _wasInCombat;
    private Vector3? _lastKnownTargetPos;
    /// <summary>循环插件的输出权当前是否由本插件暂停（用于复位时只还原本插件动过的状态）</summary>
    private bool _loopPausedByMe;

    private readonly TargetHistory _history = new();
    private readonly TransitionRecovery _recovery = new();
    private readonly Func<bool> _targetUnavailable;
    private readonly Func<TransitionProfile, bool> _interact;
    private ushort? _lastTerritory;
    private Vector3? _lastPlayerPosition;
    public bool IsRecovering => _recovery.Active;
    public TransitionProfile? ActiveTransitionProfile => _recovery.Profile;

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

    /// <summary>取玩家当前职能（ClassJob.Role：1坦克/2近战/3远程与法系/4治疗，0=未知）。由 Plugin 注入，避免引擎直接依赖 IPlayerState。</summary>
    private readonly Func<byte> _getPlayerRole;

    public FollowEngine(
        IObjectTable objectTable, IFramework framework,
        FollowConfig config, ConditionManager conditionManager, SprintController sprint,
        IPCService ipc, VnavmeshFollow vnavmesh, DebugLog debugLog,
        Func<ushort?> getTerritory, Func<byte> getPlayerRole, Func<ulong, IGameObject?> resolveContentTarget, Func<bool> targetUnavailable, Func<TransitionProfile, bool> interact)
    {
        _objectTable = objectTable; _framework = framework;
        _config = config; _conditionManager = conditionManager; _sprint = sprint;
        _ipc = ipc; _vnavmesh = vnavmesh; _debugLog = debugLog; _getTerritory = getTerritory;
        _getPlayerRole = getPlayerRole;
        _resolveContentTarget = resolveContentTarget;
        _targetUnavailable = targetUnavailable; _interact = interact;
    }

    /// <summary>
    /// 当前生效的 Boss 停步距离，按职能分两大类（ClassJob.Role 实测取值）：
    ///   近战类（坦克 Role=1 / 近战DPS Role=2）        → BossStopMelee（默认 2y）
    ///   远程类（远程与法系DPS Role=3 / 治疗 Role=4）  → BossStopRanged（默认 10y）
    /// 职能读不到时按远程类处理（保守：站远一点，避免贴进圈里）。
    /// </summary>
    private float EffectiveBossStopRange
    {
        get
        {
            var role = _getPlayerRole();
            return role is 1 or 2 ? _config.BossStopMelee : _config.BossStopRanged;
        }
    }

    private bool _started;
    public void Start() { if (_started) return; _started = true; _framework.Update += OnTick; _lastUpdate = DateTime.MinValue; _followStartTime = DateTime.UtcNow; _debugLog.Log("引擎", "启动帧监听"); }
    private void StopUpdate() { _started = false; _framework.Update -= OnTick; _debugLog.Log("引擎", "停止帧监听"); }

    private void OnTick(IFramework _)
    {
        if (_state is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped) return;
        var tickTime = DateTime.UtcNow;
        _conditionManager.Update();
        var local = _objectTable.LocalPlayer;
        var resolved = ResolveTarget();
        var territory = _getTerritory();
        var changedTerritory = _lastTerritory != null && _lastTerritory != territory;
        _lastTerritory = territory;
        if (CheckBlacklistedMap()) return;

        // These gates precede recovery, combat, distance checks and every movement command.
        if (local == null || _conditionManager.IsDead || _targetUnavailable() || resolved?.IsDead == true)
        {
            _vnavmesh.Stop();
            _recovery.Fail("角色死亡、目标不可用或玩家未加载，保持停止");
            SetState(FollowState.TargetLost, _recovery.Reason);
            return;
        }
        var playerJumped = _lastPlayerPosition is Vector3 lastPlayer && Vector3.Distance(lastPlayer, local.Position) > 12;
        _lastPlayerPosition = local.Position;
        if (_conditionManager.BetweenAreas || changedTerritory || playerJumped)
        {
            _history.Clear(); _lastKnownTargetPos = null;
            ApplyRecovery(_recovery.Hold(tickTime));
            SetState(FollowState.TargetLost, _recovery.Reason);
            if (!_config.AutoFollowAfterZoneChange) Pause("场景切换，自动恢复已关闭");
            return;
        }
        if (_recovery.Active)
        {
            var position = resolved != null && PathRequestGuard.Finite(resolved.Position)
                ? (Vector3?)resolved.Position : null;
            ApplyRecovery(_recovery.Tick(tickTime, local.Position, _vnavmesh.Status, position));
            if (_recovery.Active) PauseReason = _recovery.Reason;
            return;
        }
        if (resolved == null)
        {
            BeginRecovery("目标丢失", _config.ContinueOnTargetLost);
            return;
        }
        if (!_history.Observe(tickTime, resolved.Position))
        {
            BeginRecovery("目标位置不连续，保留传送前入口", _config.ContinueOnTargetLost);
            return;
        }
        _lastKnownTargetPos = resolved.Position;
        if (_vnavmesh.Status == PathStatus.Failed)
        {
            BeginRecovery("路径不可达，停止并等待有效路径", false);
            return;
        }

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
                    ? DistanceToBossCircle(p.Position, boss) <= EffectiveBossStopRange
                    : Vector3.Distance(p.Position, t.Position) <= 5f;

                if (reached)
                {
                    if (boss != null)
                        _debugLog.Log("state", $"距Boss圈{boss.HitboxRadius:F1}y外沿{DistanceToBossCircle(p.Position, boss):F1}y，进入战斗");
                    _vnavmesh.Stop();
                    // 进入战斗状态（SetState 内含 ResumeLoop，恢复循环插件攻击）
                    SetState(FollowState.Combat);
                    return;
                }
            }
        }

        // 脱战检测 — 每帧都检查，不跟随扫描间隔
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

        if (target == null) { BeginRecovery("目标丢失", _config.ContinueOnTargetLost); return; }
        var targetPos = target.Position;
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
            _vnavmesh.Stop();
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
        Log.Notice($"[强效跟随] 进入 Boss 战：{name} Lv{level}，将在圈外 {EffectiveBossStopRange:F0}y 停下开打");

        var role = _getPlayerRole();
        var roleName = role switch
        {
            1 => "坦克",
            2 => "近战",
            3 => "远程/法系",
            4 => "治疗",
            _ => "未知",
        };
        _debugLog.Log("boss",
            $"进入Boss战 {name} Lv{level} 圈R{anchor.HitboxRadius:F1} 当前圈外沿{gap:F1}y " +
            $"停止阈值{EffectiveBossStopRange:F0}y（职能{roleName}/{role}）");
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

    public void RejectTransition(string reason)
    {
        ApplyRecovery(_recovery.Fail(reason));
        SetState(FollowState.TargetLost, reason);
    }

    private void BeginRecovery(string reason, bool allowAttempt)
    {
        _vnavmesh.Stop(); _lastSentPosition = null; _sprint.Reset();
        var now = DateTime.UtcNow;
        var fresh = _history.Fresh(now);
        var source = fresh ? _lastKnownTargetPos : null;
        var profiles = source is Vector3 p
            ? (_config.TransitionProfiles ?? new()).Where(x => x != null && x.Matches(_getTerritory(), p)).ToArray()
            : Array.Empty<TransitionProfile>();
        // Ambiguous configuration cannot authorize interaction.
        var profile = profiles.Length == 1 ? profiles[0] : null;
        var step = _recovery.Begin(now, _objectTable.LocalPlayer?.Position, source,
            _history.Direction(now), profile, allowAttempt && fresh);
        SetState(FollowState.TargetLost, reason);
        _debugLog.Log("transition", reason);
        ApplyRecovery(step);
        PauseReason = _recovery.Reason;
    }

    private void ApplyRecovery(RecoveryStep step)
    {
        var player = _objectTable.LocalPlayer;
        switch (step.Action)
        {
            case RecoveryAction.Navigate:
                if (player != null) _vnavmesh.MoveTo(player.Position, step.Destination);
                break;
            case RecoveryAction.Direct:
                _vnavmesh.MoveDirect(step.Destination);
                break;
            case RecoveryAction.Stop:
                _vnavmesh.Stop();
                break;
            case RecoveryAction.Interact:
                _vnavmesh.Stop();
                if (_recovery.Profile is not TransitionProfile profile || !_interact(profile))
                    _recovery.Fail("入口对象或交互条件不匹配，保持停止");
                break;
            case RecoveryAction.Probe:
                if (player != null) _vnavmesh.Probe(player.Position, step.Destination);
                break;
            case RecoveryAction.Resume:
                _vnavmesh.Stop();
                _history.Clear(); _lastKnownTargetPos = null;
                _lastSentPosition = null; _lastUpdate = DateTime.MinValue;
                SetState(FollowState.Following);
                break;
        }
    }

    private void ClearRecovery()
    {
        _recovery.Clear(); _history.Clear(); _lastKnownTargetPos = null; _lastPlayerPosition = null;
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
            ClearRecovery();
            _vnavmesh.Stop(); SetState(FollowState.Paused);
        }
        return true;
    }

    private IGameObject? ResolveTarget()
    {
        if (TargetContentId is ulong contentId) return _resolveContentTarget(contentId);

        if (_followTargetId != null)
        {
            var obj = _objectTable.SearchById((uint)_followTargetId.Value);
            if (obj != null && obj.EntityId != _objectTable.LocalPlayer?.EntityId
                && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc
                && obj.Name.TextValue == _followTargetName) return obj;
        }
        if (!string.IsNullOrEmpty(_followTargetName))
        {
            var found = _objectTable.FirstOrDefault(o =>
                o.Name.TextValue == _followTargetName &&
                o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc);
            if (found != null && found.EntityId != _objectTable.LocalPlayer?.EntityId) { _followTargetId = found.EntityId; return found; }
        }
        return null;
    }

    public void WaitForParty(string reason)
    {
        if (_state != FollowState.Idle || TargetContentId != null) ResetToIdle();
        PauseReason = reason;
    }

    public void SetTarget(string? playerName, ulong? contentId = null, bool automatic = false)
    {
        if (!automatic) ManualTargetSelected?.Invoke();
        TargetContentId = contentId;

        if (string.IsNullOrWhiteSpace(playerName))
        {
            _followTargetName = null; _followTargetId = null; _lastSentPosition = null;
            ClearRecovery();
            SetState(FollowState.Idle); _vnavmesh.Stop();
            Log.Print("[强效跟随] 已清除跟随目标");
            _debugLog.Log("命令", "清除跟随目标");
            return;
        }

        _vnavmesh.Stop();
        IsEmergencyStopped = false;
        _lastTerritory = _getTerritory();
        _followTargetName = playerName; _followTargetId = null; _lastSentPosition = null;
        ClearRecovery();
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
            case FollowState.TargetLost:
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

        ClearRecovery();            // 清惯移/穿线阶段，松开穿线键
        _vnavmesh.Stop();               // 停 vnavmesh 寻路
        _sprint.Reset();                // 清疾跑标记

        TargetContentId = null;
        _followTargetName = null;       // 清跟随目标 → 迷你窗目标名/距离归零
        _followTargetId = null;
        _lastSentPosition = null;
        _lastKnownTargetPos = null;
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
        ClearRecovery();
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
                ClearRecovery();
                _debugLog.Log("cmd", "toggle -> start"); Start(); SetState(FollowState.Following);
            }
        }
        else
        {
            _debugLog.Log("cmd", "toggle -> pause");
            ClearRecovery();
            SetState(FollowState.Paused); _vnavmesh.Stop(); _sprint.Reset();
        }
    }

    public void Pause(string? reason = null)
    {
        _debugLog.Log("cmd", $"pause: {reason ?? ""}");
        PauseReason = reason ?? "手动暂停";
        ClearRecovery();
        SetState(FollowState.Paused, reason); _vnavmesh.Stop(); _sprint.Reset();   // 内含 PauseLoop
        _conditionManager.ManualPause(reason);
    }

    public void Resume()
    {
        _conditionManager.ManualResume(); IsEmergencyStopped = false;
        if (string.IsNullOrEmpty(_followTargetName)) return;
        _lastSentPosition = null;
        ClearRecovery();
        // 恢复的是「跟随」而不是「开打」：进入 Following 时输出应保持暂停，等靠近目标进 Combat 才恢复
        Start(); SetState(FollowState.Following);
    }

    private void SetState(FollowState newState, string? reason = null)
    {
        if (_state == newState) { if (reason != null) PauseReason = reason; return; }
        var old = _state; _state = newState;
        SyncLoopOutput(newState);   // 状态即真相：输出权随状态一起切换
        PauseReason = reason ?? _state switch
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

    public void Dispose() { StopUpdate(); ClearRecovery(); _vnavmesh.Stop(); _sprint.Reset(); }
}
