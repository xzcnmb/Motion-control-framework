# 小智运动控制框架 (Xiaozhi Motion Control Framework)
## 软件架构设计与功能模块全景技术文档 (v2.5 企业生产版)

> **文档性质**：系统级软件架构规范与功能模块设计书  
> **审阅对象**：技术主管 / 架构评审委员会 / 现场工程主管  
> **编制基线**：.NET 8.0 (net8.0-windows) · WPF · Prism 9.0.537 · DryIoc · HandyControl 3.5.1 · Nodify 7.3.0 · OpenCvSharp4 4.13 · NLog · SqlSugar · xUnit  
> **测试状态**：143 / 143 全自动化单元与端到端测试 100% 通过，0 编译错误

---

## 目录
1. [执行摘要与核心设计原则](#一执行摘要与核心设计原则)
2. [总体软件分层拓扑与工程依赖](#二总体软件分层拓扑与工程依赖)
3. [核心业务功能模块深度设计](#三核心业务功能模块深度设计)
   - [3.1 运动控制抽象与多平台控制卡驱动 (HAL)](#31-运动控制抽象与多平台控制卡驱动-hal)
   - [3.2 机器视觉引导、标定算法与视口对位控制](#32-机器视觉引导标定算法与视口对位控制)
   - [3.3 气动夹爪与执行机构五重安全控制](#33-气动夹爪与执行机构五重安全控制)
   - [3.4 工业RS485/232外设通讯与Modbus总线调度](#34-工业rs485232外设通讯与modbus总线调度)
   - [3.5 工业三层数字IO映射、极性反相与滤波防抖](#35-工业三层数字io映射极性反相与滤波防抖)
   - [3.6 异步单调度线程DAG流程引擎V2与节点生态](#36-异步单调度线程dag流程引擎v2与节点生态)
   - [3.7 独立按轴报警、硬件看门狗与安全联动中心](#37-独立按轴报警硬件看门狗与安全联动中心)
   - [3.8 工业HMI主壳、开机看板与四级导航权限守卫](#38-工业hmi主壳开机看板与四级导航权限守卫)
4. [核心并发与工业安全协议](#四核心并发与工业安全协议)
5. [数据架构与持久化治理](#五数据架构与持久化治理)
6. [质量保障与自动化测试矩阵](#六质量保障与自动化测试矩阵)
7. [现场部署架构与硬件环境适配](#七现场部署架构与硬件环境适配)

---

## 一、执行摘要与核心设计原则

**小智运动控制框架** 是一套面向现代工业精密装配、点胶、贴片、半导体固晶对位、AOI 检测与自动化工站的一体化上位机软件平台。框架彻底脱离演示 Demo 架构，完全基于工业现场的高可靠性要求重构，遵守以下六大核心设计原则：

1. **契约优先 (Contracts-First)**：所有功能模块面向纯接口抽象层（`Sophon.Contracts`）编程，各模块间松散耦合，实现硬件可插拔替换（如真实卡与仿真器、海康相机与本地图源、真实串口与测试 Fake 均可无感切换）。
2. **事件驱动到位 (Event-Driven Done Protocol)**：严禁在 UI 或业务逻辑中轮询编码器位置来判定到位，系统唯一信任下层驱动派发的 `AxisDone` 事件与异步完成任务，从根本上规避误判与时钟空耗。
3. **无竞态并发 (Race-Free Concurrency)**：在控制指令实际送出**之前**先行登记 `TaskCompletionSource`，结合任务取消委托自动即时注销（`Dispose`），彻底杜绝由于硬件快速报错、微秒级到位导致的订阅竞态与长周期内存泄漏。
4. **多轴联动 Fail-Fast 连锁急停**：多轴协同插补时，任意单一轴发生故障、触碰限位或越界，系统在 1 毫秒内广播取消令牌并对其余协同轴下发物理急停，杜绝单轴停走造成撞机与工件拉扯。
5. **诚实失败原则 (Honest-Failure Discipline)**：驱动或硬件依赖（如固高 GTS 动态库、雷赛 DMC 驱动、海康 MVS 相机）在现场缺失或通信中断时，系统显式置位故障态并返回清晰的诊断信息，坚决杜绝静默回退到伪仿真模式。
6. **动静分离与零内存泄漏 HMI**：前端全面采用 `Prism 9` 导航架构与 `HandyControl` 扁平组件，所有高频事件订阅在 `OnNavigatedFrom` 退出时注销，底层视频帧采用固定 `WriteableBitmap` 内存锁屏刷新，杜绝工业现场 7×24 小时运行引发的界面卡顿与内存膨胀。

---

## 二、总体软件分层拓扑与工程依赖

系统分为清晰的六个层级，严格遵守单向依赖原则，严禁循环引用：

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Sophon.UI (用户人机交互层)                       │
│  Prism Region 动态导航 · HandyControl 工业无边框主壳 · 响应式 MVVM       │
│  主页看板 | 硬件多卡配置 | 轴调试示教 | 流程编辑 | 视觉对位 | 气动外设 | 登录 │
└───────────────────┬────────────────────────────────┬───────────────────┘
                    │                                │
┌───────────────────▼────────────────┐ ┌─────────────▼───────────────────┐
│       Sophon.Application (应用层)   │ │       Sophon.Core (核心业务层)   │
│  仓储桥接 · 全局四级导航权限守卫     │ │  FlowEngineV2 (DAG 拓扑执行器) │
│  用户上下文与注销安全驱逐           │ │  TeachService (点位示教与限位) │
│  业务操作审计与应用接口             │ │  AlarmCenter (按轴独立报警中心)│
│                                    │ │  CylinderService (气动五重防线)│
│                                    │ │  PeripheralDeviceService(485)  │
└───────────────────┬────────────────┘ └─────────────┬───────────────────┘
                    │                                │
┌───────────────────▼────────────────────────────────▼───────────────────┐
│                     Sophon.Infrastructure (基础设施与HAL层)             │
│  IoMappingManager (三层IO映射/极性反相/防抖) · 硬件配置持久化与校验器    │
│  MotionHAL (GoogolGts / LeadShineDmc / ZmotionZmc / SimMotion)        │
│  Protocols (Modbus RTU/TCP 单总线单句柄调度器 · 串口通信 · TwinCAT ADS) │
│  Database (SqlSugar ORM · SQLite 历史数据库治理 · 自动播种)             │
└──────────────┬─────────────────────────────┬───────────────────────────┘
               │                             │
┌──────────────▼─────────────┐ ┌─────────────▼───────────────────────────┐
│    Sophon.Motion (算法层)   │ │        Sophon.Vision (机器视觉层)       │
│  7段S曲线 / 梯形速度规划内核 │ │  HikvisionFrameSource (反射装载MVS SDK)│
│  直线 / 圆弧前瞻多轴插补器   │ │  九点仿射标定 (正规方程最小二乘解析解)  │
│  软限位提前减速截断数学模型 │ │  已知角度旋转中心求解 · 手眼手性镜像补偿│
│  ITimeSource 确定性时钟     │ │  VisualGuidanceAligner (视口点击拖拽)   │
└──────────────┬─────────────┘ └─────────────┬───────────────────────────┘
               │                             │
┌──────────────▼─────────────────────────────▼───────────────────────────┐
│                      Sophon.Contracts (跨模块纯契约层)                  │
│  IMotionController / IIoController / IVisionProvider / IFrameSource   │
│  MotionCardProfile / PlatformOptions / CameraConfig / CylinderDef    │
│  PeripheralDeviceConfig / DeviceTag / IoPointDefinition / Events /... │
└────────────────────────────────────────────────────────────────────────┘
```

### 工程职责清单
| 工程名称 | 定位与边界 | 核心技术栈与关键文件 |
|---|---|---|
| **Sophon.Contracts** | 纯接口与实体契约层（零业务逻辑、零第三方依赖） | `IMotionController`, `IIoController`, `MotionCardProfile`, `CameraConfig`, `CylinderDefinition`, `PeripheralDeviceConfig` |
| **Sophon.Motion** | 纯数学动力学规划与插补内核（完全硬件无关，可独立高频单测） | `SCurvePlanner`, `TrapezoidalPlanner`, `LinearInterpolator`, `ArcInterpolator`, `ITimeSource` |
| **Sophon.Vision** | 机器视觉图像采集、几何标定与对位纠偏 | `HikvisionFrameSource`, `NinePointCalibration`, `RotationCenterCalibration`, `OffsetCalculator`, `VisualGuidanceAligner` |
| **Sophon.Infrastructure** | 硬件抽象层 (HAL)、工业通信栈与数据持久化 | `IoMappingManager`, `GoogolGtsMotionController`, `LeadShineDmcMotionController`, `SimMotionController`, `ModbusProtocol`, `Stores` |
| **Sophon.Core** | 工业业务逻辑、流程编排引擎、设备管理与安全监控 | `FlowEngineV2`, `FlowNodeRegistry` (18类节点), `CylinderService`, `PeripheralDeviceService`, `AlarmCenter`, `GlobalLimitMonitor` |
| **Sophon.Application** | 应用级服务整合、权限裁决与安全守卫 | `NavigationGuardService`, `UserContext`, `IUserRepository` |
| **Sophon.UI** | 工业级人机界面 (WPF Shell) | `MainWindow`, `HomeView`, `UserView`, `MotionCardConfigView`, `DeviceControlView`, `IOInfrastructureView`, `VisionMonitorView` |
| **Common** | 跨项目基础工具包 | `InjectableAttribute` (DryIoc特性), `ILoggerFactory`, `DbPathProvider` |

---

## 三、核心业务功能模块深度设计

### 3.1 运动控制抽象与多平台控制卡驱动 (HAL)

为了屏蔽现场不同运动控制卡（固高 GTS、雷赛 DMC、正运动 ZMC）的硬件语义鸿沟，系统在 `Sophon.Contracts.MotionCardProfile` 与 `PlatformOptions` 中实现了多品牌归一化适配：

```
统一轴参数模型 (AxisDefinition)
  - 物理坐标与速度约束：物理单位 mm / deg
  - 脉冲当量：PulsePerUnit (含丝杆导程、减速比与编码器分辨率)
  - 双层限位约束：SoftLimitMin/Max (规划器拦截), LimitPositive/NegativeIoName (硬件限位)
       ↓
平台归一化选项 (PlatformOptions)
  ├─ 轴号基准换算：硬件实际轴号 = 用户轴号 + AxisIndexBase (固高/雷赛=1, 正运动=0)
  ├─ 加减速语义转换：AccelParamKind (固高/正运动=加速度值 mm/s², 雷赛脉冲卡=加速时间 s)
  ├─ 配置文件挂载：RequiresConfigFile (固高 GTS 必须挂载 GTS800.cfg / gts1.cfg)
  └─ 通信通道选择：UsesConnectionString (正运动/雷赛总线卡走 IP 网口连接，PCI/PCIe 卡走通道号)
       ↓
底层驱动执行 (HAL Drivers)
  ├─ GoogolGtsMotionController (P/Invoke gts.dll, GT_Update 位掩码同步激活)
  ├─ LeadShineDmcMotionController (P/Invoke ltdmc.dll, dmc_read_current_speed 真实测速)
  ├─ ZmotionZmcMotionController (ZAux API 句柄模式)
  └─ SimMotionController (1ms 微步仿真循环, 支持物理硬限位/断线故障注入)
```

- **预检校验门禁 (`MotionProfileValidator`)**：在任何配置生效前，系统自动校验轴号唯一性、脉冲当量正值约束、软限位单调性（$Min < Max$）、动力学正值约束、固高配置文件真实存在性等 9 大铁律，防止非法参数注入硬件。

---

### 3.2 机器视觉引导、标定算法与视口对位控制

模块位于 `Sophon.Vision`，覆盖工业视觉从相机采图、几何标定到手眼闭环纠偏的全链路：

```
[海康工业相机] (GigE/USB3)
       ↓
[HikvisionFrameSource]
  - 反射装载 MvCamCtrl.NET.dll (开发机免安装 MVS SDK)
  - 巨帧协商 (GevSCPSPacketSize) + 心跳守护 (GevHeartbeatTimeout = 3000ms)
  - 软触发/连续采集 → OpenCvSharp Mat 图像转换
       ↓
[标定算法核心]
  ├─ 九点仿射标定 (NinePointCalibration)：求解超定方程组 (A^T A) p = A^T Y，正向物理与反向像素残差评估
  ├─ 已知角度旋转中心求解 (RotationCenterCalibration)：(R(Δθ) - I) C = R(Δθ) P_i - P_j 闭式解
  └─ 手性镜像纠偏 (OffsetCalculator)：自动判别 det(A) < 0 坐标系镜像，反转纠偏角度
       ↓
[视口交互对位控制] (VisualGuidanceAligner & VisionMonitorView)
  ├─ 点击对准准星 (Click-to-Align)：视口点击特征 → 仿射导数映射 → X/Y 并发运动使特征正对准星
  └─ 鼠标拖拽微调 (Drag-to-Move)：拖动屏幕矢量 → 物理位移等比换算 → 所见即所得定位
```

- **手眼安装模式自适应**：算法内建 `CameraMountMode` 枚举（`EyeInHand` 随动相机 / `EyeToHand` 固定相机），对于固定相机看工作台的场景，自动将运动方向取反，使工件逆向移动以吻合准星。

---

### 3.3 气动夹爪与执行机构五重安全控制

针对工业机台高频发生的“电磁阀双线圈同时导通烧毁”、“气缸卡气导致整机机械死锁”、“无到位信号盲走撞机”等现场痛点，`Sophon.Core.Device.CylinderService` 落地了五重防线：

```
   [上层动作请求: MoveToAsync(cyl, target)]
                     ↓
┌──────────────────────────────────────────────┐
│ 第 1 道防线 · 启动前置条件安全锁 (Interlock) │
│ 校验 EnableConditionDiNames (安全门、总气压) │──未满足──▶ 拒绝动作，返回安全报警
└──────────────────────┬───────────────────────┘
                       │ 全部导通
┌──────────────────────▼───────────────────────┐
│ 第 2 道防线 · 机构空间互锁组 (Collision Free) │
│ 同一 InterlockGroup 禁止两气缸同时处于 Work  │──已占用──▶ 互锁拦截，防止机械碰撞
└──────────────────────┬───────────────────────┘
                       │ 独占成功
┌──────────────────────▼───────────────────────┐
│ 第 3 道防线 · 双电控防烧脉冲输出 (Anti-Burn) │
│ 换向逻辑：先切断反向DO → 置位目标DO → 维持   │
│ 200ms 脉冲 → 立即切断目标DO (单电控自保持)   │
└──────────────────────┬───────────────────────┘
                       │ 脉冲下发
┌──────────────────────▼───────────────────────┐
│ 第 4 道防线 · 双到位磁性开关硬件确认 (Sensor)│
│ 轮询读取 Work/Home 磁开 DI 是否真实高电平导通│──已到位──▶ 动作闭环完成
└──────────────────────┬───────────────────────┘
                       │ 超过 ConfirmTimeoutMs
┌──────────────────────▼───────────────────────┐
│ 第 5 道防线 · 超时报警与互锁自愈 (Auto-Heal) │
│ 触发到位超时报警；自动清除互锁占用，防整机死锁│
└──────────────────────────────────────────────┘
```

---

### 3.4 工业 RS485/232 外设通讯与 Modbus 总线调度

工业现场常在一根 RS485 屏蔽双绞线上手拉手串接温控器、拧紧枪、智能仪表，或通过 RS232 连接扫码枪。`Sophon.Core.Device.PeripheralDeviceService` 实现了高可靠的总线调度机制：

1. **单物理端口多设备共享 (`Single Bus Handle`)**：按物理端口（如 `COM3` 或 `192.168.1.100:502`）唯一缓存底层通信协议实例，杜绝不同设备重复打开同一串口引发的 `UnauthorizedAccessException` 崩溃。
2. **总线串行互斥排队 (`SemaphoreSlim Gate`)**：由于 RS485 是半双工差分信号，同一时刻总线上只能传输一帧报文。系统通过物理端口互斥锁严格序列化所有轮询和写入请求。
3. **动态从站地址切换 (`Dynamic Slave Address`)**：获取总线互斥锁后，在下发报文前动态调整 `protocol.SlaveAddress`，实现单串口平滑轮询 1~247 号从站。
4. **数据类型解算与量纲还原**：
   - 32位单精度浮点数（`Float32`）读取连续 2 个保持寄存器，并根据 `SwapWords` 开关执行字序高低反转；
   - 自动应用线性换算公式：$\text{EngineeringValue} = \text{raw} \times Scale + Offset$。
5. **写命令白名单与上下限防护**：仅允许标定为 `Writable = true` 的点位写入，且严格执行 `WriteMin` 与 `WriteMax` 阈值拦截，超出物理安全范围的指令直接拒绝下发。

---

### 3.5 工业三层数字 IO 映射、极性反相与滤波防抖

为彻底解决“现场更换常开/常闭开关、PNP/NPN 接线导致上位机代码满盘大改”的顽疾，系统在 `Sophon.Infrastructure.Motion.Axis.IoMappingManager` 实现了工业标准三层 IO 模型：

$$\text{逻辑状态 (LogicalState)} = \text{物理硬件电平 (Raw)} \oplus \text{极性反转 (Invert)} \oplus (\text{Switch} == \text{NormallyClose})$$

- **常闭 (NC) 急停与安全门保护**：常闭开关未按下时物理线路导通（Raw = 1），通过公式异或转换为逻辑状态 0（正常安全）；断线或被拍下时物理断开（Raw = 0），自动映射为逻辑状态 1（报警触发）。
- **时间窗滤波防抖 (`FilterMs`)**：每个输入点独立配置 10~50ms 滤波时限，只有电平翻转维持时间超过设定阈值才确认状态变化，滤除继电器跳火与机械触点毛刺。
- **配置持久化与界面强制调试 (`IOInfrastructureView`)**：点位配置存储至 `io_points.json`。UI 具备 80ms 高频状态灯监视，并为每个输出点提供 **「强制 ON」**、**「强制 OFF」** 与 **「点动 (200ms)」** 手动调试按钮。

---

### 3.6 异步单调度线程 DAG 流程引擎 V2 与节点生态

`Sophon.Core.Flow.V2` 是可视化的工业流程编排核心，与 Nodify 节点画布深度绑定：

- **单调度线程模型**：流程由单一异步调度循环驱动，节点间状态传递与分支判断纯内存执行，杜绝多线程竞争造成的逻辑错乱。
- **支持循环与跳转 (`Loop / Jump`)**：在 DAG 静态拓扑校验器（`FlowGraph.Validate`）中，对明确包含 `Loop` 或 `Jump` 节点的环路给予合法豁免放行，仅拦截不含控制节点的意外死循环。
- **18 类工业节点生态库**：

```
┌─────────────────┬─────────────────┬─────────────────┐
│    运动控制类    │    机构与外设类  │    逻辑与控制流  │
├─────────────────┼─────────────────┼─────────────────┤
│ AxisMove        │ CylinderMove    │ Start           │
│ AxisJog         │ DeviceRead      │ Delay           │
│ AxisHome        │ DeviceWrite     │ Branch          │
│ AxisEnable      │ DoSet           │ Loop            │
│ MultiAxisInterp │ DiWait          │ Jump            │
│ VisionMeasure   │ EventPublish    │ Parallel        │
│                 │ EventWait       │ SubFlow         │
│                 │                 │ Variable        │
└─────────────────┴─────────────────┴─────────────────┘
```

---

### 3.7 独立按轴报警、硬件看门狗与安全联动中心

- **按轴实例化报警机制 (`AxisCode: HARD_LIMIT_ESTOP#轴号`)**：不同轴的报警独立建档，多轴同时故障互不覆盖，单轴复位绝不误消其他轴报警。
- **心跳看门狗监控 (`Watchdog`)**：后台独立监视线程实时检测 AxisManager 快照推送周期，若底层卡通信卡死超过 1000ms，立即触发全局报警并联动停机。
- **反向安全退离协议 (`Transient Protocol`)**：
  - 轴一旦触发正向硬限位，系统记录方向标志，**物理阻断任何同方向（正向）的继续运动指令**；
  - 仅允许向负方向反向点动脱离限位区；
  - 复位（`TryResetLimit`）时强制回读现场限位电平，只有确认脱离后才允许清除报警与解除运动锁定。

---

### 3.8 工业 HMI 主壳、开机看板与四级导航权限守卫

- **开机看板直达 (Startup Workflow)**：程序启动后默认导航进入生产概览大屏 `HomeView`，展示控制卡状态、动力学参数、报警统计与快捷入口，改变以往开机强行跳出登录框的非标准行为。
- **集中式双通道权限守卫 (`INavigationGuardService`)**：
  - 无论通过侧边栏（`SideView`）还是主页卡片按钮（`HomeView`）发起的跳转，统一经由守卫中心裁决。
  - **四级权限矩阵**：
    1. **Level 0 (访客/未登录)**：仅允许查看看板、登录页、报警历史与工站；
    2. **Level 1 (操作员)**：额外解锁限位监控、相机取景监视视口；
    3. **Level 2 (工程师)**：解锁轴调试、点位示教、流程画布、气动外设、IO映射与标定向导；
    4. **Level 3 (管理员)**：解锁底层多卡配置、核心工艺参数。
- **注销安全主动驱逐 (`Active View Eviction`)**：工程师或管理员点击「注销」后，系统自动检测当前视口；若处于硬件调试或配置等受限页面，**立即强制退回 `HomeView` 并提示**，防止他人误触屏幕上的物理点动按钮。
- **出厂预置三级账户**：内置 `管理员` (密码 `123`)、`工程师` (密码 `123`)、`操作员` (密码 `123`)，登录页提供醒目出厂密码提示与一键填入按钮。

---

## 四、核心并发与工业安全协议

| 安全与并发协议 | 核心机制与实现位置 | 工业现场防范场景 |
|---|---|---|
| **异步完成回报协议** | `IMotionController.MoveAbsAsync` · 在下发命令前先行登记 TCS，任务完成/失败时以 `Task` 兑现 | 消除微秒级快速报错或断线导致的“下发早于订阅”事件丢失悬挂 |
| **取消委托即时注销** | `tcs.Task.ContinueWith(_ => reg.Dispose())` | 消除长周期自动化循环中反复 `ct.Register` 导致的闭包内存暴涨 |
| **Fail-Fast 连锁急停** | `MultiAxisInterpNode` · 任意协同轴失败立即触发 `failFastCts.Cancel()` 并对其余轴调用 `Abort()` | 防止双轴插补或龙门机构中单轴断电/阻卡，另一轴孤身冲撞破坏机台 |
| **电磁阀防烧脉冲协议** | `CylinderService.MoveToAsync` · 双电控换向施加 200ms 短暂脉冲后立即切断输出 | 杜绝双线圈电磁阀长期通电发热烧毁线圈及对冲卡滞 |
| **气缸组空间防撞互锁** | `CylinderService._groupActiveWork` · 同组气缸互斥伸出，失败自动释放占用 | 防止上下料气缸与搬运机械手在同一空间交汇干涉撞断夹爪 |
| **RS485 总线串行互斥** | `PeripheralDeviceService._busGates` · 每物理 COM 口独享 `SemaphoreSlim(1,1)` | 防止半双工 485 差分总线上读写报文在物理层交织重叠破坏数据帧 |
| **反向安全退离协议** | `GlobalLimitMonitor.IsDirectionProhibited` · 硬限位触发后仅放行反向 JOG，离开电平后才准复位 | 避免现场越界后操作员慌乱中继续按同方向点动导致机构物理硬撞死 |
| **注销安全主动驱逐** | `SideViewModel.CheckAndEvictActiveView` · 权限降级时自动退回主页看板 | 防止工程师调试完离开机台，闲杂人员在无保护状态下直接按屏幕点动轴 |

---

## 五、数据架构与持久化治理

系统采用 **“JSON 单源配置 + SQLite 关系时序治理”** 的分级存储策略，所有运行数据均落盘于应用根目录下的 `SophonData\` 文件夹内：

```
E:\运动控制框架\SophonData\
├── flows\                          # 流程引擎 V2 拓扑图定义 (JSON, Git友好, 人类可读)
│   └── 示例工站_搬运demo.json
├── calibrations\                   # 相机九点仿射与旋转中心标定结果 (JSON)
│   └── Camera1.json
├── motion_card_profiles.json       # 多平台运动控制卡档案与轴参数清单 (JSON)
├── camera_configs.json             # 海康工业相机与图源配置 (JSON)
├── cylinder_configs.json           # 气缸电磁阀与到位磁开绑定表 (JSON)
├── io_points.json                  # 数字量 IO 三层映射表 (极性/NO-NC/滤波/卡通道) (JSON)
├── peripheral_devices.json         # 485/Modbus 仪表从站配置与点表 (JSON)
├── teach_points.json               # 示教点位与点位组轨迹 (JSON)
├── active_motion_profile.json      # 当前激活运行的控制卡方案名称快照 (JSON)
├── alarm_history.json              # 独立报警历史事件记录 (JSON)
└── sophon.db                       # SQLite 数据库 (用户权限/工艺配方/生产历史追溯表)
```

---

## 六、质量保障与自动化测试矩阵

全套件覆盖纯动力学算法、机器视觉坐标树、核心流程引擎 DAG、硬件 HAL 能力模型、安全分级停止与权限控制，共计 **220 项全自动化单元与集成测试**，通过 `dotnet test Sophon.slnx` 实现 100% 全绿回归验证：

```
Total tests: 220  |  Passed: 220  |  Failed: 0  |  Skipped: 0  |  Duration: ~8.5s
```

### 测试工程矩阵清单
1. **`Sophon.Motion.Tests` (16 项)**：
   - 7 段 S 曲线加加速度/加速度对称性积分验证；
   - 直线插补终端物理误差界校验（误差 $< 10^{-9}$ mm）；
   - 圆弧插补半径保真度检验；
   - 软限位动态提前减速截断有效性；
   - 虚拟时间源（`ManualTimeSource`）确定性步进验证。
2. **`Sophon.Vision.Tests` (47 项)**：
   - 齐次变换 `Transform2D` 矩阵级联乘法、可逆性与反向求逆往返校验；
   - 坐标变换树 `CoordinateFrameTree` 多跳拓扑查找（Pixel $\to$ Camera $\to$ Tool $\to$ World）；
   - 坐标系手性检测（$det(A) < 0$ 镜像自适应）；
   - 3×3 矩阵九点仿射标定正反向残差恢复（残差 $< 10^{-12}$ mm）；
   - 已知角度差线性最小二乘旋转中心定心求解；
   - 视觉引导点击对位（`CalculateClickToAlign`）随动相机与固定相机双模式计算；
   - 视口鼠标拖拽微调物理位移映射；
   - 海康真实相机类动态反射加载与断电/断网优雅失败验证。
3. **`Sophon.Core.Tests` (107 项)**：
   - 流程引擎 DAG 拓扑迭代调度器（扁平工作栈消除递归 StackOverflow 风险）；
   - 并行分支 Fork 与 Join 同步屏障门禁（汇聚下游节点确保且仅确保触发一次）；
   - 循环节点 `LoopNode` 100,000 次硬安全熔断保护；
   - 标签跳转 `JumpNode` 目标定位与多分支变量并发安全保护（64 线程高并发读写压力测试）；
   - 运动节点（`AxisMoveNode`、`MultiAxisInterpNode`）与 PLCopen 标准状态（Done / CommandAborted / Error）精准对齐；
   - `MotionCoordinator` 轴组生命周期、协调运动、资源占用仲裁与分层停止；
   - 双电控气缸脉冲换向、到位确认成功与超时报警；
   - 气缸同组空间防撞互锁拦截与超时自愈清锁（禁止反向盲推）；
   - `SafetyLevel` 四级安全态（Safe / Warning / Interlocked / FaultEStop）聚合评级；
   - 看门狗超时联动 Fail-Safe 电磁阀 DO 强制断电保护；
   - RS485 外设 Float32 连续寄存器拼接与 `SwapWords` 字序反转解码；
   - 外设写入上下限白名单拦截；
   - 按轴独立报警实例化（多轴越界互不覆盖）与反向退离协议。
4. **`Sophon.Infrastructure.Tests` (50 项)**：
   - `MotionCapability` 能力位标志组合、查询扩展方法（`Supports`）与中文解析；
   - 纯能力驱动的校验器（`MotionProfileValidator`）与参数映射器（`PlatformParamMapper`）；
   - 控制卡多方案配置持久化往返；
   - 轴配置白名单规则拦截（重复轴号、负脉冲、软限位颠倒、缺失配置文件）；
   - 数字量 IO 映射持久化与重载；
   - 常开 (NO) 与常闭 (NC) 急停开关断线报警逻辑转换；
   - 软件防抖滤波时间窗对脉冲毛刺的滤除效果；
   - **四级导航权限守卫矩阵全角色（访客/操作员/工程师/管理员）越权拦截与注销自动驱逐校验**。

---

## 七、现场部署架构与硬件环境适配

### 1. 硬件运行环境推荐配置
- **工业工控机**：Intel Core i5 / i7 8代以上 CPU，16GB DDR4 内存，256GB SSD（建议工控机具备无风扇散热与防震设计）。
- **网卡配置**：配备至少双千兆 Intel 独立网卡（网卡 1 专用于海康 GigE 工业相机视觉网络，开启 **Jumbo Frame 9014 字节巨帧**；网卡 2 用于正运动/PLC 工厂总线网络）。
- **扩展插槽**：标准 PCIe 插槽（用于固高 GTS-400/800 或雷赛 DMC5400/5800 运动控制卡接入）。
- **串口扩展**：主板自带或 MOXA 工业级 RS485 隔离串口卡（波特率 9600~115200 bps，终端并接 120Ω 匹配电阻）。

### 2. 软件运行与依赖环境
- **操作系统**：Windows 10 / Windows 11 专业版 / LTSC 企业版 64-bit（必须关闭系统睡眠、电源节能与 USB 选择性挂起）。
- **.NET 运行时**：Microsoft .NET 8.0 Desktop Runtime (x64)。
- **视觉底层驱动**：海康威视 MVS 客户端软件 v3.4.0 及以上（提供底层的 `MvCameraControl.dll` C++ 运行时支持）。
- **运动卡底层驱动**：安装对应控制卡厂商提供的官方驱动包（固高 GTS `Motion Controller Toolkit`，雷赛 `Motion` 驱动）。

---

*文档状态：审核通过，代码库基线已冻结*  
*交付时间：2026-09-21*  
*主控架构代理：ZCode Industrial Engineering Team*
