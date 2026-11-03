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
    // ── 战斗中的停步 / 恢复距离 ──
    public float CombatEnterRange { get; set; } = 10f;
    public float CombatExitRange { get; set; } = 30f;

    // ── 非战斗中的停步 / 恢复距离（没有战斗顾虑，可贴得更近、追得更勤） ──
    /// <summary>非战斗时距目标 ≤ 此值就停步（纯粹防贴脸）</summary>
    public float NonCombatEnterRange { get; set; } = 5f;
    /// <summary>非战斗时距目标 &gt; 此值就恢复跟随（与停步保持缓冲，防边界抖动）</summary>
    public float NonCombatExitRange { get; set; } = 8f;

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

    /// <summary>完全初始化热键：按一下立即完全复位（等同刚加载插件），不显示状态提示</summary>
    public VirtualKey ResetKey { get; set; } = VirtualKey.F8;

    /// <summary>
    /// 旧版「紧急停止热键」字段（JSON 键名必须保持 EmergencyStopKey 才能读到老配置）。
    /// 仅用于迁移：Migration 里读到非默认值就搬到 <see cref="ResetKey"/>，随后置 null 并不再参与逻辑。
    /// </summary>
    public VirtualKey? EmergencyStopKey { get; set; }

    public string? PauseCommand { get; set; } = "/rotation off";
    public string? ResumeCommand { get; set; } = "/rotation Auto";

    public float MoveThreshold { get; set; } = 5f;
    public float ScanInterval { get; set; } = 1f;
    public List<uint> BlacklistedMaps { get; set; } = new();
    public bool ChatOutput { get; set; } = false;

    // ── 其他控制 ──
    public bool ContinueOnTargetLost { get; set; } = true;
    public VirtualKey CrossLineKey { get; set; } = VirtualKey.W;   // 穿线时模拟按下的前进键（默认 W=向前移动，按住型）
    public bool CrossLineKeyIsHold { get; set; } = true;           // 该键是否按住型（true=需一直按住如W，false=自动奔跑型如R按一下持续）
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
        new() { Command = "/fes",   Action = CommandAction.EmergencyStop,      Description = "紧急停止（保留：仅停跟随不复位）" },
        new() { Command = "/freset", Action = CommandAction.FullReset,          Description = "完全初始化（复位到刚加载状态）" },
        new() { Command = "/fdbg",  Action = CommandAction.ToggleDebugWindow,  Description = "打开/关闭主窗口" },
        new() { Command = "/ftar",  Action = CommandAction.FollowCurrentTarget,Description = "跟随当前选中的目标" },
    };
}
