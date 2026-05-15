using System.Numerics;
using ImGuiNET;
using AutoFollow.IPC;
using AutoFollow.Models;
using AutoFollow.Commands;

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
        ImGui.TextUnformatted($"距离: {distStr}码");
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
        ImGui.TextUnformatted("--- 小地图旗 ---");
        if (ImGui.Button("移动到旗"))
            _onMoveFlag();
        ImGui.SameLine();
        if (ImGui.Button("飞到旗"))
            _onFlyFlag();
        ImGui.SameLine();
        if (ImGui.Button("停止移动"))
            _onStop();

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
            if (ImGui.InputText("疾跑距离(码)##st", ref stStr, 16)) { float v; if (float.TryParse(stStr, out v)) _config.SprintThreshold = v; }
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
  打本自动跟人走。依靠 vnavmesh 寻路，必须先装好 vnavmesh。

【命令】

  /ftar         跟当前选中的目标
  /ft 玩家名    按名字跟随
  /ff           切换跟/不跟
  /fes          紧急停止
  /flp          暂停循环插件
  /flr          恢复循环插件
  /flt          切换循环
  /fst          看状态
  /fdbg         打开/关闭主窗口

【核心运行逻辑】

  脱战检测每帧都跑，坐标扫描按设定间隔执行（默认1秒）。

  ● 开始跟随
     设置目标后 2 秒内不触发暂停（启动保护期）

  ● 跟人走路中
     距离 ≤ 10码 + 过了启动保护期 → 暂停跟随
     发恢复命令给循环插件接手打怪

  ● 停住后怎么恢复
     距离 > 30码 → 恢复跟随，暂停循环
     脱战 > 1秒  → 恢复跟随（但距离还在10码内不恢复，防反复横跳）
     Boss监测：目标为BattleNpc、等级>80、血量>玩家20倍 → 不恢复

  ● 每次恢复跟随时
     疾跑好了就开（或用坐骑）
     立即扫描一次坐标（不等间隔）
     清坐标缓存，强制发移动

  ● 疾跑触发
     距离>20码 或 跟随目标正在疾跑 → 自动开疾跑

  ● 移动方式
     目标移动超过0.5码 → 立刻发新路径给 vnavmesh
     vnavmesh自动中断当前路径重新规划

  ● 疾跑/坐骑
     疾跑和坐骑互斥，只能开一个
     坐骑模式下非战斗自动上坐骑

  ● 地图黑名单
     添加地图ID后，在该地图中跟随自动暂停

【迷你窗口】

  ●小明 12.3码 [急停] [跟随]

  绿/黄/红圈 = 跟随中/暂停/紧急停止
  [急停] = 紧急停止，暂停状态下显示[恢复]
  [跟随] = 智能跟随（选玩家直接跟，选怪跟怪的目标）

【主窗口 - 设置页】

  进入战斗区(码)  默认10  跟目标≤此值就暂停
  离开战斗区(码)  默认30  跟目标>此值就恢复
  疾跑触发(码)     >20码或目标疾跑时开
  扫描间隔(秒)    默认1   隔几秒看一次目标
  热键: F8 按一下就停
  循环命令: /rotation off / rotation Auto

【坐标页 - 旗子快捷按钮】
  [移动到旗] /vnav moveflag
  [飞到旗]   先上坐骑→/vnav flyflag
  [停止移动] /vnav stop

【插件状态页】
  检测 vnavmesh / BossMod / RotationSolver 是否安装启用。

【指令日志页】
  顶部分类按钮可点击过滤。
  [复制] 一键复制当前显示的日志。

【循环插件配合】

  暂停命令: /rotation off
  恢复命令: /rotation Auto
  用别的插件自己改。

  工作流程：
    跟人走 → 距离≤10码 → 暂停 → 通知循环开始打
    打完 → 脱战1秒或距离>30码 → 恢复 → 通知循环停

【地图黑名单】
  添加地图ID后在此地图中不触发跟随。

【可能的问题】

  加载不出来：检查ApiLevel、ImGui.NET.dll、/xllog
  跟了不走：检查vnavmesh、看日志move记录
  急停没反应：检查热键绑定";
}
