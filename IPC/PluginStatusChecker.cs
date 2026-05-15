using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoFollow.IPC;

/// <summary>
/// 第三方插件状态检查器 — 通过 Dalamud 的 InstalledPlugins 列表检测
/// vnavmesh、BossMod、BossModReborn、RotationSolverReborn 等插件
/// 是否已安装并启用。
/// </summary>
public sealed class PluginStatusChecker
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _logger;
    private readonly List<PluginCheck> _plugins = new();

    public IReadOnlyList<PluginCheck> Results => _plugins;

    /// <summary>已知的内置名称列表（InternalName 匹配）</summary>
    private static readonly Dictionary<string, (string DisplayName, PluginKind Kind)> KnownPlugins = new()
    {
        ["vnavmesh"] = ("vnavmesh", PluginKind.Pathfinding),
        ["BossMod"] = ("BossMod", PluginKind.Combat),
        ["BossModReborn"] = ("BossModReborn", PluginKind.Combat),
        ["RotationSolverReborn"] = ("RotationSolverReborn", PluginKind.Rotation),
        ["RotationSolver"] = ("RotationSolver", PluginKind.Rotation),
    };

    public PluginStatusChecker(IDalamudPluginInterface pi, IPluginLog logger)
    {
        _pi = pi;
        _logger = logger;
    }

    /// <summary>扫描已安装的插件</summary>
    public void ScanAll()
    {
        _plugins.Clear();

        var installed = _pi.InstalledPlugins?.ToList() ?? new();

        foreach (var kv in KnownPlugins)
        {
            var internalName = kv.Key;
            var (displayName, kind) = kv.Value;

            var plugin = installed.FirstOrDefault(p =>
                p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase));

            var check = new PluginCheck
            {
                Name = displayName,
                Kind = kind,
            };

            if (plugin != null)
            {
                check.Status = plugin.IsLoaded ? PluginStatus.Connected : PluginStatus.NotInstalled;
                check.Message = plugin.IsLoaded ? "已启用" : "已安装但未启用";
            }
            else
            {
                check.Status = PluginStatus.NotInstalled;
                check.Message = "未安装";
            }

            _logger.Info("插件检测: {0} -> {1}", displayName, check.Message);
            _plugins.Add(check);
        }
    }
}

public sealed class PluginCheck
{
    public string Name { get; init; } = "";
    public PluginKind Kind { get; init; }
    public PluginStatus Status { get; set; }
    public string Message { get; set; } = "";
}

public enum PluginKind { Pathfinding, Combat, Rotation }
public enum PluginStatus { Connected, NotInstalled }
