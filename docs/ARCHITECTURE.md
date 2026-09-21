# 架构设计

## 分层结构

```
Common\                 公共基础（DI/日志/配置/路径）
Sophon.Contracts\       跨模块契约（IMotionController/IIoController/AxisDefinition/IVisionProvider/ITimeSource/事件记录）
Sophon.Motion\          纯算法库（速度规划/插补/前瞻/软限位约束，硬件无关，可单测）
Sophon.Vision\          视觉（Sim/OpenCv 提供者、九点/旋转中心标定、XYR 纠偏）
Sophon.Infrastructure\  HAL（Sim 控制器、固高/雷赛适配器、AxisManager、IO 点名、数据库、协议）
Sophon.Core\            核心业务（流程引擎 v2、工站状态机、报警中心、全局限位联动、示教服务）
Sophon.Application\     应用服务（仓储桥接、业务入口）
Sophon.UI\              WPF 界面（无边框主壳、流程编辑器、轴调试、示教、报警、视觉页面）
SophonData\             运行数据（SQLite、轴配置、示教点、标定、流程 JSON）
tests\                  xUnit（Motion/Core/Infrastructure/Vision 四个测试工程）
```

## 关键设计决策（架构评审确认）

1. **到位判定**：全系统只认 `AxisDone(requestId, result)` 事件，禁止轮询位置判断到位。
2. **单位体系**：接口一律物理单位 mm/deg；脉冲当量（PulsePerUnit 含丝杆导程/减速比）只在驱动适配器内换算。
3. **插补模式**：真卡 = 整段下发/PVT（卡内插补）；上位机 1ms 软插补仅限 Sim/离线（Windows 非实时，`IPositionStreamSink` 已预留）。
4. **限位双层**：软限位在规划器层强制（越界拒绝/提前减速截断）；硬限位优先卡内硬件处理，上位轮询为第二道防线。
5. **流程并发模型**：单调度线程 + await 协作；暂停挂在节点边界；停止 = 取消令牌；节点内禁止无限阻塞（等待类节点一律带超时）。
6. **持久化单源**：流程/点位/标定/轴配置 = JSON（git 友好）；运行数据/报警历史 = SQLite。
7. **驱动模式显式**：UI 状态栏标识当前驱动；请求真卡失败**不静默回退 Sim**（`MotionControllerFactory.Create(..., allowSimFallback: false)`）。

## 数据流（视觉引导搬运示例）

```
FlowEditor(Nodify) ──保存/加载──▶ FlowGraphStore(JSON)
FlowEngineV2(单调度线程)
  ├─ AxisHomeNode ─▶ IMotionController.Home ─▶ AxisDone ✓
  ├─ VisionMeasureNode ─▶ IVisionProvider.Trigger ─▶ VisionResult → 上下文(VisionOk/WorldX/Y)
  ├─ BranchNode(conditionKey=VisionOk)
  │    ├─ True ─▶ MultiAxisInterpNode(targetsFromContext) ─▶ MoveAbs×N ─▶ WhenAll(AxisDone)
  │    └─ False ─▶ DoSet(RedLight)
  └─ 节点状态 ─▶ FlowNodeStateChanged ─▶ UI 高亮
GlobalLimitMonitor(20ms) ─▶ 软/硬限位 ─▶ AlarmCenter ─▶ MotionAlarmLinkageHandler ─▶ Stop/Abort/急停
```

## 契约文件（Sophon.Contracts）

`Enums.cs`（DriverKind/ConnectionState/MotionCapability/HomingMode/HomeDirection）、`Events.cs`（AxisDoneArgs/AxisFaultArgs/LimitTriggeredArgs/VisionResult/VisionFrame）、`IMotionController.cs`、`AxisDefinition.cs`、`IIoController.cs`、`IVisionProvider.cs`、`ITimeSource.cs`。所有模块只对契约编程，实现可替换。

## 测试策略

- `Sophon.Motion.Tests`（16）：曲线积分/对称性、直线终点误差 ≤1e-9、圆弧半径误差、Blend 误差界、软限位截断、虚拟时钟确定性。
- `Sophon.Infrastructure.Tests`（19）：Sim 到位/软限位拒绝/急停/回零、故障注入（硬限位/断线）、工厂不回退、AxisManager。
- `Sophon.Core.Tests`（58）：工站生命周期回归（原版缺陷场景）、流程引擎 v2（拓扑/暂停/断点/事件总线/子流程）、报警中心/限位联动/示教、示例工站端到端。
- `Sophon.Vision.Tests`（16）：九点标定矩阵恢复、旋转中心两圆法、XYR 纠偏数值、亚像素匹配 ≤0.05px。
