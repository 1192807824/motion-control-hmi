using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ControlHub.Services.Persistence;

public sealed record BatchMeasurement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string BatchNumber { get; init; } = "";
    public Guid ProductId { get; init; }
    public DateTimeOffset MeasuredAt { get; init; } = DateTimeOffset.Now;
    public int StationNumber { get; init; }
    public string Instrument { get; init; } = "";
    public string MeasurementMode { get; init; } = "";
    public double? Value { get; init; }
    public string Unit { get; init; } = "";
    public double? DissipationFactor { get; init; }
    public bool ValidReading { get; init; }
    public bool StationPassed { get; init; }
    public bool ProductPassedSoFar { get; init; }
    public string Bin { get; init; } = "";
    public int InstrumentStatus { get; init; }
    public string Description { get; init; } = "";
    public string RawResponse { get; init; } = "";
    public double? ElapsedSeconds { get; init; }
    public bool TimedOut { get; init; }
    public int Attempts { get; init; }
    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
    public double? MaximumTestSeconds { get; init; }
    public string StationResult => StationPassed ? "OK" : "NG";
    public string ProductResult => ProductPassedSoFar ? "OK（截至本站）" : "NG";
    public string LocalTime => MeasuredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
}

/// <summary>
/// Embedded SQLite storage. Each completed station result is committed in its own transaction.
/// </summary>
public sealed class BatchMeasurementStore
{
    private const string LegacyMigrationKey = "legacy_json_migration_v1";
    private static readonly ConcurrentDictionary<string, object> InitializationLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> LegacyScans = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _connectionString;

    public string RootDirectory { get; }
    public string DatabasePath { get; }

    public BatchMeasurementStore(string? directory = null)
    {
        RootDirectory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControlHub", "batches"));
        DatabasePath = Path.Combine(RootDirectory, "batch-measurements.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 10
        }.ToString();

        var gate = InitializationLocks.GetOrAdd(DatabasePath, static _ => new object());
        lock (gate)
        {
            Directory.CreateDirectory(RootDirectory);
            InitializeDatabase();
            if (LegacyScans.TryAdd(DatabasePath, 0))
            {
                try { MigrateLegacyJson(); }
                catch { LegacyScans.TryRemove(DatabasePath, out _); throw; }
            }
        }
    }

    public static string NormalizeBatchNumber(string batch)
    {
        var value = batch.Trim();
        if (value.Length is 0 or > 80 || value.Any(char.IsControl))
            throw new ArgumentException("请输入1～80个字符的批次号，不能包含换行或控制字符。");
        return value;
    }

    public void PrepareBatch(string batch)
    {
        batch = NormalizeBatchNumber(batch);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO batches(batch_number, created_utc_ticks, last_measurement_utc_ticks)
            VALUES($batch, $now, NULL)
            ON CONFLICT(batch_number) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$batch", batch);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        command.ExecuteNonQuery();
    }

    public void Append(BatchMeasurement record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var batch = NormalizeBatchNumber(record.BatchNumber);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        UpsertBatch(connection, transaction, batch, record.MeasuredAt.UtcTicks);
        InsertMeasurement(connection, transaction, record with { BatchNumber = batch }, ignoreDuplicate: false);
        transaction.Commit();
    }

    public IReadOnlyList<string> ListBatches()
    {
        using var connection = OpenConnection(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch_number FROM batches
            ORDER BY COALESCE(last_measurement_utc_ticks, created_utc_ticks) DESC, batch_number DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<BatchMeasurement> ReadBatch(string batch)
    {
        batch = NormalizeBatchNumber(batch);
        using var connection = OpenConnection(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, batch_number, product_id, measured_at_utc_ticks, measured_at_offset_minutes,
                   station_number, instrument, measurement_mode, value, unit, dissipation_factor,
                   valid_reading, station_passed, product_passed_so_far, bin, instrument_status,
                   description, raw_response, elapsed_seconds, timed_out, attempts,
                   lower_limit, upper_limit, maximum_test_seconds
            FROM measurements WHERE batch_number = $batch
            ORDER BY measured_at_utc_ticks, id;
            """;
        command.Parameters.AddWithValue("$batch", batch);
        using var reader = command.ExecuteReader();
        var result = new List<BatchMeasurement>();
        while (reader.Read()) result.Add(ReadMeasurement(reader));
        return result;
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS batches(
                batch_number TEXT PRIMARY KEY NOT NULL,
                created_utc_ticks INTEGER NOT NULL,
                last_measurement_utc_ticks INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS measurements(
                id TEXT PRIMARY KEY NOT NULL,
                batch_number TEXT NOT NULL REFERENCES batches(batch_number) ON UPDATE CASCADE ON DELETE RESTRICT,
                product_id TEXT NOT NULL,
                measured_at_utc_ticks INTEGER NOT NULL,
                measured_at_offset_minutes INTEGER NOT NULL,
                station_number INTEGER NOT NULL,
                instrument TEXT NOT NULL,
                measurement_mode TEXT NOT NULL,
                value REAL NULL,
                unit TEXT NOT NULL,
                dissipation_factor REAL NULL,
                valid_reading INTEGER NOT NULL CHECK(valid_reading IN (0, 1)),
                station_passed INTEGER NOT NULL CHECK(station_passed IN (0, 1)),
                product_passed_so_far INTEGER NOT NULL CHECK(product_passed_so_far IN (0, 1)),
                bin TEXT NOT NULL,
                instrument_status INTEGER NOT NULL,
                description TEXT NOT NULL,
                raw_response TEXT NOT NULL,
                elapsed_seconds REAL NULL,
                timed_out INTEGER NOT NULL CHECK(timed_out IN (0, 1)),
                attempts INTEGER NOT NULL,
                lower_limit REAL NULL,
                upper_limit REAL NULL,
                maximum_test_seconds REAL NULL
            );
            CREATE INDEX IF NOT EXISTS ix_measurements_batch_time ON measurements(batch_number, measured_at_utc_ticks, id);
            CREATE INDEX IF NOT EXISTS ix_measurements_batch_station ON measurements(batch_number, station_number, instrument, measurement_mode);
            CREATE INDEX IF NOT EXISTS ix_measurements_product ON measurements(product_id, measured_at_utc_ticks);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection(bool readOnly = false)
    {
        var connectionString = readOnly
            ? new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString()
            : _connectionString;
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = readOnly
            ? "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;"
            : "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void MigrateLegacyJson()
    {
        var errors = new List<string>();
        var batchFiles = Directory.EnumerateFiles(RootDirectory, "batch.json", SearchOption.AllDirectories).ToArray();
        var measurementFiles = Directory.EnumerateFiles(RootDirectory, "*.json", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), "batch.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        using var target = OpenConnection();
        using var transaction = target.BeginTransaction();

        foreach (var file in batchFiles)
        {
            try
            {
                var batch = JsonSerializer.Deserialize<string>(File.ReadAllText(file));
                if (!string.IsNullOrWhiteSpace(batch))
                    UpsertBatch(target, transaction, NormalizeBatchNumber(batch), DateTimeOffset.UtcNow.UtcTicks);
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
            {
                errors.Add($"{file}: {exception.Message}");
            }
        }

        foreach (var file in measurementFiles)
        {
            try
            {
                var record = JsonSerializer.Deserialize<BatchMeasurement>(File.ReadAllText(file))
                    ?? throw new InvalidDataException("JSON内容为空。");
                var batch = NormalizeBatchNumber(record.BatchNumber);
                UpsertBatch(target, transaction, batch, record.MeasuredAt.UtcTicks);
                InsertMeasurement(target, transaction, record with { BatchNumber = batch }, ignoreDuplicate: true);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException or SqliteException)
            {
                errors.Add($"{file}: {exception.Message}");
            }
        }

        using (var marker = target.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES($key, $value);";
            marker.Parameters.AddWithValue("$key", LegacyMigrationKey);
            marker.Parameters.AddWithValue("$value", JsonSerializer.Serialize(new
            {
                CompletedAt = DateTimeOffset.Now, BatchFiles = batchFiles.Length,
                MeasurementFiles = measurementFiles.Length, Errors = errors.Count
            }));
            marker.ExecuteNonQuery();
        }
        transaction.Commit();

        if (errors.Count > 0)
            File.WriteAllLines(Path.Combine(RootDirectory, "sqlite-migration-errors.log"),
                new[] { $"{DateTimeOffset.Now:O} JSON迁移完成，但有 {errors.Count} 个文件无法导入：" }.Concat(errors));
    }

    private static void UpsertBatch(SqliteConnection connection, SqliteTransaction transaction, string batch, long measuredAtUtcTicks)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO batches(batch_number, created_utc_ticks, last_measurement_utc_ticks)
            VALUES($batch, $time, $time)
            ON CONFLICT(batch_number) DO UPDATE SET last_measurement_utc_ticks = CASE
                WHEN batches.last_measurement_utc_ticks IS NULL OR excluded.last_measurement_utc_ticks > batches.last_measurement_utc_ticks
                THEN excluded.last_measurement_utc_ticks ELSE batches.last_measurement_utc_ticks END;
            """;
        command.Parameters.AddWithValue("$batch", batch);
        command.Parameters.AddWithValue("$time", measuredAtUtcTicks);
        command.ExecuteNonQuery();
    }

    private static void InsertMeasurement(SqliteConnection connection, SqliteTransaction transaction,
        BatchMeasurement record, bool ignoreDuplicate)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $$"""
            INSERT {{(ignoreDuplicate ? "OR IGNORE" : "")}} INTO measurements(
                id, batch_number, product_id, measured_at_utc_ticks, measured_at_offset_minutes,
                station_number, instrument, measurement_mode, value, unit, dissipation_factor,
                valid_reading, station_passed, product_passed_so_far, bin, instrument_status,
                description, raw_response, elapsed_seconds, timed_out, attempts,
                lower_limit, upper_limit, maximum_test_seconds)
            VALUES($id, $batch, $product, $time, $offset, $station, $instrument, $mode, $value, $unit, $loss,
                $valid, $station_passed, $product_passed, $bin, $status, $description, $raw, $elapsed,
                $timed_out, $attempts, $lower, $upper, $maximum);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("N"));
        command.Parameters.AddWithValue("$batch", record.BatchNumber);
        command.Parameters.AddWithValue("$product", record.ProductId.ToString("N"));
        command.Parameters.AddWithValue("$time", record.MeasuredAt.UtcTicks);
        command.Parameters.AddWithValue("$offset", checked((int)record.MeasuredAt.Offset.TotalMinutes));
        command.Parameters.AddWithValue("$station", record.StationNumber);
        command.Parameters.AddWithValue("$instrument", record.Instrument);
        command.Parameters.AddWithValue("$mode", record.MeasurementMode);
        command.Parameters.AddWithValue("$value", DbValue(record.Value));
        command.Parameters.AddWithValue("$unit", record.Unit);
        command.Parameters.AddWithValue("$loss", DbValue(record.DissipationFactor));
        command.Parameters.AddWithValue("$valid", record.ValidReading ? 1 : 0);
        command.Parameters.AddWithValue("$station_passed", record.StationPassed ? 1 : 0);
        command.Parameters.AddWithValue("$product_passed", record.ProductPassedSoFar ? 1 : 0);
        command.Parameters.AddWithValue("$bin", record.Bin);
        command.Parameters.AddWithValue("$status", record.InstrumentStatus);
        command.Parameters.AddWithValue("$description", record.Description);
        command.Parameters.AddWithValue("$raw", record.RawResponse);
        command.Parameters.AddWithValue("$elapsed", DbValue(record.ElapsedSeconds));
        command.Parameters.AddWithValue("$timed_out", record.TimedOut ? 1 : 0);
        command.Parameters.AddWithValue("$attempts", record.Attempts);
        command.Parameters.AddWithValue("$lower", DbValue(record.LowerLimit));
        command.Parameters.AddWithValue("$upper", DbValue(record.UpperLimit));
        command.Parameters.AddWithValue("$maximum", DbValue(record.MaximumTestSeconds));
        command.ExecuteNonQuery();
    }

    private static object DbValue(double? value) => value.HasValue && double.IsFinite(value.Value) ? value.Value : DBNull.Value;

    private static BatchMeasurement ReadMeasurement(SqliteDataReader reader)
    {
        var utc = new DateTimeOffset(new DateTime(reader.GetInt64(3), DateTimeKind.Utc));
        return new BatchMeasurement
        {
            Id = ParseGuid(reader.GetString(0)), BatchNumber = reader.GetString(1), ProductId = ParseGuid(reader.GetString(2)),
            MeasuredAt = utc.ToOffset(TimeSpan.FromMinutes(reader.GetInt32(4))), StationNumber = reader.GetInt32(5),
            Instrument = reader.GetString(6), MeasurementMode = reader.GetString(7), Value = NullableDouble(reader, 8),
            Unit = reader.GetString(9), DissipationFactor = NullableDouble(reader, 10), ValidReading = reader.GetInt32(11) != 0,
            StationPassed = reader.GetInt32(12) != 0, ProductPassedSoFar = reader.GetInt32(13) != 0,
            Bin = reader.GetString(14), InstrumentStatus = reader.GetInt32(15), Description = reader.GetString(16),
            RawResponse = reader.GetString(17), ElapsedSeconds = NullableDouble(reader, 18), TimedOut = reader.GetInt32(19) != 0,
            Attempts = reader.GetInt32(20), LowerLimit = NullableDouble(reader, 21), UpperLimit = NullableDouble(reader, 22),
            MaximumTestSeconds = NullableDouble(reader, 23)
        };
    }

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    private static Guid ParseGuid(string value) => Guid.TryParseExact(value, "N", out var guid) || Guid.TryParse(value, out guid) ? guid : Guid.Empty;
}
