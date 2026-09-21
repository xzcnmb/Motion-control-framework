# 驱动接入指南（固高 GTS / 雷赛 DMC）

> ⚠️ 本框架内的真卡适配器为 **P/Invoke 骨架（未经真机验证）**：接口契约、命令路由、AxisDone 事件链均已完整实现，厂商 DLL 函数调用点已按手册签名声明并以 try/catch 防护。真机首验请按文末检查单执行。

## 1. 架构约定

- 所有业务代码只依赖 `Sophon.Contracts.IMotionController` / `IIoController` / `AxisDefinition`，与卡品牌无关。
- 单位一律物理单位（mm/deg）；`AxisDefinition.PulsePerUnit`（脉冲/物理单位，含丝杆导程与减速比）在适配器内完成换算。
- 到位只认 `AxisDone` 事件；停止分级：`StopMotion`（减速停）/ `Abort`（急停）/ `AbortAll`。
- **不静默回退**：请求真卡而初始化失败 → 抛异常并写日志，除非显式 `allowSimFallback: true`。

## 2. 配置切换

驱动类型经 `DriverKind` 配置（当前 UI 默认 Simulated）：

```csharp
var controller = MotionControllerFactory.Create(
    kind: DriverKind.GoogolGts,          // 或 LeadShineDmc
    axes: axisDefinitions,
    allowSimFallback: false);            // 现场严禁 true
await controller.ConnectAsync();         // 失败则上层处理并保持报警状态
```

替换点：`Sophon.UI/ShellUiModuleRegister.cs` 中 `IMotionController` 的注册（Sim 换真卡工厂），UI 状态栏同步显示驱动模式与"未经真机验证"标识。

## 3. 固高 GTS 适配器（GoogolGtsMotionController）

- 文件：`Sophon.Infrastructure/Motion/Drivers/GoogolGtsMotionController.cs` + `Drivers/Native/GoogolGtsNative.cs`
- 已声明 P/Invoke：`GT_Open/GT_Close/GT_Reset/GT_AxisOn/GT_AxisOff/GT_PrfTrap/GT_SetTrapPrm/GT_SetPos/GT_GetPos/GT_SetVel/GT_GetVel/GT_Update/GT_Stop/GT_GetSts` 等
- 未完成项（真机补齐）：卡初始化参数（脉冲当量写入）、限位配置 `GT_LmtsOn`、运动完成中断回调映射到 AxisDone、PVT（`GT_PrfPvt`）通道

## 4. 雷赛 DMC 适配器（LeadShineDmcMotionController）

- 文件：`Sophon.Infrastructure/Motion/Drivers/LeadShineDmcMotionController.cs` + `Drivers/Native/LeadShineDmcNative.cs`
- 已声明 P/Invoke：`dmc_board_init/dmc_board_close/dmc_set_sevon_enable/dmc_set_profile/dmc_pmove/dmc_vmove/dmc_stop/dmc_get_position/dmc_set_position/dmc_axis_io_status` 等
- 未完成项：单轴定位返回等待（卡内到位标志→AxisDone）、限位输入使能、连续插补缓冲下发

## 5. IO 点名映射

IIoController 使用虚拟点名（如 `LimitX+`、`GreenLight`），真卡适配器负责点名 →（卡号, 位号）映射表（配置文件），业务与限位监视代码零改动。

## 6. 真机首验检查单

1. 卡初始化返回码与固件版本核对；2. 使能后驱动器 READY 信号；3. 报警清除时序（驱动器报警需先复位再使能）；4. 单轴 1mm/10mm/100mm 到位重复精度；5. AxisDone 与卡内到位标志一致；6. 硬限位输入接线与卡内限位使能；7. 急停硬接线；8. 断线后卡级停车；9. 回零模式（限位/原点/Z相）与方向。
