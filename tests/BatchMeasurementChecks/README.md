# 批次采集检查

运行：`dotnet run --project tests/BatchMeasurementChecks/BatchMeasurementChecks.csproj`

不连接运动卡或实际仪表。验证 SQLite 文件格式、旧 JSON 自动迁移、批次隔离、重启后读取、双站并发逐条提交、非法批次号、均值/样本标准差、单/双站图表、E4981A 的 nF 与损耗 D、SM7110 达标值与超时末次读数、无效数值、跨站产品编号和数据库故障上报。使用真实主页和查询控件离屏渲染模拟数据，图片输出至 `outputs/batch-measurement-qa`。

配合 `tests/TestStationFlowChecks` 回归现有仪表协议、阈值判定、重测、停止放电与 DD 安全联锁。

另验证查询刷新与快照、切换批次/空结果禁止导出，以及 CSV 全字段、UTF-8 BOM、中文/逗号/引号/换行、跨区域小数格式、空值、公式样式文本和目标文件被占用时保留旧文件。
