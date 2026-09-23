# Demo 对标整改任务书

- **项目**：工业运动控制上位机（Sophon）
- **版本**：1.0
- **日期**：2026-09-23
- **用途**：交给后续 Agent 分阶段执行、验证和交接

## 1. 任务目标

结合两个历史运动控制 Demo 的控制逻辑和现场使用思路，整改当前框架的运动控制、安全、IO、板卡配置和工业 HMI。

参考项目：

```text
E:\运动控制相关Demo\固高运控视觉\固高运控视觉\MosionAndVisionAPP
E:\运动控制相关Demo\最新版博众3C部门工控程序 C# WPF运动控制架.BZ\AutoStudio.BZ
```

本任务只迁移控制思想，不复制 Demo 的代码、全局状态、阻塞等待或空实现。

必须继续遵守：

- 业务代码通过 `Sophon.Contracts` 使用运动和 IO 契约。
- 新功能使用 `FlowEngineV2`，不在 v1 线上增加新节点类型。
- 新 HAL 与旧 Card 栈不得混用。
- 真卡 DLL 缺失、连接失败或功能未实现时必须失败可见。
- 不允许真卡失败后静默回退 Sim。
- 到位使用控制器完成事件，不能轮询位置值判断到位。
- 急停必须有硬接线安全回路，上位机不能作为唯一急停手段。
- 安全相关改动必须同步 `docs/SAFETY.md` 并重跑对应测试。

## 2. 当前已完成基线

以下问题已经在当前工作区处理，后续 Agent 不应重复实现或回退：

- NLog 配置复制到 UI 输出目录，启动期异常记录已补齐。
- `AxisManager` 的请求登记、异步完成、空闲 Stop 和刷新异常处理已整改。
- Sim 故障注入时在途请求统一完成，避免任务永久悬挂。
- GTS 点动方向、复位轴号和 DI 类型参数错误已修正。
- DMC 不再用清零位置冒充故障复位。
- IO 桥的通道、DLL 和调用错误不再静默吞掉。
- 普通 `StopAllAxes` 与急停 `AbortAll` 已分开。
- 工站循环临时上下文、子流程递归和 EventBus 订阅者异常已处理。
- 配置文件原子写入、损坏文件隔离和密码哈希已加入。
- 首页、报警、限位和部分轴调试页面已完成第一轮 HMI 整改。
- 相对定位统一经过 `AxisManager`。

当前测试基线：

```text
Sophon.Motion.Tests          16/16
Sophon.Vision.Tests          47/47
Sophon.Core.Tests           137/137
Sophon.Infrastructure.Tests  50/50
总计                        250/250
```

## 3. Demo 可借鉴内容

### 3.1 固高 Demo

关键目录：

```text
E:\运动控制相关Demo\固高运控视觉\固高运控视觉\MosionAndVisionAPP\运动\GTS
E:\运动控制相关Demo\固高运控视觉\固高运控视觉\MosionAndVisionAPP\运动\运动控制辅助库
E:\运动控制相关Demo\固高运控视觉\固高运控视觉\MosionAndVisionAPP\运动\运动基站
```

可借鉴的控制链路：

```text
GT_Open
  -> GT_Reset
  -> GT_LoadConfig
  -> GT_ClrSts
  -> GT_AxisOn
```

点位运动前需要检查：

- 轴是否已使能；
- 是否已有规划运动；
- 正、负限位是否触发；
- 驱动报警和跟随误差；
- 停止或急停输入；
- 目标、速度和加减速度是否合法。

停止语义应保留差异：

```text
Halt       工艺暂停/平滑停止
Stop       受控停止
Abort      急停/硬中止
```

回零应包含清状态、设置回零参数、启动回零、阶段监视、停止确认和最终置零等阶段，不能用固定延时直接返回成功。

### 3.2 博乐 Demo

关键目录：

```text
E:\运动控制相关Demo\最新版博众3C部门工控程序 C# WPF运动控制架.BZ\AutoStudio.BZ\PLC
E:\运动控制相关Demo\最新版博众3C部门工控程序 C# WPF运动控制架.BZ\AutoStudio.BZ\ViewModels\Pages
```

可借鉴的统一设备入口：

```text
OpenCard
CloseCard
AxisOn
AxisOff
ClrSts
SetVel
SetAxisSoftLimit
GetAxisSoftLimit
AxisStop
GetDi
GetDo
```

当前框架应继续使用 `IMotionController`、`IIoController`、请求 ID、完成事件和结构化错误，不能简单复制 Demo 的 `bool` 返回模式。

## 4. 禁止迁移的 Demo 模式

后续 Agent 不得引入以下模式：

- `FormMain.Instance` 一类全局静态状态。
- `goto` 重试运动。
- UI 或流程线程中的 `Thread.Sleep`。
- 用 `sRtn += apiCall()` 汇总错误。
- 未实现功能直接 `return true`。
- 读取失败时固定返回 `false` 并让流程继续。
- 用清零位置冒充故障复位。
- 用固定延时冒充回零或到位完成。
- 真卡失败后自动切换 Sim。
- 在新 HAL 中接入旧 Card 栈的 `cardNo + 位号` 接口。

## 5. P0：必须优先完成

### P0-1 默认档案和 Sim IO

**目标**：无真卡环境可以明确选择并完整运行 Sim，现场档案不能被静默覆盖。

重点检查：

- `SeedDefaults()` 是否仍默认生成 GTS；
- Sim 是否注入真实可变的 Sim IO；
- `IoMappingManager` 默认读入是否仍固定返回 false；
- 已有 GTS/DMC 档案是否被启动流程改写；
- `allowSimFallback` 是否一直为 `false`。

相关文件：

```text
Sophon.UI/ShellUiModuleRegister.cs
Sophon.Infrastructure/Config/MotionCardProfile.cs
Sophon.Infrastructure/Config/MotionCardProfileStore.cs
Sophon.Infrastructure/Config/MotionCardCatalog.cs
Sophon.Infrastructure/Motion/MotionControllerFactory.cs
Sophon.Infrastructure/Motion/Drivers/MotionCardIoBridge.cs
Sophon.Infrastructure/Motion/Sim/
```

验收：Sim 的 DI 可以改变并被 `ReadDi` 读到，DO 可以被 `SnapshotDo` 读到；缺少真卡 DLL 时错误可见且没有 Sim 回退。

### P0-2 IO 后台轮询、去抖和边沿

当前 `IoMappingManager` 只有主动 `ReadDi`，`SetVirtualDi` 只发事件而没有同步内部读值，`Reload` 也存在清空后逐项写入窗口。

要求：

- 增加可停止的后台 DI 轮询；
- 使用候选值、候选首次时间和稳定值实现防抖；
- 相同值不重复发布 `DiChanged`；
- `SetVirtualDi` 与后续 `ReadDi` 保持一致；
- `Reload` 一次性替换不可变映射快照；
- 轮询异常记录并可诊断；
- `Dispose` 停止后台任务。

相关文件：

```text
Sophon.Infrastructure/Motion/Axis/IoMappingManager.cs
Sophon.Contracts/IIoController.cs
Sophon.Infrastructure/Config/IoPointConfigStore.cs
```

### P0-3 Sim 急停锁定

`Abort`/`EmergencyStop` 后必须：

- 完成当前请求；
- 设置 `ErrorStop` 并保留故障原因；
- 清除运动类型；
- 拒绝新的 Move/Jog/Home；
- 必要时下使能；
- 只有 `ResetAxis` 后才允许恢复；
- 所有完成事件在锁外派发。

普通取消必须返回 `CommandAborted`，不能升级成 `ErrorStop`。`Halt` 和普通 `Stop` 也必须与急停有可观察差异。

相关文件：

```text
Sophon.Infrastructure/Motion/Sim/SimMotionController.cs
Sophon.Infrastructure/Motion/Axis/AxisManager.cs
```

### P0-4 全局限位监视启动期接线

`GlobalLimitMonitor` 不能等用户打开限位页面后才启动。

要求：

- 应用启动或运动服务注册时 eager resolve；
- 启动时校验限位点名存在；
- 限位沿触发时只创建一次活动报警；
- 触发同向运动必须被拒绝；
- 只允许反向退离；
- 复位前重新读取 DI，仍触发则拒绝；
- 报警码按轴生成：`HARD_LIMIT_ESTOP#轴号`。

相关文件：

```text
Sophon.Core/Safety/GlobalLimitMonitor.cs
Sophon.Core/Safety/MotionAlarmLinkageHandler.cs
Sophon.Infrastructure/Motion/Axis/AxisManager.cs
docs/SAFETY.md
```

## 6. P1：运动、真卡和 HMI

### P1-1 Sim 停止分级

实现可测试的行为差异：

| 操作 | 行为 | 是否进入故障 |
|---|---|---|
| `Halt` | 工艺减速暂停 | 否 |
| `Stop` | 受控减速停止 | 通常否 |
| `Abort` | 急停或硬中止 | 是，进入 `ErrorStop` |

### P1-2 Sim 回零状态机

至少包含：

```text
Preparing -> Searching -> Detected -> Escaping -> Settling -> Zeroing -> Completed
```

必须覆盖超时、未找到原点、限位、取消、急停、断线和回零后重新置零。

### P1-3 GTS 初始化和自检

完善：

```text
GT_Open -> GT_Reset -> GT_LoadConfig -> GT_ClrSts -> GT_AxisOn
```

每一步单独检查返回值，记录卡号、轴号和函数名。多卡、不支持型号和未验证能力必须明确拒绝，不能猜测成功。

### P1-4 GTS 状态沿监视

增加上一周期状态，只有状态沿变化时才生成报警；恢复时发布恢复事件；通信故障与轴故障分开记录；轮询线程异常不得逃逸到 UI 命令线程。

### P1-5 轴命令门禁

使能、下使能、点动、绝对定位、相对定位、回零、停止、复位和全局动作都要根据以下条件计算 `CanExecute`：

- 权限；
- 控制器连接和 Ready；
- 现场验证状态；
- ErrorStop；
- 限位方向；
- 在途命令；
- 参数；
- 工站轴组占用；
- 急停和安全互锁。

UI 应显示禁用原因。

### P1-6 报警复位联动

“清除报警”必须调用真实的 `AxisManager.ResetAxis` 或驱动复位接口。限位 DI 未释放、硬接线急停未复位或驱动条件未满足时，不能只删除 UI 记录。

### P1-7 退出安全收尾

退出流程：

```text
停止接收新命令
 -> 停止流程和工站
 -> 受控停止轴组
 -> Fail-Safe 输出复位
 -> 停止 IO 轮询
 -> 释放限位监视
 -> 断开控制器
 -> 释放资源
 -> 刷新日志
```

每一步要有超时和日志。

### P1-8 多轴插补能力声明

如果当前只实现多轴同步定位，应将 UI 和文档中的“多轴插补”改名为“多轴同步定位”。只有真正具备轨迹规划、同步、完成、取消和 Fail-Fast 后才能继续使用“插补”名称。

## 7. 板卡配置和菜单图标

### 7.1 中文显示

内部保留稳定 ID，UI 不直接显示枚举名。建议显示：

```text
固高 GTS
雷赛 DMC
正运动 ZMC
仿真控制器
```

### 7.2 卡类型

建议使用：

```csharp
public enum MotionCardCategory
{
    Simulation,
    Pulse,
    Bus
}
```

目录应维护：

```text
Model
DisplayName
Driver
Category
Vendor
AxisIndexBase
RequiresConnectionString
RequiresConfigFile
IsImplemented
IsFieldVerified
```

总线卡和 ZMC 没有适配器时必须显示未实现并禁止激活，不能使用脉冲 DLL 代替。

### 7.3 配置联动

```text
板卡类别 -> 厂商/驱动 -> 板卡型号 -> 连接参数/配置文件 -> 轴参数
```

旧 JSON 缺类别时按现有 `Driver`/`CardModel` 推导，但不能覆盖已有现场参数。保存和激活前必须执行配置验证。

当前启动注册已经使用：

```text
MotionCardProfileStore.Normalize
MotionCardCatalog.Find
MotionCardCatalog.IsImplemented
```

后续 Agent 必须扩展现有目录，不要新建第二套型号表。

### 7.4 菜单图标

当前 `SideView.xaml` 已有：

```text
Home.png
Param.png
Axis.png
Infra.png
Station.png
IO.png
Protocol.png
Alarm.png
User.png
```

需要补齐：

- 机器视觉使用相机/视觉图标，不再复用 IO 图标；
- 板卡配置使用硬件/控制卡图标，不再复用普通参数图标；
- 图标风格统一，资源随应用发布；
- 颜色用于表达异常状态，不用于装饰。

## 8. P2：配置和报警可靠性

- `HardLimitEnabled` 时必须校验正负限位点名和 IO 映射。
- `HomeIoName`、急停、安全门、气压、夹具等点名保存前必须校验。
- 调用 `MotionProfileValidator.ValidateAgainstController` 校验轴号、速度、加减速度、软限位和轴号基准。
- 区分规划位置、反馈位置、编码器位置、跟随误差、到位状态和速度；不可用数据不能显示成普通 0。
- 未完成的 PVT/PositionStream 要标记实验性或明确不支持。
- “保存并应用”若不能安全热切换，应改为“保存，重启后生效”。
- 报警至少区分 `Active`、`Acknowledged`、`Cleared`、`Latched`。
- 旧 Card 栈不得被新流程节点引用或覆盖新 HAL。

## 9. P3：文档同步

检查并同步：

```text
README.md
docs/ARCHITECTURE.md
docs/HANDOFF.md
docs/SAFETY.md
AGENTS.md
```

重点核对 Sim 默认策略、GTS/DMC 实现状态、ZMC 和总线卡状态、v1/v2 流程路径、运行数据目录、日志目录、测试数量、停止/急停语义和真卡验证边界。

## 10. 执行顺序

### 阶段 0：基线

```bash
dotnet build Sophon.slnx
dotnet test Sophon.slnx
```

记录当前配置、活动档案、测试数量和 Sim 启动结果。

### 阶段 1：Sim 和 IO

完成默认档案、Sim IO、DI 轮询、防抖、边沿事件、急停锁定和请求完成语义。

### 阶段 2：安全和运动

完成停止分级、回零状态机、限位监视启动、反向退离、报警复位和退出安全收尾。

### 阶段 3：真卡适配器

完成 GTS 初始化和状态沿监视，明确 DMC、ZMC、总线卡的支持范围和未验证边界。

### 阶段 4：板卡配置和 HMI

完成中文名称、卡类型、型号联动、未实现阻断、菜单图标、全局状态和命令门禁。

### 阶段 5：全量回归和文档

完成流程、认证、配置 Store、文档同步和全量测试。

## 11. 验收命令

```bash
dotnet build Sophon.slnx
dotnet test Sophon.slnx
dotnet test tests/Sophon.Core.Tests/Sophon.Core.Tests.csproj --filter FullyQualifiedName~WorkStationLifecycleTests
```

重点场景：

- Sim 无硬件启动并完整运行；
- Sim DI/DO 可读写；
- DI 防抖只产生一次边沿；
- 未打开页面时限位监视仍运行；
- 正限位禁止正向、允许反向退离；
- 限位未释放不能复位；
- 普通取消不进入急停故障；
- 急停后新运动被拒绝；
- 复位后才能恢复；
- 所有运动请求最终完成、取消或超时；
- 多轴任一轴失败后其余轴取消并 Abort；
- 缺 DLL、未实现型号和通信失败均故障可见；
- 配置损坏被隔离；
- 默认密码不出现在日志和 UI；
- 1440×880 和 1920×1080 页面布局可用。

## 12. 真机验证边界

以下内容在现场联调前不得标记为已验证：

- GTS/DMC 实际限位电平；
- 急停输入和停止类别；
- 回零阶段值和驱动复位条件；
- DMC 总线卡、EtherCAT 和多卡通信；
- 编码器到位误差；
- 真机断线和驱动报警恢复；
- STO、硬接线急停和外部安全门；
- 现场 IO 极性。

现场验证必须按 `docs/SAFETY.md` 执行单轴使能、点动、方向、限位、回零、停止、急停、断线、报警复位、IO 和多轴 Fail-Fast 测试。

## 13. Agent 提交要求

每个后续 Agent 必须说明：

1. 修改了哪些文件、类型和方法；
2. 原行为和新行为；
3. 是否涉及安全语义；
4. 是否同步 `docs/SAFETY.md`；
5. 是否涉及真卡和 `IsFieldVerified`；
6. 新增或修改的测试；
7. 实际执行的命令和结果；
8. 未现场验证的部分；
9. 兼容性和迁移风险。

不能只写“修复问题”或“优化代码”。

## 14. 完成标准

只有在以下条件全部满足后，才能标记任务完成：

- Sim 可脱离硬件运行完整链路；
- Sim IO、DI 边沿和防抖正确；
- 急停进入 `ErrorStop`，复位前拒绝运动；
- `Halt`、`Stop`、`Abort` 可区分；
- 限位监视器启动期接入并按轴报警；
- GTS/DMC 失败可见，未实现型号不能激活；
- 板卡类别、厂商、型号和中文显示一致；
- UI 有连接、报警、用户和安全状态；
- 危险命令有权限和设备状态门禁；
- 报警复位作用于真实轴状态；
- 所有请求能完成、取消或超时；
- 全量测试通过；
- 安全、架构、交接和 README 文档已同步；
- 真卡未验证内容仍明确标记；
- 没有复制 Demo 的全局状态、阻塞等待、`goto` 重试、空实现成功或静默回退 Sim。
