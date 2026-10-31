using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using AutoFollow.Commands;
using AutoFollow.Models;

namespace AutoFollow;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public FollowConfig Follow { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        _pluginInterface = pi;
    }

    public static Configuration Load(IDalamudPluginInterface pi)
    {
        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pi);
        config.Migrate();
        return config;
    }

    /// <summary>
    /// 幂等迁移：每次加载都跑一遍，只在缺项时补齐，不会覆盖用户的自定义改动。
    /// 老配置由 Newtonsoft 反序列化得到，未识别的旧字段（如 EmergencyStopKey）会被静默丢弃。
    /// </summary>
    private void Migrate()
    {
        var dirty = false;

        // ── v1.10.12：EmergencyStopKey → ResetKey（老用户改过的键不能丢）──
        // 老配置里若存在该字段就会反序列化到 EmergencyStopKey，搬到 ResetKey 后清空。
        if (Follow.EmergencyStopKey is { } legacyKey)
        {
            // 只有用户确实改过（非默认 F8）才覆盖，避免把已经调好的 ResetKey 冲掉。
            // NO_KEY（主动禁用热键）也算"改过"，要一并迁移。
            if (legacyKey != VirtualKey.F8) Follow.ResetKey = legacyKey;
            Follow.EmergencyStopKey = null;
            dirty = true;
        }

        var cmds = Follow.CustomCommands ??= new();

        // ── 补齐 v1.10.12 新增的 /freset（老配置的命令列表是持久化的，不会自动带上新默认项）──
        if (!cmds.Any(c => c.Action == CommandAction.FullReset))
        {
            var idx = cmds.FindIndex(c => c.Action == CommandAction.ToggleDebugWindow);
            var entry = new CustomCommandEntry
            {
                Command = "/freset",
                Action = CommandAction.FullReset,
                Description = "完全初始化（复位到刚加载状态）",
            };
            if (idx >= 0) cmds.Insert(idx, entry);
            else cmds.Add(entry);
            dirty = true;
        }

        // 迁移改动落盘，避免每次加载重复迁移
        if (dirty) Save();
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
