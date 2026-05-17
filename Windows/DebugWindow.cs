using System.Numerics;
using ImGuiNET;
using AutoFollow.IPC;
using AutoFollow.Models;
namespace AutoFollow.Windows;

public sealed class DebugWindow
{
    private readonly DebugLog _log;
    private readonly PluginStatusChecker _statusChecker;
    private readonly FollowConfig _config;
    private readonly Func<Vector3?> _getPlayerPos;
    private readonly Func<Vector3?> _getTargetPos;
    private readonly Action<Vector3> _onManualMove;
    private readonly Action _onSave;
    private readonly Action _onCommandReload;
    private readonly Action _onMoveFlag;
    private readonly Action _onFlyFlag;
    private readonly Action _onStop;
    private readonly Func<ushort?> _getTerritory;
    // 状态栏
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Func<bool> _getBossActive;
    private readonly Func<bool> _getInCombat;
    private readonly Action _onClearTarget;

    private bool _autoScroll = true;
    private string _filter = "";
    private bool _waitingForKey;
    private string _hotkeyButtonLabel = "";
    private float _manualX, _manualY, _manualZ;
    private DateTime _lastScan;

    public bool IsOpen { get; set; } = false;

    public DebugWindow(DebugLog log, PluginStatusChecker statusChecker,
        FollowConfig config, Func<Vector3?> getPlayerPos, Func<Vector3?> getTargetPos,
        Action<Vector3> onManualMove, Action onSave, Action onCommandReload,
        Func<FollowState> getState, Func<string?> getTargetName, Func<float> getDistance,
        Func<bool> getBossActive, Func<bool> getInCombat, Action onClearTarget,
        Action onMoveFlag, Action onFlyFlag, Action onStop, Func<ushort?> getTerritory)
    {
        _log = log; _statusChecker = statusChecker; _config = config;
        _getPlayerPos = getPlayerPos; _getTargetPos = getTargetPos;
        _onManualMove = onManualMove; _onSave = onSave; _onCommandReload = onCommandReload;
        _getState = getState; _getTargetName = getTargetName; _getDistance = getDistance;
        _getBossActive = getBossActive; _getInCombat = getInCombat; _onClearTarget = onClearTarget; _onMoveFlag = onMoveFlag; _onFlyFlag = onFlyFlag; _onStop = onStop; _getTerritory = getTerritory;
    }

    public void Draw()
    {
        if (!IsOpen) return;
        ImGui.SetNextWindowSize(new Vector2(680, 420), ImGuiCond.FirstUseEver);
        var open = IsOpen;

        if (!ImGui.Begin("强效跟随", ref open, ImGuiWindowFlags.NoScrollbar)) { IsOpen = open; ImGui.End(); return; }
        IsOpen = open;

        // ════════ 状态栏（常驻显示） ════════
        DrawStatusBar();

        ImGui.BeginTabBar("##tabs", ImGuiTabBarFlags.None);
        if (ImGui.BeginTabItem("设置")) { DrawSettingsTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("坐标")) { DrawCoordTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("插件状态")) { DrawStatusTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("使用说明")) { DrawHelpTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("地图黑名单")) { DrawBlacklistTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("指令日志")) { DrawLogTab(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
        ImGui.End();
    }

    private void DrawStatusBar()
    {
        var state = _getState();
        var target = _getTargetName();
        var dist = _getDistance();

        var isPaused = state is FollowState.Combat or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost;
        var inCombat = _getInCombat();
        var stateStr = isPaused ? "暂停中" : "跟随中";
        var stateColor = isPaused ? new Vector4(1, 0.5f, 0, 1) : new Vector4(0, 1, 0, 1);
        var combatColor = inCombat ? new Vector4(1, 0.3f, 0, 1) : new Vector4(0.5f, 0.8f, 1, 1);
        var combatStr = inCombat ? "战斗中" : "非战斗";

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));
        ImGui.PushStyleColor(ImGuiCol.Text, stateColor);
        ImGui.TextUnformatted($" {stateStr} ");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextUnformatted($"目标: {target ?? "无"}");
        ImGui.SameLine();
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";
        ImGui.TextUnformatted($"距离: {distStr}y");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, combatColor);
        ImGui.TextUnformatted($" [{combatStr}]");
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.Separator();
    }

    private void DrawFilterButton(string label, ref string filter, string keyword, Vector4 color)
    {
        var exclude = "!" + keyword;
        var active = filter.Contains(exclude);
        if (!active)
            ImGui.PushStyleColor(ImGuiCol.Button, color * 0.6f);
        else
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.3f, 0.3f, 1));
        if (ImGui.Button(label))
        {
            if (filter.Contains(exclude))
                filter = filter.Replace(exclude, "").Trim();
            else
                filter = (filter + " " + exclude).Trim();
        }
        ImGui.PopStyleColor();
    }

    private void DrawLogTab()
    {
        ImGui.Checkbox("自动滚动", ref _autoScroll); ImGui.SameLine();
        ImGui.SetNextItemWidth(100); ImGui.InputText("过滤", ref _filter, 128); ImGui.SameLine();
        if (ImGui.Button("清空")) _log.Clear(); ImGui.SameLine();
        var e = _log.Enabled; if (ImGui.Checkbox("启用日志", ref e)) _log.Enabled = e;
        ImGui.SameLine();
        if (ImGui.Button("复制"))
        {
            var sb = new System.Text.StringBuilder(4096);
            foreach (var entry2 in _log.Entries)
            {
                if (!string.IsNullOrEmpty(_filter) && !$"{entry2.Category} {entry2.Message}".Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append('[').Append(entry2.Timestamp.ToString("HH:mm:ss.fff")).Append("] [").Append(entry2.Category).Append("] ").AppendLine(entry2.Message);
            }
            ImGui.SetClipboardText(sb.ToString());
        }
        ImGui.Separator();
        // 类别过滤按钮
        DrawFilterButton("IPC", ref _filter, "IPC", new Vector4(0,1,0,1));
        ImGui.SameLine();
        DrawFilterButton("移动", ref _filter, "移动", new Vector4(0.5f,0.5f,1,1));
        ImGui.SameLine();
        DrawFilterButton("状态", ref _filter, "状态", new Vector4(1,1,0,1));
        ImGui.SameLine();
        DrawFilterButton("引擎", ref _filter, "引擎", new Vector4(0,1,1,1));
        ImGui.SameLine();
        DrawFilterButton("命令", ref _filter, "命令", new Vector4(1,1,1,1));
        ImGui.SameLine();
        DrawFilterButton("sprint", ref _filter, "sprint", new Vector4(0.6f,0.8f,1,1));
        ImGui.Separator();
        // 解析排除规则（!keyword）
        var excluded = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.StartsWith('!')).Select(w => w.TrimStart('!')).ToList();
        // 逐行显示+颜色
        ImGui.BeginChild("##logscroll", new Vector2(0, 0), false);
        foreach (var entry in _log.Entries)
        {
            // 排除匹配
            if (excluded.Any(e => entry.Category.Contains(e, StringComparison.OrdinalIgnoreCase))) continue;
            // 包含匹配（仅非排除词）
            var include = _filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.StartsWith('!')).ToList();
            if (include.Any() && !include.Any(i => $"{entry.Category} {entry.Message}".Contains(i, StringComparison.OrdinalIgnoreCase))) continue;
            var c = entry.Category switch
            {
                "IPC" => new Vector4(0,1,0,1),
                "move" or "移动" => new Vector4(0.5f,0.5f,1,1),
                "state" or "状态" => new Vector4(1,1,0,1),
                "引擎" => new Vector4(0,1,1,1),
                "sprint" => new Vector4(0.6f,0.8f,1,1),
                _ => new Vector4(1,1,1,1),
            };
            var line = $"[{entry.Timestamp:HH:mm:ss.fff}] [{entry.Category}] {entry.Message}";
            ImGui.PushStyleColor(ImGuiCol.Text, c);
            ImGui.Selectable(line, false);
            ImGui.PopStyleColor();
        }
        if (_autoScroll) ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
    }

    private void DrawStatusTab()
    {
        if (ImGui.Button("重新检测")) { _statusChecker.ScanAll(); _lastScan = DateTime.Now; }
        ImGui.SameLine();
        if (_lastScan.Ticks > 0) ImGui.TextUnformatted($"上次检测: {_lastScan:HH:mm:ss}");
        else ImGui.TextUnformatted("点击按钮扫描已安装的插件");
        ImGui.Separator();
        ImGui.BeginChild("##st", new Vector2(0,0), false);
        DrawGroup("寻路导航", r => r.Kind == PluginKind.Pathfinding);
        DrawGroup("战斗辅助", r => r.Kind == PluginKind.Combat);
        DrawGroup("自动输出", r => r.Kind == PluginKind.Rotation);
        ImGui.EndChild();
    }

    private void DrawGroup(string name, Func<PluginCheck, bool> f)
    {
        var list = _statusChecker.Results.Where(f).ToList(); if (list.Count == 0) return;
        if (ImGui.CollapsingHeader($"{name} ({list.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!ImGui.BeginTable("##t", 3, ImGuiTableFlags.Borders|ImGuiTableFlags.RowBg|ImGuiTableFlags.SizingStretchProp)) return;
            ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("信息", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();
            foreach (var p in list) { ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Name);
                ImGui.TableNextColumn(); ImGui.TextColored(p.Status==PluginStatus.Connected?new Vector4(0,1,0,1):new Vector4(1,0.5f,0,1), p.Status==PluginStatus.Connected?"已连接":"未连接");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(p.Message); }
            ImGui.EndTable();
        }
    }

    private void DrawCoordTab()
    {
        ImGui.BeginChild("##ct", new Vector2(0,0), false);

        ImGui.TextUnformatted("--- 玩家坐标 ---");
        var pp = _getPlayerPos();
        ImGui.TextUnformatted(pp != null ? $"X: {pp.Value.X:F2}  Y: {pp.Value.Y:F2}  Z: {pp.Value.Z:F2}" : "无法获取");

        ImGui.Spacing();
        ImGui.TextUnformatted("--- 跟随目标坐标 ---");
        var tp = _getTargetPos();
        ImGui.TextUnformatted(tp != null ? $"X: {tp.Value.X:F2}  Y: {tp.Value.Y:F2}  Z: {tp.Value.Z:F2}" : "无目标");

        ImGui.Spacing();
        ImGui.TextUnformatted("--- flag坐标 ---");
        if (ImGui.Button("步行至flag坐标"))
            _onMoveFlag();
        ImGui.SameLine();
        if (ImGui.Button("飞行至flag坐标"))
            _onFlyFlag();
        ImGui.SameLine();
        if (ImGui.Button("停止移动"))
            _onStop();
        ImGui.TextUnformatted("《飞行至flag坐标功能需要在坐骑上点击才有效果》");

        ImGui.Spacing();
        ImGui.TextUnformatted("--- 手动移动 ---");
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("X##mx", ref _manualX); ImGui.SameLine();
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("Y##my", ref _manualY); ImGui.SameLine();
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("Z##mz", ref _manualZ);
        if (ImGui.Button("移动到坐标")) { _onManualMove(new Vector3(_manualX, _manualY, _manualZ)); }
        ImGui.SameLine();
        if (ImGui.Button("复制玩家坐标")) { if (pp != null) { _manualX = pp.Value.X; _manualY = pp.Value.Y; _manualZ = pp.Value.Z; } }
        ImGui.SameLine();

        ImGui.EndChild();
    }

    private void DrawSettingsTab()
    {
        ImGui.BeginChild("##ss", new Vector2(0, 0), false);

        // ── 距离阈值（默认打开） ──
        if (ImGui.CollapsingHeader("距离阈值", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("##dist", 2, ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("val", ImGuiTableColumnFlags.WidthFixed, 140);

                DrawDragRow("进入战斗区 (y)", _config.CombatEnterRange, 0.5f, 0f, 50f,
                    v => _config.CombatEnterRange = v,
                    "与目标距离 ≤ 此值时暂停跟随，恢复循环插件");
                DrawDragRow("离开战斗区 (y)", _config.CombatExitRange, 0.5f, 0f, 200f,
                    v => _config.CombatExitRange = v,
                    "与目标距离 > 此值时恢复跟随，暂停循环插件");
                DrawDragRow("扫描间隔 (秒)", _config.ScanInterval, 0.1f, 0.5f, 5f,
                    v => _config.ScanInterval = v,
                    "每 N 秒检测一次目标坐标，越小响应越快但 CPU 略高");
                DrawDragRow("疾跑触发 (y)", _config.SprintThreshold, 0.5f, 0f, 100f,
                    v => _config.SprintThreshold = v,
                    "目标距离超过此值时自动开启疾跑");
                DrawDragRow("移动阈值 (y)", _config.MoveThreshold, 0.05f, 0.1f, 5f,
                    v => _config.MoveThreshold = v,
                    "目标移动超过此距离才发送新路径，降低可减少跟随延迟");

                ImGui.EndTable();
            }

            ImGui.Separator();
            if (ImGui.Button("恢复默认")) { ResetDistanceDefaults(); _onSave(); }
        }

        // ── 疾跑/坐骑（默认关闭） ──
        if (ImGui.CollapsingHeader("疾跑 / 坐骑"))
        {
            var spr = _config.SprintEnabled;
            if (ImGui.Checkbox("启用疾跑##sprintEnabled", ref spr))
            { _config.SprintEnabled = spr; if (spr) _config.UseMount = false; }
            var mount = _config.UseMount;
            if (ImGui.Checkbox("使用坐骑##useMount", ref mount))
            { _config.UseMount = mount; if (mount) _config.SprintEnabled = false; }
            if (_config.SprintEnabled)
            {
                var ao = _config.SprintAlwaysOn;
                if (ImGui.Checkbox("无脑疾跑##sprintAlwaysOn", ref ao)) _config.SprintAlwaysOn = ao;
                if (!ao)
                {
                    var v = _config.SprintOnlyInCombat;
                    if (ImGui.Checkbox("仅战斗疾跑##sprintCombatOnly", ref v)) _config.SprintOnlyInCombat = v;
                    ImGui.TextDisabled("距离 > 疾跑阈值或目标疾跑时自动开启");
                }
            }
        }

        // ── 聊天消息（默认关闭） ──
        if (ImGui.CollapsingHeader("聊天消息"))
        {
            var v = _config.ChatOutput;
            if (ImGui.Checkbox("启用聊天提示##chatOutput", ref v)) { _config.ChatOutput = v; _onSave(); }
            ImGui.TextDisabled("关闭后关键状态变更仍会输出到指令日志");
        }

        // ── 紧急停止热键（默认关闭） ──
        if (ImGui.CollapsingHeader("紧急停止热键"))
        {
            var ki = (int)_config.EmergencyStopKey;
            DrawHotkeySelector("热键##ek", ref ki, vk => _config.EmergencyStopKey = (Dalamud.Game.ClientState.Keys.VirtualKey)vk);
        }

        // ── 循环插件命令（默认关闭） ──
        if (ImGui.CollapsingHeader("循环插件命令"))
        {
            var s = _config.PauseCommand ?? "";
            if (ImGui.InputText("暂停命令##pc", ref s, 128))
                _config.PauseCommand = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            s = _config.ResumeCommand ?? "";
            if (ImGui.InputText("恢复命令##rc", ref s, 128))
                _config.ResumeCommand = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            ImGui.TextDisabled("默认 RotationSolverReborn: /rotation off / /rotation Auto");
            ImGui.TextDisabled("AEAssist: 暂停 /aestop，恢复留空，并打开目标选择器");
        }

        // ── Mini 窗口按钮颜色（默认关闭） ──
        if (ImGui.CollapsingHeader("Mini 窗口按钮颜色"))
        {
            ImGui.TextDisabled("自定义三个状态的按钮底色，即时生效无需保存");

            var c1 = _config.BtnColorIdle;
            if (ImGui.ColorEdit4("空闲 (跟随)", ref c1, ImGuiColorEditFlags.AlphaPreview))
                _config.BtnColorIdle = c1;

            var c2 = _config.BtnColorFollowing;
            if (ImGui.ColorEdit4("跟随中 (暂停)", ref c2, ImGuiColorEditFlags.AlphaPreview))
                _config.BtnColorFollowing = c2;

            var c3 = _config.BtnColorPaused;
            if (ImGui.ColorEdit4("暂停 (继续)", ref c3, ImGuiColorEditFlags.AlphaPreview))
                _config.BtnColorPaused = c3;

            if (ImGui.Button("恢复默认颜色"))
            {
                _config.BtnColorIdle = new System.Numerics.Vector4(0.45f, 0.45f, 0.45f, 1f);
                _config.BtnColorFollowing = new System.Numerics.Vector4(0.20f, 0.80f, 0.30f, 1f);
                _config.BtnColorPaused = new System.Numerics.Vector4(1.00f, 0.70f, 0.10f, 1f);
            }
            ImGui.SameLine();
            if (ImGui.Button("保存颜色")) { _onSave(); }
        }

        ImGui.Separator();
        if (ImGui.Button("保存全部")) { _onSave(); _onCommandReload(); }
        ImGui.EndChild();
    }

    private static void DrawDragRow(string label, float value, float speed, float min, float max,
        Action<float> onSet, string tooltip)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(tooltip))
            ImGui.SetTooltip(tooltip);
        ImGui.TableNextColumn();
        var v = value;
        var id = "##dr" + label.Replace(" ", "");
        if (ImGui.DragFloat(id, ref v, speed, min, max, "%.2f"))
            onSet(Math.Clamp(v, min, max));
    }

    private void ResetDistanceDefaults()
    {
        _config.CombatEnterRange = 10f;
        _config.CombatExitRange = 30f;
        _config.SprintThreshold = 20f;
        _config.ScanInterval = 0.5f;
        _config.MoveThreshold = 0.5f;
    }

    private void DrawHotkeySelector(string label, ref int keyValue, Action<int> onSet)
    {
        var ck = (Dalamud.Game.ClientState.Keys.VirtualKey)keyValue;
        var bl = _waitingForKey ? "按下按键..." : ck == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY ? "未设置 (点击绑定)" : $"{ck}";
        if (ImGui.Button(bl)) { _waitingForKey = !_waitingForKey; _hotkeyButtonLabel = label; }
        if (_waitingForKey && _hotkeyButtonLabel == label)
        {
            foreach (Dalamud.Game.ClientState.Keys.VirtualKey vk in Enum.GetValues<Dalamud.Game.ClientState.Keys.VirtualKey>())
            {
                if (vk == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY || vk == Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL || vk == Dalamud.Game.ClientState.Keys.VirtualKey.MENU || vk == Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT) continue;
                if (ImGui.GetIO().KeysDown[(int)vk]) { onSet((int)vk); _waitingForKey = false; break; }
            }
            if (ImGui.GetIO().KeysDown[(int)Dalamud.Game.ClientState.Keys.VirtualKey.ESCAPE]) _waitingForKey = false;
        }
    }

    // ════════════ 页: 使用说明 ════════════

    private void DrawHelpTab()
    {
        ImGui.BeginChild("##help", new Vector2(0, 0), false);

        if (ImGui.CollapsingHeader("快速上手", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawCmd("/ftar", "选中一个队友，输入此命令即开始跟随");
            DrawCmd("/ff", "切换跟随开/关");
            DrawCmd("F8", "紧急停止（可在设置页改绑）");
            DrawCmd("/fdbg", "打开设置窗口");
            ImGui.Spacing();
            ImGui.TextWrapped("迷你窗口默认显示在屏幕上，点击「跟随」开始，「暂停」停止，右键弹出快捷菜单。什么都不用设置，装好就能用。");
        }

        if (ImGui.CollapsingHeader("命令大全"))
        {
            if (ImGui.BeginTable("##cmdtable", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("命令", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("作用", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var c in _config.CustomCommands)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Command);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Action.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(c.Description);
                }
                ImGui.EndTable();
            }
        }

        if (ImGui.CollapsingHeader("插件自动做的事（核心机制）"))
        {
            ImGui.TextWrapped("跟随中目标走远 → 暂停循环插件 → vnavmesh 寻路追过去");
            ImGui.TextWrapped("接近目标进入战斗区 → 暂停跟随，恢复循环插件 → BossMod 接管");
            ImGui.TextWrapped("脱战 → 恢复跟随，暂停循环插件 → 继续跟人");
            ImGui.Spacing();
            ImGui.BulletText("进入战斗区（≤10y）停跟，离开战斗区（>30y）恢复，0.5 秒扫描一次坐标");
            ImGui.BulletText("设置目标后 2 秒内不触发暂停（启动保护），Boss 战中不恢复跟随");
            ImGui.BulletText("疾跑自动开（目标 > 20y 或目标在跑时），也可设无脑疾跑/仅战斗/坐骑模式");
        }

        if (ImGui.CollapsingHeader("常见问题"))
        {
            ImGui.BulletText("跟了不走？检查 vnavmesh 是否启用、NavMesh 是否已加载，查看指令日志");
            ImGui.BulletText("循环不自动停？默认支持 RSR，AEAssist 请在设置页填暂停命令 /aestop，恢复留空");
            ImGui.BulletText("换目标？/ft <新名字> 或 /ft 清除目标后重新选");
            ImGui.BulletText("提示 >30y？暂停恢复时目标必须在 30y 内，靠近后再试");
            ImGui.BulletText("迷你窗口不见了？卫月图标 → 插件 → 强效跟随 → 显示迷你窗口");
        }

        ImGui.EndChild();
    }

    private static void DrawCmd(string cmd, string desc)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.2f, 0.8f, 0.2f, 1));
        ImGui.TextUnformatted("  " + cmd);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextUnformatted(" — " + desc);
    }

    // ════════════ 页: 地图黑名单 ════════════
    private string _newMapId = "";
    private void DrawBlacklistTab()
    {
        ImGui.BeginChild("##bl", new Vector2(0, 0), false);
        var tid = _getTerritory();
        ImGui.TextUnformatted(tid != null ? $"当前地图ID: {tid.Value}" : "当前地图ID: 无法获取");
        ImGui.Separator();
        ImGui.TextUnformatted("黑名单中的地图不会触发跟随，多个ID用逗号分隔");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("地图ID", ref _newMapId, 64);
        ImGui.SameLine();
        if (ImGui.Button("添加"))
        {
            foreach (var part in _newMapId.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (ushort.TryParse(part.Trim(), out var id) && !_config.BlacklistedMaps.Contains(id))
                    _config.BlacklistedMaps.Add(id);
            }
            _newMapId = ""; _onSave();
        }
        ImGui.SameLine();
        if (ImGui.Button("保存")) _onSave();
        ImGui.Separator();
        int toRemove = -1;
        if (_config.BlacklistedMaps.Count == 0)
            ImGui.TextUnformatted("（无）");
        else for (int i = 0; i < _config.BlacklistedMaps.Count; i++)
        {
            ImGui.TextUnformatted($"  {_config.BlacklistedMaps[i]}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"X##b{i}")) toRemove = i;
        }
        if (toRemove >= 0) { _config.BlacklistedMaps.RemoveAt(toRemove); _onSave(); }
        ImGui.EndChild();
    }


}
