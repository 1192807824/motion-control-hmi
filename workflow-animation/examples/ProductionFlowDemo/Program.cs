using ProductionFlowDemo;

var state = new RotaryFlowState();
var orchestrator = new ProductionOrchestrator(
    state,
    new DemoIndexTable(),
    new DemoPressAxes(),
    [
        new DemoTestStation(TestStationKind.T1),
        new DemoTestStation(TestStationKind.T2),
        new DemoTestStation(TestStationKind.T3),
    ],
    new DemoLoader(),
    new DemoUnloader(),
    new DemoInterlocks(),
    new DemoPersistence()
);

await orchestrator.PrimeAsync(CancellationToken.None);

for (var beat = 1; beat <= 8; beat++)
{
    Console.WriteLine($"\n========== 第 {beat} 拍 ==========");
    await orchestrator.RunOneBeatAsync(CancellationToken.None);
}
