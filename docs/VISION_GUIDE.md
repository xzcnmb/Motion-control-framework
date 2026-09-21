# 视觉接入指南

## 1. 提供者架构

所有视觉能力经 `IVisionProvider` 契约（`Sophon.Contracts`）：`Trigger` → `ResultReady(VisionResult)`（像素 + 标定后物理坐标），`StartLive/StopLive` → `FrameReady(VisionFrame)`（灰度行优先）。

已交付提供者：
- `SimVisionProvider`：合成图像 + 真值 + 噪声/故障注入，全链路离线开发用。
- `OpenCvVisionProvider`：OpenCvSharp4 NCC 模板匹配（亚像素峰值 ≤0.05px）+ 多角度搜索；图像源抽象 `IFrameSource` 提供 `DirectoryFrameSource`（轮询相机落盘目录，工业联调常用）与 `InMemoryFrameSource`。

创建方式见 `VisionProviderFactory`。UI 页面（视觉监视/标定向导）已按 Sim 提供者接通。

## 2. 标定流程（页面操作：视觉监视 → 标定向导）

1. **九点标定**：机构带特征依次走 9 个已知物理点 → 每点触发测量回填像素 → 录入物理坐标 → 计算 → 查看残差（RMS/最大值）→ 合格（PASS）后保存。
2. **旋转中心标定（两圆法）**：位置 1 触发 → 旋转 θ → 位置 2 触发 → 解算旋转中心 (Cx,Cy) 与半径。
3. **示教基准**：标准产品就位 → 采集基准（`VisionTeachBase`）。
4. 标定结果存 `SophonData\calibrations\{cameraId}.json`（JSON 单源，随配方管理）。

## 3. 流程内视觉引导

流程节点 `VisionMeasure`（NodeType: `VisionMeasure`）：
- 参数：`cameraId`、`timeoutMs`、各输出变量名（默认 `VisionOk/VisionX/VisionY/VisionWorldX/VisionWorldY/...`）。
- 用法：`VisionMeasure` → `Branch(conditionKey=VisionOk)` → `MultiAxisInterp(targetsFromContext=[VisionWorldX,VisionWorldY])`，即"视觉定位→补偿搬运"闭环（示例：`SophonData\flows\示例工站_搬运demo.json`）。
- 提供者注入：创建 `FlowEngineV2` 时传 `services`（IServiceProvider 内含 `IVisionProvider`）；UI 编辑器运行时若未注入，节点会如实报告"视觉提供者未注入"。

## 4. 接入海康等真实相机

推荐路径：实现 `IFrameSource`（海康 MvCamCtrl 回调 / VisionMaster 结果落盘）→ 复用 `OpenCvVisionProvider` 的匹配定位；或整体实现 `IVisionProvider` 对接 VM SDK（加载 .sol、执行流程、读全局变量），替换 UI/流程的提供者注册即可，其余代码零改动。

## 5. 精度与坑位提示

- 相机平面与物料平面平行、特征居中（边缘畸变）；连续拍照重复性 <1px 再标定。
- 旋转中心随拍照位置变化，换位需复标；温度变化 >5℃ 建议复标。
- 模板重建后基准值变化，示教基准需重新采集。
- WPF 取景渲染：固定复用 WriteableBitmap + WritePixels，严禁每帧 new BitmapSource（内存泄漏）。
