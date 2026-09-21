# 安全设计说明

> 本文档是验收与现场维护的依据。任何安全相关改动必须同步更新本文档并重跑对应测试。

## 1. 限位安全（双层防线）

| 防线 | 机制 | 响应 | 失败后果 |
|---|---|---|---|
| 第一层：软限位（规划器） | `MotionLimits.ValidateTarget/ClampProfile`；Sim 控制器 MoveAbs 前强制校验 | 目标越界拒绝；轨迹按限位提前减速截断 | 只影响运动精度，不构成人身风险 |
| 第二层：软限位（运行监控） | `GlobalLimitMonitor` 20ms 轮询快照位置 | 越界 → `SOFT_LIMIT_ERROR`（Error 级，单轴停） | 需保证监控线程存活（见看门狗） |
| 硬限位 | 优先卡内硬件处理（GTS `GT_LmtsOn`/DMC 限位输入，us 级）；Sim 注入 `LimitTriggered(IsHardLimit=true)` 模拟 | `HARD_LIMIT_ESTOP`（EStop 级，AbortAll + 工站 Alarm） | 机械缓冲耗尽则碰撞——真机必须确认卡内限位已启用 |

## 2. 停止语义分级

| 级别 | 命令 | 语义 | 恢复 |
|---|---|---|---|
| AlarmStop（停机） | `StopMotion`（按 profile 减速停） | 流程可感知，允许继续运行 | 自动 |
| EStop（急停） | `Abort/AbortAll`（立即停 + 伺服使能时序） | 旁路流程引擎直达运动层 | 人工复位 |
| 急停按钮 | 硬接线到卡/驱动器 ESTOP 输入，**不得依赖上位机** | 硬件级 | 人工复位 |

## 3. 看门狗与断线

- `GlobalLimitMonitor` 快照看门狗：AxisManager 停止推送超过 1s → `WATCHDOG_TIMEOUT`（Error 级，StopAllAxes）；恢复推送自动清除。
- 真卡断线：连接状态机 `Fault → Reconnecting`；断线期间卡内缓冲可能继续执行，依赖卡看门狗/硬件急停兜底——真机联调时验证此路径。

## 4. 退离限位瞬态协议

硬限位触发后：
1. 记录触发方向，**禁止继续向该方向运动**（`IsDirectionProhibited`）；
2. 允许反向点动退离限位区；
3. 复位门禁：`TryResetLimit` 先读限位 DI 电平，仍处触发态则拒绝复位；
4. 复位成功 → 清除 `HARD_LIMIT_ESTOP` → 工站 Alarm → Idle。

## 5. 报警联动矩阵

| 报警码 | 级别 | 联动动作 |
|---|---|---|
| `HARD_LIMIT_ESTOP` | EStop | AbortAll + 工站 Alarm |
| `SOFT_LIMIT_ERROR` | Error | 单轴 StopMotion |
| `WATCHDOG_TIMEOUT` | Error | StopAllAxes |
| 自定义（AlarmDefinition） | 可配 | None/StopAxis/StopAllAxes/StopFlow/EStopAll |

## 6. 已验证场景（自动化测试）

- 工站步骤失败 → Alarm（可复位重启）、Idle 时 Stop 不残留、重复启动单实例（`WorkStationLifecycleTests`）
- Sim 注入正硬限位 → 全轴停 + 报警 + 复位恢复（`Alarm_GlobalLimitMonitorTests`）
- 软限位越界拒绝、点动触软限位停（`Sophon.Infrastructure.Tests`、`Sophon.Motion.Tests`）
- 流程 DiWait/EventWait 一律超时上限（`FlowV2_*`）

## 7. 真机联调检查单（固高/雷赛首验必做）

1. 卡内硬限位输入启用（us 级）；2. 急停按钮硬接线验证；3. 使能/报警清除/到位信号时序；4. 断线后卡级停车策略；5. 回零模式与原点传感器方向核对；6. 软限位与行程余量复核。
