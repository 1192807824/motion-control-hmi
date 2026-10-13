using System.Globalization;
using System.IO;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
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
        // 最终有效性由110脚本结果决定，不再依赖108匹配框数量。
        Require(RecognizedCenterValidation.GetScriptError(1, 1) is null, "One valid script row is accepted");
        var invalidMatches = new[] { (0, 1), (1, 0), (1, 2), (1, -1) };
        var errors = invalidMatches.Select(value => RecognizedCenterValidation.GetScriptError(value.Item1, value.Item2)).ToArray();
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
        CheckAlignmentCorrection(first, second);
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
        Console.WriteLine("PASS: protocol parsing, independent nozzle targets, first XY restriction, invalid results, recognition failure, cancellation, position drift, signed XY fine correction, stale session rejection, corrected profile save/reload. No hardware accessed.");
    }

    private static void CheckAlignmentCorrection(VisionMotionTarget first, VisionMotionTarget second)
    {
        foreach (var target in new[] { first, second })
        {
            // Arrived feedback can differ from the commanded target. The saved offset must include it.
            var arrived = new CalibrationCenterPosition(1, 2, target.X + 2, target.Y - 3);
            var session = new NozzleAlignmentCorrection(Calibration, target, arrived);
            Require(session.Matches(Calibration, target.Tool), "Original nozzle calibration remains valid");
            Require(!session.Matches(Calibration, target.Tool == VisionTargetTool.Nozzle1 ? VisionTargetTool.Nozzle2 : VisionTargetTool.Nozzle1),
                "Switching nozzles invalidates the correction");
            Require(!session.Matches(Calibration with { AxisSet = VisionCalibrationAxisSet.Second }, target.Tool), "Other XY set rejected");
            Require(!session.Matches(Calibration with { CalibrationFilePath = "other.xml" }, target.Tool), "Other calibration file rejected");
            Require(!session.Matches(Calibration with { Nozzle1OffsetX = 0, Nozzle2OffsetX = 0 }, target.Tool), "Recalibration invalidates correction");
            session.EnsurePositionUnchanged(arrived, 10);
            Reject<InvalidOperationException>(() => session.EnsurePositionUnchanged(arrived with { ActualX = arrived.ActualX + 11 }, 10));
            Reject<InvalidOperationException>(() => session.RecordMove(arrived with { ActualY = double.NaN }));
            Reject<InvalidOperationException>(() => session.RecordMove(arrived with { XHardwareAxisNo = 3 }));
            session.RecordMove(arrived with { ActualX = arrived.ActualX + 100 });
            var aligned = arrived with { ActualX = arrived.ActualX + 75, ActualY = arrived.ActualY - 50 };
            session.RecordMove(aligned);
            var offset = session.CalculateOffset(aligned, 10);
            var oldX = target.Tool == VisionTargetTool.Nozzle1 ? Calibration.Nozzle1OffsetX : Calibration.Nozzle2OffsetX;
            var oldY = target.Tool == VisionTargetTool.Nozzle1 ? Calibration.Nozzle1OffsetY : Calibration.Nozzle2OffsetY;
            Require(offset == (oldX + 77, oldY - 53), "Correction uses signed actual delta from the original target");
            Require(session.CalculateOffset(aligned, 10) == offset, "Repeated calculation does not accumulate correction");
            var updated = target.Tool == VisionTargetTool.Nozzle1
                ? Calibration with { Nozzle1OffsetX = offset.X, Nozzle1OffsetY = offset.Y }
                : Calibration with { Nozzle2OffsetX = offset.X, Nozzle2OffsetY = offset.Y };
            Require(!session.Matches(updated, target.Tool), "A saved correction cannot be reused with the new calibration");
            var next = RecognizedCenterMove.Plan(Result, Position, updated, target.Tool);
            Require(next.X == aligned.ActualX && next.Y == aligned.ActualY, "Next recognition moves directly to the corrected target");
            var otherTool = target.Tool == VisionTargetTool.Nozzle1 ? VisionTargetTool.Nozzle2 : VisionTargetTool.Nozzle1;
            Require(RecognizedCenterMove.Plan(Result, Position, updated, otherTool) == (otherTool == VisionTargetTool.Nozzle1 ? first : second),
                "Other nozzle target remains unchanged");

            var directory = Path.Combine(Path.GetTempPath(), "nozzle-alignment-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var xml = Path.Combine(directory, "calibration.xml");
            File.WriteAllText(xml, "test fixture; not used for transformation");
            var profile = new VisionCalibrationProfile
            {
                CalibrationFilePath = xml,
                Nozzle1Calibrated = true, Nozzle2Calibrated = true,
                Nozzle1OffsetXPulses = updated.Nozzle1OffsetX, Nozzle1OffsetYPulses = updated.Nozzle1OffsetY,
                Nozzle2OffsetXPulses = updated.Nozzle2OffsetX, Nozzle2OffsetYPulses = updated.Nozzle2OffsetY
            };
            var store = new VisionCalibrationProfileStore();
            var path = Path.Combine(directory, "nozzles.json");
            store.Save(path, profile);
            var reloaded = store.Load(path);
            Require(reloaded.Nozzle1OffsetXPulses == updated.Nozzle1OffsetX && reloaded.Nozzle1OffsetYPulses == updated.Nozzle1OffsetY &&
                reloaded.Nozzle2OffsetXPulses == updated.Nozzle2OffsetX && reloaded.Nozzle2OffsetYPulses == updated.Nozzle2OffsetY,
                "Saved profile reloads corrected and untouched nozzle offsets");
        }
        Reject<InvalidOperationException>(() => new NozzleAlignmentCorrection(Calibration, first with { Tool = VisionTargetTool.Camera }, Position));
        Reject<InvalidOperationException>(() => new NozzleAlignmentCorrection(Calibration with { Nozzle1Calibrated = false }, first, Position));
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
