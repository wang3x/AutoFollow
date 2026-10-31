namespace AutoFollow.Commands;

public enum CommandAction
{
    // ── 循环插件控制 ──
    PauseLoop,
    ResumeLoop,
    ToggleLoop,
    SetLoopEnable,

    // ── 跟随控制 ──
    PauseFollow,
    ResumeFollow,
    ToggleFollow,
    EmergencyStop,
    /// <summary>完全初始化：停寻路、清目标、迷你窗复位到刚加载状态</summary>
    FullReset,

    // ── 目标管理 ──
    FollowCurrentTarget,
    SetFollowTarget,
    ClearFollowTarget,

    // ── 调试 ──
    ToggleDebugWindow,
    OpenConfig,
    StatusReport,
}
