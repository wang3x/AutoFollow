namespace AutoFollow.Models;

public enum FollowState
{
    /// <summary>未启动跟随</summary>
    Idle,

    /// <summary>正常跟随中</summary>
    Following,

    /// <summary>追赶目标中（距离较远）</summary>
    CatchingUp,

    /// <summary>进入战斗范围，暂停跟随</summary>
    Combat,

    /// <summary>被手动/条件暂停</summary>
    Paused,

    /// <summary>目标丢失</summary>
    TargetLost,

    /// <summary>紧急停止（热键触发，不自动恢复）</summary>
    EmergencyStopped,
}
