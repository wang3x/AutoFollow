using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace AutoFollow;

/// <summary>
/// 全局统一日志/聊天输出 — 参考安米儿的 PrintPluginMessage 模式。
/// 始终写入日志文件；ChatOutput 配置控制是否在游戏聊天框显示。
/// 聊天消息带 �AF� 图标 + XivChatType.Echo 输出。
/// 初始化后所有模块可直接调用，无需传递实例引用。
/// </summary>
public static class Log
{
    private static IChatGui? _chat;
    private static IPluginLog? _log;
    private static Func<bool>? _isChatEnabled;

    /// <summary>在 Plugin 启动时调用一次</summary>
    public static void Initialize(IChatGui chat, IPluginLog log, Func<bool> isChatEnabled)
    {
        _chat = chat;
        _log = log;
        _isChatEnabled = isChatEnabled;
    }

    /// <summary>写入日志 + 按 ChatOutput 设置决定是否聊天输出（带 AF 图标）</summary>
    public static void Print(string msg)
    {
        _log?.Info(msg);
        if (_isChatEnabled?.Invoke() ?? false)
            ChatPrint(msg);
    }

    /// <summary>写入日志 + 强制聊天输出（不受 ChatOutput 控制，带 AF 图标）</summary>
    public static void Notice(string msg)
    {
        _log?.Info(msg);
        ChatPrint(msg);
    }

    private static void ChatPrint(string msg)
    {
        if (_chat == null) return;

        var entry = new XivChatEntry
        {
            Type = XivChatType.Echo,
            Message = new SeStringBuilder()
                .AddUiForeground(SeIconChar.BoxedLetterA.ToIconString(), 2)
                .AddUiForeground(SeIconChar.BoxedLetterF.ToIconString(), 2)
                .AddUiForeground($" {msg}", 24)
                .Build(),
        };
        _chat.Print(entry);
    }

    public static void Debug(string msg)   => _log?.Debug(msg);
    public static void Info(string msg)    => _log?.Info(msg);
    public static void Warning(string msg) => _log?.Warning(msg);
    public static void Error(string msg)   => _log?.Error(msg);
}
