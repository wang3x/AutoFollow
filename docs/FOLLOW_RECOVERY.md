# 自动小队跟随与传送恢复

## 使用

服务器信息栏中的 `跟随 OFF` 是自动小队跟随开关。每次加载插件默认 OFF，点击 ON 后在所有区域生效。先筛选存活、在线的小队成员：只有一个 T 时跟随 T；多个 T 时跟随队长。优先目标不可用或是自己时，按小队槽位顺序选择其他有效成员；没有候选则等待。联盟只使用本小队。

以 ContentId 锁定自动目标；对象表中消失不等于离线。客户端小队社交数据中的离线/断线状态优先于可见性；在线状态不明确且不可见的成员不会成为新的目标。原有目标暂时不可见时先执行恢复流程，不立即换人。

手动指定目标退出自动选人模式。手动暂停和 F8 不允许自动恢复，可用原有恢复操作，或 OFF → ON 重新开启。关闭开关停止移动。启用“副本通关后停止”时，通关会关闭自动模式、清除目标和路径，包括正在等待传送的状态。

## 传送行为

正常跟随仍使用 vnavmesh 绕障碍寻路。目标丢失或在同一地图内位置突然跳变时，立即撤销旧路径，保留传送前的最后可靠位置，不追逐传送后的远处坐标。

开启“目标丢失后有限尝试传送”时，一次丢失只尝试一次：

1. 只使用最近两秒的可靠位置，入口距离不得超过 30y；接近入口最多十秒，路径失败或受阻立即停止。
2. 未知入口仅在最近轨迹提供有效行进方向时直走，最多两秒或累计 5y；没有方向则原地等待。不会根据“寻路停止”猜测过图线，也不会模拟 W/R 按键。
3. 等待最多十秒，随后保持停止。目标重新可见且完整路径探测成功后才自动恢复。失败不会循环重试直走。

加载、过场、死亡、断线、目标变更、手动暂停和紧急停止都会阻止旧寻路结果再次启动移动。地图 ID 不变的传送也会检测。

这不能保证所有未知传送入口都成功：直走不会绕墙、避坑或判断机关；自动交互仅支持明确配置的入口。未知对象和不匹配的确认框不点击。

## 已验证入口配置

首版没有内置入口数据。关闭插件后，在 Dalamud 的 AutoFollow 配置文件中编辑 `Follow.TransitionProfiles`，保存后重新加载。先在游戏中人工核实入口，再加入配置。每个位置只能匹配一条配置；重叠匹配不会授权自动交互。

| 字段 | 含义 |
| --- | --- |
| EntryId | 人工指定的非空入口标识 |
| TerritoryId | 实测地图 ID，必须非零 |
| Source | 入口世界坐标 `{ "X": ..., "Y": ..., "Z": ... }` |
| Radius | 入口匹配半径，默认 3y，必须大于零且不超过 5y |
| Kind | 0：走入；1：站位等待；2：交互 |
| WalkDestination | Kind=0 必填；距 Source 不超过 5y，高差不超过 3y |
| ObjectDataId | Kind=2 必填；准确的事件对象 BaseId，不是实例 EntityId |
| ConfirmationText | 可选，当前客户端语言下确认框的完整原文；没有确认框时留空 |

已配置入口会接近到 Source 的 0.5y 内再执行动作。交互还要求：同地图、唯一匹配且可交互的 EventObj、在入口半径内、距玩家不超过 3.5y，并且没有预先打开的确认框。仅在本次交互后的五秒内，原入口仍有效且原文完全匹配时确认一次。列表选择、复杂机关、多步对话暂不自动处理。

旧 `CrossLineKey` / `CrossLineKeyIsHold` 字段仅保留反序列化兼容；不再注入系统按键。`ContinueOnTargetLost=false` 会禁止推测移动，但仍保持停止并探测恢复条件。

## 开发与验证

项目目标为 .NET 10 / Dalamud API 15。可用外部开发程序集目录覆盖默认 XIVLauncher 路径：

```powershell
dotnet build -c Release -p:DalamudLibPath=C:/path/to/dalamud
dotnet run --project tests/AutoFollow.Tests.csproj
git diff --check
```

开发目录需要 Dalamud、FFXIVClientStructs、Lumina、Lumina.Excel、InteropGenerator.Runtime 和上游插件使用的 ImGui.NET.dll；不要提交这些运行时程序集。独立测试程序不依赖游戏客户端或第三方测试包，失败返回非零退出码。

源码核对依据：[vnavmesh IPC](https://github.com/awgil/ffxiv_navmesh/blob/HEAD/vnavmesh/IPCProvider.cs)、[Dalamud IPartyList](https://github.com/goatcorp/Dalamud/blob/HEAD/Dalamud/Plugin/Services/IPartyList.cs)、[Dalamud IDtrBar](https://github.com/goatcorp/Dalamud/blob/HEAD/Dalamud/Plugin/Services/IDtrBar.cs)、[客户端交互接口](https://github.com/aers/FFXIVClientStructs/blob/HEAD/FFXIVClientStructs/FFXIV/Client/Game/Control/TargetSystem.cs)。`Path.MoveTo` 能跳过寻路执行坐标移动，仍需要 vnavmesh；本次没有独立维护底层移动 Hook。

### 游戏内验收（尚未执行）

- 单 T、多 T、自己是 T/队长、队长换人、无 T、死亡/断线/退队，确认自动选人及不跟自己。
- OFF、手动暂停、F8、通关、重新加载及地图黑名单下不被自动选人/异步回调重新启动。
- 普通跟随、Boss 停步、疾跑/坐骑、旗标导航及循环插件控制回归。
- 走到道路尽头触发传送；站位后延时触发；配置对象交互及精确确认；同地图 ID 的传送。
- 没有可达路径、部分路径、目标不可见、未知入口/未知确认框、受阻及超时后保持停止。
- 加载期间停止旧路径；目标重新可见后验证路径再恢复；失败不得重复推测移动。

在实际游戏与国服插件环境中完成这些项目之前，PR 应保持草稿状态。
