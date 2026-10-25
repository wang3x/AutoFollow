using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using AutoFollow.Commands;

namespace AutoFollow.Models;

[Serializable]
public class FollowConfig
{
    // ── Mini 窗口按钮颜色（可自定义） ──
    public Vector4 BtnColorIdle      { get; set; } = new(0.45f, 0.45f, 0.45f, 1f); // 灰
    public Vector4 BtnColorFollowing { get; set; } = new(0.20f, 0.80f, 0.30f, 1f); // 绿
    public Vector4 BtnColorPaused    { get; set; } = new(1.00f, 0.70f, 0.10f, 1f); // 琥珀
    public float CombatEnterRange { get; set; } = 10f;
    public float CombatExitRange { get; set; } = 30f;

    // ── Boss 战专用：按「目标圈外沿」判定停止，小怪不受影响 ──
    public bool BossCircleStop { get; set; } = true;
    /// <summary>Boss 识别范围：只有距玩家在此距离内的 Boss 才参与判定（默认 40y）</summary>
    public float BossScanRange { get; set; } = 40f;

    // ── Boss 停步距离：按「近战 / 远程」两大类区分，均可手调 ──
    // 近战类 = 坦克 + 近战DPS；远程类 = 远程DPS + 法系DPS + 治疗
    public float BossStopMelee  { get; set; } = 2f;   // 近战（坦克/近战DPS）
    public float BossStopRanged { get; set; } = 10f;  // 远程（远程DPS/法系DPS/治疗）
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

    public float MoveThreshold { get; set; } = 5f;
    public float ScanInterval { get; set; } = 1f;
    public List<uint> BlacklistedMaps { get; set; } = new();
    public bool ChatOutput { get; set; } = false;

    // ── 其他控制 ──
    public List<TransitionProfile> TransitionProfiles { get; set; } = new();
    public bool ContinueOnTargetLost { get; set; } = true;
    // Legacy serialization only; no system key injection is performed.
    public VirtualKey CrossLineKey { get; set; } = VirtualKey.W;   // 仅保留旧配置反序列化兼容，不再注入按键
    public bool CrossLineKeyIsHold { get; set; } = true;           // 仅保留旧配置兼容
    public bool AutoFollowAfterZoneChange { get; set; } = true;
    public bool AutoAcceptTeleport { get; set; } = true;
    public bool PauseOnDutyComplete { get; set; } = true;

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
