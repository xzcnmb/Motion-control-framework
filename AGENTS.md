# AGENTS.md

面向本仓库的编码代理。人类入门说明见 `README.md` 与 `docs/`。安全相关改动必须同步 `docs/SAFETY.md` 并重跑对应测试。

## 项目

工业运动控制上位机：**.NET 8 (`net8.0-windows`) + WPF + Prism 9 + DryIoc**。仿真（Sim）可离线跑通全链路；固高 GTS / 雷赛 DMC 为 P/Invoke 骨架，**未经真机验证**。解决方案文件是 `Sophon.slnx`（不是 `.sln`）。无 CI、无 lockfile、无统一 lint/format 配置。

## 命令

```bash
dotnet build Sophon.slnx
dotnet test Sophon.slnx
dotnet test tests/Sophon.Core.Tests/Sophon.Core.Tests.csproj --filter FullyQualifiedName~WorkStationLifecycleTests
dotnet run --project Sophon.UI
```

无独立 lint / typecheck 脚本。包管理是 SDK 风格 `PackageReference` + `dotnet restore`（`dotnet build` 会带上）。测试框架 xUnit 2.9。文档里的用例数量互相矛盾（109 / 143 / 220），以 `dotnet test` 实际输出为准。

## 分层（单向依赖，禁止循环引用）

```
Sophon.UI            WPF 壳 / 导航 / Nodify 流程编辑器
Sophon.Application   仓储桥接、NavigationGuard、应用服务
Sophon.Core          流程引擎、工站、报警、示教、气缸/外设业务
Sophon.Infrastructure HAL、协议、SQLite、配置 Store
Sophon.Vision        视觉提供者 / 标定 / 纠偏（可被 UI 与测试引用，不要被 Core 反向依赖）
Sophon.Motion        纯算法（规划/插补/前瞻/软限位），硬件无关
Sophon.Contracts     纯契约，零第三方包
Common               日志 / 路径 / InjectableAttribute / 旧配置加载
SophonData/          仓库内配方与示例；运行时写入见下方路径陷阱
tests/               四个 xUnit 工程，无 UI 测试工程
```

业务代码只依赖 `Sophon.Contracts` 的 `IMotionController` / `IIoController` / `IVisionProvider` / `ITimeSource`。新驱动、新相机、新 IO 实现都经工厂替换，不要在 Core/UI 里写卡号或厂商 API。

`Sophon.Motion` 只引用 Contracts。不要把 HAL、WPF、NLog、SqlSugar 拉进去。

## 两套 HAL，不要混用

| 栈 | 位置 | 接口 | 用途 |
|---|---|---|---|
| **新 HAL（当前 UI 走这条）** | `Sophon.Infrastructure/Motion/` | `Sophon.Contracts.IMotionController`、`Sophon.Contracts.IIoController`（**虚拟点名**） | Sim / GTS / DMC 适配器、`AxisManager`、`IoMappingManager` |
| **旧 Card 栈** | `Sophon.Infrastructure/Card/` | `Sophon.Infrastructure.IAxisController`、`Sophon.Infrastructure.IIoController`（**cardNo + 位号**） | `ICardFactory` + AppSettings `CardBrand`；`InfrastructureModuleRegister` 仍会注册 |

两个 `IIoController` 同名不同命名空间，签名完全不同。新代码用 Contracts 那个。不要把旧 `SetOut(cardNo, ioNo)` 接到流程节点或限位监视上。

`App.xaml.cs` 注册顺序：`RegisterCommon` → `RegisterInfrastructure`（旧栈）→ `RegisterCore` → `RegisterApplication` → `RegisterShellUi`（**覆盖为新 HAL，且写死 `DriverKind.Simulated`**）→ 报警/视觉 UI。真卡切换点在 `Sophon.UI/ShellUiModuleRegister.cs`，现场必须 `allowSimFallback: false`。

`DriverKind.ZmotionZmc` 在枚举和 UI 文案里存在，**`MotionControllerFactory` 会 `NotSupportedException`，仓库里没有 ZMC 适配器**。不要假装已接入。

## 运动与安全铁律

1. **到位只认事件**：用 `MoveAbsAsync` / `HomeAsync`（TCS 必须在下发命令**之前**登记）。禁止轮询 `GetPosition` 判断到位。同步 `MoveAbs` 返回的是 `requestId`，完成靠 `AxisDone`。
2. **单位**：接口一律 mm/deg。`PulsePerUnit` 只在驱动适配器内换算。
3. **停止分级**：`Halt`（Cat2 平滑停）/ `Stop`·`StopMotion`（Cat1 受控停）/ `EmergencyStop`·`Abort`（急停）。急停按钮必须硬接线，不得依赖上位机。
4. **不静默回退 Sim**：`MotionControllerFactory.Create(..., allowSimFallback: false)` 是现场默认。UI 当前对 Sim 传了 `true`，切真卡时改掉。
5. **插补**：真卡整段下发/PVT；上位 1ms 软插补仅限 Sim（Windows 非实时）。
6. **多轴 Fail-Fast**：`MultiAxisInterpNode` 任一轴 `Success==false` 必须 `failFastCts.Cancel()` + 其余轴 `Abort`。不要改成“等所有轴自己停”。
7. **等待类节点必须有超时**（`DiWait` / `EventWait` / 运动到位）。节点内禁止无限阻塞。
8. **硬限位退离**：触发后禁止同向运动；只允许反向点动；`TryResetLimit` 必须先读限位 DI，仍触发则拒绝复位。
9. **报警码按轴**：`HARD_LIMIT_ESTOP#轴号`，禁止多轴共用一个码互相覆盖。

安全矩阵与验收场景以 `docs/SAFETY.md` 为准，不要只信 `docs/HANDOFF.md`（它把未实现的 ZMC 写成已交付）。

## 流程

- **新流程用 `FlowEngineV2`**（DAG，Nodify 编辑，JSON 单源 `SophonData/flows/{FlowName}.json`）。新节点：继承 `FlowNodeBase`，在 `FlowNodeRegistry` 静态构造函数 `Register<T>("NodeType")`，并给 `ParameterSchemas`。
- **`FlowEngine`（v1 线性 `IFlowStep`）仍被 `WorkStation` 使用**。工站生命周期测试走 v1。不要在 v1 上加新节点类型；旧 JSON 经 `FlowJsonMigrator` 迁到 v2。
- 暂停挂在节点边界；停止 = CTS；`WorkStation` 自己持有 CTS，不要对外暴露。步骤/节点异常必须在工站内吃掉，禁止逃逸到 UI 或测试线程。
- `Loop`/`Jump` 有静态环检测豁免，但有运行期迭代上限，不要关掉熔断。

## DI 与 UI

- 可自动入容器的类型打 `[Injectable(DependencyLifetime.Singleton)]`（`Sophon.Common`）。各层 `RegisterXxx` 只用 `RegisterMany` 扫 **Singleton**。`Delegate` 生命周期**不会**被扫进去，必须手写 `RegisterDelegate`/`RegisterSingleton`。
- 导航一律经 `INavigationGuardService`（侧栏和 Home 卡片都是）。新页面同时改权限矩阵，否则未登录也能进轴调试。
- ViewModel 高频事件在 `INavigationAware.OnNavigatedFrom` 里注销（看 `HomeViewModel.Unsubscribe`）。视觉取景复用 `WriteableBitmap`，禁止每帧 `new BitmapSource`。
- UI 字符串与页面是中文。View 的 `x:Name` 导航名必须与 `RegisterForNavigation<...>("XxxView")` 一致。

## 持久化

| 数据 | 形式 | 默认路径 |
|---|---|---|
| 流程 / 轴 / IO / 卡档案 / 示教 / 标定 / 气缸 / 外设 | JSON，UTF-8 无 BOM | **`AppDomain.CurrentDomain.BaseDirectory/SophonData/...`**（即 `Sophon.UI/bin/Debug/net8.0-windows/SophonData`） |
| 用户 / 报警历史等运行库 | SQLite | `DbPathProvider`：配置缺失时同上 `SophonData/sophon.db` |

仓库根目录 `SophonData/`（含 `flows/示例工站_搬运demo.json`）**不会**被运行时自动使用。`PathResolver` 会剥掉 `bin\Debug\` / `Sophon.UI\`，但多数 Store **不用**它。改配方时先确认你改的是仓库副本还是 bin 副本。

新配置 Store 用 `System.Text.Json`；UI/部分 Application 仍用 Newtonsoft（`ProtocolConfigConverter` 在 `App.OnStartup` 注册）。不要在同一文件混用两套序列化设置。

## 测试惯例

- 确定性时间用 `ManualTimeSource`，不要 `Thread.Sleep` 测规划器。
- 运动精度断言常见 `Assert.Equal(..., 9)`（1e-9）。视觉亚像素 ≤0.05px。
- Core 测试大量中文方法名 + 文件内 Fake（`Fakes.cs`、`AlarmTeach_TestFakes.cs`）。工站测试必须能从测试线程 `Stop`/`Pause`，引擎要 `Task.Run`。
- `Sophon.Motion` / `Sophon.Vision` 工程 `Nullable`+`ImplicitUsings` 开启；Core/UI/Infrastructure/Common **关闭**。新文件跟所在工程走，不要在 Core 里写 `global using`。
- 安全/限位/工站生命周期回归在 `WorkStationLifecycleTests`、`Alarm_GlobalLimitMonitorTests`、`SafetySemanticsTests`、`MotionLimitsAndSafetyTests`。改这些路径必跑对应工程。

## 其他陷阱

- **真卡 DLL**：`GoogolGtsNative` / `LeadShineDmcNative` 仅 P/Invoke 声明。缺 DLL 时必须失败可见，禁止 catch 后改走 Sim。
- **海康相机**：`HikvisionFrameSource` 反射加载 `MvCamCtrl.NET.dll`。开发机无 MVS 应 `IsAvailable=false` 或抛 `HikvisionCameraException`，不要空实现假装在采图。
- **NLog** 写死 `C:/SophonLog/logs/...`（`Common/NLog.config`），不是仓库内。
- **`Directory.Build.props` 全局 `NoWarn` NU1701**：`ini-parser` 只有 netfx 资产。不要“顺便”清掉；替换配置库是独立任务。
- **默认账户**（`DatabaseInitializer` 播种）：`管理员` / `工程师` / `操作员`，密码 `123`。不要写进生产配置示例以外的地方。
- 无 `App.config` 进仓库；`ConfigurationManager.AppSettings["CardBrand"]` 在未配置时旧栈会回退 LeadShine 空壳——这是遗留行为，新 HAL 不要模仿。
