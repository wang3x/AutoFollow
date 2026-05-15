using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace AutoFollow.Conditions;

/// <summary>
/// 条件管理器 — 收集所有需要暂停跟随的原因。
/// 其他插件通过 IPC 也能添加/移除暂停原因。
/// </summary>
public sealed class ConditionManager
{
    private readonly ICondition _condition;
    private readonly Dictionary<string, PauseCondition> _pauseReasons = new();

    /// <summary>当前是否有任何暂停原因激活</summary>
    public bool ShouldPause => _pauseReasons.Values.Any(p => p.IsActive);

    /// <summary>所有激活的暂停原因</summary>
    public IReadOnlyList<string> ActivePauseReasons =>
        _pauseReasons.Where(p => p.Value.IsActive).Select(p => p.Key).ToList();

    /// <summary>当前是否在战斗中</summary>
    public bool InCombat { get; private set; }

    /// <summary>当前是否在场景切换</summary>
    public bool BetweenAreas { get; private set; }

    /// <summary>当前是否已死亡</summary>
    public bool IsDead { get; private set; }

    public ConditionManager(ICondition condition)
    {
        _condition = condition;
    }

    /// <summary>每帧更新 — 自动检测游戏状态</summary>
    public void Update()
    {
        InCombat = _condition[ConditionFlag.InCombat];
        BetweenAreas = _condition[ConditionFlag.BetweenAreas]
                    || _condition[ConditionFlag.OccupiedInQuestEvent]
                    || _condition[ConditionFlag.OccupiedInCutSceneEvent];
        IsDead = _condition[ConditionFlag.Unconscious];

        // 战斗状态
        if (InCombat)
            AddPauseReason("战斗中", PauseSource.Combat);
        else
            RemovePauseReason("战斗中");

        // 场景切换
        if (BetweenAreas)
            AddPauseReason("场景切换", PauseSource.Environment);
        else
            RemovePauseReason("场景切换");

        // 死亡 — 检查角色状态
        if (IsDead)
            AddPauseReason("已死亡", PauseSource.Environment);
        else
            RemovePauseReason("已死亡");
    }

    /// <summary>添加暂停原因</summary>
    public void AddPauseReason(string reason, PauseSource source = PauseSource.External)
    {
        if (!_pauseReasons.ContainsKey(reason))
            _pauseReasons[reason] = new PauseCondition { Source = source };

        _pauseReasons[reason].IsActive = true;
        _pauseReasons[reason].LastActive = DateTime.UtcNow;
    }

    /// <summary>移除暂停原因</summary>
    public void RemovePauseReason(string reason)
    {
        if (_pauseReasons.TryGetValue(reason, out var pc))
            pc.IsActive = false;
    }

    /// <summary>手动暂停</summary>
    public void ManualPause(string? reason = null)
    {
        AddPauseReason(reason ?? "手动暂停", PauseSource.Manual);
    }

    /// <summary>手动恢复</summary>
    public void ManualResume()
    {
        RemovePauseReason("手动暂停");
    }

    /// <summary>清除所有非环境暂停原因</summary>
    public void ClearExternalReasons()
    {
        foreach (var kv in _pauseReasons)
        {
            if (kv.Value.Source != PauseSource.Environment)
                kv.Value.IsActive = false;
        }
    }
}

/// <summary>暂停原因来源</summary>
public enum PauseSource
{
    Combat,      // 进入战斗
    Environment, // 场景切换/死亡/过场
    Manual,      // 用户手动
    External,    // 其他插件通过 IPC 请求
    Distance,    // 距离过远
}

/// <summary>暂停条件</summary>
public class PauseCondition
{
    public PauseSource Source { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastActive { get; set; }
}
