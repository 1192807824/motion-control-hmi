# 雷赛 EtherCAT 运动控制联调清单

本项目的运动页已按《雷赛控制 EtherCAT 总线卡用户使用手册 V2.0（2025-07-28）》接入真实
`LTDMC.dll`。下面的步骤用于第一次上机，不能代替设备风险评估、机械限位和独立急停回路。

## 程序与手册的接口对应

| 功能 | 实际接口 | 关键规则 |
| --- | --- | --- |
| 初始化与选卡 | `dmc_board_init`, `dmc_get_CardInfList`, `nmc_get_total_axes` | EtherCAT 轴数必须使用 `nmc_get_total_axes`；`dmc_get_total_axes` 只返回本地脉冲轴数，可能为 0 |
| EtherCAT 状态 | `nmc_get_errcode(card, 2, ...)` | 总线错误码必须为 0 才允许使能和运动 |
| 轴使能/解除 | `nmc_set_axis_enable`, `nmc_set_axis_disable` | 使能后等待状态机变为 4（OP_ENABLE） |
| 相对/绝对定位 | `dmc_set_profile_unit`, `dmc_set_s_profile`, `dmc_pmove_unit` | 相对位置使用 `posi_mode=0`；绝对位置使用 `posi_mode=1`，目标可为负数或零 |
| 连续 JOG | `dmc_set_profile_unit`, `dmc_set_s_profile`, `dmc_vmove` | 按住正/负方向运行；松开、失焦或窗口停用立即发送单轴减速停止 |
| 完成等待与到位 | `dmc_check_done`, `dmc_get_position_unit`, `dmc_get_encoder_unit`, `dmc_get_target_position_unit` | 停止状态为 1，并同时校验指令/反馈与目标误差；超时会进入安全停止 |
| 总线轴回零 | `nmc_set_home_profile`, `nmc_home_move`, `dmc_get_home_result` | 不使用脉冲轴的 `dmc_home_move` |
| 位置与速度 | `dmc_get_encoder_unit`, `dmc_get_position_unit`, `dmc_get_target_position_unit`, `dmc_read_current_speed_unit` | 界面当前位置显示编码器反馈值 |
| 状态与联锁 | `nmc_get_axis_state_machine`, `nmc_get_axis_errcode`, `dmc_axis_io_status_ex`, `dmc_get_stop_reason` | 读取 ALM、EL+/EL-、EMG、SL+/SL- 并阻止危险方向运动 |
| 停止 | `dmc_set_dec_stop_time`, `dmc_stop`, `dmc_emg_stop` | JOG 松开/单轴减速停使用 `dmc_stop(...,0)`；单轴立即停使用 `dmc_stop(...,1)`；全轴急停使用 `dmc_emg_stop`；停止确认超时自动升级全轴急停 |
| 报警清除 | `nmc_clear_errcode`, `nmc_clear_axis_errcode`, `dmc_clear_stop_reason` | 清除后立即重新轮询，未消失的故障会重新记录 |
| 本地 I/O | `dmc_read_inport_ex`, `dmc_read_outport_ex`, `dmc_write_outbit` | 数字输出只在人工点击对应点时写入 |
| 模拟量 | `dmc_get_ad_input`, `dmc_get_da_output`, `dmc_set_da_output` | 模拟输出限定在配置的最小/最大范围内，按“写”才下发 |

手册重点页：PDF 第 79-85 页（使能、回零、点位运动流程），第 175、183-190 页（总线与轴接口），
第 199-207 页（初始化和单轴运动），第 238-244 页（位置、状态、停止原因和限位），第 291 页起
（错误码）。

## 第一次上机前

1. 安装 **x86 .NET 9 Windows Desktop Runtime** 和雷赛驱动/运行库。仓库随附的雷赛 `LTDMC.dll` v2.4.6.9
   是 32 位版本，因此项目已固定为 `win-x86` / `PlatformTarget=x86`；部署时 `ControlHub.exe` 与同目录的
   `LTDMC.dll` 必须都是 x86，不能使用 x64 DLL，也不能只安装 x64 .NET Runtime。Solution 中的 `Any CPU`
   和 `x64` 只是兼容配置标签，`ControlHub.csproj` 仍会强制生成 x86 进程；请始终使用生成的 `win-x86`
   输出。若以后更换为 x64 SDK，必须同时修改项目运行时目标并重新验证全部 P/Invoke。
2. 使用雷赛 Motion 软件扫描全部 EtherCAT 从站，核对顺序和设备描述 XML，并下载 ENI 配置。
3. 断开或抬起危险负载，确认机械硬限位、伺服报警和独立急停回路有效。
4. 卡号、轴数等基础配置仍在 `motion-settings.json`；运动页面会自动保存各轴输入参数：
   - `CardNo: null` 自动选第一张检测到的硬件卡；多卡时应明确填写硬件卡号。
   - 界面轴 1..16 对应控制卡硬件轴 0..15。
   - `AxisCount` 只表示本程序允许操作的前 N 个轴，不会因为控制卡支持 64 轴而自动全部使能。
   - `MoveProfile` 的时间单位是秒，速度单位是配置脉冲当量后的 `unit/s`。
5. 在“回原点”页按当前轴填写回零模式、低速、高速、加减速、偏移和回零次序。回零模式可直接输入控制器要求的数字；
   “全局超时”供所有轴共用。确认无误后再勾选“启用当前轴回零”。回零次序 `0` 表示不参加多轴顺序回零，`1..N`
   表示明确的执行先后。模式、速度、加减速、偏移、启用状态和顺序都会保存；正常关闭后下次打开自动恢复。

## 建议的低风险联调顺序

1. 只上控制电，不使能电机；启动程序，确认标题显示“EtherCAT 正常”，卡号和轴数正确。
2. 手动触发正限位、负限位、急停和原点传感器，确认轴表灯号与真实信号一致。
3. 切换 I/O 四个页签，核对 DI/DO/AI/AO 通道数量。首次 DO/AO 测试前断开执行器负载。
4. 单独使能一个无负载轴，确认状态机进入 4；解除使能后确认驱动器实际失能。
5. 将点动速度和距离设到机械允许的最小值，分别测试正、负方向。确认界面当前位置来自编码器反馈，
   并确认触发相应限位后同方向命令被拒绝。
6. 只给一个轴启用正确的回零配置，先用“当前轴回原点”测试。确认完成信号为 1、位置和偏移正确、超时能
   立即停止，再为需要参加的轴填写经过机械防碰验证的非零回零次序，最后测试“按次序回原点”。程序不会为空序列
   自动猜测轴号顺序；无效输入或保存失败时两个回零按钮都会锁定。
7. 最后分别测试单轴减速停止、单轴立即停止、全轴急停、报警清除以及程序关闭时的全轴急停与停稳确认。

## 无硬件验证

将 `SimulationMode` 临时改为 `true` 可做界面和流程验证。仿真器会按时间更新位置、速度、回零和 I/O，
但它不能验证 DLL 位数、ENI、PDO 映射、驱动器回零模式、接线极性或真实机械安全。硬件模式初始化失败
时程序只报告离线，不会偷偷切换到仿真。
