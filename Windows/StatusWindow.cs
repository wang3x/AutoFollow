using ImGuiNET;
using AutoFollow.Models;

namespace AutoFollow.Windows;

/// <summary>状态显示窗口 — 纯 ImGui 实现，不依赖 Dalamud Window 基类</summary>
public sealed class StatusWindow
{
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTarget;
    private readonly Func<float> _getDistance;
    private readonly Func<string> _getZone;
    private readonly Func<string> _getMode;
    private readonly Func<bool> _getSprinting;
    private readonly Func<bool> _getPaused;
    private readonly Func<string?> _getLoopState;

    public bool IsOpen { get; set; }

    public StatusWindow(
        Func<FollowState> getState,
        Func<string?> getTarget,
        Func<float> getDistance,
        Func<string> getZone,
        Func<string> getMode,
        Func<bool> getSprinting,
        Func<bool> getPaused,
        Func<string?> getLoopState)
    {
        _getState = getState;
        _getTarget = getTarget;
        _getDistance = getDistance;
        _getZone = getZone;
        _getMode = getMode;
        _getSprinting = getSprinting;
        _getPaused = getPaused;
        _getLoopState = getLoopState;
    }

    public void Draw()
    {
        if (!IsOpen) return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(300, 200), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (!ImGui.Begin("强效跟随状态", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        var state = _getState();
        var target = _getTarget();
        var distance = _getDistance();
        var zone = _getZone();
        var mode = _getMode();
        var sprinting = _getSprinting();
        var paused = _getPaused();
        var loopState = _getLoopState();

        var stateColor = state switch
        {
            FollowState.Following => new System.Numerics.Vector4(0, 1, 0, 1),
            FollowState.CatchingUp => new System.Numerics.Vector4(1, 1, 0, 1),
            FollowState.Combat => new System.Numerics.Vector4(1, 0, 0, 1),
            FollowState.Paused or FollowState.TargetLost or FollowState.EmergencyStopped => new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1),
            _ => new System.Numerics.Vector4(1, 1, 1, 1),
        };

        ImGui.Text("状态: ");
        ImGui.SameLine();
        ImGui.TextColored(stateColor, state switch
        {
            FollowState.Idle => "空闲",
            FollowState.Following => "跟随中",
            FollowState.CatchingUp => "追赶中",
            FollowState.Combat => "战斗中",
            FollowState.Paused => "已暂停",
            FollowState.TargetLost => "目标丢失",
            FollowState.EmergencyStopped => "紧急停止",
            _ => "未知",
        });

        ImGui.Separator();
        ImGui.Text($"目标: {target ?? "未设置"}");
        ImGui.Text($"距离: {distance:F1} y");
        ImGui.Text($"区域: {zone}");
        ImGui.Text($"模式: {mode}");

        if (sprinting)
        {
            ImGui.SameLine();
            ImGui.TextColored(new System.Numerics.Vector4(0, 1, 1, 1), " [疾跑中]");
        }

        ImGui.Separator();
        if (paused)
            ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "暂停原因: 手动/外部");
        if (loopState != null)
            ImGui.Text($"循环插件: {loopState}");
        else
            ImGui.Text("循环插件: 未连接");

        ImGui.End();
    }
}
