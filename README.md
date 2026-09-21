# Motion Control Framework

Windows 工业运动控制上位机（开源）。

.NET 8 + WPF + Prism 9。面向产线现场：轴调试、点位示教、流程图、工站运行、相机配置与标定、气缸与 485 外设。

仓库：https://github.com/xzcnmb/Motion-control-framework

> **安全**：急停必须硬接线，不得依赖本软件。固高 GTS / 雷赛 DMC 适配器未经真机验收。现场禁止静默回退仿真。

## 功能

| 模块 | 说明 |
|---|---|
| 运动控制 | 接口单位 mm/deg；到位只认 `AxisDone`；点动 / 绝对 / 相对 / 回零；多轴插补与前瞻 |
| 轴调试 | 一行一轴表格：使能、限位灯、点动、定位、回零、停止 |
| 点位示教 | 使能后点动/步进；失败会提示，不静默 |
| 流程与工站 | Nodify DAG 编辑器（FlowEngineV2）；工站启停 / 暂停 / 继续 |
| 视觉 | 先配相机（厂商、序列号、曝光、增益）再应用到运行时，然后标定、监视 |
| 外设 | 485/Modbus 档案、气缸 DO/DI；监视页手动「连接采集」，不自动开串口 |
| 权限 | 管理员 / 工程师 / 操作员；未登录先全屏登录，成功后进入主页 |

## 环境

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 真卡：固高 `gts.dll` 或雷赛 `LTDMC.dll` 放在可加载路径
- 海康相机：安装 MVS，并在「相机配置」填写序列号

解决方案文件是 **`Sophon.slnx`**（不是 `.sln`）。

```bash
dotnet restore Sophon.slnx
dotnet build Sophon.slnx
dotnet test Sophon.slnx
dotnet run --project Sophon.UI
```

## 首次使用

1. 启动后先登录。出厂账号：`管理员` / `工程师` / `操作员`，密码均为 `123`（**上线前务必改掉**）。
2. 登录成功后进入欢迎主页。
3. 打开「控制卡与轴配置」，选择固高 GTS 或雷赛 DMC，保存后重启连接。
4. 底栏显示控制卡状态（已连接 / 未连接 / 故障），不再显示仿真。
5. 轴调试前先使能当前轴。相机标定前先在「相机配置」填写设备并「应用到运行时」。外设先配串口/从站，再在监视页点「连接采集」。

运行时配方写在：

`Sophon.UI/bin/Debug/net8.0-windows/SophonData/`

仓库根目录 `SophonData/` 只是示例，程序启动不会自动用那份。

## 工程结构

```
Sophon.UI              WPF 壳、导航、流程编辑器
Sophon.Application     仓储桥接、权限导航
Sophon.Core            流程、工站、报警、示教、气缸/外设
Sophon.Infrastructure  运动 HAL、协议、SQLite、JSON 配置
Sophon.Vision          视觉提供者、标定
Sophon.Motion          纯算法（规划/插补/前瞻），与硬件无关
Sophon.Contracts       纯契约，零第三方包
Common                 日志、路径、DI 标记
SophonData/            仓库内示例配方
tests/                 xUnit（Core / Infrastructure / Motion / Vision）
```

业务代码只依赖契约：`IMotionController`、`IIoController`、`IVisionProvider`。新驱动、新相机走工厂，不要在 UI 里写卡号或厂商 API。

更细的约定：[AGENTS.md](AGENTS.md)  
安全矩阵：[docs/SAFETY.md](docs/SAFETY.md)  
架构：[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)  
驱动：[docs/DRIVER_GUIDE.md](docs/DRIVER_GUIDE.md)  
视觉：[docs/VISION_GUIDE.md](docs/VISION_GUIDE.md)

## 现状（请先读）

| 项 | 现状 |
|---|---|
| 固高 GTS / 雷赛 DMC | P/Invoke 已接，**未经真机验收** |
| 正运动 ZMC | 枚举有，无适配器，工厂会抛错 |
| 海康相机 | 反射加载 `MvCamCtrl.NET.dll`，没装 MVS 则连不上 |
| 仿真控制器 | 仅测试工程使用；UI 启动按控制卡档案连接，禁止回退仿真 |
| 急停 | 必须硬接线 |

欢迎 Issue 与 PR。改限位、急停、工站生命周期请同步 `docs/SAFETY.md` 并跑对应测试。

## 许可

[MIT](LICENSE)。架构原型来自 [JeffreyXXL/Sophon](https://github.com/JeffreyXXL/Sophon)。
