using System.Globalization;
using System.IO;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;
using VisionMasterHost;

internal static class Program
{
    private static readonly VisionCalibrationSnapshot Calibration = new(VisionCalibrationAxisSet.First,
        "First", 1, 2, "test.xml", true, "", false, 200000, true, 1200, -3400, true, -5600, 7800);
    private static readonly CalibrationCenterPosition Position = new(1, 2, 100000, 200000);
    private static readonly RecognizedCenterResult Result = new(3165.894, 1706.633, 10.25, 19.5,
        2735.5, 1823.5, 10, 20);

    private static async Task Main()
    {
        // 现场同一条通用错误曾混淆NG、无目标、多目标和结果缺失；逐项保留原因。
        Require(RecognizedCenterValidation.GetMatchError(1, 1, 1) is null, "One valid rectangle is accepted");
        var invalidMatches = new[] { (0, 1, 1), (1, 0, 0), (1, 2, 2), (1, 1, 0) };
        var errors = invalidMatches.Select(value => RecognizedCenterValidation.GetMatchError(value.Item1, value.Item2, value.Item3)).ToArray();
        Require(errors.All(error => !string.IsNullOrWhiteSpace(error)) && errors.Distinct().Count() == 4,
            "NG, zero targets, multiple targets and missing rectangles have distinct errors");
        var sdkFailure = new Exception("SDK parameter error");
        var stagedFailure = new RecognizedCenterStageException("读取107本次图像尺寸", "错误码 0xE0000001", sdkFailure);
        Require(stagedFailure.Message.Contains("读取107本次图像尺寸") && stagedFailure.Message.Contains("0xE0000001") &&
                ReferenceEquals(stagedFailure.InnerException, sdkFailure), "Stage, SDK code and original exception are retained");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Require(RecognizedCenterResult.Parse("3165.894\t1706.633\t10.25\t19.5\t2735.5\t1823.5\t10\t20") == Result,
                "Protocol uses invariant culture");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        Reject<InvalidDataException>(() => RecognizedCenterResult.Parse("1\t2"));
        Reject<InvalidDataException>(() => RecognizedCenterResult.Parse("NaN\t1\t1\t1\t100\t100\t1\t1"));
        Reject<InvalidDataException>(() => (Result with { PixelX = 5472 }).Validate());
        Reject<InvalidDataException>(() => (Result with { PixelY = -1 }).Validate());
        Reject<InvalidDataException>(() => (Result with { CenterPixelX = 0 }).Validate());
        Reject<InvalidDataException>(() => (Result with { TransformedX = double.PositiveInfinity }).Validate());

        var first = RecognizedCenterMove.Plan(Result, Position, Calibration, VisionTargetTool.Nozzle1);
        var second = RecognizedCenterMove.Plan(Result, Position, Calibration, VisionTargetTool.Nozzle2);
        Require(first == new VisionMotionTarget(98700, 201600, VisionTargetTool.Nozzle1), "Nozzle 1 applies signed center delta and its offset");
        Require(second == new VisionMotionTarget(91900, 212800, VisionTargetTool.Nozzle2), "Nozzle 2 uses its independent offset");
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position, Calibration, VisionTargetTool.Camera));
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position, Calibration with { Nozzle1Calibrated = false }, VisionTargetTool.Nozzle1));
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position, Calibration with { Nozzle2Calibrated = false }, VisionTargetTool.Nozzle2));
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position, Calibration with { AxisSet = VisionCalibrationAxisSet.Second }, VisionTargetTool.Nozzle1));
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position with { XHardwareAxisNo = 3 }, Calibration, VisionTargetTool.Nozzle1));
        Reject<InvalidOperationException>(() => RecognizedCenterMove.Plan(Result, Position with { ActualX = double.NaN }, Calibration, VisionTargetTool.Nozzle1));

        var captures = 0;
        var moves = 0;
        Task Move(VisionMotionTarget target, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Require(target == first, "Execution passes the planned nozzle target");
            moves++;
            return Task.CompletedTask;
        }
        CalibrationCenterPosition Capture() { captures++; return Position; }
        var target = await RecognizedCenterMove.ExecuteAsync(Calibration, VisionTargetTool.Nozzle1, Capture,
            _ => Task.FromResult(Result), Move, CancellationToken.None);
        Require(target == first && moves == 1 && captures == 2, "Recognition success moves once after position validation");
        moves = captures = 0;
        await RejectAsync<InvalidOperationException>(() => RecognizedCenterMove.ExecuteAsync(Calibration,
            VisionTargetTool.Nozzle1, Capture, _ => throw new InvalidOperationException("Vision failed"), Move, CancellationToken.None));
        Require(moves == 0, "Vision failure never moves");
        using var cancellation = new CancellationTokenSource();
        await RejectAsync<OperationCanceledException>(() => RecognizedCenterMove.ExecuteAsync(Calibration,
            VisionTargetTool.Nozzle1, Capture, _ => { cancellation.Cancel(); return Task.FromResult(Result); }, Move, cancellation.Token));
        Require(moves == 0, "Late vision response after cancellation never moves");
        foreach (var changed in new[] { Position with { ActualX = 100011 }, Position with { ActualY = 200011 },
                     Position with { YHardwareAxisNo = 4 }, Position with { ActualX = double.NaN } })
        {
            captures = 0;
            await RejectAsync<InvalidOperationException>(() => RecognizedCenterMove.ExecuteAsync(Calibration,
                VisionTargetTool.Nozzle1, () => ++captures == 1 ? Position : changed,
                _ => Task.FromResult(Result), Move, CancellationToken.None));
            Require(moves == 0, "Position drift or wrong axis never moves");
        }
        await RejectAsync<InvalidDataException>(() => RecognizedCenterMove.ExecuteAsync(Calibration,
            VisionTargetTool.Nozzle1, Capture, _ => Task.FromResult(Result with { PixelX = -1 }), Move, CancellationToken.None));
        Require(moves == 0, "Invalid detection never moves");
        Console.WriteLine("PASS: protocol parsing, independent nozzle targets, first XY restriction, invalid results, recognition failure, cancellation, position drift. No hardware accessed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
