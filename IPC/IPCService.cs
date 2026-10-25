using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AutoFollow.Windows;

namespace AutoFollow.IPC;

public sealed class IPCService : IDisposable
{
    public VnavmeshIPC Vnavmesh { get; }
    public CommandLoopController LoopController { get; }

    public IPCService(IDalamudPluginInterface pi, ICommandManager commandManager, DebugLog debugLog,
        Func<Vector3?> getPlayerPos, IFramework framework, Func<ushort?> getTerritory)
    {
        Vnavmesh = new VnavmeshIPC(pi, debugLog, getPlayerPos, framework, getTerritory);
        LoopController = new CommandLoopController(commandManager);
    }

    public string GetLoopPluginSummary()
    {
        if (!LoopController.IsConfigured) return "未配置";
        var info = $"暂停:{LoopController.PauseCommand}";
        if (!string.IsNullOrEmpty(LoopController.ResumeCommand)) info += $" 恢复:{LoopController.ResumeCommand}";
        return info;
    }

    public bool PauseLoop() => LoopController.SendPause();
    public bool ResumeLoop() => LoopController.SendResume();
    public void Dispose() { Vnavmesh.Dispose(); }
}
