# 转盘测试流程动画

这个目录与现有 `ControlHub`、`VisionMasterHost` 完全独立，用来说明整机自动流程和验证状态机思路。

## 动画采用的工位模型

- 转盘按 16 个位置建模，每次 DD 轴只分度一格。
- 位置 0、1：第一套 XY 一次放入两个产品。
- 位置 2、3、4：T1、T2、T3，轴 13、14、15 同时下压，三个仪表并行测试不同产品。
- 位置 6、7：第二套 XY 一次取出两个产品，并分别按最终 BIN 放置。
- 每转两格，位置 0、1 会同时腾空，因此第一套 XY 补一对新料；稳定流水后，位置 6、7 也会同时到达一对完成品。

关键规则：状态按“单个产品”保存。T1 NG 的产品立即确定为 `BIN_T1_NG`，之后到 T2、T3 只跳过它，不影响同批另一件产品。

## 预览与渲染

```powershell
npm install
npm run dev
npm run still
npm run render
```

成片输出到 `out/rotary-workflow.mp4`。

## C# 流程示例

`examples/ProductionFlowDemo` 是可单独编译运行的状态机示例，不会参与设备解决方案构建。

```powershell
dotnet run --project .\examples\ProductionFlowDemo\ProductionFlowDemo.csproj
```

示例刻意把以下职责拆开：

- `ProductionOrchestrator`：整拍编排、互锁、异常退出。
- `RotaryFlowState`：16 个转盘位置、拍号、产品状态。
- `ITestStation`：仪表 START、等待结果、返回 PASS/NG。
- `ILoader` / `IUnloader`：两套 XY 的上料和按 BIN 下料。
- `IFlowPersistence`：每次检测后立即持久化，防止异常时丢失产品去向。

仪表通信超时、运动报警、急停不应转换成产品 NG；这些异常应停止自动循环并保留当前工位，等待人工恢复或重试。
