# ControlHub

## Project layout

- `Assets`: UI images and icon resources.
- `Data`: temporary seed data used to populate the current dashboard.
- `Models`: axis, alarm, station, and I/O data models.
- `Services/Motion`: motion-card abstractions and hardware integrations.
- `Services/Persistence`: local JSON stores for remembered UI settings.
- `ViewModels`: bindable UI state and notification helpers.
- `Views`: WPF windows and dialogs.

## Development notes

- Keep view-specific interaction code in the matching `Views/*.xaml.cs` file.
- Keep bindable screen state in `ViewModels`.
- Keep hardware calls behind interfaces in `Services/Motion`.
- Keep persisted UI data behind stores in `Services/Persistence`.
- Replace `Data/DashboardSeedData` with real runtime data sources as the device integration grows.

## Local files

The running application writes these JSON files next to the executable:

- `axis-settings.json`: axis name, jog speed, and jog distance/absolute target by axis number.
- `remembered-login.json`: last successful login user name and role for dialog prefilling.
- `motion-settings.json`: real card selection, polling, every per-axis move-profile input, per-axis EtherCAT homing parameters, explicit homing sequence, wait/tolerance settings, and the shared homing timeout.
- `vibration-feeder-settings.json`: vibration feeder TCP endpoint and line-ending settings. Feeder communication uses fixed 2-second connect/write timeouts and ASCII payloads.
  It also stores the directional-vibration frequency, amplitude, and pulse duration used by the eight direction buttons and the scatter/gather controls.

Legacy `axis-names.json` and `permission-session.json` files are still read when present so older local data is not lost.

## EtherCAT motion control

The motion page now uses the real Leadshine EtherCAT API by default. It discovers the hardware card ID with
`dmc_get_CardInfList`, maps displayed axis 1 to hardware axis 0, checks the EtherCAT bus before commands, and polls
feedback position, target, speed, CiA 402 state, limits, homing result, stop reason, and local digital inputs.
The homing page now edits each axis independently (enable, numeric mode, low/high speed, acceleration/deceleration, offset,
and sequence order), with a clearly labelled shared homing timeout. Sequence order `0` excludes the axis from multi-axis homing;
there is no hidden fallback sequence. Valid motion inputs are saved automatically on focus change, axis change, and normal shutdown, then
restored the next time the same build is opened. If saving fails during shutdown, the window stays open and reports why.
Invalid or unsaved homing input disables both homing commands, so a stale profile cannot be executed.
Until the card opens successfully, the axis table, I/O area, and alarm table remain empty and all motion controls are disabled.
The I/O tabs are live: digital inputs and outputs are read back from the selected local port, digital outputs can be
toggled, analog inputs are polled, and analog outputs are written only after pressing the channel's `写` button.

Before running on the machine:

1. Install the Leadshine driver/runtime and the **x86 .NET 9 Windows Desktop Runtime**. This repository ships the
   32-bit Leadshine `LTDMC.dll`, so both `ControlHub.exe` and the DLL beside it must be x86; do not substitute an x64 DLL
   or rely on an x64-only .NET runtime.
2. Use Leadshine Motion to scan the EtherCAT slaves and download the ENI configuration.
3. Review `motion-settings.json`. `CardNo: null` selects the first detected hardware ID.
4. On the homing tab, enter the selected axis mode, speeds, acceleration/deceleration, offset, shared timeout, and sequence
   order, then check `启用当前轴回零`. Use sequence order `0` until the machine-safe multi-axis order has been verified.
   Homing is deliberately disabled by default because the correct mode depends on the drive, sensor wiring, and mechanism.
5. Confirm that the displayed 1-based axis order matches hardware axes 0 through 15 before enabling motion.

`ControlHub.csproj` fixes `PlatformTarget=x86` and `RuntimeIdentifier=win-x86`. The solution's `Any CPU` and `x64`
configuration labels are compatibility labels only; they still build this project as x86 and do not change the process
architecture. Use the generated `win-x86` output and keep its copied `LTDMC.dll` next to `ControlHub.exe`.

Set `SimulationMode` to `true` only for explicit offline UI testing. Simulation performs time-based moves and status
updates; hardware mode never falls back silently to simulation.

Live DO/AO values are read from hardware and are not replayed automatically after restart. This is intentional: restoring
an output command at startup could energize equipment unexpectedly.

## Vibration feeder TCP connection

The vibration feeder connection is a raw TCP client. Existing ASCII/HEX device commands are sent unchanged over the
socket; this is transport-level TCP, not Modbus TCP register framing. Configure the feeder's IP address and listening
port on the connection page. The UI reports remote disconnects automatically, and connection/write operations use the
configured timeouts.
## 料仓手动振动

连接配置页的振动盘设备下增加“料仓振动控制”，与振动盘共用 TCP 连接。可设置频率（1–999）、振幅（0–99%）和时间（100–30000 ms）；默认值为 28、50%、300 ms，来自协议示例，现场需按物料调整。点击“启动料仓”保存参数并发送 `&13,xx,yyy$`，定时结束先发送 `&13,00,yyy$` 将料仓振幅归零，再发送 `&04$`。归零沿用本次启动频率，不改变保存的运行振幅。参数随当前配置及产品配方保存。

现场验证发现 `&04$` 虽然有回包，料仓仍持续振动；单独发送 `&13,00,035$` 可以停振，因此不能仅依赖协议中“全部停止”的描述。手动全部停止、断开连接和正常退出均执行“料仓振幅归零 → &04$”，即使当前没有软件计时任务也可发送。两条指令共用发送锁，中间不能插入新启动；停止发送失败时保留待停状态并报警，手动断开也保留连接以便重试。日志表示指令已发送，不代表机械状态反馈。料仓手动动作与方向振动互斥，生产、收料、复位或定位动作期间禁止手动启动料仓。此功能为手动调试，不会自动加入生产补料流程。

## 历史数据与报警清空

数据查询页的“清空全部数据”删除 SQLite 内所有批次及测量记录，不受当前批次筛选影响，执行前有确认提示。生产、暂停和生产收尾期间禁止清空。完成后刷新查询表、统计图和主页缓存；旧 JSON 备份保留，但不会在下次启动时重新导入已清空数据，新生产数据仍可正常写入。

顶部“报警记录”现在打开真实历史查询页。运动控制、生产/收料/复位、仪表/振动盘连接和通讯故障、仪表状态故障、视觉/显微镜、参数操作及系统异常通过统一入口写入 `%LOCALAPPDATA%\ControlHub\alarms.db`，记录本地显示时间、来源、代码、级别和完整内容。记录在后台写入，故障写盘不阻塞运动处理；写入失败保留待写记录并在报警菜单/查询页显示异常，恢复后自动补写。持续的同一轴故障和连续相同仪表状态不重复刷屏，恢复后再发生会重新记录。普通产品NG仍属于测量记录。

报警页默认查询当天，可选择开始/结束年月日（包括结束日全天），也可查全部日期；每页200条，支持翻页和双击详情。报警历史不设200条存储上限，运动页仅显示最近200条。“清空全部报警”删除所有日期的历史，清空前确认；设备报警复位不删除历史，清空历史也不会复位设备。此版本开始记录的历史会跨重启保留，旧版未落盘的内存报警无法恢复。

## E4981A 电容区间重试

参数配置页的“仪表测试重试 · E4981A 电容区间”只保留一个“统一重试次数”（0～10，0关闭），沿用原测试重试次数，不再读取旧版独立区间次数。E4981A电容低于下限或高于上限时复测，例如设1～1000 nF，0.05 nF会重试，1和1000 nF均在区间内。电容区间外、仪表测量失败（含损耗超限）和通讯异常共用同一额度，每站独立计数，不累加两套次数；SM7110仍按原判定条件使用此次数。每次复测只对本站执行回等待位、下压、稳定等待，次数用完按末次结果分料，不额外改变BIN或损耗判定。参数自动保存并随产品配方恢复，生产启动时锁定；上下限同时留空兼容旧配方，仅按原仪表失败条件重试。

# 批次数据采集

主页输入批次号后开始生产；运行和暂停期间批次号锁定。同名批次再次启动会继续累计。只采集已启用且有料工位的最终测量结果，每轮机械重测中的中间结果不重复计入图表。

- E4981A：保存电容（nF）、损耗 D、BIN 和判定结果。
- SM7110 电阻模式：保存最终测量轮首次达到门限的电阻（GΩ）及耗时；超时保存该轮最后读数并标记 NG；从未得到有效读数时保留空值及仪表状态。
- 每条记录包含批次、跨站产品编号、时间、工位、仪表/模式、测量轮数、本站判定、截至本站的产品判定与原始响应。SM7110 的合格不会清除前站的不良判定。
- 主页隐藏原转盘示意图，按已启用工位显示频数柱状图与正态拟合线。统计包括有效的 OK/NG 最终读数，排除无效读数；少于两个有效样本或标准差为零时仅显示实际频数。
- 顶部“数据查询”菜单进入专用页面，可按批次查询、刷新批次列表和导出全部查询明细。切换页面沿用现有停止生产逻辑；主页“批次查询”按钮打开相同功能的独立窗口，可在生产中查看和导出。
- 点击“导出当前批次 CSV”选择保存位置，文件含中文表头、批次、产品编号、最终值/单位、损耗D、判定、耗时、重测轮数、门限与原始响应，Excel 可打开。导出使用最近一次查询的完整快照，新增记录需先重新查询；空结果或修改批次后尚未查询时禁止导出。

数据保存在内嵌 SQLite 数据库 `%LOCALAPPDATA%\ControlHub\batches\batch-measurements.db`，无需安装数据库服务。每个工位的最终结果使用独立事务立即提交，记录提交后才计入主页；双站结果可分别落库。启用 WAL、完整同步、外键和查询索引，保存失败会使生产流程报错并沿用现有安全退出逻辑。取消或通讯异常导致测量没有最终结果时，不生成虚假的测量数值。

SQLite 版本启动时会扫描原有批次 JSON 并自动导入，记录编号作为主键，重复启动不会重复导入；这样旧版程序在切换前最后写入的记录也能在下次启动时补入。每个进程只扫描一次。旧 JSON 保留作为迁移备份；若个别旧文件损坏，会跳过该文件并在同目录生成 `sqlite-migration-errors.log`。
