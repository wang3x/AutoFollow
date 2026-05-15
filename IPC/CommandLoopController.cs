using Dalamud.Plugin.Services;

namespace AutoFollow.IPC;

/// <summary>
/// 循环插件命令控制器 — 通过发送陌语命令来控制第三方循环插件。
/// 所有命令内容由用户手填。
/// </summary>
public sealed class CommandLoopController
{
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _logger;

    private string? _pauseCmd;
    private string? _resumeCmd;

    /// <summary>是否已配置（至少填了暂停命令）</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_pauseCmd);

    /// <summary>暂停命令内容</summary>
    public string? PauseCommand => _pauseCmd;

    /// <summary>恢复命令内容</summary>
    public string? ResumeCommand => _resumeCmd;

    public CommandLoopController(ICommandManager commandManager, IPluginLog logger)
    {
        _commandManager = commandManager;
        _logger = logger;
    }

    /// <summary>更新命令配置（用户保存设置时调用）</summary>
    public void SetCommands(string? pause, string? resume)
    {
        _pauseCmd = string.IsNullOrWhiteSpace(pause) ? null : pause.Trim();
        _resumeCmd = string.IsNullOrWhiteSpace(resume) ? null : resume.Trim();
        _logger.Info("循环插件命令已更新: 暂停=[{0}] 恢复=[{1}]", _pauseCmd ?? "", _resumeCmd ?? "");
    }

    /// <summary>发送暂停命令</summary>
    public bool SendPause()
    {
        if (!IsConfigured) return false;
        _logger.Debug("执行暂停命令: {0}", _pauseCmd ?? "");
        _commandManager.ProcessCommand(_pauseCmd!);
        return true;
    }

    /// <summary>发送恢复命令</summary>
    public bool SendResume()
    {
        if (string.IsNullOrWhiteSpace(_resumeCmd))
        {
            if (!IsConfigured) return false;
            _logger.Debug("未配置恢复命令，发送暂停命令: {0}", _pauseCmd ?? "");
            _commandManager.ProcessCommand(_pauseCmd!);
            return true;
        }
        _logger.Debug("执行恢复命令: {0}", _resumeCmd ?? "");
        _commandManager.ProcessCommand(_resumeCmd!);
        return true;
    }
}
