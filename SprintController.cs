using System;
using AutoFollow.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using ConditionFlag = Dalamud.Game.ClientState.Conditions.ConditionFlag;

namespace AutoFollow;

/// <summary>自动疾跑控制</summary>
public sealed class SprintController : IDisposable
{
    private const uint SprintActionId = 4;

    private readonly IChatGui _chatGui;
    private readonly ICondition _condition;
    private readonly FollowConfig _config;

    private DateTime _lastSprintAttempt;
    private bool _isSprinting;

    public bool IsSprinting => _isSprinting;

    public SprintController(IChatGui chatGui, ICondition condition, FollowConfig config)
    {
        _chatGui = chatGui;
        _condition = condition;
        _config = config;
    }

    /// <summary>每帧调用，根据距离决定是否疾跑</summary>
    public void Update(float distanceToTarget, bool inCombat)
    {
        if (!_config.SprintEnabled)
        {
            _isSprinting = false;
            return;
        }

        if (_config.SprintOnlyInCombat && !inCombat)
        {
            _isSprinting = false;
            return;
        }

        // 距离超阈值 → 尝试疾跑
        if (distanceToTarget > _config.SprintThreshold)
        {
            TrySprint();
        }
        else
        {
            // 接近目标 → 标记疾跑结束（状态自然解除，不需要取消）
            _isSprinting = false;
        }
    }

    /// <summary>检查目标是否在疾跑中（statusId 50 = Sprint）</summary>
    public static unsafe bool TargetIsSprinting(Dalamud.Game.ClientState.Objects.Types.IGameObject? target)
    {
        if (target == null) return false;
        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)target.Address;
        if (chara == null) return false;
        var mgr = chara->GetStatusManager();
        if (mgr == null) return false;
        return mgr->HasStatus(50);
    }

    private unsafe void TrySprint()
    {
        if (_isSprinting) return;

        // 防刷
        var now = DateTime.UtcNow;
        if ((now - _lastSprintAttempt).TotalSeconds < 1.0) return;

        _lastSprintAttempt = now;

        var actionMgr = ActionManager.Instance();
        if (actionMgr == null) return;

        // 检查可用且不在 CD
        if (!actionMgr->IsActionOffCooldown(ActionType.GeneralAction, SprintActionId))
            return;

        // 检查是否可消耗（资源足够）
        if (actionMgr->GetActionStatus(ActionType.GeneralAction, SprintActionId) != 0)
            return;

        actionMgr->UseAction(ActionType.GeneralAction, SprintActionId);
        _isSprinting = true;
    }

    /// <summary>强制尝试疾跑（跟随时调用，无视距离检查）</summary>
    public void TryForceSprint()
    {
        if (!_config.SprintEnabled) return;
        _isSprinting = false;
        TrySprint();
    }

    /// <summary>尝试使用坐骑</summary>
    public unsafe void TryMount()
    {
        if (!_config.UseMount) return;
        if (_condition[ConditionFlag.Mounted]) return;
        if (_condition[ConditionFlag.InCombat]) return;
        if (_condition[ConditionFlag.BetweenAreas]) return;
        if (_condition[ConditionFlag.Casting]) return;

        var am = ActionManager.Instance();
        if (am == null) return;

        // 使用默认坐骑（ActionType.Mount, mountId）
        // mountId 0 = 默认/随机坐骑
        am->UseAction(ActionType.Mount, 0);
    }

    public void Reset()
    {
        _isSprinting = false;
    }

    public void Dispose()
    {
        Reset();
    }
}
