using CsvHelper;
using CsvHelper.Configuration;
using Hangfire;
using Npgsql;
using System.Globalization;

namespace GuestImporterApi.Services;

public class GuestCsvImporter
{
    private readonly IConfiguration _config;

    // Injection of logger
    private readonly ILogger<GuestCsvImporter> _logger;
    private readonly ImportProgressStore _store;


    public GuestCsvImporter(
        IConfiguration config,
        ILogger<GuestCsvImporter> logger,
        ImportProgressStore store)
    {
        _config = config;
        _logger = logger;
        _store = store;
    }

    // DTO mapping the CSV columns
    private sealed class GuestCsvRow
    {
        public string? guestName { get; set; }
        public string? IDNumber { get; set; }
        public string? IDIssuePlace { get; set; }
        public string? Phone { get; set; }
        public string? PassportNumber { get; set; }
        public string? Name_ar { get; set; }
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ImportAsync(
        Guid jobId,
        string csvFilePath,
        int batchSize,
        //ImportProgress progress,
        CancellationToken ct)
    {
        // Get progress object from store
        var progress = _store.GetOrCreate(jobId);

        progress.IsRunning = true;
        progress.IsCompleted = false;
        progress.Error = null;

        var connString = _config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing connection string Default");

        try
        {
            // Stream file: does NOT load into memory
            using var fs = File.OpenRead(csvFilePath);
            using var reader = new StreamReader(fs);

            // CsvHelper reads from TextReader in streaming mode
            var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                BadDataFound = null,            // skip bad-data callback
                MissingFieldFound = null,       // ignore missing fields
                HeaderValidated = null,         // ignore header validation
                DetectDelimiter = true
            };

            using var csv = new CsvReader(reader, csvConfig);

            // If CSV column names differ, add ClassMap instead.
            var records = csv.GetRecords<GuestCsvRow>();

            var buffer = new List<GuestCsvRow>(batchSize);

            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync(ct);

            // Iterate records one-by-one (streaming)
            foreach (var r in records)
            {
                ct.ThrowIfCancellationRequested();

                progress.TotalRead++;
                buffer.Add(r);

                if (buffer.Count >= batchSize)
                {
                    var inserted = await CopyBatchAsync(conn, buffer, ct, _logger, progress);
                    progress.TotalInserted += inserted;
                    progress.LastBatchSize = inserted;
                    progress.LastUpdateUtc = DateTimeOffset.UtcNow;

                    buffer.Clear();
                }
            }

            // final partial batch
            if (buffer.Count > 0)
            {
                var inserted = await CopyBatchAsync(conn, buffer, ct, _logger, progress);
                progress.TotalInserted += inserted;
                progress.LastBatchSize = inserted;
                progress.LastUpdateUtc = DateTimeOffset.UtcNow;
                buffer.Clear();
            }

            progress.IsCompleted = true;
        }
        catch (OperationCanceledException)
        {
            progress.Error = "Import cancelled.";
        }
        catch (Exception ex)
        {
            progress.Error = ex.Message;
            throw;
        }
        finally
        {
            progress.IsRunning = false;
            progress.LastUpdateUtc = DateTimeOffset.UtcNow;
        }
    }

    // Fast bulk insert using COPY BINARY
    private static async Task<int> CopyBatchAsync(
        NpgsqlConnection conn,
        List<GuestCsvRow> batch,
        CancellationToken ct,
        ILogger<GuestCsvImporter> logger,
        ImportProgress progress)
    {
        // NOTE: column order must match table columns in COPY command
        // Also: use quoting for PascalCase table/columns if created with quotes.
        //var copyCmd = """
        //    COPY "Guest" ("GuestId","FirstName","LastName","Email","Phone","CreatedAt")
        //    FROM STDIN (FORMAT BINARY)
        //    """;

        var copyCmd = """
            COPY "Guest"
            ("Id","guestName","IDNumber","IDIssuePlace","Phone","PassportNumber","Name_ar")
            FROM STDIN (FORMAT BINARY)
            """;


        await using var writer = await conn.BeginBinaryImportAsync(copyCmd, ct);

        foreach (var r in batch)
        {
            await writer.StartRowAsync(ct);
            //Id auto generate by PostgreSQL
            writer.Write(Guid.NewGuid(), NpgsqlTypes.NpgsqlDbType.Uuid);
            writer.Write(r.guestName, NpgsqlTypes.NpgsqlDbType.Text);
            writer.Write(r.IDNumber, NpgsqlTypes.NpgsqlDbType.Text);
            writer.Write(r.IDIssuePlace, NpgsqlTypes.NpgsqlDbType.Text);
            writer.Write(r.Phone, NpgsqlTypes.NpgsqlDbType.Text);
            writer.Write(r.PassportNumber, NpgsqlTypes.NpgsqlDbType.Text);
            writer.Write(r.Name_ar, NpgsqlTypes.NpgsqlDbType.Text);
        }

        await writer.CompleteAsync(ct);

        //logger.LogInformation(
        //    "Batch inserted. Read={Read}, Inserted={Inserted}, LastBatch={Batch}, Time={Time}",
        //progress.TotalRead,
        //progress.TotalInserted + batch.Count,  // use + batch.Count to show updated total
        //batch.Count,
        //DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Batch {BatchSize} inserted successfully. Total read: {TotalRead}, Total inserted: {TotalInserted}, Elapsed: {ElapsedMs}ms",
            batch.Count,
            progress.TotalRead,
            progress.TotalInserted,
            DateTimeOffset.UtcNow.Subtract(progress.LastUpdateUtc).TotalMilliseconds);

        return batch.Count;
    }
}
