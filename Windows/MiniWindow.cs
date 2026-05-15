using System.Numerics;
using ImGuiNET;
using AutoFollow.Models;

namespace AutoFollow.Windows;

/// <summary>
/// 迷你状态窗口 — 小巧常驻，实时显示跟随状态、距离和战斗信息
/// </summary>
public sealed class MiniWindow
{
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Func<bool> _getInCombat;
    private readonly Action _openMainWindow;
    private readonly Action _emergencyStop;

    public bool IsOpen { get; set; } = true;

    public MiniWindow(
        Func<FollowState> getState,
        Func<string?> getTargetName,
        Func<float> getDistance,
        Func<bool> getInCombat,
        Action openMainWindow,
        Action emergencyStop)
    {
        _getState = getState;
        _getTargetName = getTargetName;
        _getDistance = getDistance;
        _getInCombat = getInCombat;
        _openMainWindow = openMainWindow;
        _emergencyStop = emergencyStop;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        var state = _getState();
        var target = _getTargetName();
        var dist = _getDistance();
        var inCombat = _getInCombat();
        var isPaused = state is FollowState.Combat or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost;

        var stateColor = isPaused ? new Vector4(1, 0.8f, 0, 1) : state is FollowState.EmergencyStopped ? new Vector4(1, 0.2f, 0.2f, 1) : new Vector4(0.3f, 0.9f, 0.3f, 1);
        var combatStr = inCombat ? "战中" : "休";
        var combatColor = inCombat ? new Vector4(1, 0.3f, 0, 1) : new Vector4(0.5f, 0.8f, 1, 1);
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";

        ImGui.SetNextWindowSize(new Vector2(220, 32), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (!ImGui.Begin("跟随", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        // 状态圆点
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var center = new Vector2(pos.X + 6, pos.Y + 7);
        dl.AddCircleFilled(center, 5, ImGui.ColorConvertFloat4ToU32(stateColor));
        ImGui.SetCursorScreenPos(new Vector2(pos.X + 14, pos.Y));

        ImGui.TextUnformatted(target ?? "无目标");

        ImGui.SameLine();
        ImGui.TextUnformatted(distStr + "码");

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, combatColor);
        ImGui.TextUnformatted(combatStr);
        ImGui.PopStyleColor();

        ImGui.SameLine();
        if (ImGui.SmallButton("主"))
            _openMainWindow();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1, 0.3f, 0.3f, 1));
        if (ImGui.SmallButton("停"))
            _emergencyStop();
        ImGui.PopStyleColor(2);

        ImGui.End();
    }
}
