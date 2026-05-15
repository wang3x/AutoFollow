using Dalamud.Configuration;
using Dalamud.Plugin;
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
        return config;
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
