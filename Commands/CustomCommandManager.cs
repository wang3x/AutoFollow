using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using AutoFollow.Models;

namespace AutoFollow.Commands;

/// <summary>
/// 自定义陌语命令管理器 — 用户可自己写命令文本和绑定动作。
/// 配置变更后调用 Reload() 重新注册。
/// </summary>
public sealed class CustomCommandManager : IDisposable
{
    private readonly ICommandManager _commandManager;
    private readonly FollowConfig _config;
    private readonly Action<CommandAction, string> _executeAction;

    // 已注册的命令列表
    private readonly List<string> _registeredCommands = new();

    public CustomCommandManager(
        ICommandManager commandManager,
        FollowConfig config,
        Action<CommandAction, string> executeAction)
    {
        _commandManager = commandManager;
        _config = config;
        _executeAction = executeAction;
    }

    /// <summary>重新加载所有自定义命令</summary>
    public void Reload()
    {
        UnregisterAll();

        foreach (var entry in _config.CustomCommands)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.Command)) continue;

            var cmdText = entry.Command.Trim();
            if (!cmdText.StartsWith('/')) cmdText = "/" + cmdText;

            // 跳过已注册的
            if (_registeredCommands.Contains(cmdText)) continue;

            try
            {
                _commandManager.AddHandler(cmdText, new CommandInfo((cmd, args) =>
                {
                    _executeAction(entry.Action, args);
                })
                {
                    HelpMessage = BuildHelpText(entry),
                    ShowInHelp = entry.ShowInHelp,
                });

                _registeredCommands.Add(cmdText);
                Log.Info($"注册命令: {cmdText} → {entry.Action}");
            }
            catch (Exception ex)
            {
                Log.Warning($"命令注册失败 {cmdText}: {ex.Message}");
            }
        }
    }

    private static string BuildHelpText(CustomCommandEntry entry)
    {
        var text = entry.Description;
        if (!string.IsNullOrEmpty(entry.ArgTemplate))
            text += " " + entry.ArgTemplate;
        return text;
    }

    public void UnregisterAll()
    {
        foreach (var cmd in _registeredCommands)
        {
            _commandManager.RemoveHandler(cmd);
        }
        _registeredCommands.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}
