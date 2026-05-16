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
        ImGui.BeginChild("##ss", new Vector2(0,0), false);
        if (ImGui.CollapsingHeader("距离阈值", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var str = _config.CombatEnterRange.ToString("F1");
            if (ImGui.InputText("进入战斗区(y)##ce", ref str, 16)) { float v; if (float.TryParse(str, out v)) _config.CombatEnterRange = v; }
            str = _config.CombatExitRange.ToString("F1");
            if (ImGui.InputText("离开战斗区(y)##cx", ref str, 16)) { float v; if (float.TryParse(str, out v)) _config.CombatExitRange = v; }
            var scanStr = _config.ScanInterval.ToString("F1");
            if (ImGui.InputText("坐标扫描间隔(秒)##si", ref scanStr, 16)) { float v; if (float.TryParse(scanStr, out v) && v >= 0.5f) _config.ScanInterval = v; }
            var stStr = _config.SprintThreshold.ToString("F1");
            if (ImGui.InputText("疾跑距离(y)##st", ref stStr, 16)) { float v; if (float.TryParse(stStr, out v)) _config.SprintThreshold = v; }
            ImGui.TextUnformatted("说明: ≤进入战斗区暂停跟随, >离开战斗区继续跟随, 脱战1秒恢复");
            ImGui.Spacing();
            if (ImGui.Button("保存距离")) { _onSave(); }
            ImGui.SameLine();
            if (ImGui.Button("恢复默认"))
            {
                _config.CombatEnterRange = 10f;
                _config.CombatExitRange = 30f;
                _config.SprintThreshold = 20f;
                _config.ScanInterval = 1f;
                _onSave();
            }
        }
        if (ImGui.CollapsingHeader("疾跑设置", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var spr = _config.SprintEnabled;
            if (ImGui.Checkbox("启用疾跑##s1", ref spr))
            {
                _config.SprintEnabled = spr;
                if (spr) _config.UseMount = false;
            }
            var mount = _config.UseMount;
            if (ImGui.Checkbox("使用坐骑##s2", ref mount))
            {
                _config.UseMount = mount;
                if (mount) _config.SprintEnabled = false;
            }
            if (_config.SprintEnabled)
            {
                var ao = _config.SprintAlwaysOn;
                if (ImGui.Checkbox("无脑疾跑##s4", ref ao)) _config.SprintAlwaysOn = ao;
                if (!ao)
                {
                    var v = _config.SprintOnlyInCombat;
                    if (ImGui.Checkbox("仅战斗疾跑##s3", ref v)) _config.SprintOnlyInCombat = v;
                    ImGui.TextUnformatted("距离>疾跑距离或目标疾跑时自动开");
                }
            }
        }
        if (ImGui.CollapsingHeader("聊天消息", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var v = _config.ChatOutput;
            if (ImGui.Checkbox("启用聊天提示##chat", ref v)) { _config.ChatOutput = v; _onSave(); }
        }
        if (ImGui.CollapsingHeader("紧急停止热键", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var ki = (int)_config.EmergencyStopKey;
            DrawHotkeySelector("热键##ek", ref ki, (vk) => _config.EmergencyStopKey = (Dalamud.Game.ClientState.Keys.VirtualKey)vk);
        }
        if (ImGui.CollapsingHeader("循环插件命令", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var s = _config.PauseCommand ?? "";             if (ImGui.InputText("暂停命令##pc", ref s, 128)) _config.PauseCommand = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            s = _config.ResumeCommand ?? ""; if (ImGui.InputText("恢复命令##rc", ref s, 128)) _config.ResumeCommand = string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            ImGui.TextUnformatted("默认支持 RotationSolverReborn（/rotation off, /rotation Auto）");
            ImGui.TextUnformatted("使用 AEAssist 时，暂停命令填写 /aestop，恢复命令留空，并打开目标选择器");
        }
        ImGui.Separator();
        if (ImGui.Button("保存全部")) { _onSave(); _onCommandReload(); }
        ImGui.EndChild();
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
        ImGui.TextUnformatted(HelpText);
        ImGui.EndChild();
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

    private const string HelpText = @"
【插件说明】
FF14 卫月 (Dalamud) 自动跟随插件 — vnavmesh 寻路跟随 · 自动疾跑 · 循环插件协同 · 地图黑名单

【快速开始】
/ftar          → 跟随当前选中的玩家
/ft <玩家名>    → 按名字跟随，如 /ft 小明
/ff            → 切换跟随开/关
/fdbg          → 打开/关闭主窗口

选好目标后插件自动通过 vnavmesh 寻路走到目标身边。
跟随期间会自动处理疾跑、战斗暂停、循环插件协同。

【所有命令】
/ff            ToggleFollow       切换跟随开/关
/ftar          FollowCurrentTarget 跟随当前选中的目标
/ft <名>       SetFollowTarget     按名字设置跟随目标
/fes           EmergencyStop       紧急停止（停止 + 暂停循环）
/flp           PauseLoop           暂停循环插件
/flr           ResumeLoop          恢复循环插件
/flt           ToggleLoop          切换循环插件
/fst           StatusReport        输出状态报告到聊天
/fdbg          ToggleDebugWindow   打开/关闭主窗口

支持自定义命令：主窗口 → 设置页 → 自定义命令面板可增删改。

【跟随机制 · 距离状态机】
每 1 秒扫描一次目标坐标（扫描间隔可调），检测到移动超过 0.5y 即发送新路径给 vnavmesh。

进入战斗区（默认 ≤10y） → 暂停跟随，恢复循环插件 → BossMod 接管
离开战斗区（默认 >30y） → 恢复跟随，暂停循环插件
脱战后 >1秒              → 恢复跟随（距目标≤10y时不恢复，防反复横跳）

● 启动保护期：设置目标后 2 秒内不触发暂停，防止一出门就停
● Boss 监测：扫描全场景 BattleNpc，分级判断，Boss 战中不恢复跟随
  Lv≥80 + HP>玩家20倍 / Lv50-79 + HP>玩家15倍 / Lv<50 + HP>玩家10倍

【自动疾跑】
疾跑和坐骑二选一互斥，通过设置页切换。
● 智能疾跑（默认）：目标>20y 或目标正在疾跑时自动开
● 无脑疾跑：一直开着疾跑
● 仅战斗疾跑：仅在战斗状态中开疾跑
● 坐骑模式：脱战→上坐骑；战斗中→放弃坐骑改用疾跑

【智能跟随】
迷你窗口单按钮三态切换：
  启动（灰色）   → 智能跟随：选中的玩家直接跟；选中的敌方 NPC 则跟随其当前目标
  跟随中（绿色） → 紧急停止（跟随 + 循环插件暂停），变为暂停
  暂停（黄色）   → 先尝试智能跟随（看当前选中的目标），失败则恢复上一个跟随目标（需在 30y 内）

● 暂停恢复时若目标距离超过 30y，提示""目标距离超过30y""并阻止恢复
● 目标栏位为空时无法启动跟随

【紧急停止】
默认热键 F8 按一次即触发。可在设置页改绑其他按键。
触发后：停止跟随 + 暂停循环插件。需点击暂停按钮或 /ff 恢复。

【地图黑名单】
设置页 → 地图黑名单页，可查看当前地图 ID 并添加。
在黑名单地图中自动暂停跟随，支持批量添加（逗号分隔）。

【主窗口 · 标签页】
设置      距离阈值、疾跑/坐骑、热键绑定、循环插件命令
坐标      玩家/目标坐标、步行至flag坐标、飞行至flag坐标、手动输入坐标移动
插件状态  检测 vnavmesh / BossMod / RotationSolver 是否安装启用
使用说明  本说明的游戏内版本
地图黑名单 查看当前地图 ID，添加/删除黑名单
指令日志  颜色分类的操作日志，支持过滤和复制

【坐标分页】
● 显示玩家坐标和跟随目标坐标
● 步行至flag坐标 — 通过 vnavmesh 寻路走到小旗位置
● 飞行至flag坐标 — 上坐骑后起飞到小旗位置（需先上坐骑再点击）
● 手动移动 — 输入 X/Y/Z 坐标直接移动

【迷你窗口】
默认打开，右键单击切换主窗口显示。界面元素：
● 指示圈：灰=空闲、绿=跟随中、黄=暂停（直径缩小，颜色柔和）
● 目标名 + 距离(y)
● 单按钮：[启动] / [跟随中] / [暂停] 三态切换

【设置项一览】
距离阈值：进入战斗区 10y / 离开战斗区 30y / 疾跑触发 20y / 扫描间隔 1秒
疾跑：启用智能 / 无脑 / 仅战斗 / 使用坐骑
聊天：启用聊天提示（默认关闭）
热键：紧急停止 F8
循环插件：暂停 /rotation off / 恢复 /rotation Auto
行为：战斗中暂停 / 目标丢失暂停 / 死亡暂停（全部开启）
指令日志：启用日志（默认关闭）

【循环插件协同】
在设置页填写循环插件的暂停/恢复命令。

RotationSolverReborn（默认）：/rotation off / /rotation Auto
AEAssist：暂停填写 /aestop，恢复命令留空，并打开目标选择器

工作流程：
跟人走远 → 暂停循环插件 → vnavmesh 寻路绕障碍追过去
接近进入战斗区 → 暂停跟随，恢复循环插件 → BossMod 接管战斗
脱战 → 恢复跟随，暂停循环插件 → 继续追目标

不配置命令不影响跟随，仅不自动发送插件控制命令。

【可能的问题】
加载不出来：检查 AutoFollow.json 和 AutoFollow.dll 是否在同一目录，ApiLevel 是否匹配（国服 API 15），/xllog 查看错误
跟了不走：检查 vnavmesh 是否已安装启用、NavMesh 是否加载，查看指令日志
循环不自动暂停/恢复：检查设置页循环插件命令是否正确。AEAssist 请填 /aestop
换目标：/ft <新玩家名> 或 /ft 清除目标，/ftar 重新选
急停没反应：检查热键是否被绑定或被其他插件占用（按下即触发，无需长按）
提示""目标距离超过30y""：暂停状态恢复时目标必须在 30y 内，靠近后再试
地图黑名单读不到ID：v1.4.10 优化了读取优先级，仍不行尝试重启游戏";
}
