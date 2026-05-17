using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AutoFollow.Commands;
using AutoFollow.Conditions;
using AutoFollow.IPC;
using AutoFollow.Models;
using AutoFollow.Movement;
using AutoFollow.Windows;

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
        IKeyState keyState)
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

        _config = Configuration.Load(_pi);
        _followConfig = _config.Follow;

        _debugLog = new DebugLog { Enabled = true };
        _statusChecker = new PluginStatusChecker(_pi, _logger);

        _conditionManager = new ConditionManager(_condition);
        _sprint = new SprintController(_chatGui, _condition, _followConfig);

        _customCommands = new CustomCommandManager(
            _commandManager, _chatGui, _logger, _followConfig, ExecuteCommandAction);
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
            getBossActive: () => false,
            getInCombat: () => _followEngine?.Conditions.InCombat ?? false,
            onClearTarget: () => _followEngine?.SetTarget(null),
            onMoveFlag: () => _commandManager.ProcessCommand("/vnav moveflag"),
            onFlyFlag: () => { _commandManager.ProcessCommand("/mount"); _commandManager.ProcessCommand("/vnav flyflag"); },
            onStop: () => _commandManager.ProcessCommand("/vnav stop"),
            getTerritory: () => TryGetTerritory(_clientState));

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
                    .Where(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player
                        && !o.Name.TextValue.Contains("?")
                        && !string.IsNullOrEmpty(o.Name.TextValue)
                        && Vector3.Distance(selfPos, o.Position) <= 30f)
                    .Select(o => o.Name.TextValue)
                    .Distinct()
                    .ToList();
            },
            onFollowPartyMember: (name) => { _followEngine?.SetTarget(name); _chatGui.Print($"[强效跟随] 跟随队伍成员: {name}"); },
            onEmergencyStop: () => { _followEngine?.EmergencyStop(); Notify("紧急停止"); },
            onStatusReport: PrintStatus);

        _pi.UiBuilder.Draw += DrawUi;
        _pi.UiBuilder.OpenMainUi += () => _miniWindow.IsOpen = !_miniWindow.IsOpen;
        _pi.UiBuilder.OpenConfigUi += () => _debugWindow.IsOpen = !_debugWindow.IsOpen;

        _framework.Update += FirstFrameInit;

        _logger.Info("强效跟随已加载");
        _chatGui.Print("[强效跟随] 已加载！/ff 切换跟随, /ftar 跟随当前目标, /fst 状态");
_chatGui.Print("[强效跟随] 建议手动暂停自动输出插件(/rotation off), 打开BossMod的AI功能");
    }

    private void FirstFrameInit(IFramework _)
    {
        _framework.Update -= FirstFrameInit;
        _logger.Debug("首帧初始化");

        _ipc = new IPCService(_pi, _logger, _commandManager, _debugLog);
        _ipc.LoopController.SetCommands(_followConfig.PauseCommand, _followConfig.ResumeCommand);

        _vnavmesh = new VnavmeshFollow(_ipc.Vnavmesh);

        _followEngine = new FollowEngine(
            _objectTable, _chatGui, _logger, _framework,
            _followConfig, _conditionManager, _sprint,
            _ipc, _vnavmesh, _debugLog,
            getTerritory: () => TryGetTerritory(_clientState));

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

        // 紧急停止热键检查（每帧轻量检测）
        _framework.Update += CheckEmergencyHotkey;

        _logger.Debug("首帧初始化完成");
    }

    private void CheckEmergencyHotkey(IFramework _)
    {
        var key = _followConfig.EmergencyStopKey;
        if (key == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        if (_keyState[key])
            _followEngine?.EmergencyStop();
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
                    _chatGui.Print($"[强效跟随] 已发送暂停命令: {_ipc.LoopController.PauseCommand}");
                }
                else _chatGui.Print("[强效跟随] 未配置循环插件暂停命令");
                break;

            case CommandAction.ResumeLoop:
                if (_ipc?.LoopController.IsConfigured == true)
                {
                    _ipc.LoopController.SendResume();
                    _debugLog.Log("命令", "恢复循环");
                    _chatGui.Print("[强效跟随] 已发送恢复命令");
                }
                else _chatGui.Print("[强效跟随] 未配置循环插件恢复命令");
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
                _chatGui.Print("[强效跟随] 已暂停");
                break;

            case CommandAction.ResumeFollow:
                _followEngine?.Resume();
                _chatGui.Print("[强效跟随] 已恢复");
                break;

            case CommandAction.ToggleFollow:
                _followEngine?.Toggle();
                _chatGui.Print(_followEngine?.State == FollowState.Following
                    ? "[强效跟随] 开始跟随" : "[强效跟随] 已暂停");
                break;

            case CommandAction.EmergencyStop:
                _followEngine?.EmergencyStop();
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
                            _chatGui.Print($"[强效跟随] 已设置跟随目标: {targetName}");
                            _debugLog.Log("命令", $"跟随当前目标: {targetName}");
                            // 设置成功后清除当前选中目标
                            ts->Target = null;
                        }
                        else _chatGui.Print("[强效跟随] 目标名称无效");
                    }
                    else _chatGui.Print("[强效跟随] 未选中任何目标");
                }
                break;

            case CommandAction.SetFollowTarget:
                if (!string.IsNullOrWhiteSpace(args))
                    _followEngine?.SetTarget(args.Trim());
                else _chatGui.Print("[强效跟随] 用法: /ft <玩家名>");
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

        if (_followEngine != null)
        {
            var hasTarget = !string.IsNullOrEmpty(_followEngine.TargetName);
            if (hasTarget && _followEngine.DistanceToTarget <= 30f)
            {
                _followEngine.Resume();
                if (_followEngine.State == FollowState.Following)
                {
                    Notify("恢复跟随");
                    return true;
                }
            }
            else if (hasTarget)
            {
                _chatGui.Print("[强效跟随] 旧目标超过30y，无法恢复");
            }
        }

        _chatGui.Print("[强效跟随] 无跟随目标，无法恢复");
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
            if (obj != null && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
            {
                _followEngine?.SetTarget(targetName);
                ts->Target = null;
                _chatGui.Print($"[强效跟随] 开始跟随: {targetName}");
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
                    _chatGui.Print($"[强效跟随] 跟随敌方目标: {enemyTarget.Name.TextValue}");
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>保留原公开方法，供命令系统等外部调用</summary>
    private void SmartFollow()
    {
        if (!TrySmartFollow())
            _chatGui.Print("[强效跟随] 无法确定跟随目标");
    }

    /// <summary>简洁的聊天通知（仅在 ChatOutput 关闭时仍输出关键信息）</summary>
    private void Notify(string msg)
    {
        _debugLog.Log("通知", msg);
        _chatGui.Print($"[强效跟随] {msg}");
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

        _chatGui.Print("========== 强效跟随 ==========");
        _chatGui.Print($"状态: {StateName(state)}{sprinting}");
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";
        _chatGui.Print($"目标: {target}  距离: {distStr}y  区域: {zone}");
        _chatGui.Print($"模式: Vnavmesh寻路  {vnavReady}");
        _chatGui.Print($"循环插件: {loopInfo}");
        if (_followConfig.EmergencyStopKey != VirtualKey.NO_KEY)
            _chatGui.Print($"紧急停止热键: {_followConfig.EmergencyStopKey}");
        _chatGui.Print("================================");
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

    private void DrawUi()
    {
        _miniWindow.Draw();
        _debugWindow.Draw();
    }

    public void Dispose()
    {
        _framework.Update -= FirstFrameInit;
        _framework.Update -= CheckEmergencyHotkey;
        _followEngine?.Dispose();
        _customCommands?.Dispose();
        _ipc?.Dispose();
        _sprint?.Dispose();
        _pi.UiBuilder.Draw -= DrawUi;
        _logger.Info("强效跟随已卸载");
    }
}
