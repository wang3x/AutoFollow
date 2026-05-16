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
            getState: () => _followEngine?.State ?? FollowState.Idle,
            getTargetName: () => _followEngine?.TargetName,
            getDistance: () => _followEngine?.DistanceToTarget ?? float.MaxValue,
            getInCombat: () => _followEngine?.Conditions.InCombat ?? false,
            toggleMainWindow: () => _debugWindow.IsOpen = !_debugWindow.IsOpen,
            stopResume: () => { if (_followEngine?.State is FollowState.Idle or FollowState.Paused or FollowState.EmergencyStopped or FollowState.TargetLost) _followEngine?.Resume(); else _followEngine?.EmergencyStop(); },
            smartFollow: SmartFollow);

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

    private void SmartFollow()
    {
        unsafe
        {
            var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
            if (ts == null || ts->Target == null) { _chatGui.Print("[强效跟随] 未选中目标"); return; }

            var target = ts->Target;
            var targetName = target->NameString;
            if (string.IsNullOrEmpty(targetName)) return;

            // 选中了玩家 → 直接跟随
            var obj = _objectTable.SearchById(target->EntityId);
            if (obj != null && obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player)
            {
                _followEngine?.SetTarget(targetName);
                ts->Target = null;
                _chatGui.Print($"[强效跟随] 开始跟随: {targetName}");
                return;
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
                    return;
                }
            }

            _chatGui.Print("[强效跟随] 无法确定跟随目标");
        }
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

        _chatGui.Print("══════════ 强效跟随 ══════════");
        _chatGui.Print($"状态: {StateName(state)}{sprinting}");
        var distStr = dist > 150f ? "--" : dist < 100f ? $"{dist:F2}" : $"{dist:F1}";
        _chatGui.Print($"目标: {target}  距离: {distStr}码  区域: {zone}");
        _chatGui.Print($"模式: Vnavmesh寻路  {vnavReady}");
        _chatGui.Print($"循环插件: {loopInfo}");
        if (_followConfig.EmergencyStopKey != VirtualKey.NO_KEY)
            _chatGui.Print($"紧急停止热键: {_followConfig.EmergencyStopKey}");
        _chatGui.Print("══════════════════════════════");
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

    /// <summary>通过反射安全读取 TerritoryType，避免直接访问导致 MissingMethodException</summary>
    private static ushort? TryGetTerritory(IClientState cs)
    {
        // 先尝试反射具体的运行时类型（可能比接口有更多属性）
        try
        {
            var t = cs.GetType();
            var prop = t.GetProperty("TerritoryType",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(ushort))
                return (ushort)prop.GetValue(cs)!;
        }
        catch { }

        // 再尝试反射 IClientState 接口
        try
        {
            var prop = typeof(IClientState).GetProperty("TerritoryType");
            if (prop != null)
                return (ushort?)prop.GetValue(cs);
        }
        catch { }

        // 原生方式：直接读 GameMain 内存（Dalamud 内部 ClientState 也是这么读的）
        unsafe
        {
            try
            {
                var gm = FFXIVClientStructs.FFXIV.Client.Game.GameMain.Instance();
                if (gm != null)
                    return (ushort)gm->CurrentTerritoryTypeId;
            }
            catch { }
        }

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
