using CsvHelper;
using CsvHelper.Configuration;
using Hangfire;
using Npgsql;
using NpgsqlTypes;
using System.Globalization;
using System.Text;

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

        var guestMasterConnString = _config.GetConnectionString("NTouchGuestMasterDB")
            ?? _config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing connection string NTouchGuestMasterDB (or Default).");

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

            await using var conn = new NpgsqlConnection(guestMasterConnString);
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

    // Batch insert directly into target table and ignore duplicates on unique identity key.
    private static async Task<int> CopyBatchAsync(
        NpgsqlConnection conn,
        List<GuestCsvRow> batch,
        CancellationToken ct,
        ILogger<GuestCsvImporter> logger,
        ImportProgress progress)
    {
        if (batch.Count == 0)
            return 0;

        var sql = new StringBuilder();
        sql.Append(
            "INSERT INTO \"MasterGuests\" " +
            "(\"Id\",\"FirstName\",\"LastName\",\"Phone\",\"IdentityType\",\"IDSeries\",\"IdentityNumber\",\"DateOfBirth\",\"IsActive\",\"CustomerType\",\"CountryName_ar\",\"CountryName_en\",\"IdentityExpiryDate\",\"IdentityIssuePlace\",\"Email\",\"Language\",\"Gender\",\"Address\",\"City\",\"ZipCode\",\"HowDidYouFind\",\"Comment\",\"ExtraProperties\",\"ConcurrencyStamp\",\"CreationTime\",\"IsDeleted\") VALUES ");

        await using var cmd = new NpgsqlCommand { Connection = conn };

        for (var i = 0; i < batch.Count; i++)
        {
            var r = batch[i];
            var (firstName, lastName) = SplitName(r.guestName);
            var now = DateTime.UtcNow;

            if (i > 0)
                sql.Append(',');

            var rowValues = new (object? Value, NpgsqlDbType Type)[]
            {
                (Guid.NewGuid(), NpgsqlDbType.Uuid),
                (firstName, NpgsqlDbType.Text),
                (lastName, NpgsqlDbType.Text),
                (CleanOrNull(r.Phone), NpgsqlDbType.Text),
                (1, NpgsqlDbType.Integer), // IdentityType = 1 means national ID
                (null, NpgsqlDbType.Bigint),
                (CleanOrNull(r.IDNumber), NpgsqlDbType.Text),
                (null, NpgsqlDbType.Date),
                (true, NpgsqlDbType.Boolean),
                (null, NpgsqlDbType.Integer),
                (CleanOrNull(r.Name_ar), NpgsqlDbType.Text),
                (null, NpgsqlDbType.Text),
                (null, NpgsqlDbType.Date),
                (CleanOrNull(r.IDIssuePlace), NpgsqlDbType.Text),
                (null, NpgsqlDbType.Text),
                (null, NpgsqlDbType.Integer),
                (null, NpgsqlDbType.Integer),
                (null, NpgsqlDbType.Text),
                (null, NpgsqlDbType.Text),
                (null, NpgsqlDbType.Text),
                (null, NpgsqlDbType.Integer),
                (null, NpgsqlDbType.Text),
                ("{}", NpgsqlDbType.Jsonb),
                (Guid.NewGuid().ToString("N"), NpgsqlDbType.Text),
                (now, NpgsqlDbType.TimestampTz),
                (false, NpgsqlDbType.Boolean)
            };

            sql.Append('(');
            for (var col = 0; col < rowValues.Length; col++)
            {
                if (col > 0)
                    sql.Append(',');

                var paramName = $"p_{i}_{col}";
                sql.Append(AddParameter(cmd, paramName, rowValues[col].Value, rowValues[col].Type));
            }
            sql.Append(')');
        }

        sql.Append(" ON CONFLICT (\"IdentityType\",\"IdentityNumber\") DO NOTHING");
        cmd.CommandText = sql.ToString();

        var inserted = await cmd.ExecuteNonQueryAsync(ct);

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
            progress.TotalInserted + inserted,
            DateTimeOffset.UtcNow.Subtract(progress.LastUpdateUtc).TotalMilliseconds);

        return inserted;
    }

    private static (string FirstName, string LastName) SplitName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return (string.Empty, string.Empty);

        var parts = fullName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return (string.Empty, string.Empty);

        var firstName = parts[0];
        var lastName = parts.Length > 1
            ? string.Join(' ', parts.Skip(1))
            : string.Empty;

        return (firstName, lastName);
    }

    private static string? CleanOrNull(string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return clean;
    }

    private static string AddParameter(NpgsqlCommand cmd, string name, object? value, NpgsqlDbType type)
    {
        var p = cmd.Parameters.Add(name, type);
        p.Value = value ?? DBNull.Value;
        return "@" + name;
    }
}
