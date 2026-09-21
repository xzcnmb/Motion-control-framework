# 小智运动控制框架 (Xiaozhi Motion Control Framework)
## 生产级工程交付与系统交接文档 (v2.5 企业生产版)

---

### 一、系统定位与产品概览

**小智运动控制框架** 是一套面向工业自动化机台（点胶机、贴片机、AOI、半导体固晶对位、多轴机械手、装配工站）的高可靠、模块化、事件驱动的上位机控制系统平台。基于 **.NET 8 (net8.0-windows) + WPF + Prism 9 + HandyControl + Nodify + OpenCvSharp4** 构建，彻底脱离演示 Demo 范畴，全面满足工业现场安全规范与多硬件平台接入需求。

#### 核心能力指标
1. **多品牌控制卡平台归一化**：内置固高 GTS（PCI/PCIe）、雷赛 DMC（脉冲/总线）、正运动 ZMC（网口/PCIe）及全功能虚拟控制器（Simulated）。
2. **多轴联动与前瞻插补**：支持直线、圆弧插补与 7 段 S 型/梯形速度曲线规划，到位严格遵守事件回报协议（禁止轮询位置）。
3. **Fail-Fast 联动连锁急停**：单轴遇阻、越界或伺服报警时，1ms 内向协同组广播中断并急停其余伴随轴，杜绝机台撞刀与机构拉扯。
4. **机器视觉闭环引导与拖拽对位**：海康 MVS 工业相机真实 SDK 取流（反射隔离无 DLL 强依赖）、九点仿射变换、已知角度两点/多点旋转中心求解、手眼镜像手性自动补偿、图像视口“点击对齐准星 (Click-to-Align)”与“鼠标拖拽矢量对位 (Drag-to-Move)”。
5. **气动夹爪五重安全防护**：前置安全门气压联锁、气缸组空间互锁、双电控 200ms 防烧脉冲换向、双到位磁性开关硬件闭环确认与超时报警。
6. **RS485 / 232 工业外设集成**：Modbus RTU / TCP 协议栈，同一物理端口单句柄多从站调度与 `SemaphoreSlim` 串行互斥，32位浮点/双字字序反转（SwapWords）、量纲自动缩放及写入上下限白名单拦截。
7. **零内存泄漏工业 HMI**：无边框现代扁平化设计，开机欢迎流体动画，深色科技感工业登录鉴权，全页面 `INavigationAware` 高频事件注销机制。

---

### 二、系统架构体系

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Sophon.UI (用户人机界面)                         │
│  MainWindow (无边框Shell) | HomeView (欢迎动画看板) | UserView (鉴权)    │
│  MotionCardConfigView (多卡配置) | DeviceControlView (气动/485外设)     │
│  AxisDebugView (轴调试) | TeachView (示教) | FlowEditorView (节点画布)  │
│  VisionMonitorView (视觉取景/点击拖拽对位) | VisionCalibrationView (标定)│
└───────────────────────────────────┬────────────────────────────────────┘
                                    │ Prism 9 Navigation & Region
┌───────────────────────────────────▼────────────────────────────────────┐
│                    Sophon.Core (核心业务与流程引擎)                     │
│  FlowEngineV2 (单调度线程 DAG 拓扑执行器, 支持 Loop/Jump 循环回边)      │
│  FlowNodeRegistry & 18类工业节点 (AxisMove/Interp/Vision/Cylinder...)  │
│  TeachService (示教存储与安全限位校验) | AlarmCenter (5级独立报警中心) │
│  GlobalLimitMonitor (看门狗+软限位监视器) | CylinderService (气动防烧)  │
│  PeripheralDeviceService (485总线调度器, 动态站号轮询与白名单保护)     │
└─────────────┬─────────────────────┬─────────────────────┬──────────────┘
              │                     │                     │
┌─────────────▼─────────┐ ┌─────────▼─────────┐ ┌─────────▼─────────┐
│     Sophon.Motion     │ │    Sophon.Vision    │ │  Infrastructure │
│ 7段S曲线/梯形规划器   │ │ HikvisionFrameSource│ │ Card/Axis Config│
│ 直线/圆弧多轴插补内核 │ │ 9点最小二乘仿射标定 │ │ Drivers (GTS/   │
│ 前瞻平滑与动力学约束  │ │ 旋转中心正交分解算法│ │ DMC/ZMC/Sim)    │
│ ITimeSource 抽象时钟  │ │ 手性纠偏/点击拖拽对位│ │ Modbus/串口总线 │
└─────────────┬─────────┘ └─────────┬─────────┘ └─────────┬─────────┘
              │                     │                     │
┌─────────────▼─────────────────────▼─────────────────────▼──────────────┐
│                       Sophon.Contracts (纯契约抽象层)                  │
│  IMotionController / IIoController / IVisionProvider / IFrameSource    │
│  MotionCardProfile / PlatformOptions / CameraConfig / CylinderDef     │
│  PeripheralDeviceConfig / DeviceTag / AxisDefinition / AlarmDefinition │
└────────────────────────────────────────────────────────────────────────┘
```

---

### 三、多平台运动控制卡适配与配置规范

针对不同运动控制卡品牌的底层硬件与 API 语义差异，系统在 `Sophon.Contracts.MotionCardProfile` 与 `PlatformOptions` 中完成了归一化抽象：

| 平台参数维度 | 固高科技 (Googol GTS) | 雷赛智能 (LeadShine DMC) | 正运动 (Zmotion ZMC) | 虚拟仿真 (Simulated) |
|---|---|---|---|---|
| **硬件轴号基准** | 从 **1** 起算 (`AxisIndexBase = 1`) | 从 **1** 起算 (`AxisIndexBase = 1`) | 从 **0** 起算 (`AxisIndexBase = 0`) | 从 **0** 起算 (`AxisIndexBase = 0`) |
| **加减速参数语义** | 加速度物理量 ($\text{mm/s}^2$) | 传统脉冲卡为**加速时间** ($s$) | 加速度物理量 ($\text{mm/s}^2$) | 加速度物理量 ($\text{mm/s}^2$) |
| **设备标识** | `CardNo` (PCI 通道号) | `CardNo` (`dmc_board_init` 板卡号) | `ConnectionString` (网口 IP/端口) | 内存实例 |
| **硬件配置文件** | **必须加载** (`*.cfg`，如 `GTS800.cfg`) | 否（板载寄存器或软配置） | 可选加载 `*.bas` | 否 |
| **多轴启动机制** | `GT_Update` 位掩码同步激活 | 各轴独立启动或连续插补通道 | 连续插补指令流 | 内部插补器多轴同步驱动 |

#### 配置校验规则（`MotionProfileValidator`）
在配置界面点击保存时，系统自动执行九大安全校验门禁：
1. `Profile` 不可为空，且轴总数受限在 `0 ~ 64` 轴合理范围内。
2. 轴 ID 严格唯一，禁止重复配置同一轴号。
3. 传动比与脉冲当量必须为正数：`PulsePerUnit > 0`。
4. 软限位启用时，必须满足几何单调性：`SoftLimitMin < SoftLimitMax`。
5. 动力学约束强制校验：`MaxSpeed > 0`, `MaxAccel > 0`, `MaxDecel > 0`。
6. 固高平台强制校验配置文件物理存在性：`File.Exists(ConfigFilePath)`。
7. 正运动平台强制校验连接字符串：`ConnectionString` 不能为空。
8. 雷赛平台若衍生加速时间（$T_{acc} = \frac{V_{max}}{A_{max}}$）超过 10 秒，弹出过缓风险警告。

---

### 四、运动控制与工业安全机制

#### 1. 异步完成回报协议 (Async Done Protocol)
彻底废弃“下发指令后在 UI 轮询 `GetPosition`”的非标准做法，在 `IMotionController` 与 `AxisManager` 冻结如下 API：
```csharp
Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default);
Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default);
```
- **TCS 登记先行**：在底层向控制卡或仿真驱动下发位移命令**之前**，先在并发字典中登记 `TaskCompletionSource`。即使硬件由于断线、报错在 10 微秒内极速失败，结果也能可靠捕获，绝无订阅竞态。
- **StopMotion 失败语义**：减速停止时，主动以 `Success = false (原因: "减速停止")` 兑现任务，避免调用方无限挂起。
- **Token 委托即时释放**：挂接取消回调时，通过 `tcs.Task.ContinueWith(_ => reg.Dispose())` 在运动正常到达时立即注销委托，防止长周期连续运动导致的委托链内存泄露。

#### 2. 多轴插补 Fail-Fast 连锁急停机制
在多轴插补节点 `MultiAxisInterpNode` 中实施工业连锁保护：
- 并发启动协同轴时，创建联合取消令牌 `failFastCts`。
- 任意轴一旦最先返回 `Success == false`、触发硬限位或报错抛出异常，**立即自动触发 `failFastCts.Cancel()` 并对其余全部协同轴调用 `motion.Abort()` 实施物理急停**。
- 彻底解决“X 轴因干涉故障停下，Y 轴仍盲跑完整轨迹导致撞机”的行业顽疾。

#### 3. 独立报警实例与反向退离协议 (Transient Protocol)
- **按轴实例化报警**：报警代码按 `HARD_LIMIT_ESTOP#轴号`、`SOFT_LIMIT_ERROR#轴号` 独立登记。多轴同时越界互不覆盖，单轴复位绝不误消其他轴报警。
- **看门狗超时监控**：后台独立线程监视快照更新时间戳，若底层通信挂起超过 1000ms，触发 `WATCHDOG_TIMEOUT` 报警并联动停机。
- **反向退离协议**：
  - 轴触碰正向硬限位后，`GlobalLimitMonitor` 置位标志，禁止继续向正向运动。
  - 系统仅允许向负方向点动（Jog）反向脱离限位电平。
  - 调用 `TryResetLimit` 时，系统自动读取限位 DI 电平；只有电平完全离开后才允许复位并解除锁定。

---

### 五、机器视觉与视口引导对位开发规范

#### 1. 海康工业相机真实 SDK 集成 (`HikvisionFrameSource`)
- **反射动态装载**：动态探测并加载 `MvCamCtrl.NET.dll`，类名 `MvCamCtrl.NET.MyCamera`。开发机在未安装 MVS 客户端时可直接通过编译。
- **诚实失败原则**：若未检测到 MVS 驱动或无相机连接，明确暴露 `IsAvailable = false` 与异常日志，禁止假装成功。
- **现场优化参数**：
  - GigE 网口相机自动探测并下发 `GevSCPSPacketSize` 最优包大小。
  - 心跳包周期配置为 `GevHeartbeatTimeout = 3000ms`，兼顾断线感知与抗干扰。
  - 图像缓冲获取后严格采用原生内存锁屏复制，不产生每秒数百兆的托管堆 GC 垃圾。

#### 2. 标定与手眼纠偏算法核心
- **九点仿射标定 (`NinePointCalibration`)**：基于设计矩阵正规方程 $(A^T A) p = A^T Y$ 最小二乘解析解，输出 $2 \times 3$ 仿射矩阵。计算正向物理残差 (RMS & Max, mm) 与反向投影像素残差 (px)，残差超阈值拒绝标定。
- **已知角度旋转中心标定 (`RotationCenterCalibration`)**：针对工业小角度旋转（±10°~±20°）场景，采用已知运动角度闭式正交分解：
  $$(R(\Delta\theta) - I) C = R(\Delta\theta) P_i - P_j$$
  避免了无角度信息纯代数圆拟合在小圆弧上的极度病态发散。
- **手眼手性镜像补偿 (`OffsetCalculator`)**：自动判定仿射矩阵行列式符号：$\det(A) < 0$ 时识别为相机下视（Y向下）与平台坐标（Y向上）的手性反转，对旋转角度增量自动取反，确保对位纠偏角度不会越转越偏。

#### 3. 视觉引导点击对位与拖拽控制 (`VisualGuidanceAligner`)
- **点击对齐到准星 (Click-to-Align)**：
  - 操作员在 `VisionMonitorView` 实时取景画面上点击感兴趣特征。
  - 界面自动计算控件坐标至原始图像像素坐标 $(px, py)$ 的反变换。
  - 提取点击点相对十字准星中心 $(cx, cy)$ 的像素偏差 $(\Delta px, \Delta py)$。
  - 通过仿射矩阵一阶导数计算物理位移：
    $$\begin{bmatrix} \Delta X_{world} \\ \Delta Y_{world} \end{bmatrix} = \begin{bmatrix} a_{11} & a_{12} \\ a_{21} & a_{22} \end{bmatrix} \begin{bmatrix} \Delta px \\ \Delta py \end{bmatrix}$$
  - 支持 **随动相机 (Eye-in-Hand)** 与 **固定相机 (Eye-to-Hand)** 方向自动翻转。
  - 自动驱动 X/Y 轴并发以设定速度定位到位，使目标特征正中视口十字线。
- **拖拽微调 (Drag-to-Move)**：
  - 鼠标在视口中拖动超过 6 像素触发矢量平移模式。
  - 将拖拽像素矢量等比转化为机械平台物理微调矢量，实现所见即所得的微调对位。

---

### 六、气动执行机构与 RS485 外设集成规范

#### 1. 气缸与夹爪五重安全防护 (`CylinderService`)
1. **前置条件联锁**：动作前逐一核对 `EnableConditionDiNames`（如气压检测 `DI_AIR_OK`、安全门开闭信号），任一条件不满足坚决拒绝驱动。
2. **同组防碰撞互锁**：配置 `InterlockGroup`（如工站1内的多个干涉气缸）。若同组内已有气缸处于伸出（Work）位，其他气缸禁止执行伸出动作。
3. **双电控防烧线圈脉冲换向**：
   - 换向至 Work：先将 `HomeDo` 置 False（互锁切断），再将 `WorkDo` 置 True，维持设定脉冲宽度（默认 200ms）后立即将 `WorkDo` 恢复为 False。
   - 彻底杜绝双线圈同时通电及长期通电导致的线圈发热烧毁。
4. **双到位磁性开关确认**：下发动作后轮询目标到位传感器 DI；只有读到高电平时才判定动作成功。
5. **到位超时与自愈复位**：在 `ConfirmTimeoutMs`（默认 1500ms）内未感应到位，抛出超时报警；同时**自动清除组互锁占用**，防止因一次卡气导致整组机械死锁。

#### 2. RS485 / 232 工业外设集成 (`PeripheralDeviceService`)
1. **单物理端口多设备复用**：针对工业现场一根 RS485 屏蔽双绞线手拉手串接多台从站（如温控器、扭矩枪）的拓扑，系统按物理总线键（`PortName`）共享单一底层协议与串口句柄，杜绝重复打开引发的 `UnauthorizedAccessException: Access to port denied` 异常。
2. **总线互斥锁**：每个物理端口独享一个 `SemaphoreSlim(1, 1)`，所有读写指令严格排队，防止半双工 RS485 报文在物理层碰撞交织。
3. **动态站号切换**：在获取总线互斥锁后，动态切换底层驱动的 `SlaveAddress`，实现单串口无缝轮询不同站号的外设。
4. **工程量与字序编解码**：
   - 32位浮点数（Float32）自动组装连续 2 个寄存器，并根据 `SwapWords` 开关执行字序高低反转。
   - 自动应用工程量转换：$\text{Value} = \text{raw} \times Scale + Offset$。
5. **写入范围安全白名单**：写入操作强制受 `WriteMin` 与 `WriteMax` 校验保护，超出量程的指令直接被拦截，保护现场温控器与精密执行器。

---

### 七、真实 IO 输入输出配置表与气缸参数化绑定系统

针对工业机台电气接线差异（常开/常闭开关混用、PNP/NPN 极性反转、抖动误触）以及气缸与电磁阀的现场绑定需求，框架彻底告别假点名与硬编码，构建了工业标准的**三层 IO 映射体系**：

```
[硬件物理层] 控制卡卡号 (CardNo) + 物理通道位号 (ChannelBit: 0~31)
       ↓
[配置映射层] 极性反转 (Invert) + 常开/常闭 (NO/NC) + 滤波防抖 (FilterMs)
       ↓
[应用逻辑层] 工艺逻辑点名 (LogicalName: DI_AIR_PRESSURE_OK, DO_GRIP_OPEN)
       ↓
[机构消费者] 气缸/夹爪执行机构直接下拉绑定已声明的真实 DO/DI 点名
```

#### 1. 真实 IO 映射管理服务 (`IoMappingManager`)
- **物理映射隔离**：上层流程节点、限位监控和气缸驱动一律面向逻辑点名编程，由 `IoMappingManager` 动态解析为物理卡号与通道位。
- **电气极性与常开常闭 (NO/NC) 换算**：
  $$\text{LogicalState} = \text{RawState} \oplus \text{Invert} \oplus (\text{Switch} == \text{NormallyClose})$$
  安全急停与安全门等常闭(NC)开关在无动作导通时读取为 0，动作断开或断线时自动映射为 1（报警触发），保证电气接线无论更换为 NO 或 NC，业务逻辑代码零修改。
- **软件防抖滤波时间窗**：支持每个输入点独立设置 `FilterMs`（默认 20~50ms），连续稳定超过时限方确认电平翻转，杜绝电磁继电器和机械抖动毛刺。
- **点位持久化**：存储至 `SophonData/io_points.json`，出厂预置急停、安全门、总气压开关、夹爪电磁阀及双到位磁开等完整种子。

#### 2. 专业级 IO 监视、强制调试与气缸绑定总控台 (`IOInfrastructureView`)
- **TAB 1: 数字量输入 (DI) 实时监视与映射配置**：
  - 80ms 周期高频刷新指示灯（绿色=导通有效 / 灰色=断开）；
  - 自由编辑逻辑点名、物理卡号、通道位、常开/常闭、极性与滤波时间，支持增删改与一键落盘。
- **TAB 2: 数字量输出 (DO) 强制调试与映射配置**：
  - 实时输出指示灯（橙色=输出高电平 / 灰色=断开）；
  - 提供现场调试必备的 **「强制 ON」**、**「强制 OFF」** 与 **「点动脉冲 (200ms)」** 操作按钮，极大方便现场电气接线测试与电磁阀换向核对。
- **TAB 3: 气缸与执行机构绑定管理**：
  - 气缸的 **工作DO、复位DO、工作到位磁开DI、复位到位磁开DI 全部直接从已配置的真实点名表中下拉选择绑定**；
  - 彻底打通“控制卡 IO 硬件 $\to$ IO 点位映射 $\to$ 气缸动作执行 $\to$ 流程画布节点编排”的全链路闭环！

---

### 八、流程引擎 V2 与节点扩展清单

`FlowEngineV2` 基于异步单调度线程模型，通过 DAG 有向拓扑驱动执行。已内置 18 类工业流程节点，支持在 Nodify 画布上可视化拖拽编排：

| 节点类型标识 | 节点显示名称 | 核心应用场景与功能说明 |
|---|---|---|
| `Start` | 流程起始 | 流程入口点，全局唯一 |
| `Delay` | 延时等待 | 毫秒级异步等待，不阻塞调度线程 |
| `AxisMove` | 单轴绝对定位 | 下发 MoveAbsAsync，严格等待到位回报，超时/取消联动急停 |
| `AxisJog` | 单轴点动 | 点动调机，支持正负向与速度设定 |
| `AxisHome` | 单轴回零 | 支持限位、原点、Z相、当前位置定零，自动使能保护 |
| `AxisEnable` | 轴使能控制 | 独立上使能/下使能控制 |
| `MultiAxisInterp` | 多轴插补定位 | 多轴协同定位，**具备单轴故障 Fail-Fast 连锁急停保护** |
| `DiWait` | 等待输入信号 | 轮询等待外部传感器电平到位 |
| `DoSet` | 设置输出点 | 驱动电磁阀、指示灯等输出点高低电平 |
| `CylinderMove` | 气缸/夹爪控制 | 驱动气缸夹爪，包含五重安全防线（脉冲换向、互锁、到位确认） |
| `DeviceRead` | 外设点位读取 | 从 485/Modbus 仪表读取实时工程量，写回流程上下文变量 |
| `DeviceWrite` | 外设点位写入 | 向外设下发设定值，严格受寄存器地址与上下限白名单防护 |
| `VisionMeasure` | 机器视觉测量 | 触发相机采图与特征匹配，提取工件位置与旋转角度 |
| `Branch` | 条件分支 | 根据变量表达式或判断条件进行 True/False 路由 |
| `Loop` | 循环控制 | 支持基于次数或条件的循环，**已获静态环检测豁免放行** |
| `Jump` | 跳转节点 | 流程间跳转，**已获静态环检测豁免放行** |
| `Parallel` | 并行分支 | 触发多个分支并发执行，多分支共享上下文加锁安全保护 |
| `SubFlow` | 子流程调用 | 模块化复用嵌套流程 |

---

### 九、现场部署、依赖排查与故障诊断指南

#### 1. 软件环境要求
- **操作系统**：Windows 10 / Windows 11 专业版 / 企业版 64-bit（务必关闭系统睡眠与深度休眠）。
- **运行环境**：.NET 8.0 Desktop Runtime (x64)。
- **视觉驱动**：海康威视 MVS (Machine Vision Software) 客户端 v3.4 及以上（标准安装路径 `C:\Program Files (x86)\MVS\`）。
- **板卡驱动**：
  - 固高 GTS：安装固高驱动及配套 MCT2008，确保配置文件（如 `GTS800.cfg`）放置在应用程序根目录或指定路径。
  - 雷赛 DMC：安装雷赛 Motion 驱动程序，放行 USB/PCIe 驱动端口。
  - 正运动 ZMC：确保工控机与控制器处于同一千兆网段（建议静态 IP，如 `192.168.0.x`）。

#### 2. 常见现场故障速查表

| 故障现象 | 常见诱因 | 现场诊断与排查步骤 |
|---|---|---|
| **相机连接报错 0x80000203 (无权限访问)** | 相机句柄被占用或心跳未超时 | 1. 检查后台是否有未杀死的 MVS 客户端或旧进程；<br>2. 等待 3~5 秒待网络心跳超时释放；<br>3. 重新插拔相机网线或电源。 |
| **相机采图帧率低或偶发丢帧** | 网卡未开启巨帧或包大小不匹配 | 1. 进入 Windows 设备管理器，设置千兆网卡属性：开启 `Jumbo Frame (巨帧 9014 字节)`；<br>2. 关闭网卡节能模式；<br>3. 在相机配置中勾选 `OptimizePacketSize`。 |
| **RS485 通信报端口占用拒绝访问** | 多个设备各自独立打开相同 COM 口 | 框架已修复此问题。若现场新增通道，确保同物理端口的波特率、校验位完全一致，由系统统一总线调度。 |
| **点击 485 / 外设界面发生闪退 (已修复)** | XAML 中引用了不存在的 StaticResource (TabControlInLine) | 框架已将 TabControl 样式改为无依赖的原生透明无边框样式，并优化了 HomeView 卡片跳转与 Ellipse 属性，彻底解决闪退。 |
| **气缸报“到位超时报警”** | 气压不足、磁性开关松动或偏移 | 1. 观察现场气压表是否达到 0.4~0.6 MPa；<br>2. 手动推拉气缸，观察磁性开关指示灯是否亮起；<br>3. 在界面中核对 `WorkSensorDiName` 是否与图纸点位一致。 |
| **多轴插补报“连锁急停”** | 协同组中有某一轴触碰限位或报警 | 1. 查看实时报警中心，确认最先报故障的轴号及具体代码；<br>2. 检查该轴软限位配置是否过窄；<br>3. 使用反向退离协议将轴移出限位区后复位。 |
| **视觉纠偏越转越偏** | 相机与机构坐标系手性相反 | 框架已自动进行行列式 $\det(A) < 0$ 手性反转补偿；若依然偏离，请重新执行「标定向导」完成九点标定。 |

---

### 十、工业 HMI 登录流程、权限拦截与默认账户体系

针对工业现场人机界面的使用习惯，框架已彻底理清「登录窗口与主界面」的从属关系，并建立了全入口的**声明式权限守卫体系**：

#### 1. 启动流程标准化 (Startup Workflow)
- **开机直达系统主页 (`HomeView`)**：开机初始化完成后，主工作区直接呈现全景生产看板（运动控制卡状态、轴动力学概览、报警统计与快捷入口），**严禁开机把操作员强行关进全屏登录白皮页面**。
- **按需提权与无感返回**：操作人员在需要执行高权限操作时点击「登录/切换」，或由受限页面触发引导；登录成功后自动返回 `HomeView`，无需操作人员手动切回。

#### 2. 集中式双通道导航权限守卫 (`INavigationGuardService`)
- 无论是从左侧边栏导航菜单（`SideViewModel`），还是从系统看板卡片跳转快捷入口（`HomeViewModel`），所有导航请求**全部收拢至 `INavigationGuardService` 统一裁决**，杜绝旁路越权。
- **工业三级权限矩阵**：
  | 权限等级 | 对应角色 | 允许访问的视图清单 | 越权处理策略 |
  |---|---|---|---|
  | **Level 0 (None)** | 访客 / 未登录 | `HomeView` (系统主页), `UserView` (认证登录), `AlarmCenterView` (实时报警查看), `AlarmHistoryView` (历史报警), `StationView` (工站) | 拦截并弹出黄色警告提示所需角色，未登录时自动跳转登录页 |
  | **Level 1 (Operator)** | 产线操作员 | 在访客基础上解锁：`LimitMonitorView` (限位监控), `VisionMonitorView` (相机实时取景视口) | 拦截硬件调试与配置页面 |
  | **Level 2 (Engineer)** | 工艺工程师 | 在操作员基础上解锁：`AxisDebugView` (单/多轴调试), `TeachView` (点位示教), `FlowEditorView` (流程编辑), `DeviceControlView` (气动外设), `IOInfrastructureView` (IO映射), `VisionCalibrationView` (标定向导), `AlarmRegisterView` | 拦截底层控制卡档案与工艺核心参数 |
  | **Level 3 (Admin)** | 系统管理员 | 全系统全视图无限制放行：解锁 `MotionCardConfigView` (控制卡配置), `ParamView` (核心参数配置) | 全放行 |

- **安全主动驱逐机制 (Active View Eviction)**：
  当工程师或管理员点击「注销」退出登录后，`SideViewModel` 自动监听并检查当前主视区激活的页面；若当前停留页面超出了新身份（访客）的权限，**系统立即将工作区自动驱逐回 `HomeView` 并下发停机提示**，防止工程师走开后他人直接操作残留的硬件轴点动按钮。

#### 3. 出厂预置账户与一键快捷选择
数据库初始化器 (`DatabaseInitializer`) 自动检测并分别独立播种预置三级角色（旧数据库亦可无缝补齐）：
- **管理员 (Admin)**：账号 `管理员` / 默认密码 `123`
- **工程师 (Engineer)**：账号 `工程师` / 默认密码 `123`
- **操作员 (Operator)**：账号 `操作员` / 默认密码 `123`
在 `UserView.xaml` 界面顶部展示醒目的绿色提示卡片，并附带 **[管理员] [工程师] [操作员] 快捷填入按钮** 与 **「返回主页」** 按钮，免去现场查阅手册猜密码的烦恼。

---

### 十一、自动化测试与工程验证记录

截至交接时，全解决方案共 **220 项单元与集成测试** 保持 100% 全绿通过：
- **`Sophon.Motion.Tests`**：16/16 全部通过（S曲线动力学、梯形轮廓、多轴直线/圆弧插补精度）。
- **`Sophon.Vision.Tests`**：47/47 全部通过（坐标系树多跳拓扑查找、Transform2D 级联与逆变换、手性镜像纠错、九点标定、已知角度旋转中心求解、视口点击/拖拽对位计算）。
- **`Sophon.Core.Tests`**：107/107 全部通过（流程引擎 DAG 调度、迭代调度器消除递归、并行分支 Fork 与 Join 同步屏障、循环节点 100k 熔断丝、MotionCoordinator 轴组协同与仲裁、双电控气缸脉冲换向、Cylinder Auto-Heal 故障自愈、看门狗失联 Fail-Safe 断电保护、485 外设浮点字序反转与上下限拦截、按轴独立报警）。
- **`Sophon.Infrastructure.Tests`**：50/50 全部通过（MotionCapability 能力位标志、纯能力驱动校验与映射、控制卡配置持久化、IO映射极性反转与常开常闭、滤波防抖、导航权限守卫矩阵全角色拦截校验）。
- **编译状态**：全解决方案编译成功，0 错误。

---

*文档编制：小智运动控制框架主控架构代理*  
*交付时间：2026-09-21*  
*代码基线：E:\运动控制框架*
