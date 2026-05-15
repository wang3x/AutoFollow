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
    // 状态栏
    private readonly Func<FollowState> _getState;
    private readonly Func<string?> _getTargetName;
    private readonly Func<float> _getDistance;
    private readonly Func<bool> _getBossActive;
    private readonly Func<bool> _getInCombat;
    private readonly Action _onClearTarget;

    private bool _autoScroll = true;
    private string _filter = "";
    private string _newCommandText = "";
    private int _newCommandAction;
    private string _newCommandDesc = "";
    private bool _showAddCommandDialog;
    private static readonly string[] ActionNames = Enum.GetNames<CommandAction>();
    private bool _waitingForKey;
    private string _hotkeyButtonLabel = "";
    private float _manualX, _manualY, _manualZ;
    private DateTime _lastScan;

    public bool IsOpen { get; set; } = true;

    public DebugWindow(DebugLog log, PluginStatusChecker statusChecker,
        FollowConfig config, Func<Vector3?> getPlayerPos, Func<Vector3?> getTargetPos,
        Action<Vector3> onManualMove, Action onSave, Action onCommandReload,
        Func<FollowState> getState, Func<string?> getTargetName, Func<float> getDistance,
        Func<bool> getBossActive, Func<bool> getInCombat, Action onClearTarget)
    {
        _log = log; _statusChecker = statusChecker; _config = config;
        _getPlayerPos = getPlayerPos; _getTargetPos = getTargetPos;
        _onManualMove = onManualMove; _onSave = onSave; _onCommandReload = onCommandReload;
        _getState = getState; _getTargetName = getTargetName; _getDistance = getDistance;
        _getBossActive = getBossActive; _getInCombat = getInCombat; _onClearTarget = onClearTarget;
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
        if (ImGui.BeginTabItem("指令日志")) { DrawLogTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("设置")) { DrawSettingsTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("坐标")) { DrawCoordTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("插件状态")) { DrawStatusTab(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("使用说明")) { DrawHelpTab(); ImGui.EndTabItem(); }
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
        ImGui.TextUnformatted("--- 手动移动 ---");
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("X##mx", ref _manualX); ImGui.SameLine();
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("Y##my", ref _manualY); ImGui.SameLine();
        ImGui.SetNextItemWidth(80); ImGui.InputFloat("Z##mz", ref _manualZ);
        if (ImGui.Button("移动到坐标")) { _onManualMove(new Vector3(_manualX, _manualY, _manualZ)); }
        ImGui.SameLine();
        if (ImGui.Button("复制玩家坐标")) { if (pp != null) { _manualX = pp.Value.X; _manualY = pp.Value.Y; _manualZ = pp.Value.Z; } }
        ImGui.SameLine();
        // 读旗功能因国服ClientStructs不兼容已移除

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
            str = _config.SprintThreshold.ToString("F1");
            if (ImGui.InputText("疾跑触发(y)##st", ref str, 16)) { float v; if (float.TryParse(str, out v)) _config.SprintThreshold = v; }
            var scanStr = _config.ScanInterval.ToString("F1");
            if (ImGui.InputText("坐标扫描间隔(秒)##si", ref scanStr, 16)) { float v; if (float.TryParse(scanStr, out v) && v >= 0.5f) _config.ScanInterval = v; }
            ImGui.TextUnformatted("说明: ≤进入战斗区暂停跟随, >离开战斗区继续跟随, 脱战1秒恢复");
            ImGui.Spacing();
            if (ImGui.Button("保存距离")) { _onSave(); }
            ImGui.SameLine();
            if (ImGui.Button("恢复默认"))
            {
                _config.CombatEnterRange = 10f;
                _config.CombatExitRange = 30f;
                _config.SprintThreshold = 10f;
                _config.ScanInterval = 1f;
                _onSave();
            }
        }
        if (ImGui.CollapsingHeader("疾跑设置", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var v = _config.SprintEnabled; if (ImGui.Checkbox("启用疾跑##s1", ref v)) _config.SprintEnabled = v;
            v = _config.SprintOnlyInCombat; if (ImGui.Checkbox("仅战斗疾跑##s2", ref v)) _config.SprintOnlyInCombat = v;
        }
        if (ImGui.CollapsingHeader("聊天消息", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var v = _config.ChatOutput;
            if (ImGui.Checkbox("启用聊天提示##chat", ref v)) _config.ChatOutput = v;
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
        if (ImGui.CollapsingHeader("自定义命令"))
        {
            DrawCommandTable();
            if (_showAddCommandDialog) DrawAddCommandDialog();
            if (ImGui.Button("+ 添加")) { _showAddCommandDialog = true; _newCommandText = ""; _newCommandAction = 0; _newCommandDesc = ""; }
            ImGui.SameLine();
            if (ImGui.Button("保存")) { _onSave(); _onCommandReload(); }
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

    private void DrawCommandTable()
    {
        if (!ImGui.BeginTable("##cm", 5, ImGuiTableFlags.Borders|ImGuiTableFlags.RowBg)) return;
        ImGui.TableSetupColumn("命令", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("动作", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 35);
        ImGui.TableSetupColumn("删除", ImGuiTableColumnFlags.WidthFixed, 35);
        ImGui.TableHeadersRow();
        int toRemove = -1;
        for (int i = 0; i < _config.CustomCommands.Count; i++)
        {
            var e2 = _config.CustomCommands[i];
            ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextUnformatted(e2.Command);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(e2.Action.ToString());
            ImGui.TableNextColumn(); ImGui.TextUnformatted(e2.Description);
            ImGui.TableNextColumn(); var en = e2.Enabled; if (ImGui.Checkbox($"##e{i}", ref en)) e2.Enabled = en;
            ImGui.TableNextColumn(); if (ImGui.Button($"X##d{i}")) toRemove = i;
        }
        ImGui.EndTable();
        if (toRemove >= 0) { _config.CustomCommands.RemoveAt(toRemove); _onCommandReload(); }
    }

    private void DrawAddCommandDialog()
    {
        ImGui.OpenPopup("添加命令");
        if (!ImGui.BeginPopupModal("添加命令", ref _showAddCommandDialog, ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.InputText("命令", ref _newCommandText, 64);
        var ai = _newCommandAction; if (ImGui.Combo("动作", ref ai, ActionNames, ActionNames.Length)) _newCommandAction = ai;
        ImGui.InputText("说明", ref _newCommandDesc, 256);
        if (ImGui.Button("确定")) { if (!string.IsNullOrWhiteSpace(_newCommandText)) { _config.CustomCommands.Add(new CustomCommandEntry{Command=_newCommandText.TrimStart('/'),Action=(CommandAction)_newCommandAction,Description=_newCommandDesc}); _showAddCommandDialog=false; } }
        ImGui.SameLine(); if (ImGui.Button("取消")) _showAddCommandDialog = false;
        ImGui.EndPopup();
    }

    // ════════════ 页: 使用说明 ════════════

    private void DrawHelpTab()
    {
        ImGui.BeginChild("##help", new Vector2(0, 0), false);
        ImGui.TextUnformatted(HelpText);
        ImGui.EndChild();
    }

    private const string HelpText = @"
【插件说明】
  打本自动跟人走。依靠 vnavmesh 寻路，所以必须先装好 vnavmesh。

【命令】

  /ftar         跟当前选中的目标（选人→打命令→开始跟）
  /ft 玩家名    指定名字跟
  /ff          切换跟/不跟
  /fes         紧急停止，跟随和循环全停
  /flp         暂停循环插件
  /flr         恢复循环插件
  /flt         切换循环
  /fst         看状态
  /fdbg        打开/关闭主窗口

【运行逻辑】

  每 N 秒（默认 2 秒）扫描一次目标坐标，发给 vnavmesh 走过去。

  跟人走路中
    → 距离目标 ≤ 10码 + 过了2秒启动保护
    → 停！暂停跟随，通知循环插件开始打

  停住后
    → 距离目标 > 30码 → 走！恢复跟随，暂停循环
    → 脱战超过 1 秒   → 走！（但距离还在10码内就不走，防反复横跳）

  每次恢复走的时候：
    疾跑能用就立刻开疾跑
    立刻扫一次目标坐标（不等那2秒间隔）
    清掉坐标缓存，强制发一次移动

  刚设好目标 2 秒内不会因为距离近就停，免得刚跟上就停。

  脱战检测每帧都跑，不跟随扫描间隔，所以一脱战最多等 1 秒就恢复。

【迷你窗口】
  加载后自动打开，不占地方：

    ●小明 12.3码 战 [主] [停]

  圆点颜色：绿=跟随中  黄=暂停中  红=紧急停止
  战/休 = 角色自己在不在打
  [主] = 打开主窗口  [停] = 紧急停止

  迷你窗口可以拖到不碍眼的地方。

【主窗口 - 设置页】

  进入战斗区(码)  默认 10   跟目标小于这个就停
  离开战斗区(码)  默认 30   跟目标大于这个就走
  疾跑触发(码)    默认 10   超过这个距离开疾跑
  坐标扫描间隔(秒)默认 1    隔几秒看一次目标位置

  紧急停止热键    默认 F8，按一下就停（不用长按）

  循环插件命令    默认 /rotation off 和 /rotation Auto
                  用别的插件自己改

  自定义命令      自己加/删/改命令，默认折叠

【主窗口 - 坐标页】
  实时看自己和目标的坐标，可以手动填坐标让 vnavmesh 走过去。

【主窗口 - 插件状态页】
  点重新检测看 vnavmesh、BossMod 有没有装。

【主窗口 - 指令日志页】
  插件每步操作都记在这里。
  顶部的类别按钮可以点，点一下隐藏该类日志，再点恢复。
  复制按钮一键复制当前显示的日志。

【主窗口 - 使用说明页】
  就是你现在看的这个。

【循环插件配合】

  设置里填好命令（默认 RotationSolverReborn）：
    暂停命令: /rotation off
    恢复命令: /rotation Auto

  工作流程：
    跟人走 → 距离 ≤ 10码 → 停 → 通知循环插件开始打
    打完 → 脱战1秒 或 距离 > 30码 → 恢复跟 → 通知循环插件停

【可能的问题】

  加载不出来：
    DalamudApiLevel 是不是 15
    ImGui.NET.dll 有没有放一起
    去 /xllog 看报错

  跟了不走：
    vnavmesh 装了没、开了没
    vnavmesh 界面是不是绿色
    看指令日志有没有 move 记录
    去坐标页手动填坐标测 vnavmesh

  紧急停止没反应：
    热键绑上了没
    别的插件占了同一个键";
}
