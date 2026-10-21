using ControlHub.Services.Devices;

namespace ControlHub.Services.Persistence;

/// <summary>UI statistics only. Complete records remain in SQLite, including raw responses.</summary>
public sealed class BatchMeasurementStatistics
{
    private readonly Dictionary<(int Station, string Instrument, string Mode, string Unit), MeasurementSummary> _groups = [];
    public int Count { get; private set; }
    public IReadOnlyDictionary<(int Station, string Instrument, string Mode, string Unit), MeasurementSummary> Groups => _groups;

    public void Add(BatchMeasurement row)
    {
        var key = (row.StationNumber, row.Instrument, row.MeasurementMode, row.Unit);
        if (!_groups.TryGetValue(key, out var summary)) _groups.Add(key, summary = new());
        summary.Add(row);
        Count++;
    }

    public void Clear() { _groups.Clear(); Count = 0; }

    public MeasurementSummary? Find(int station, string instrument, string mode, string unit) =>
        _groups.GetValueOrDefault((station, instrument, mode, unit));
}

/// <summary>Single-owner accumulator; publish background-loaded instances only after loading completes.</summary>
public sealed class MeasurementSummary
{
    private const int BinCount = 16;
    private readonly int[] _histogram = new int[BinCount];
    private int _sampleCount;
    private double _histogramLow, _binWidth;
    private double _mean, _sumSquares, _minimum, _maximum;
    public int Count { get; private set; }
    public int Passed { get; private set; }
    public int Ng { get; private set; }
    public int Unmeasured { get; private set; }
    public string Unit { get; private set; } = "";

    public void Add(BatchMeasurement row)
    {
        Count++;
        if (Count == 1) Unit = row.Unit;
        if (row.IsUnmeasured) Unmeasured++;
        else if (row.StationPassed) Passed++;
        else Ng++;
        if (!row.HasDistributionSample) return;
        var value = row.Value!.Value;
        _sampleCount++;
        var delta = value - _mean;
        _mean += delta / _sampleCount;
        _sumSquares += delta * (value - _mean);
        _minimum = _sampleCount == 1 ? value : Math.Min(_minimum, value);
        _maximum = _sampleCount == 1 ? value : Math.Max(_maximum, value);
        AddHistogram(value);
    }

    private double StandardDeviation => _sampleCount > 1
        ? Math.Sqrt(Math.Max(0, _sumSquares / (_sampleCount - 1))) : 0;

    public MeasurementDistribution Distribution => new(_sampleCount, _mean, StandardDeviation,
        _minimum, _maximum, [])
    {
        HistogramLow = _histogramLow,
        HistogramHigh = _histogramLow + BinCount * _binWidth,
        HistogramCounts = (int[])_histogram.Clone()
    };

    // Only 16 counters are retained. Expand by a factor of two and merge adjacent
    // buckets, preserving their exact frequency at the new resolution. No samples,
    // raw responses or full records are retained, and no database rescan is needed.
    private void AddHistogram(double value)
    {
        if (_sampleCount == 1)
        {
            var desiredWidth = Math.Max(Math.Abs(value) * 0.05, 0.001) / 8;
            _binWidth = Math.ScaleB(1, Math.ILogB(desiredWidth));
            _histogramLow = (Math.Floor(value / _binWidth) - 8) * _binWidth;
        }
        var deviation = StandardDeviation;
        var low = double.IsFinite(deviation) ? Math.Min(_minimum, _mean - 3.5 * deviation) : _minimum;
        var high = double.IsFinite(deviation) ? Math.Max(_maximum, _mean + 3.5 * deviation) : _maximum;
        // Extreme numeric ranges already suppress fitting in the chart. Do not loop on overflow.
        if (!double.IsFinite(low) || !double.IsFinite(high)) return;
        while (low < _histogramLow || high >= _histogramLow + BinCount * _binWidth)
        {
            var span = BinCount * _binWidth;
            if (!double.IsFinite(span * 2)) return;
            var expandLeft = low < _histogramLow;
            if (expandLeft)
            {
                _histogramLow -= span;
                for (var i = BinCount / 2 - 1; i >= 0; i--)
                    _histogram[i + BinCount / 2] = _histogram[2 * i] + _histogram[2 * i + 1];
                Array.Clear(_histogram, 0, BinCount / 2);
            }
            else
            {
                for (var i = 0; i < BinCount / 2; i++)
                    _histogram[i] = _histogram[2 * i] + _histogram[2 * i + 1];
                Array.Clear(_histogram, BinCount / 2, BinCount / 2);
            }
            _binWidth *= 2;
        }
        var bin = Math.Clamp((int)((value - _histogramLow) / _binWidth), 0, BinCount - 1);
        _histogram[bin]++;
    }
}
