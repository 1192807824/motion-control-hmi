using System.IO;
using ControlHub.Services.Persistence;
using Microsoft.Data.Sqlite;

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var root = Path.Combine(Path.GetTempPath(), "batch-storage-checks-" + Guid.NewGuid().ToString("N"));
var store = new BatchMeasurementStore(root);
store.Append(new BatchMeasurement { BatchNumber = "existing", Value = 42, StationNumber = 1 });

if (args is ["--shared-cache-repro"])
{
    // Reproduce the previously shipped connection policy without changing production files.
    var field = typeof(BatchMeasurementStore).GetField("_connectionString",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var shared = new SqliteConnectionStringBuilder((string)field.GetValue(store)!) { Cache = SqliteCacheMode.Shared };
    field.SetValue(store, shared.ToString());
    SqliteConnection.ClearAllPools();
    store.ReadBatch("next-batch");
    try
    {
        store.PrepareBatch("next-batch");
        throw new Exception("Old shared-cache policy did not reproduce the reported failure.");
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 8)
    {
        Console.WriteLine($"REPRODUCED: old shared-cache policy after preview: {exception.Message}");
    }
    return;
}

// Simulate idle connection eviction, then a preview opening the database first.
// Disposing the preview keeps its read-only connection alive in the pool.
SqliteConnection.ClearAllPools();
Require(store.ReadBatch("existing").Single().Value == 42, "Preview lost existing data.");
store.PrepareBatch("0001");
store.Append(new BatchMeasurement { BatchNumber = "0001", Value = 123, StationNumber = 1 });
Require(store.ReadBatch("0001").Single().Value == 123, "Write after read-only preview failed.");
Console.WriteLine("PASS: read-only preview before production preparation and measurement write.");

SqliteConnection.ClearAllPools();
Require(store.ListBatches().Contains("0001"), "Batch listing lost data.");
var reopened = new BatchMeasurementStore(root);
reopened.PrepareBatch("0002");
Require(reopened.ReadBatch("existing").Single().Value == 42, "Reopening changed existing records.");
Console.WriteLine("PASS: batch listing before store initialization; existing data preserved.");

for (var i = 0; i < 20; i++)
{
    // Stop releases the collection lease; changing the batch opens a read-only preview.
    using (reopened.BeginCollection()) reopened.PrepareBatch("previous-batch");
    SqliteConnection.ClearAllPools();
    var batch = $"restart-{i}";
    Require(reopened.ReadBatch(batch).Count == 0, "New batch should initially be empty.");
    using (reopened.BeginCollection())
    {
        reopened.PrepareBatch(batch);
        reopened.Append(new BatchMeasurement { BatchNumber = batch, Value = i });
    }
    Require(reopened.ReadBatch(batch).Single().Value == i, "Batch switch after stop lost its measurement.");
}
Console.WriteLine("PASS: 20 stop/preview/new-batch/restart storage cycles.");

Parallel.For(0, 40, i =>
{
    reopened.ReadBatch("0001");
    reopened.Append(new BatchMeasurement { BatchNumber = "concurrent", Value = i, StationNumber = i % 3 + 1 });
});
Require(reopened.ReadBatch("concurrent").Count == 40, "Concurrent queries/writes lost records.");
SqliteConnection.ClearAllPools();
Require(new BatchMeasurementStore(root).ReadBatch("concurrent").Count == 40, "Records did not persist.");
Console.WriteLine("PASS: concurrent queries/writes and persistence after reopening.");
Console.WriteLine($"Isolated test database: {root}");
