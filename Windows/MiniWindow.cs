using System.Numerics;
using ImGuiNET;
using AutoFollow.Models;

namespace AutoFollow.Windows;

public sealed class MiniWindow
{
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Func<bool> _getInCombat;
    private readonly Action _stopResume;
    private readonly Action _smartFollow;

    public bool IsOpen { get; set; } = true;

    public MiniWindow(
        Func<FollowState> getState,
        Func<string?> getTargetName,
        Func<float> getDistance,
        Func<bool> getInCombat,
        Action toggleMainWindow,
        Action stopResume,
        Action smartFollow)
    {
        _getState = getState;
        _getTargetName = getTargetName;
        _getDistance = getDistance;
        _getInCombat = getInCombat;
        _stopResume = stopResume;
        _smartFollow = smartFollow;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        var state = _getState();
        var target = _getTargetName();
        var dist = _getDistance();
        var isPaused = state is FollowState.Combat or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost;
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";

        ImGui.SetNextWindowSize(new Vector2(300, 0), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (!ImGui.Begin("跟随", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
        { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;

        // 红绿灯指示圈 + 目标 + 距离 + 两个按钮 同一排对齐
        var stateColor = state is FollowState.EmergencyStopped ? new Vector4(1, 0.2f, 0.2f, 1)
            : isPaused ? new Vector4(1, 0.8f, 0, 1)
            : new Vector4(0.3f, 0.9f, 0.3f, 1);

        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var fh = ImGui.GetFrameHeight();
        var halfFh = fh * 0.5f;

        // 指示圈
        dl.AddCircleFilled(new Vector2(pos.X + halfFh, pos.Y + halfFh), halfFh - 1, ImGui.ColorConvertFloat4ToU32(stateColor));

        // 文字
        ImGui.SetCursorScreenPos(new Vector2(pos.X + fh + 4, pos.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(target ?? "无");
        ImGui.SameLine();
        ImGui.TextUnformatted(distStr + "码");
        ImGui.SameLine();

        // 急停/恢复按钮
        var stopLabel = isPaused ? "恢复" : "急停";
        var stopColor = isPaused ? new Vector4(0.2f, 0.8f, 0.2f, 1) : new Vector4(0.8f, 0.2f, 0.2f, 1);
        ImGui.PushStyleColor(ImGuiCol.Button, stopColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, stopColor * 1.2f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, stopColor * 0.8f);
        if (ImGui.Button(stopLabel, new Vector2(40, 0))) _stopResume();
        ImGui.PopStyleColor(3);

        ImGui.SameLine(0, 2);
        var following = !isPaused && !string.IsNullOrEmpty(target);
        var followColor = following ? new Vector4(0.2f, 0.7f, 0.2f, 1) : new Vector4(0.4f, 0.4f, 0.4f, 1);
        ImGui.PushStyleColor(ImGuiCol.Button, followColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, followColor * 1.2f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, followColor * 0.8f);
        if (ImGui.Button("跟随", new Vector2(40, 0))) _smartFollow();
        ImGui.PopStyleColor(3);

        ImGui.End();
    }
}
