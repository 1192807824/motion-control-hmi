# 雷赛 EtherCAT 运动控制联调清单

本项目的运动页已按《雷赛控制 EtherCAT 总线卡用户使用手册 V2.0（2025-07-28）》接入真实
`LTDMC.dll`。下面的步骤用于第一次上机，不能代替设备风险评估、机械限位和独立急停回路。

## 程序与手册的接口对应

| 功能 | 实际接口 | 关键规则 |
| --- | --- | --- |
| 初始化与选卡 | `dmc_board_init`, `dmc_get_CardInfList`, `dmc_get_total_axes` | 不假设卡号为 0，默认选择检测列表第一张卡 |
| EtherCAT 状态 | `nmc_get_errcode(card, 2, ...)` | 总线错误码必须为 0 才允许使能和运动 |
| 轴使能/解除 | `nmc_set_axis_enable`, `nmc_set_axis_disable` | 使能后等待状态机变为 4（OP_ENABLE） |
| 相对寸动 | `dmc_set_profile_unit`, `dmc_set_s_profile`, `dmc_pmove_unit` | `posi_mode=0`，速度/加减速来自配置和界面 |
| 连续运动能力 | `dmc_vmove` | 已封装在 `IMotionCard.Jog`，当前界面使用定距寸动 |
| 总线轴回零 | `nmc_set_home_profile`, `nmc_home_move`, `dmc_get_home_result` | 不使用脉冲轴的 `dmc_home_move` |
| 位置与速度 | `dmc_get_encoder_unit`, `dmc_get_position_unit`, `dmc_get_target_position_unit`, `dmc_read_current_speed_unit` | 界面当前位置显示编码器反馈值 |
| 状态与联锁 | `nmc_get_axis_state_machine`, `nmc_get_axis_errcode`, `dmc_axis_io_status_ex`, `dmc_get_stop_reason` | 读取 ALM、EL+/EL-、EMG、SL+/SL- 并阻止危险方向运动 |
| 停止 | `dmc_stop`, `dmc_emg_stop` | 单轴按钮减速停；底部红色按钮为全轴急停 |
| 报警清除 | `nmc_clear_errcode`, `nmc_clear_axis_errcode`, `dmc_clear_stop_reason` | 清除后立即重新轮询，未消失的故障会重新记录 |
| 本地 I/O | `dmc_read_inport_ex`, `dmc_read_outport_ex`, `dmc_write_outbit` | 数字输出只在人工点击对应点时写入 |
| 模拟量 | `dmc_get_ad_input`, `dmc_get_da_output`, `dmc_set_da_output` | 模拟输出限定在配置的最小/最大范围内，按“写”才下发 |

手册重点页：PDF 第 79-85 页（使能、回零、点位运动流程），第 175、183-190 页（总线与轴接口），
第 199-207 页（初始化和单轴运动），第 238-244 页（位置、状态、停止原因和限位），第 291 页起
（错误码）。

## 第一次上机前

1. 确认 PC、控制卡驱动、`LTDMC.dll` 和程序进程位数一致。项目默认在 64 位 Windows 上按 x64
   运行；若现场只有 32 位运行库，需要连同项目平台目标一起改为 x86。
2. 使用雷赛 Motion 软件扫描全部 EtherCAT 从站，核对顺序和设备描述 XML，并下载 ENI 配置。
3. 断开或抬起危险负载，确认机械硬限位、伺服报警和独立急停回路有效。
4. 打开 `motion-settings.json`：
   - `CardNo: null` 自动选第一张检测到的硬件卡；多卡时应明确填写硬件卡号。
   - 界面轴 1..16 对应控制卡硬件轴 0..15。
   - `AxisCount` 只表示本程序允许操作的前 N 个轴，不会因为控制卡支持 64 轴而自动全部使能。
   - `MoveProfile` 的时间单位是秒，速度单位是配置脉冲当量后的 `unit/s`。
5. 根据每个驱动器手册和机构实际情况填写回零模式、方向含义、低速、高速和偏移。确认无误后才把
   全局或单轴 `HomeProfile.Enabled` 改为 `true`。默认禁用回零是有意的安全门槛。

## 建议的低风险联调顺序

1. 只上控制电，不使能电机；启动程序，确认标题显示“EtherCAT 正常”，卡号和轴数正确。
2. 手动触发正限位、负限位、急停和原点传感器，确认轴表灯号与真实信号一致。
3. 切换 I/O 四个页签，核对 DI/DO/AI/AO 通道数量。首次 DO/AO 测试前断开执行器负载。
4. 单独使能一个无负载轴，确认状态机进入 4；解除使能后确认驱动器实际失能。
5. 将点动速度和距离设到机械允许的最小值，分别测试正、负方向。确认界面当前位置来自编码器反馈，
   并确认触发相应限位后同方向命令被拒绝。
6. 只给一个轴启用正确的回零配置，先用行内“回零”测试。确认完成信号为 1、位置和偏移正确、超时能
   立即停止，再测试底部“顺序回原点”。
7. 最后测试单轴减速停止、全轴急停、报警清除以及程序关闭时的全轴急停。

## 无硬件验证

将 `SimulationMode` 临时改为 `true` 可做界面和流程验证。仿真器会按时间更新位置、速度、回零和 I/O，
但它不能验证 DLL 位数、ENI、PDO 映射、驱动器回零模式、接线极性或真实机械安全。硬件模式初始化失败
时程序只报告离线，不会偷偷切换到仿真。
