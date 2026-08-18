using System.Numerics;
using ImGuiNET;
using AutoFollow.Models;
using AutoFollow.Utilities;

namespace AutoFollow.Windows;

public sealed class MiniWindow
{
    /// <summary>按钮独立状态机，不随引擎状态自动变化</summary>
    public enum BtnState { Idle, Following, Paused }

    private readonly FollowConfig _config;
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Action _toggleMainWindow;
    private readonly Func<BtnState, bool> _onMainButtonClick;
    private readonly Func<IReadOnlyList<string>> _getPartyList;
    private readonly Action<string> _onFollowPartyMember;
    private readonly Action _onEmergencyStop;
    private readonly Action _onStatusReport;
    private readonly Func<Vector3?> _getPlayerPos;
    private readonly Func<Vector3?> _getFlagPos;
    private readonly Action _onMoveFlag;
    private readonly Action _onFlyFlag;
    private readonly Action _onStop;
    private BtnState _btnState = BtnState.Idle;
    private string? _engineStatus;
    private bool _flagExpanded;

    public bool IsOpen { get; set; } = true;

    public MiniWindow(
        FollowConfig config,
        Func<FollowState> getState,
        Func<string?> getTargetName,
        Func<float> getDistance,
        Action toggleMainWindow,
        Func<BtnState, bool> onMainButtonClick,
        Func<IReadOnlyList<string>> getPartyList,
        Action<string> onFollowPartyMember,
        Action onEmergencyStop,
        Action onStatusReport,
        Func<Vector3?> getPlayerPos,
        Func<Vector3?> getFlagPos,
        Action onMoveFlag,
        Action onFlyFlag,
        Action onStop)
    {
        _config = config;
        _getState = getState;
        _getTargetName = getTargetName;
        _getDistance = getDistance;
        _toggleMainWindow = toggleMainWindow;
        _onMainButtonClick = onMainButtonClick;
        _getPartyList = getPartyList;
        _onFollowPartyMember = onFollowPartyMember;
        _onEmergencyStop = onEmergencyStop;
        _onStatusReport = onStatusReport;
        _getPlayerPos = getPlayerPos;
        _getFlagPos = getFlagPos;
        _onMoveFlag = onMoveFlag;
        _onFlyFlag = onFlyFlag;
        _onStop = onStop;
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

        // 固定窗口大小：宽度恒定（含状态预留 + 安全余量），高度按折叠状态计算
        var fhCalc = ImGui.GetFrameHeight();
        var nameWCalc = ImGui.CalcTextSize("字字字字").X;
        var distWCalc = ImGui.CalcTextSize(" 123.4y").X; // 距离最宽格式：3位整数+1位小数+小数点+单位
        var statusWCalc = ImGui.CalcTextSize("(跟)").X; // 引擎状态最宽（单字）
        var contentW = fhCalc + 6 + nameWCalc + 5 + distWCalc + 5 + statusWCalc + 5 + 66 + 5 + 24;
        var winW = contentW + ImGui.GetStyle().WindowPadding.X * 2 + ImGui.GetStyle().WindowBorderSize * 2 + 6; // +6 安全余量，防止折叠按钮被裁剪
        var winH = (_flagExpanded ? 4 * fhCalc + 8 : fhCalc) + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);
        var open = IsOpen;
        if (!ImGui.Begin("跟随", ref open, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize))
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

        // 目标名 — 固定 4 字宽度（悬停显示全称，无目标时保持空位，窗口宽度恒定）
        var nameText = target ?? "";
        var nameW = ImGui.CalcTextSize("字字字字").X;
        var nameDisplay = string.IsNullOrEmpty(nameText) ? "" : TruncateText(nameText, nameW);
        ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextPrimary);
        if (nameDisplay.Length > 0)
        {
            ImGui.TextUnformatted(nameDisplay);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(nameText);
            // 精确补齐到固定 4 字宽度（不依赖空格宽度，避免英文名超宽）
            ImGui.SameLine(0, 0);
            var nameRemain = (pos.X + fh + 6 + nameW) - ImGui.GetItemRectMax().X;
            if (nameRemain > 0)
                ImGui.Dummy(new Vector2(nameRemain, 0));
        }
        else
        {
            ImGui.Dummy(new Vector2(nameW, fh)); // 无目标：固定 4 字空位
        }
        ImGui.PopStyleColor();

        // 距离 — 分区色（固定宽度，避免距离值变化导致按钮位置移动）
        ImGui.SameLine(0, 5);
        var distColor = FollowColors.GetDistColor(dist);
        ImGui.PushStyleColor(ImGuiCol.Text, distColor);
        ImGui.TextUnformatted(" " + distStr + "y");
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        var distRemain = (pos.X + fh + 6 + nameW + 5 + distWCalc) - ImGui.GetItemRectMax().X;
        if (distRemain > 0)
            ImGui.Dummy(new Vector2(distRemain, 0));

        // 引擎状态区域 — 始终预留固定宽度（无状态时 Dummy 占位），保证按钮位置恒定
        var hasStatus = _engineStatus != null && !isIdle && isPausedLike;
        ImGui.SameLine(0, 5);
        if (hasStatus)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextAccent);
            ImGui.TextUnformatted("(" + ShortenStatus(_engineStatus!) + ")");
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_engineStatus);
        }
        else
        {
            ImGui.Dummy(new Vector2(statusWCalc, fh));
        }

        ImGui.SameLine(0, 5);

        // ════════════════════════════════════════
        //  主按钮 — 动作标签 + 颜色
        // ════════════════════════════════════════
        var (btnLabel, btnColor) = _btnState switch
        {
            BtnState.Idle      => ("跟随",  _config.BtnColorIdle),
            BtnState.Following => ("暂停",  _config.BtnColorFollowing),
            BtnState.Paused    => ("继续",  _config.BtnColorPaused),
            _                  => ("跟随",  _config.BtnColorIdle),
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

        // ════════════════════════════════════════
        //  折叠按钮 [▼]/[▲] — 在主按钮右侧
        // ════════════════════════════════════════
        ImGui.SameLine(0, 5);
        var foldLabel = _flagExpanded ? "▲" : "▼";
        if (ImGui.Button(foldLabel, new Vector2(24, 0)))
            _flagExpanded = !_flagExpanded;

        // ════════════════════════════════════════
        //  折叠内容：旗标操作区
        // ════════════════════════════════════════
        if (_flagExpanded)
        {
            var playerPos = _getPlayerPos();
            var flagPos = _getFlagPos();
            var flagValid = flagPos != null;

            // ── 分隔线 ──
            var availW = ImGui.GetContentRegionAvail().X;
            var sepY = pos.Y + fh + 2;
            dl.AddLine(new Vector2(pos.X + 2, sepY), new Vector2(pos.X + availW - 2, sepY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.12f)), 1f);

            // ── 玩家坐标 ──
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 4, sepY + 4));
            ImGui.PushStyleColor(ImGuiCol.Text, FollowColors.TextPrimary);
            ImGui.TextUnformatted("玩家: " + (playerPos != null
                ? $"{playerPos.Value.X:F1}, {playerPos.Value.Y:F1}, {playerPos.Value.Z:F1}"
                : "---"));
            ImGui.PopStyleColor();

            // ── 旗标坐标 ──
            var flagY = sepY + 4 + fh;
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 4, flagY));
            ImGui.PushStyleColor(ImGuiCol.Text, flagValid ? FollowColors.TextAccent : FollowColors.TextMuted);
            ImGui.TextUnformatted("旗标: " + (flagValid
                ? $"X: {flagPos!.Value.X:F1}  Z: {flagPos!.Value.Z:F1}"
                : "未设置"));
            ImGui.PopStyleColor();

            // ── 三个按钮 ──
            var btnY = flagY + fh + 2;
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 4, btnY));
            var fullW = availW - 8;
            var btnW = (fullW - 4f) / 3f;

            FlagButton(dl, "步行至flag", btnW, fh, FollowColors.Paused, _onMoveFlag);
            ImGui.SameLine(0, 2);
            FlagButton(dl, "飞行至flag", btnW, fh, FollowColors.Following, _onFlyFlag);
            // 自动上坐骑逻辑在 Plugin.cs onFlyFlag 回调中处理
            ImGui.SameLine(0, 2);
            FlagButton(dl, "停止移动", btnW, fh, FollowColors.Emergency, _onStop);

            // 占用空间以便 AutoResize 正确计算
            ImGui.SetCursorScreenPos(new Vector2(pos.X + 4, btnY + fh));
            ImGui.Dummy(new Vector2(1, 1));
        }

        ImGui.End();
    }

    /// <summary>暂停原因 → 单字缩写（内部完整原因不变，悬停 tooltip 显示完整文本）</summary>
    private static string ShortenStatus(string status) => status switch
    {
        "战斗姿态" or "战斗中" or "Combat"  => "战",
        "手动暂停" or "Paused"              => "停",
        "紧急停止" or "EmergencyStopped"     => "急",
        "目标丢失" or "TargetLost"           => "丢",
        "地图黑名单"                          => "黑",
        "副本已完成"                          => "完",
        "用户命令"                            => "停",
        "场景切换" or "BetweenAreas"         => "图",
        "已死亡" or "Dead"                   => "死",
        _ => status.Length > 1 ? status[..1] : status,
    };

    /// <summary>文本超宽时截断加省略号，保证窗口宽度恒定</summary>
    private static string TruncateText(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        if (text.Length <= 1) return text;
        var t = text;
        while (t.Length > 1 && ImGui.CalcTextSize(t + "…").X > maxWidth)
            t = t[..^1];
        return t + "…";
    }

    private static void FlagButton(ImDrawListPtr dl, string label, float w, float fh,
        Vector4 color, Action onClick)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color * 1.15f);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, color * 0.85f);
        if (ImGui.Button(label, new Vector2(w, fh)))
            onClick();
        ImGui.PopStyleColor(3);

        // 黑色描边文字（适配深色底色）
        var btnMin = ImGui.GetItemRectMin();
        var textSz = ImGui.CalcTextSize(label);
        var txtPos = new Vector2(
            btnMin.X + (w - textSz.X) * 0.5f,
            btnMin.Y + (fh - textSz.Y) * 0.5f);
        var outlineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0.85f));
        var txtCol = ImGui.ColorConvertFloat4ToU32(FollowColors.TextPrimary);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    dl.AddText(txtPos + new Vector2(dx, dy), outlineCol, label);
        dl.AddText(txtPos, txtCol, label);
    }
}
