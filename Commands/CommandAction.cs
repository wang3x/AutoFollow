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

    // ── 目标管理 ──
    FollowCurrentTarget,
    SetFollowTarget,
    ClearFollowTarget,

    // ── 调试 ──
    ToggleDebugWindow,
    OpenConfig,
    StatusReport,
}
