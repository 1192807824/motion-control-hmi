# 运动功能与雷赛 API 核对表

本文列出运动控制页面当前实际接入的功能，供上机前逐项核对。界面、报警、持久化配置和雷赛 SDK 均使用 0-based 硬件轴号。

## 页面操作

| 页面功能 | 关键实现 | 雷赛 API / 判定 | 当前语义 |
| --- | --- | --- | --- |
| 启动枚举卡 | `LeisaiMotionCard.Open` | `dmc_board_init`, `dmc_get_CardInfList` | 显示全部检测卡和当前卡；失败时清空轴列表、禁用运动并弹框 |
| 读取轴数 | `LeisaiMotionCard.Open` | `nmc_get_total_axes` | EtherCAT 轴从 0 开始显示；全轴安全操作按控制卡实际轴数，不受界面显示上限截断 |
| 单轴伺服使能/解除 | `ServoOn` | `nmc_set_axis_enable`, `nmc_set_axis_disable`, `nmc_get_axis_state_machine` | 使能后等待状态机 4；解除前必须确认轴停止 |
| 全轴伺服使能/解除 | `SetAllServos` | 同上，逐硬件轴执行 | 解除使能前先对控制卡全部硬件轴做停止预检，再逐轴解除并复核 |
| 连续 JOG | `Jog` | `dmc_set_profile_unit`, `dmc_set_s_profile`, `dmc_set_dec_stop_time`, `dmc_vmove` | 负向 `direction=0`，正向 `direction=1`；按住运行，松开、移出、失焦或窗口停用即减速停止 |
| 相对定距 | `MoveRelative` | `dmc_pmove_unit(..., posi_mode=0)` | 距离必须为非零有限值；正负号决定方向 |
| 绝对定位 | `MoveAbsolute` | `dmc_pmove_unit(..., posi_mode=1)` | 目标允许正数、负数或零；先按当前位置判断方向和限位 |
| 等待运动完成 | `WaitForPositionMoveAsync` / 后台跟踪 | `dmc_check_done`, `dmc_get_position_unit`, `dmc_get_encoder_unit`, `dmc_get_target_position_unit` | 可选择等待/不等待；停止后还要满足目标误差；超时进入安全停止 |
| 当前轴回原点 | `Home` | `nmc_set_home_profile`, `dmc_home_move`, `dmc_get_home_result` | 使用每轴回零配置；按 E3064S 现场验证流程启动；完成必须同时满足已回零、轴已停止、无报警/急停 |
| 按次序回原点 | `HomeAll_Click` | 同上 | 只执行界面中用非零回零次序明确加入 `HomeSequence` 的轴；空序列禁止执行，不猜测轴号顺序；逐轴预检、逐轴等待，不自动使能 |
| 单轴减速停止 | `StopAxis(immediate:false)` | `dmc_stop(..., stop_mode=0)` | JOG 松开及灰色“单轴减速停”按钮使用；之后持续确认停止 |
| 单轴立即停止 | `StopAxis(immediate:true)` | `dmc_stop(..., stop_mode=1)` | 橙色“单轴立即停”按钮；只停止当前活动/选中轴，之后持续确认停止 |
| 全轴急停 | `StopAll_Click` | `dmc_emg_stop` | 红色按钮；覆盖控制卡全部硬件轴并逐轴确认停止 |
| 程序关闭 | `TryShutdown` | `dmc_emg_stop` + 全硬件轴状态确认 | 急停失败、状态读取失败或超时未停稳时拒绝关闭窗口，不会直接关卡 |
| 报警/限位联锁 | `ReadAxis` / `EnsureAxisReadyForDirection` | `nmc_get_errcode`, `nmc_get_axis_errcode`, `dmc_axis_io_status_ex`, `dmc_get_stop_reason` | `0x0228` 仅提示环网冗余断开且继续轮询；其他总线错误、ALM、EMG、正负限位或状态读取异常仍阻止运动 |
| 清除报警 | `ClearAlarms` | `nmc_clear_errcode`, `nmc_clear_axis_errcode`, `dmc_clear_stop_reason` | 运动、回零或停止确认期间禁止清除 |

## 运动参数

| 界面字段 | 保存值 / 下发值 | 接口参数 | 约束 |
| --- | --- | --- | --- |
| 移动距离 / 目标位置 | `AxisStatus.JogDistance` | `dmc_pmove_unit.distance` | 相对模式不可为 0；绝对模式可为负数或 0 |
| 运行速度 | `AxisStatus.JogSpeed` | `dmc_set_profile_unit.max_vel` | 必须大于 0；JOG 使用其绝对值并由方向按钮决定正负 |
| 起始速度 | `StartVelocity` | `dmc_set_profile_unit.min_vel` | 大于等于 0，且不能高于运行速度 |
| 停止速度 | `StopVelocity` | `dmc_set_profile_unit.stop_vel` | 大于等于 0，且不能高于运行速度 |
| 加速时间 | 界面 ms，内部除以 1000 保存/下发为 s | `dmc_set_profile_unit.tacc` | 必须大于 0 |
| 减速时间 | 界面 ms，内部除以 1000 保存/下发为 s | `dmc_set_profile_unit.tdec` | 必须大于 0 |
| S 曲线时间 | 界面 ms，内部除以 1000 保存/下发为 s | `dmc_set_s_profile(..., mode=0, s_para)` | 可为 0 |
| 减速停止时间 | 界面 ms，内部除以 1000 保存/下发为 s | `dmc_set_dec_stop_time` | 用于 `dmc_stop(...,0)` 的减速停止过程 |
| 等待运动完成 | `WaitForCompletion` | 软件等待策略 | 勾选时命令调用等待到位；未勾选时后台仍继续监控到位/超时 |
| 完成超时 | `CompletionTimeoutMilliseconds` | 软件截止时间 | 超时后先单轴减速停；停止确认超时再升级全轴急停 |
| 到位误差 | `CompletionTolerance` | 编码器反馈与目标差值 | 必须大于 0；与 `dmc_check_done==1` 同时满足才算完成 |
| 回零模式 | 当前轴 `HomeTuning.Mode` | `nmc_set_home_profile.home_mode` | 可直接输入 0..65535 的整数，必须按驱动器与传感器确认 |
| 回零低速/高速 | 当前轴独立配置 | `nmc_set_home_profile.low/high_vel` | 都必须大于 0，且高速不得小于低速 |
| 回零加速/减速 | 界面 ms，保存/下发为 s | `nmc_set_home_profile.tacc/tdec` | 必须大于 0 |
| 回零偏移 | 当前轴独立配置 | `nmc_set_home_profile.offset_pos` | 允许正、负或零，必须是有限数值 |
| 回零全局超时 | `HomeTimeoutSeconds` | 软件截止时间 | 所有轴共用，1..3600 秒；超时后立即停止并进入停止确认 |
| 回零次序 | 当前轴 `HomeTuning.SequenceOrder` | 软件执行顺序 | `0` 表示不参加按次序回零；`1..AxisCount` 表示明确先后，重复位置按列表插入并自动重排 |

每个轴可保存独立运动曲线。JOG 速度/距离、全部运动曲线字段、定位等待字段、回零字段、启用状态和回零次序都会在正常关闭前落盘并在下次启动恢复；实时 DO/AO 不自动重放。参数在轴运动、回零或停止确认期间禁止修改，避免把“在线改速”误当成已实现。

## 当前有意未使用的接口

| 接口 | 当前处理 |
| --- | --- |
| `dmc_change_speed_unit` | 未接入。当前策略是在运动期间锁定参数编辑；若以后需要在线变速，应单独设计限幅、斜率和失败回退。 |
| `dmc_set_factor_error_unit`, `dmc_check_success_pulse`, `dmc_check_success_encoder` | 未接入。当前到位判定使用 `dmc_check_done==1`，再校验指令位置、编码器反馈与目标误差。 |

## 上机核对顺序

1. 机构脱开或限制在安全行程，先核对卡号、轴号、单位、正负方向和限位极性。
2. 每次只使能一个轴，以最低速度测试 JOG 正/负方向和松开减速停止。
3. 分别验证相对定距、绝对正位置、绝对负位置和绝对零位。
4. 分别验证单轴减速停、单轴立即停、全轴急停以及停稳确认超时后的锁定。
5. 回零模式、方向、速度和偏移必须按驱动器与机构确认后再启用。

仿真和静态检查不能替代实体卡、驱动器、传感器及机械机构的低速联调。
