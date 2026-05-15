using System.Text.Json.Serialization;

namespace AutoFollow.Commands;

[Serializable]
public class CustomCommandEntry
{
    /// <summary>命令文本，如 "/flp"</summary>
    public string Command { get; set; } = "";

    /// <summary>绑定的动作</summary>
    public CommandAction Action { get; set; }

    /// <summary>参数模板说明</summary>
    public string? ArgTemplate { get; set; }

    /// <summary>启用/禁用</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>备注描述</summary>
    public string Description { get; set; } = "";

    /// <summary>添加到帮助列表</summary>
    public bool ShowInHelp { get; set; } = true;
}
