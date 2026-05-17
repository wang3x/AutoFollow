using System.Numerics;
using ImGuiNET;
using AutoFollow.Models;
using AutoFollow.Utilities;

namespace AutoFollow.Windows;

public sealed class MiniWindow
{
    /// <summary>按钮独立状态机，不随引擎状态自动变化</summary>
    public enum BtnState { Idle, Following, Paused }

    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Action _toggleMainWindow;
    private readonly Func<BtnState, bool> _onMainButtonClick;
    private readonly Func<IReadOnlyList<string>> _getPartyList;
    private readonly Action<string> _onFollowPartyMember;
    private readonly Action _onEmergencyStop;
    private readonly Action _onStatusReport;
    private BtnState _btnState = BtnState.Idle;
    private string? _engineStatus;

    public bool IsOpen { get; set; } = true;

    public MiniWindow(
        Func<FollowState> getState,
        Func<string?> getTargetName,
        Func<float> getDistance,
        Action toggleMainWindow,
        Func<BtnState, bool> onMainButtonClick,
        Func<IReadOnlyList<string>> getPartyList,
        Action<string> onFollowPartyMember,
        Action onEmergencyStop,
        Action onStatusReport)
    {
        _getState = getState;
        _getTargetName = getTargetName;
        _getDistance = getDistance;
        _toggleMainWindow = toggleMainWindow;
        _onMainButtonClick = onMainButtonClick;
        _getPartyList = getPartyList;
        _onFollowPartyMember = onFollowPartyMember;
        _onEmergencyStop = onEmergencyStop;
        _onStatusReport = onStatusReport;
    }

    /// <summary>由外部设置引擎状态描述（如"战斗中""目标丢失"），null 清除</summary>
    public void SetEngineStatus(string? status) => _engineStatus = status;

    /// <summary>外部启动/停止跟随时同步按钮状态</summary>
    public void SyncBtnState(FollowState engineState)
    {
        if (engineState == FollowState.Idle)
        {
            _btnState = BtnState.Idle;
            _engineStatus = null;
        }
        else if (engineState is FollowState.Following or FollowState.CatchingUp)
        {
            _btnState = BtnState.Following;
            _engineStatus = null;
        }
    }

    public void Draw()
    {
        if (!IsOpen) return;

        var state = _getState();
        var target = _getTargetName();
        var dist = _getDistance();
        var isIdle = state == FollowState.Idle;
        var isPausedLike = state is FollowState.Combat or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost;
        var distStr = FormatHelper.DistCompact(dist);

        ImGui.SetNextWindowSize(new Vector2(320, 0), ImGuiCond.FirstUseEver);
        var open = IsOpen;
        if (!ImGui.Begin("跟随", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
        { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;

        // ── 右键菜单 ──
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.None) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##miniCtx");

        if (ImGui.BeginPopup("##miniCtx"))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextPrimary);
            if (ImGui.MenuItem("打开主窗口")) _toggleMainWindow();
            ImGui.PopStyleColor();

            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextAccent);
            if (ImGui.MenuItem("状态报告")) _onStatusReport();
            ImGui.Separator();
            if (ImGui.MenuItem("紧急停止")) _onEmergencyStop();
            ImGui.PopStyleColor();
            ImGui.Separator();
            var party = _getPartyList();
            if (party.Count > 0)
            {
                ImGui.Text("跟随队伍成员");
                ImGui.Separator();
                foreach (var m in party)
                    if (ImGui.MenuItem(m)) _onFollowPartyMember(m);
            }
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextAccent);
            if (ImGui.MenuItem("关闭窗口")) IsOpen = false;
            ImGui.PopStyleColor();
            ImGui.EndPopup();
        }

        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var fh = ImGui.GetFrameHeight();
        var halfFh = fh * 0.5f;
        var center = new Vector2(pos.X + halfFh, pos.Y + halfFh);

        // ════════════════════════════════════════
        //  指示圈 — 双倍大小 + 形状双重编码
        // ════════════════════════════════════════
        var indicatorRadius = (halfFh - 1) * 0.85f;
        var stateColor = FollowColors.ForState(state);

        // 外发光
        var glowCol = stateColor with { W = 0.18f };
        dl.AddCircle(center, indicatorRadius + 3f, ImGui.ColorConvertFloat4ToU32(glowCol), 0, 3f);

        if (isIdle)
        {
            // 空闲 → 空心圆环
            dl.AddCircle(center, indicatorRadius, ImGui.ColorConvertFloat4ToU32(stateColor), 0, 2.5f);
        }
        else if (isPausedLike)
        {
            // 暂停 → 实心 + ⏸ 双杠
            dl.AddCircleFilled(center, indicatorRadius, ImGui.ColorConvertFloat4ToU32(stateColor));
            var bw = indicatorRadius * 0.25f;
            var bh = indicatorRadius * 1.1f;
            var by = center.Y - bh * 0.5f;
            var bc = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.85f));
            dl.AddRectFilled(new Vector2(center.X - bw * 1.5f, by), new Vector2(center.X - bw * 0.5f, by + bh), bc);
            dl.AddRectFilled(new Vector2(center.X + bw * 0.5f, by), new Vector2(center.X + bw * 1.5f, by + bh), bc);
        }
        else
        {
            // 跟随中 → 实心圆 + 白边
            dl.AddCircleFilled(center, indicatorRadius, ImGui.ColorConvertFloat4ToU32(stateColor));
            dl.AddCircle(center, indicatorRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.25f)), 0, 1.5f);
        }

        // 指示圈悬停 tooltip
        var mp = ImGui.GetMousePos();
        if (mp.X >= pos.X && mp.X <= pos.X + fh && mp.Y >= pos.Y && mp.Y <= pos.Y + fh)
        {
            if (isIdle) ImGui.SetTooltip("空闲");
            else if (isPausedLike) ImGui.SetTooltip(_engineStatus ?? "已暂停");
            else ImGui.SetTooltip("跟随中");
        }

        // ════════════════════════════════════════
        //  文字行：目标名 + 距离 + 引擎状态
        // ════════════════════════════════════════
        ImGui.SetCursorScreenPos(new Vector2(pos.X + fh + 6, pos.Y));
        ImGui.AlignTextToFramePadding();

        // 目标名 — 主色
        ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextPrimary);
        ImGui.TextUnformatted(target ?? "无目标");
        ImGui.PopStyleColor();

        // 距离 — 分区色
        ImGui.SameLine();
        var distColor = FollowColors.GetDistColor(dist);
        ImGui.PushStyleColor(ImGuiCol.Text, distColor);
        ImGui.TextUnformatted(" " + distStr + "y");
        ImGui.PopStyleColor();

        // 引擎暂停原因
        if (_engineStatus != null && !isIdle && isPausedLike)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextAccent);
            ImGui.TextUnformatted($"({_engineStatus})");
            ImGui.PopStyleColor();
        }

        ImGui.SameLine(0, fh);

        // ════════════════════════════════════════
        //  主按钮 — 动作标签 + 颜色
        // ════════════════════════════════════════
        var (btnLabel, btnColor) = _btnState switch
        {
            BtnState.Idle      => ("跟随",  FollowColors.Idle),
            BtnState.Following => ("暂停",  FollowColors.Following),
            BtnState.Paused    => ("继续",  FollowColors.Paused),
            _                  => ("跟随",  FollowColors.Idle),
        };

        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnColor * 1.15f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnColor * 0.85f);
        ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextPrimary);
        if (ImGui.Button(btnLabel, new Vector2(66, 0)))
        {
            // 捕获回调前的按钮状态，防止 SyncBtnState 修改 _btnState 后 switch 读到错误的值
            var prevBtn = _btnState;
            if (_onMainButtonClick(_btnState))
            {
                _btnState = prevBtn switch
                {
                    BtnState.Idle      => BtnState.Following,
                    BtnState.Following => BtnState.Paused,
                    BtnState.Paused    => BtnState.Following,
                    _                  => BtnState.Idle,
                };
                _engineStatus = null;
            }
        }
        ImGui.PopStyleColor(4);

        // 用 DrawList 在按钮上方再描一层黑边文字（适配任何底色）
        var btnMin = ImGui.GetItemRectMin();
        var btnMax = ImGui.GetItemRectMax();
        var textSz = ImGui.CalcTextSize(btnLabel);
        var txtPos = new Vector2(
            btnMin.X + (66 - textSz.X) * 0.5f,
            btnMin.Y + (fh - textSz.Y) * 0.5f);
        var outlineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.85f));
        var txtCol = ImGui.ColorConvertFloat4ToU32(FollowColors.TextPrimary);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    dl.AddText(txtPos + new Vector2(dx, dy), outlineCol, btnLabel);
        dl.AddText(txtPos, txtCol, btnLabel);

        ImGui.End();
    }
}
