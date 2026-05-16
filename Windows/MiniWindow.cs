using System.Numerics;
using ImGuiNET;
using AutoFollow.Models;

namespace AutoFollow.Windows;

public sealed class MiniWindow
{
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Action _toggleMainWindow;
    private readonly Action _onMainButtonClick;

    public bool IsOpen { get; set; } = true;

    public MiniWindow(
        Func<FollowState> getState,
        Func<string?> getTargetName,
        Func<float> getDistance,
        Action toggleMainWindow,
        Action onMainButtonClick)
    {
        _getState = getState;
        _getTargetName = getTargetName;
        _getDistance = getDistance;
        _toggleMainWindow = toggleMainWindow;
        _onMainButtonClick = onMainButtonClick;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        var state = _getState();
        var target = _getTargetName();
        var dist = _getDistance();
        var isIdle = state == FollowState.Idle;
        var isFollowing = state is FollowState.Following or FollowState.CatchingUp;
        var isPaused = state is FollowState.Combat or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost;
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";

        ImGui.SetNextWindowSize(new Vector2(300, 0), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (!ImGui.Begin("跟随", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
        { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;

        // 右键单击打开主窗口
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.None) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            _toggleMainWindow();

        // 指示圈颜色：灰(空闲) / 绿(跟随中) / 黄(暂停)
        var stateColor = isIdle ? new Vector4(0.45f, 0.45f, 0.45f, 1)
            : isPaused ? new Vector4(0.9f, 0.7f, 0.2f, 1)
            : new Vector4(0.35f, 0.75f, 0.35f, 1);

        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var fh = ImGui.GetFrameHeight();
        var halfFh = fh * 0.5f;

        // 指示圈 — 直径缩小一半，圆心不变
        dl.AddCircleFilled(new Vector2(pos.X + halfFh, pos.Y + halfFh), (halfFh - 1) * 0.5f, ImGui.ColorConvertFloat4ToU32(stateColor));

        // 文字
        ImGui.SetCursorScreenPos(new Vector2(pos.X + fh + 4, pos.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(target ?? "无");
        ImGui.SameLine();
        ImGui.TextUnformatted(distStr + "y");
        ImGui.SameLine(0, fh);

        // 单按钮 — 三态文字与颜色
        string btnLabel;
        Vector4 btnColor;
        if (isIdle)
        {
            btnLabel = "启动";
            btnColor = new Vector4(0.45f, 0.45f, 0.45f, 1);
        }
        else if (isFollowing)
        {
            btnLabel = "跟随中";
            btnColor = new Vector4(0.3f, 0.6f, 0.3f, 1);
        }
        else
        {
            btnLabel = "暂停";
            btnColor = new Vector4(0.7f, 0.55f, 0.2f, 1);
        }

        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnColor * 1.1f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnColor * 0.9f);
        if (ImGui.Button(btnLabel, new Vector2(60, 0))) _onMainButtonClick();
        ImGui.PopStyleColor(3);

        ImGui.End();
    }
}
