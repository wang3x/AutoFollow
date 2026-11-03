namespace AutoFollow.Models;

/// <summary>
/// 常见循环插件的命令预设。
///
/// 设计要点：预设**只负责把命令文本填进输入框**，不保存到配置里、也不作为配置的身份标识。
/// 配置始终只有 <see cref="FollowConfig.PauseCommand"/> / <see cref="FollowConfig.ResumeCommand"/>
/// 两个字符串 —— 用户点预设后仍可继续手改，存的就是改后的文本，不会出现"显示某某预设、实际存着别的命令"的脱节。
/// </summary>
public static class LoopPresets
{
    /// <summary>自定义：不填任何内容，两个框保持用户当前输入</summary>
    public static readonly LoopPreset Custom =
        new("自定义", null, null, "两个输入框完全由你自己填写，不做任何改动");

    /// <summary>内置预设列表（下拉框顺序）</summary>
    public static readonly IReadOnlyList<LoopPreset> All = new[]
    {
        Custom,
        new LoopPreset("RotationSolver", "/rotation off", "/rotation Auto",
            "RSR 的命令实测为 /rotation 与 /rsr"),
        new LoopPreset("AEAssist", "/aestop", null,
            "AEAssist 恢复留空即可；另需在它自己的界面打开目标选择器"),
    };

    /// <summary>
    /// 根据当前命令文本反查匹配的预设下标；匹配不上返回 0（自定义）。
    /// 用于打开设置页时把下拉框定位到用户当前在用的那套。
    /// </summary>
    public static int MatchIndex(string? pause, string? resume)
    {
        if (string.IsNullOrWhiteSpace(pause)) return 0;

        var p = pause.Trim();
        var r = resume?.Trim() ?? "";

        for (var i = 1; i < All.Count; i++)
        {
            var preset = All[i];
            if (string.Equals(preset.Pause?.Trim(), p, StringComparison.OrdinalIgnoreCase)
                && string.Equals(preset.Resume?.Trim() ?? "", r, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }
}

/// <summary>
/// 一个循环插件预设。点「填入」时按字段逐个写入：
/// <see cref="Pause"/> / <see cref="Resume"/> 为 <c>null</c> 表示**把该输入框清空**（不是"跳过"），
/// 因为像 AEAssist 这类插件恢复命令本就该留空。
/// </summary>
/// <param name="Name">下拉框显示名</param>
/// <param name="Pause">点击后填入的暂停命令（null = 清空）</param>
/// <param name="Resume">点击后填入的恢复命令（null = 清空）</param>
/// <param name="Note">选中后显示的补充说明</param>
public sealed record LoopPreset(string Name, string? Pause, string? Resume, string Note);
