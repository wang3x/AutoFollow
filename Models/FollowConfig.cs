using Dalamud.Game.ClientState.Keys;
using AutoFollow.Commands;

namespace AutoFollow.Models;

[Serializable]
public class FollowConfig
{
    public float CombatEnterRange { get; set; } = 10f;
    public float CombatExitRange { get; set; } = 30f;
    public bool SprintEnabled { get; set; } = true;
    public bool SprintAlwaysOn { get; set; } = false;
    public bool SprintOnlyInCombat { get; set; } = false;
    public bool UseMount { get; set; } = false;
    public float SprintThreshold { get; set; } = 20f;

    public bool PauseOnCombat { get; set; } = true;
    public bool PauseOnTargetLost { get; set; } = true;
    public bool PauseOnDead { get; set; } = true;

    public VirtualKey EmergencyStopKey { get; set; } = VirtualKey.F8;

    public string? PauseCommand { get; set; } = "/rotation off";
    public string? ResumeCommand { get; set; } = "/rotation Auto";

    public float ScanInterval { get; set; } = 1f;
    public List<uint> BlacklistedMaps { get; set; } = new();
    public bool ChatOutput { get; set; } = true;

    public List<CustomCommandEntry> CustomCommands { get; set; } = new()
    {
        new() { Command = "/flp",   Action = CommandAction.PauseLoop,          Description = "暂停循环插件" },
        new() { Command = "/flr",   Action = CommandAction.ResumeLoop,         Description = "恢复循环插件" },
        new() { Command = "/flt",   Action = CommandAction.ToggleLoop,         Description = "切换循环插件" },
        new() { Command = "/ff",    Action = CommandAction.ToggleFollow,       Description = "切换跟随开关" },
        new() { Command = "/fst",   Action = CommandAction.StatusReport,       Description = "输出状态报告" },
        new() { Command = "/ft",    Action = CommandAction.SetFollowTarget,    Description = "设置跟随目标 <玩家名>" },
        new() { Command = "/fes",   Action = CommandAction.EmergencyStop,      Description = "紧急停止" },
        new() { Command = "/fdbg",  Action = CommandAction.ToggleDebugWindow,  Description = "打开/关闭主窗口" },
        new() { Command = "/ftar",  Action = CommandAction.FollowCurrentTarget,Description = "跟随当前选中的目标" },
    };
}
