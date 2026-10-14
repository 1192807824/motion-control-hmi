using System.Globalization;
using System.IO;
using System.Text;

namespace ControlHub.Services.Persistence;

public static class BatchMeasurementCsvExporter
{
    public static string SuggestedFileName(string batch)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(batch.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray());
        return $"批次_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
    }

    public static void Export(string path, IReadOnlyList<BatchMeasurement> records)
    {
        if (records.Count == 0) throw new InvalidOperationException("当前没有可导出的查询数据。");
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, leaveOpen: true))
                {
                    writer.NewLine = "\r\n";
                    writer.WriteLine("序号,批次号,采集时间,工位,仪表,模式,最终值,单位,损耗D,本站结果,产品结果,BIN,耗时(s),超时,测量轮数,有效读数,下限,上限,最长时间(s),仪表状态,产品编号,说明,原始响应,记录编号");
                    for (var i = 0; i < records.Count; i++)
                    {
                        var row = records[i];
                        writer.WriteLine(string.Join(",", new[] {
                            Number(i + 1), Text(row.BatchNumber), Text(row.MeasuredAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)),
                            Number(row.StationNumber), Text(row.Instrument), Text(row.MeasurementMode), Number(row.Value), Text(row.Unit),
                            Number(row.DissipationFactor), Text(row.StationResult), Text(row.ProductResult), Text(row.Bin), Number(row.ElapsedSeconds),
                            Text(row.TimedOut ? "是" : "否"), Number(row.Attempts), Text(row.ValidReading ? "是" : "否"),
                            Number(row.LowerLimit), Number(row.UpperLimit), Number(row.MaximumTestSeconds), Number(row.InstrumentStatus),
                            Text(row.ProductId.ToString()), Text(row.Description), Text(row.RawResponse), Text(row.Id.ToString())
                        }));
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Number(double? value) => value.HasValue && double.IsFinite(value.Value)
        ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";

    private static string Text(string value)
    {
        // User/device text must not become a formula when opened in Excel.
        var significant = value.AsSpan().TrimStart();
        if (!significant.IsEmpty && "=+-@".Contains(significant[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
