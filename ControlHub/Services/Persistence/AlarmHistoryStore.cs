using System.IO;
using ControlHub.Models;
using Microsoft.Data.Sqlite;

namespace ControlHub.Services.Persistence;

public sealed record AlarmQueryResult(long Total, IReadOnlyList<AlarmInfo> Records);

public sealed class AlarmHistoryStore
{
    public string DatabasePath { get; }
    public AlarmHistoryStore(string? path = null) => DatabasePath = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControlHub", "alarms.db"));

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS alarms(
                    id TEXT PRIMARY KEY, time_ticks INTEGER NOT NULL, source TEXT NOT NULL,
                    code TEXT NOT NULL, message TEXT NOT NULL, level TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_alarms_time ON alarms(time_ticks DESC, id);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public void Append(AlarmInfo alarm)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO alarms VALUES($id,$time,$source,$code,$message,$level);";
        command.Parameters.AddWithValue("$id", alarm.Id.ToString("N"));
        command.Parameters.AddWithValue("$time", alarm.OccurredAt.UtcTicks);
        command.Parameters.AddWithValue("$source", alarm.Source);
        command.Parameters.AddWithValue("$code", alarm.Code);
        command.Parameters.AddWithValue("$message", alarm.Message);
        command.Parameters.AddWithValue("$level", alarm.Level);
        command.ExecuteNonQuery();
    }

    // Date pickers use the machine's local calendar; the exclusive upper bound includes the entire final day.
    public AlarmQueryResult Query(DateTime? from, DateTime? through, int page = 0, int pageSize = 200)
    {
        if (page < 0 || pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(page));
        if (from?.Date > through?.Date) throw new ArgumentException("开始日期不能晚于结束日期。");
        long lower = from.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Local)).UtcTicks : 0;
        long upper = through.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(through.Value.Date.AddDays(1), DateTimeKind.Local)).UtcTicks : DateTimeOffset.MaxValue.UtcTicks;
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$from", lower);
        command.Parameters.AddWithValue("$until", upper);
        command.CommandText = "SELECT COUNT(*) FROM alarms WHERE time_ticks >= $from AND time_ticks < $until;";
        var total = (long)command.ExecuteScalar()!;
        command.CommandText = "SELECT id,time_ticks,source,code,message,level FROM alarms WHERE time_ticks >= $from AND time_ticks < $until ORDER BY time_ticks DESC,id LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)page * pageSize);
        using var reader = command.ExecuteReader();
        var records = new List<AlarmInfo>();
        while (reader.Read()) records.Add(new AlarmInfo
        {
            Id = Guid.ParseExact(reader.GetString(0), "N"), OccurredAt = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
            Source = reader.GetString(2), Code = reader.GetString(3), Message = reader.GetString(4), Level = reader.GetString(5),
            Status = "历史记录"
        });
        return new(total, records);
    }

    public int ClearAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM alarms;";
        return command.ExecuteNonQuery();
    }
}
