using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.DutyState;
using AutoFollow.Commands;
using AutoFollow.Conditions;
using AutoFollow.IPC;
using AutoFollow.Models;
using AutoFollow.Movement;
using AutoFollow.Windows;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoFollow;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "强效跟随";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chatGui;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IFramework _framework;
    private readonly IPluginLog _logger;
    private readonly IKeyState _keyState;
    private readonly IAetheryteList _aetheryteList;
    private readonly IGameGui _gameGui;
    private readonly IDutyState _dutyState;
    private readonly IPlayerState _playerState;

    private Configuration _config = null!;
    private FollowConfig _followConfig = null!;
    private ConditionManager _conditionManager = null!;
    private SprintController _sprint = null!;
    private IPCService _ipc = null!;
    private VnavmeshFollow _vnavmesh = null!;
    private CustomCommandManager _customCommands = null!;
    private FollowEngine _followEngine = null!;
    private DebugLog _debugLog = null!;
    private DebugWindow _debugWindow = null!;
    private MiniWindow _miniWindow = null!;
private DateTime _flyMountStart;
    private int _flyVersion;
    private ushort? _lastTerritory;
    private DateTime _lastTeleportClick = DateTime.MinValue;
    /// <summary>完全初始化热键上一帧是否按下（用于上升沿检测）</summary>
    private bool _resetKeyDown;
    /// <summary>本轮旗标流程等待 NavMesh 就绪的起始时间（过图后 NavMesh 需重建）</summary>
    private DateTime _navWaitStart;
    private PluginStatusChecker _statusChecker = null!;

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commandManager,
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        ICondition condition,
        IFramework framework,
        IPluginLog logger,
        IKeyState keyState,
        IAetheryteList aetheryteList,
        IGameGui gameGui,
        IDutyState dutyState,
        IPlayerState playerState)
    {
        _pi = pi;
        _commandManager = commandManager;
        _chatGui = chatGui;
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _framework = framework;
        _logger = logger;
        _keyState = keyState;
        _aetheryteList = aetheryteList;
        _gameGui = gameGui;
        _dutyState = dutyState;
        _playerState = playerState;

        _config = Configuration.Load(_pi);
        _followConfig = _config.Follow;

        // 初始化全局日志/聊天输出（之后都用 Log.Print/Log.Notice/Log.*）
        Log.Initialize(_chatGui, _logger, () => _followConfig.ChatOutput);

_debugLog = new DebugLog();
        _statusChecker = new PluginStatusChecker(_pi);

        _conditionManager = new ConditionManager(_condition);
        _sprint = new SprintController(_condition, _followConfig);

        _customCommands = new CustomCommandManager(
            _commandManager, _followConfig, ExecuteCommandAction);
        _customCommands.Reload();

        _debugWindow = new DebugWindow(_debugLog, _statusChecker, _followConfig,
            getPlayerPos: () => _objectTable[0]?.Position,
            getTargetPos: () => {
                var p = _objectTable[0]; if (p == null) return null;
                unsafe { var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance(); if (ts != null && ts->Target != null) return ts->Target->Position; }
                return null;
            },
            onManualMove: (pos) => _followEngine?.ManualMoveTo(pos),
            onSave: () => _config.Save(), onCommandReload: OnConfigChanged,
            getState: () => _followEngine?.State ?? FollowState.Idle,
            getTargetName: () => _followEngine?.TargetName,
getDistance: () => _followEngine?.DistanceToTarget ?? float.MaxValue,
            getInCombat: () => _followEngine?.Conditions.InCombat ?? false,
            onClearTarget: () => _followEngine?.SetTarget(null),
            onMoveFlag: () => MoveToFlag(fly: false),
            onFlyFlag: () => MoveToFlag(fly: true),
            onStop: () => _commandManager.ProcessCommand("/vnav stop"),
            getTerritory: () => TryGetTerritory(_clientState),
            getKeyState: vk => _keyState.IsVirtualKeyValid(vk) && _keyState[vk]);

        _miniWindow = new MiniWindow(
            _followConfig,
            getState: () => _followEngine?.State ?? FollowState.Idle,
            getTargetName: () => _followEngine?.TargetName,
            getDistance: () => _followEngine?.DistanceToTarget ?? float.MaxValue,
            toggleMainWindow: () => _debugWindow.IsOpen = !_debugWindow.IsOpen,
            onMainButtonClick: MiniMainButtonAction,
            getPartyList: () => {
                var self = _objectTable[0];
                if (self == null) return new List<string>();
                var selfPos = self.Position;
                return _objectTable
                    .Where(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc
                        && !o.Name.TextValue.Contains("?")
                        && !string.IsNullOrEmpty(o.Name.TextValue)
                        && Vector3.Distance(selfPos, o.Position) <= 30f)
                    .Select(o => o.Name.TextValue)
                    .Distinct()
                    .ToList();
            },
            onFollowPartyMember: (name) => { _followEngine?.SetTarget(name); Log.Notice($"[强效跟随] 跟随队伍成员: {name}"); },
            onEmergencyStop: () => { _followEngine?.EmergencyStop(); Notify("紧急停止"); },
            onStatusReport: PrintStatus,
            getPlayerPos: () => _objectTable[0]?.Position,
            getFlagPos: () => {
                try {
                    unsafe {
                        var agentMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
                        if (agentMap != null && agentMap->FlagMarkerCount > 0) {
                            var marker = agentMap->FlagMapMarkers[0];
                            return new Vector3(marker.XFloat, 0f, marker.YFloat);
                        }
                    }
                } catch { }
                return null;
            },
            onMoveFlag: () => MoveToFlag(fly: false),
            onFlyFlag: () => MoveToFlag(fly: true),
            onStop: () => _commandManager.ProcessCommand("/vnav stop"));

        _pi.UiBuilder.Draw += DrawUi;
        _pi.UiBuilder.OpenMainUi += () => _miniWindow.IsOpen = !_miniWindow.IsOpen;
        _pi.UiBuilder.OpenConfigUi += () => _debugWindow.IsOpen = !_debugWindow.IsOpen;

        _framework.Update += FirstFrameInit;

        Log.Info("强效跟随已加载");
        Log.Notice("[强效跟随] 已加载！/ff 切换跟随, /ftar 跟随当前目标, /fst 状态");
        Log.Notice("[强效跟随] 建议手动暂停自动输出插件(/rotation off), 打开BossMod的AI功能");
    }

    private void FirstFrameInit(IFramework _)
    {
        _framework.Update -= FirstFrameInit;
        Log.Debug("首帧初始化");

        _ipc = new IPCService(_pi, _commandManager, _debugLog,
            getPlayerPos: () => _objectTable[0]?.Position,
            framework: _framework);
        _ipc.LoopController.SetCommands(_followConfig.PauseCommand, _followConfig.ResumeCommand);

        // ── 诊断：异步探测运行环境（不阻塞主线程），结果切回主线程输出 ──
        var probeTask = ProbeEnvironmentAsync();   // fire-and-forget，故意不 await（内部自捕获异常）

        _vnavmesh = new VnavmeshFollow(_ipc.Vnavmesh);

        _followEngine = new FollowEngine(
            _objectTable, _framework,
            _followConfig, _conditionManager, _sprint,
            _ipc, _vnavmesh, _debugLog,
            getTerritory: () => TryGetTerritory(_clientState),
            getPlayerRole: GetPlayerRole);

        _followEngine.OnStateChanged += (oldState, newState) =>
        {
            _miniWindow?.SyncBtnState(newState);
            if (newState is FollowState.Combat or FollowState.TargetLost or FollowState.Paused or FollowState.EmergencyStopped)
            {
                var reason = _followEngine?.PauseReason ?? newState.ToString();
                _miniWindow?.SetEngineStatus(reason);
                if (newState == FollowState.TargetLost)
                    Notify("目标丢失");
            }
            else if (newState == FollowState.Following)
            {
                _miniWindow?.SetEngineStatus(null);
            }
        };

        // 完全初始化热键检查（每帧轻量检测，按下沿触发一次）
        _framework.Update += CheckResetHotkey;

        // 区域切换检查
        _framework.Update += CheckZoneChange;

        // 自动接受传送邀请
        _framework.Update += CheckTeleportInvitation;

        // 副本通关后停止并复位到初始状态
        _dutyState.DutyCompleted += OnDutyCompleted;

        Log.Debug("首帧初始化完成");
    }

    /// <summary>
    /// 完全初始化热键：按一下把插件恢复到刚加载时的状态（停寻路、清目标、迷你窗复位）。
    /// 只在按下的那一帧触发一次（上升沿），按住不会重复执行；不输出聊天提示。
    /// </summary>
    private void CheckResetHotkey(IFramework _)
    {
        var key = _followConfig.ResetKey;
        if (key == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY)
        {
            _resetKeyDown = false;
            return;
        }

        var down = _keyState.IsVirtualKeyValid(key) && _keyState[key];
        if (down && !_resetKeyDown)
            FullReset();

        _resetKeyDown = down;
    }

    /// <summary>
    /// 完全复位到插件刚加载时的状态：取消飞行/传送轮询链、停寻路、清跟随目标、
    /// 迷你窗回到「跟随」初始态。不弹任何聊天提示（只写调试日志）。
    /// </summary>
    private void FullReset()
    {
        _flyVersion++;                       // 作废所有在途的飞行/传送轮询链
        _followEngine?.ResetToIdle();        // 停寻路 + 清目标/惯移/条件暂停 + 状态回 Idle
        _miniWindow?.ResetToInitial();       // 按钮回「跟随」、状态文案清空、折叠区收起
        _debugLog.Log("cmd", "完全初始化（热键）");
    }

    /// <summary>
    /// 异步探测运行环境（是否被主线程断言拦截、标记绕行是否生效），
    /// 结果切回框架主线程输出。探测最多 3s，期间不阻塞游戏。
    /// </summary>
    private async Task ProbeEnvironmentAsync()
    {
        try
        {
            var (repo, ver, blocked, afterMark, probeErr) =
                await VnavmeshIPC.ProbeEnvironmentAsync(_objectTable).ConfigureAwait(false);

            var repoName = repo.Contains("ottercorp", StringComparison.OrdinalIgnoreCase) ? "国服(ottercorp)"
                         : repo.Contains("goatcorp", StringComparison.OrdinalIgnoreCase) ? "国际服(goatcorp)"
                         : repo;
            var blockedText = blocked == true ? "【有拦截】" : blocked == false ? "【无拦截】" : "【探测超时】";
            var afterText = afterMark == true ? "【仍被拦】" : afterMark == false ? "【已放行】" : "【未知】";

            // 聊天输出必须回主线程（RunOnFrameworkThread 返回 Task，这里不等待）
            var t = _framework.RunOnFrameworkThread(() =>
            {
                Log.Notice($"[强效跟随][诊断] 运行环境: {repoName}  v{ver}");
                Log.Notice($"[强效跟随][诊断] 后台线程读: 未标记 {blockedText}   标记后 {afterText}");
                _debugLog.Log("诊断", $"repo={repo} ver={ver} blocked={blocked} afterMark={afterMark} err={probeErr}");
            });
            await t.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                var t2 = _framework.RunOnFrameworkThread(
                    () => Log.Notice($"[强效跟随][诊断] 环境探测失败: {ex.Message}"));
                await t2.ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>检测区域切换 → 自动恢复跟随</summary>
    private void CheckZoneChange(IFramework _)
    {
        if (!_followConfig.AutoFollowAfterZoneChange) return;
        if (_followEngine == null) return;

        var current = TryGetTerritory(_clientState);
        if (current == null) return;

        if (_lastTerritory != null && _lastTerritory != current)
        {
            Log.Debug($"区域变更: {_lastTerritory} → {current}");
            _lastTerritory = current;

            // 等加载完成后再处理（BetweenAreas 结束后延迟一帧）
            if (!_condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas])
            {
                _framework.RunOnTick(() =>
                {
                    if (_followEngine == null) return;

                    // 换图 = 换了场景，旧目标多半已不在。迷你窗一律复位成「跟随」，
                    // 且**不再**按引擎状态回同步 —— 引擎此刻可能还残留着上一张图的
                    // Combat/EmergencyStopped，同步回去会把按钮拉成「继续」，
                    // 于是新本里点一下走的就是"恢复旧目标"逻辑而非"开始跟随"。
                    Log.Info($"换图 → 迷你窗复位为「跟随」（引擎原状态 {_followEngine.State}）");
                    _miniWindow?.ResetToInitial();

                    if (!_followConfig.AutoFollowAfterZoneChange) return;
                    if (string.IsNullOrEmpty(_followEngine.TargetName)) return;
                    if (_followEngine.State is FollowState.Following) return;

                    Log.Info("换图后自动恢复跟随");
                    Log.Notice("[强效跟随] 换图后自动恢复跟随");
                    _followEngine.Start();
                }, TimeSpan.FromSeconds(1.5));
            }
        }
        else
        {
            _lastTerritory = current;
        }
    }

    /// <summary>
    /// 副本通关后停止跟随并复位到初始状态（BOSS击杀完毕、系统提示"副本已完成"时）：
    /// 停寻路 → 清空跟随目标 → 迷你窗回到刚加载时的样子（按钮「跟随」、无状态、折叠区收起）。
    /// </summary>
    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (!_followConfig.PauseOnDutyComplete) return;
        if (_followEngine == null) return;
        // 只在引擎活跃时（跟随/追赶/战斗）才处理；空闲/已暂停/紧急停止直接忽略，不打扰用户
        if (_followEngine.State is not (FollowState.Following or FollowState.CatchingUp
            or FollowState.Combat))
            return;

        Log.Info("副本通关，停止跟随并复位到初始状态");
        // 用 Log.Print：受「启用聊天提示」控制，默认关闭时不再强制刷屏
        Log.Print("[强效跟随] 副本通关，已停止跟随并清除目标（循环插件已暂停）");
        _debugLog.Log("状态", "副本通关，停止跟随并复位到初始状态；已发送循环暂停命令");   // 始终记入「指令日志」

        // restoreLoopOutput:false → 复位的同时明确发一次「停止战斗循环」命令，
        // 避免通关瞬间处于 Combat 态（输出已恢复）导致出本后 RSR 一直 auto
        _followEngine.ResetToIdle(restoreLoopOutput: false);
        _miniWindow.ResetToInitial();    // 迷你窗复位：按钮「跟随」、状态清空、折叠区收起
    }

    /// <summary>每帧检测传送邀请确认框，自动点"是"</summary>
    private unsafe void CheckTeleportInvitation(IFramework _)
    {
        if (!_followConfig.AutoAcceptTeleport) return;
        if (_followEngine == null) return;
        // 跟随相关状态都接受（含追赶/战斗/目标丢失），避免传送弹窗瞬间状态不对导致漏点
        if (_followEngine.State is not (FollowState.Following or FollowState.CatchingUp
            or FollowState.Combat or FollowState.TargetLost))
            return;

        // 节流：避免同一弹窗每帧狂点
        if ((DateTime.UtcNow - _lastTeleportClick).TotalMilliseconds < 500)
            return;

// 游戏内 addon 名是 SelectYesno（小写 n）；AtkUnitBasePtr 用 .Address 取址
        var addon = GetSelectYesnoAddon();
        if (addon == null || !addon->IsVisible) return;

        // 优先读 AddonSelectYesno.PromptText，其次 node id=2
        var text = string.Empty;
        try
        {
            var yesno = (AddonSelectYesno*)addon;
            if (yesno->PromptText != null)
                text = yesno->PromptText->NodeText.ToString() ?? string.Empty;
        }
        catch { /* ignore */ }

        if (string.IsNullOrEmpty(text))
        {
            var textNode = addon->GetTextNodeById(2);
            if (textNode != null)
                text = textNode->NodeText.ToString() ?? string.Empty;
        }

        // 部分客户端 node 布局不同：扫一遍可见文本节点
        if (string.IsNullOrEmpty(text) || !IsTeleportPrompt(text))
        {
            text = TryReadSelectYesnoText(addon) ?? text;
        }

        if (string.IsNullOrEmpty(text) || !IsTeleportPrompt(text))
            return;

        _lastTeleportClick = DateTime.UtcNow;
        Log.Info($"自动接受传送邀请: {text}");
        Log.Notice("[强效跟随] 自动接受传送邀请");
        _debugLog.Log("传送", $"自动点是: {text}");

        // FireCallbackInt(0) = 点"是"（比手动构造 AtkValue 更稳）
        addon->FireCallbackInt(0);
    }

    private unsafe AtkUnitBase* GetSelectYesnoAddon()
    {
        var ptr = ResolveAddonPtr("SelectYesno");
        if (ptr != null) return ptr;
        return ResolveAddonPtr("SelectYesNo");
    }

    private unsafe AtkUnitBase* ResolveAddonPtr(string name)
    {
        try
        {
            var handle = _gameGui.GetAddonByName(name, 1);
            var addr = handle.Address;
            if (addr != nint.Zero)
                return (AtkUnitBase*)addr;
        }
        catch (Exception ex)
        {
            Log.Debug($"GetAddonByName({name}) 失败: {ex.Message}");
        }
        return null;
    }

    private static bool IsTeleportPrompt(string text)
    {
        // 中/英常见传送确认文案
        return text.Contains("传送", StringComparison.Ordinal)
            || text.Contains("瞬移", StringComparison.Ordinal)
            || text.Contains("Teleport", StringComparison.OrdinalIgnoreCase)
            || text.Contains("teleport", StringComparison.OrdinalIgnoreCase)
            || text.Contains("想要对你使用", StringComparison.Ordinal);
    }

    private static unsafe string? TryReadSelectYesnoText(AtkUnitBase* addon)
    {
        try
        {
            var count = addon->UldManager.NodeListCount;
            var list = addon->UldManager.NodeList;
            if (list == null) return null;
            for (var i = 0; i < count; i++)
            {
                var node = list[i];
                if (node == null || node->Type != NodeType.Text) continue;
                var tn = (AtkTextNode*)node;
                var s = tn->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(s) && s.Length > 4)
                    return s;
            }
        }
        catch { }
        return null;
    }

    private void OnConfigChanged()

    {
        _config.Save();
        _customCommands.Reload();
        _ipc?.LoopController.SetCommands(_followConfig.PauseCommand, _followConfig.ResumeCommand);
    }

    private void ExecuteCommandAction(CommandAction action, string args)
    {
        switch (action)
        {
            case CommandAction.PauseLoop:
                if (_ipc?.LoopController.IsConfigured == true)
                {
                    _ipc.LoopController.SendPause();
                    _debugLog.Log("命令", $"暂停循环: {_ipc.LoopController.PauseCommand}");
                    Log.Notice($"[强效跟随] 已发送暂停命令: {_ipc.LoopController.PauseCommand}");
                }
                else Log.Notice("[强效跟随] 未配置循环插件暂停命令");
                break;

            case CommandAction.ResumeLoop:
                if (_ipc?.LoopController.IsConfigured == true)
                {
                    _ipc.LoopController.SendResume();
                    _debugLog.Log("命令", "恢复循环");
                    Log.Notice("[强效跟随] 已发送恢复命令");
                }
                else Log.Notice("[强效跟随] 未配置循环插件恢复命令");
                break;

            case CommandAction.ToggleLoop:
                if (_ipc?.LoopController.IsConfigured == true)
                {
                    _ipc.LoopController.SendPause();
                    _debugLog.Log("命令", "切换循环");
                }
                break;

            case CommandAction.PauseFollow:
                _followEngine?.Pause("用户命令");
                Log.Notice("[强效跟随] 已暂停");
                break;

            case CommandAction.ResumeFollow:
                _followEngine?.Resume();
                Log.Notice("[强效跟随] 已恢复");
                break;

            case CommandAction.ToggleFollow:
                _followEngine?.Toggle();
                Log.Notice(_followEngine?.State == FollowState.Following
                    ? "[强效跟随] 开始跟随" : "[强效跟随] 已暂停");
                break;

            case CommandAction.EmergencyStop:
                _followEngine?.EmergencyStop();
                break;

            case CommandAction.FullReset:
                FullReset();
                break;

            case CommandAction.FollowCurrentTarget:
                unsafe
                {
                    var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
                    if (ts != null && ts->Target != null)
                    {
                        var targetName = ts->Target->NameString;
                        if (!string.IsNullOrEmpty(targetName))
                        {
                            _followEngine?.SetTarget(targetName);
                            Log.Notice($"[强效跟随] 已设置跟随目标: {targetName}");
                            _debugLog.Log("命令", $"跟随当前目标: {targetName}");
                            // 设置成功后清除当前选中目标
                            ts->Target = null;
                        }
                        else Log.Notice("[强效跟随] 目标名称无效");
                    }
                    else Log.Notice("[强效跟随] 未选中任何目标");
                }
                break;

            case CommandAction.SetFollowTarget:
                if (!string.IsNullOrWhiteSpace(args))
                    _followEngine?.SetTarget(args.Trim());
                else Log.Notice("[强效跟随] 用法: /ft <玩家名>");
                break;

            case CommandAction.ClearFollowTarget:
                _followEngine?.SetTarget(null);
                break;

            case CommandAction.StatusReport:
                PrintStatus();
                break;

            case CommandAction.ToggleDebugWindow:
                _debugWindow.IsOpen = !_debugWindow.IsOpen;
                break;

            case CommandAction.OpenConfig:
                _debugWindow.IsOpen = !_debugWindow.IsOpen;
                break;
        }
    }

    /// <summary>三态按钮主逻辑，返回是否操作成功（成功才切换按钮状态）</summary>
    private bool MiniMainButtonAction(MiniWindow.BtnState btnState)
    {
        if (btnState == MiniWindow.BtnState.Idle)
            return TrySmartFollow();

        if (btnState == MiniWindow.BtnState.Following)
        {
            _followEngine?.EmergencyStop();
            Notify("紧急停止");
            return true;
        }

        // Paused — 优先智能跟随（用户可能选了新目标），失败则恢复旧目标
        if (TrySmartFollow())
            return true;

        if (_followEngine != null && !string.IsNullOrEmpty(_followEngine.TargetName))
        {
            // 不再限制 30y：Resume() 本身不读距离，引擎已有惯移/穿线逻辑能处理远距离目标，
            // 旧的 30y 门槛只会让"目标稍远就恢复不了"，纯属绊脚石。
            _followEngine.Resume();
            if (_followEngine.State == FollowState.Following)
            {
                Notify("恢复跟随");
                return true;
            }
        }

        Log.Notice("[强效跟随] 无跟随目标，无法恢复");
        return false;
    }

    /// <summary>根据当前游戏选中目标确定跟随目标，成功返回 true</summary>
    private bool TrySmartFollow()
    {
        unsafe
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts == null || ts->Target == null) return false;

            var target = ts->Target;
            var targetName = target->NameString;
            if (string.IsNullOrEmpty(targetName)) return false;

            // 选中了玩家 → 直接跟随
            var obj = _objectTable.SearchById(target->EntityId);
            if (obj != null && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
            {
                _followEngine?.SetTarget(targetName);
                ts->Target = null;
                Log.Notice($"[强效跟随] 开始跟随: {targetName}");
                return true;
            }

            // 选中了敌方NPC → 跟随这个NPC的当前目标（通常是坦克）
            var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)target;
            var enemyTargetId = chara->TargetId;
            if (enemyTargetId.Id != 0 && enemyTargetId.Id != 0xE0000000)
            {
                var enemyTarget = _objectTable.SearchById(enemyTargetId.Id);
                if (enemyTarget != null)
                {
                    _followEngine?.SetTarget(enemyTarget.Name.TextValue);
                    ts->Target = null;
                    Log.Notice($"[强效跟随] 跟随敌方目标: {enemyTarget.Name.TextValue}");
                    return true;
                }
            }

return false;
        }
    }

    /// <summary>简洁的聊天通知</summary>
    private void Notify(string msg)
    {
        _debugLog.Log("通知", msg);
        Log.Notice($"[强效跟随] {msg}");
    }

    private void PrintStatus()
    {
        var state = _followEngine?.State ?? FollowState.Idle;
        var target = _followEngine?.TargetName ?? "未设置";
        var dist = _followEngine?.DistanceToTarget ?? float.MaxValue;
        var zone = _followEngine != null ? (_followEngine.State == FollowState.Combat ? "战斗" : "跟随") : "空闲";
        var loopInfo = _ipc?.GetLoopPluginSummary() ?? "未配置";
        var sprinting = _followEngine?.Sprint.IsSprinting == true ? " [疾跑]" : "";
        var vnavReady = _vnavmesh?.IsAvailable == true ? "vnavmesh OK" : "vnavmesh 未连接";

        Log.Notice("========== 强效跟随 ==========");
        Log.Notice($"状态: {StateName(state)}{sprinting}");
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";
        Log.Notice($"目标: {target}  距离: {distStr}y  区域: {zone}");
        Log.Notice($"模式: Vnavmesh寻路  {vnavReady}");
        Log.Notice($"循环插件: {loopInfo}");
        if (_followConfig.ResetKey != VirtualKey.NO_KEY)
            Log.Notice($"完全初始化热键: {_followConfig.ResetKey}");
        Log.Notice("================================");
    }

    private static string StateName(FollowState s) => s switch
    {
        FollowState.Idle => "空闲",
        FollowState.Following => "跟随中",
        FollowState.CatchingUp => "追赶中",
        FollowState.Combat => "战斗中",
        FollowState.Paused => "已暂停",
        FollowState.TargetLost => "目标丢失",
        FollowState.EmergencyStopped => "紧急停止",
        _ => "未知",
    };

    /// <summary>读取旗标数据（位置 + 所在区域 ID）</summary>
    private unsafe (Vector3 pos, uint territoryId)? ReadFlagMarker()
    {
        try
        {
            var agentMap = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap.Instance();
            if (agentMap != null && agentMap->FlagMarkerCount > 0)
            {
                var marker = agentMap->FlagMapMarkers[0];
                return (new Vector3(marker.XFloat, 0f, marker.YFloat), marker.TerritoryId);
            }
        }
        catch { }
        return null;
    }

    /// <summary>在指定区域找一个已解锁的可用水晶</summary>
    private uint? FindAetheryteForTerritory(uint territoryId)
    {
        foreach (var aetheryte in _aetheryteList)
        {
            if (aetheryte.TerritoryId == territoryId)
                return aetheryte.AetheryteId;
        }
        return null;
    }

    /// <summary>
    /// 读取玩家当前职能（ClassJob.Role，实测取值见右）：
    ///   1=坦克, 2=近战DPS, 3=远程DPS与法系DPS（同为3）, 4=治疗；读不到时返回 0。
    /// 引擎按「1/2 → 近战停步，其余 → 远程停步」二分取用，0 也归入远程档（保守取远）。
    /// </summary>
    private byte GetPlayerRole()
    {
        try
        {
            var job = _playerState.ClassJob;
            if (!job.IsValid) return 0;
            return job.Value.Role;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>读取当前 TerritoryType，按可靠性优先级：原生 GameMain → 反射</summary>
    private static unsafe ushort? TryGetTerritory(IClientState cs)
    {
        // 1. GameMain 原生读取（最可靠，跟 安米儿 一样的路径）
        var gm = FFXIVClientStructs.FFXIV.Client.Game.GameMain.Instance();
        if (gm != null && gm->CurrentTerritoryTypeId != 0)
            return (ushort)gm->CurrentTerritoryTypeId;

        // 2. 反射 IClientState 接口
        try
        {
            var prop = typeof(IClientState).GetProperty("TerritoryType");
            if (prop != null)
                return (ushort?)prop.GetValue(cs);
        }
        catch { }

        // 3. 反射具体运行时类型
        try
        {
            var t = cs.GetType();
            var prop = t.GetProperty("TerritoryType",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(ushort))
                return (ushort)prop.GetValue(cs)!;
        }
        catch { }

        return null;
    }

    // ── 旗标移动（步行 / 飞行）—— 两个按钮共用同一条链路，仅落地后的动作不同 ──

    /// <summary>原生上坐骑（陆行鸟）。ProcessCommand("/mount") 不转发游戏原生命令，必须走 ActionManager。</summary>
    private unsafe void UseMount()
    {
        var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (am != null) am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Mount, 1);
    }

    private bool IsMounted => _condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted];

    /// <summary>
    /// 前往旗标统一入口。
    /// </summary>
    /// <param name="fly">
    ///   <c>true</c>  —— 飞行（/vnav flyflag，需要坐骑就绪，轮询等待，5s 超时回退步行）；
    ///   <c>false</c> —— 步行（先 /vnav moveflag 动起来，再在移动中上坐骑，不等读条）。
    /// </param>
    /// <remarks>
    /// 同一区域 → 直接开始移动；
    /// 跨区域 → 先用 Telepo 传送到该区域已解锁水晶，落地后再按模式开始移动
    /// （超时 20s、落地后等 1.5s 让世界加载完成）。
    /// </remarks>
    private void MoveToFlag(bool fly)
    {
        var flagInfo = ReadFlagMarker();
        if (flagInfo == null)
        {
            Log.Notice("[强效跟随] 未设置旗标");
            return;
        }

        var mode = fly ? "飞行" : "步行";
        var currentTerr = TryGetTerritory(_clientState);
        if (currentTerr == null || currentTerr.Value != (ushort)flagInfo.Value.territoryId)
        {
            // ── 跨区域：先传送 ──
            var aetheryteId = FindAetheryteForTerritory(flagInfo.Value.territoryId);
            if (aetheryteId == null)
            {
                Log.Notice("[强效跟随] 旗标所在区域未开通传送点");
                return;
            }

            _vnavmesh.Stop();
            Log.Notice($"[强效跟随] 旗标在另一区域，正在传送（落地后{mode}前往）...");
            _flyVersion++;
            var v = _flyVersion;
            _flyMountStart = DateTime.UtcNow;   // 复用为「本轮流程」计时起点（传送/等坐骑共用）
            unsafe
            {
                var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
                if (telepo != null) telepo->Teleport(aetheryteId.Value, 0);
            }
            _framework.RunOnTick(() => OnTeleportCheck(v, flagInfo.Value.territoryId, fly), TimeSpan.FromSeconds(1.0));
            return;
        }

        // ── 同区域：直接开始 ──
        BeginFlagMove(fly);
    }

    /// <summary>
    /// 开始向旗标移动（不含传送）。
    /// 步行：先 moveflag 动起来，再在移动中触发上坐骑（坐骑读条允许移动，无需等待）；
    /// 飞行：必须先等坐骑就绪才能 flyflag，所以轮询等待、5s 超时回退步行。
    /// 两者都会先等 NavMesh 就绪 —— 刚进图/刚传送时 NavMesh 尚在重建，
    /// 此时发的 moveflag/flyflag 会被 vnavmesh 丢弃，表现为"上马了但人不动"。
    /// </summary>
    private void BeginFlagMove(bool fly)
    {
        // 等 NavMesh 就绪（同图刚进图也可能还没生成完）
        if (!_vnavmesh.IsNavReady)
        {
            _navWaitStart = DateTime.UtcNow;
            _flyVersion++;
            var v0 = _flyVersion;
            _framework.RunOnTick(() => BeginFlagMoveWhenNavReady(v0, fly), TimeSpan.FromMilliseconds(500));
            return;
        }

        BeginFlagMoveNow(fly);
    }

    /// <summary>NavMesh 已就绪，真正开始向旗标移动</summary>
    private void BeginFlagMoveNow(bool fly)
    {
        if (fly)
        {
            if (IsMounted)
            {
                _commandManager.ProcessCommand("/vnav flyflag");
                return;
            }

            _vnavmesh.Stop();
            UseMount();
            _flyVersion++;
            var ver = _flyVersion;
            _flyMountStart = DateTime.UtcNow;
            _framework.RunOnTick(() => OnFlyCheckMount(ver), TimeSpan.FromSeconds(1.0));
            return;
        }

        // 步行：先发移动指令，再在移动中上坐骑。
        // 游戏的坐骑读条允许移动，所以不必等坐骑就绪才走 —— 先动起来，随后边跑边读条，
        // 读条完成时角色已经在路上，整体更快，也避免了原地读条期间被怪打断后卡住。
        _commandManager.ProcessCommand("/vnav moveflag");

        if (IsMounted) return;

        // 已经在移动中，直接触发上坐骑（读条期间 vnavmesh 继续驱动角色）
        UseMount();
    }

    /// <summary>同图出发前等待 NavMesh 就绪（最多 30s），就绪后继续 BeginFlagMove</summary>
    private void BeginFlagMoveWhenNavReady(int version, bool fly)
    {
        if (version != _flyVersion) return;

        if (!_vnavmesh.IsNavReady)
        {
            if ((DateTime.UtcNow - _navWaitStart).TotalSeconds >= 30)
            {
                Log.Notice("[强效跟随] 寻路数据 30s 未就绪，无法自动移动；请检查 vnavmesh 的 NavMesh 是否已生成");
                return;
            }
            var v = version; var f = fly;
            _framework.RunOnTick(() => BeginFlagMoveWhenNavReady(v, f), TimeSpan.FromMilliseconds(500));
            return;
        }

        _debugLog.Log("状态", $"NavMesh 就绪，耗时 {(DateTime.UtcNow - _navWaitStart).TotalSeconds:F1}s");
        BeginFlagMoveNow(fly);
    }

    /// <summary>轮询检测坐骑是否就绪，成功→飞行，超时→步行回退</summary>
    private void OnFlyCheckMount(int version)
    {
        if (version != _flyVersion) return; // 旧轮询链，丢弃

        if (IsMounted)
        {
            _commandManager.ProcessCommand("/vnav flyflag");
            return;
        }
        if ((DateTime.UtcNow - _flyMountStart).TotalSeconds >= 5)
        {
            _commandManager.ProcessCommand("/vnav moveflag");
            Log.Notice("[强效跟随] 上坐骑失败，已改为步行至旗标");
            return;
        }
        var v = version;
        _framework.RunOnTick(() => OnFlyCheckMount(v), delay: TimeSpan.FromSeconds(1.0));
    }

    /// <summary>轮询等待传送完成；落地后按 fly 决定走 flyflag 还是 moveflag</summary>
    private void OnTeleportCheck(int version, uint targetTerritory, bool fly)
    {
        if (version != _flyVersion) return;

        if ((DateTime.UtcNow - _flyMountStart).TotalSeconds >= 20)
        {
            Log.Notice("[强效跟随] 传送超时，已取消");
            return;
        }

        // 仍在传送/加载中
        if (_condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas])
        {
            var v = version; var t = targetTerritory; var f = fly;
            _framework.RunOnTick(() => OnTeleportCheck(v, t, f), delay: TimeSpan.FromSeconds(1.0));
            return;
        }

        // 确认已抵达目标区域
        var currentTerr = TryGetTerritory(_clientState);
        if (currentTerr == null || currentTerr.Value != (ushort)targetTerritory)
        {
            var v = version; var t = targetTerritory; var f = fly;
            _framework.RunOnTick(() => OnTeleportCheck(v, t, f), delay: TimeSpan.FromSeconds(1.0));
            return;
        }

        // 已到达 → 稍等让世界加载完成，再开始移动（沿用同一版本号，避免中途被作废）
        _flyMountStart = DateTime.UtcNow;
        _navWaitStart = DateTime.UtcNow;   // NavMesh 等待计时从这里开始
        _framework.RunOnTick(() => AfterTeleportArrived(version, fly), delay: TimeSpan.FromSeconds(1.5));
    }

    /// <summary>
    /// 传送落地后：先等 NavMesh 重建完成（过图后 vnavmesh 有一段时间 Nav.IsReady=false，
    /// 此时发的 moveflag/flyflag 会被丢弃，表现为"上马了但人不动"），再开始移动。
    /// </summary>
    private void AfterTeleportArrived(int version, bool fly)
    {
        if (version != _flyVersion) return;

        // 等 NavMesh 就绪：最多等 30s，每 0.5s 探测一次
        if (!_vnavmesh.IsNavReady)
        {
            if ((DateTime.UtcNow - _navWaitStart).TotalSeconds >= 30)
            {
                Log.Notice("[强效跟随] 已到目标区域但寻路数据 30s 未就绪，无法自动移动；请检查 vnavmesh 的 NavMesh 是否已生成");
                return;
            }

            var v = version; var f = fly;
            _framework.RunOnTick(() => AfterTeleportArrived(v, f), delay: TimeSpan.FromMilliseconds(500));
            return;
        }

        _debugLog.Log("状态", $"NavMesh 就绪，耗时 {(DateTime.UtcNow - _navWaitStart).TotalSeconds:F1}s");

        if (!fly)
        {
            // 步行：先发移动，再在移动中上坐骑（坐骑读条允许移动，无需等待）
            _commandManager.ProcessCommand("/vnav moveflag");
            if (!IsMounted) UseMount();
            Log.Notice("[强效跟随] 已抵达目标区域，开始步行至旗标");
            return;
        }

        // 飞行：已上坐骑（传送后可能恢复坐骑状态）→ 直接飞；否则上坐骑并轮询等待
        if (IsMounted)
        {
            _commandManager.ProcessCommand("/vnav flyflag");
            Log.Notice("[强效跟随] 已抵达目标区域，开始飞行至旗标");
            return;
        }

        UseMount();
        _flyMountStart = DateTime.UtcNow;
        _framework.RunOnTick(() => OnFlyCheckMount(version), delay: TimeSpan.FromSeconds(1.0));
    }

    private void DrawUi()
    {
        _miniWindow.Draw();
        _debugWindow.Draw();
    }

    public void Dispose()
    {
        _framework.Update -= FirstFrameInit;
        _framework.Update -= CheckResetHotkey;
        _framework.Update -= CheckZoneChange;
        _framework.Update -= CheckTeleportInvitation;
        _dutyState.DutyCompleted -= OnDutyCompleted;
        _followEngine?.Dispose();
        _customCommands?.Dispose();
        _ipc?.Dispose();
        _sprint?.Dispose();
        _pi.UiBuilder.Draw -= DrawUi;
        Log.Info("强效跟随已卸载");
    }
}
