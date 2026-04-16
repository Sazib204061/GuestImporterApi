# Guest Importer API - Project Documentation

## 1. Project Overview
The **GuestImporterApi** is a high-performance ASP.NET Core Web API designed to import massive CSV datasets (10M+ rows) into a PostgreSQL database. It addresses common challenges such as memory overflows, slow insertion speeds, and lack of progress tracking by implementing a streaming, batch-processing architecture.

### Key Features
*   **Memory Efficiency**: Uses `StreamReader` and `CsvHelper` to read files line-by-line, preventing `OutOfMemoryException`.
*   **High Performance**: Utilizes PostgreSQL's `COPY` command (Binary Import) for extremely fast bulk inserts.
*   **Reliability**: Implements a Staging Table strategy to safely handle duplicates without failing entire batches.
*   **Progress Tracking**: Real-time monitoring of import progress (Read vs. Inserted counts).
*   **Background Processing**: Uses **Hangfire** for robust background job execution, persistence, and retries.
*   **Observability**: Integrated **Serilog** for structured logging to console and files.

---

## 2. Technology Stack
*   **Framework**: .NET Core 8 Web API
*   **Database**: PostgreSQL
*   **ORM / Data Access**:
    *   `Npgsql` (PostgreSQL Driver)
    *   `Npgsql.EntityFrameworkCore.PostgreSQL` (EF Core)
*   **CSV Processing**: `CsvHelper`
*   **Background Jobs**: `Hangfire` + `Hangfire.PostgreSql`
*   **Logging**: `Serilog`

---

## 3. Architecture & Design Strategies

### 3.1 Streaming & Batching
Instead of loading a 2GB CSV file into memory, the application reads the file line-by-line. It accumulates records into a buffer (e.g., 1000 rows) and flushes them to the database. This keeps memory usage low and constant regardless of file size.

### 3.2 Fast Bulk Insert (PostgreSQL COPY)
Standard EF Core `AddRange` is too slow for millions of rows. We use the **Npgsql Binary Importer** (`COPY ... FROM STDIN (FORMAT BINARY)`). This bypasses most SQL overhead and writes data directly to the table, achieving maximum throughput.

### 3.3 Handling Duplicates (Staging Table Strategy)
The `COPY` command is transactional and will fail a whole batch if a single Primary Key violation occurs. To solve this:
1.  Data is inserted into a **Staging Table** (`Guest_Staging`) which has no constraints.
2.  After the import completes, a SQL command moves data to the final `Guest` table using `ON CONFLICT DO NOTHING`, effectively filtering duplicates.

### 3.4 Background Job Execution
Long-running imports should not block the HTTP request. **Hangfire** is used to:
*   Offload the import task to a background worker.
*   Persist job status in the database (survives app restarts).
*   Provide a dashboard for monitoring and retrying failed jobs.

---

## 4. Implementation Guide

### Step 1: Project Setup & Dependencies
**Create Project:**
```powershell
dotnet new webapi -n GuestImporterApi
cd GuestImporterApi
```

**Install Packages:**
```powershell
dotnet add package Npgsql
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package CsvHelper
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Sinks.Console
dotnet add package Serilog.Sinks.File
dotnet add package Hangfire
dotnet add package Hangfire.PostgreSql
```

### Step 2: Database Configuration
**appsettings.json**:
```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Database=Guestdb;Username=postgres;Password=your_password"
  }
}
```

### Step 3: Domain Model & Data
**Guest.cs**:
```csharp
public class Guest
{
    public long GuestId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

**Database Schema (SQL)**:
```sql
-- Final Table
CREATE TABLE "Guest" (
  "GuestId" BIGINT PRIMARY KEY,
  "FirstName" TEXT NULL,
  "LastName" TEXT NULL,
  "Email" TEXT NULL,
  "Phone" TEXT NULL,
  "CreatedAt" TIMESTAMPTZ NOT NULL
);

-- Staging Table (No PK)
CREATE TABLE "Guest_Staging" (
  "GuestId" BIGINT,
  "FirstName" TEXT,
  "LastName" TEXT,
  "Email" TEXT,
  "Phone" TEXT,
  "CreatedAt" TIMESTAMPTZ NOT NULL
);
```

### Step 4: Core Logic - Streaming Importer
The `GuestCsvImporter` service reads the CSV and writes to the Staging table.

**Key Method: `CopyBatchAsync`**:
```csharp
private static async Task<int> CopyBatchAsync(
    NpgsqlConnection conn,
    List<GuestCsvRow> batch,
    CancellationToken ct,
    ILogger logger,
    ImportProgress progress)
{
    // Uses COPY BINARY for maximum speed
    var copyCmd = "COPY \"Guest_Staging\" (\"GuestId\", \"FirstName\", ...) FROM STDIN (FORMAT BINARY)";
    await using var writer = await conn.BeginBinaryImportAsync(copyCmd, ct);
    
    foreach (var r in batch)
    {
        await writer.StartRowAsync(ct);
        writer.Write(r.GuestId, NpgsqlTypes.NpgsqlDbType.Bigint);
        // ... write other fields
    }
    
    await writer.CompleteAsync(ct);
    
    logger.LogInformation("Batch inserted. Total: {Total}", progress.TotalInserted + batch.Count);
    return batch.Count;
}
```

### Step 5: Background Jobs with Hangfire
We configure Hangfire in `Program.cs` to use PostgreSQL for storage and register the Dashboard.

**ImportController.cs**:
```csharp
[HttpPost("guests")]
public IActionResult StartImport([FromBody] StartImportRequest req)
{
    var jobId = Guid.NewGuid();
    _store.GetOrCreate(jobId); // Pre-initialize progress

    // Enqueue job to Hangfire
    var hangfireId = BackgroundJob.Enqueue<GuestCsvImporter>(importer => 
        importer.ImportAsync(jobId, req.CsvFilePath, req.BatchSize, CancellationToken.None));

    return Ok(new { jobId, HangfireJobId = hangfireId });
}
```

---

## 5. Usage Guide

### 5.1 Starting an Import
**Endpoint**: `POST /api/import/guests`
**Body**:
```json
{
  "csvFilePath": "D:\\data\\guests_10million.csv",
  "batchSize": 1000
}
```
*Note: The `csvFilePath` must be a local path on the server.*

### 5.2 Monitoring Progress
**Endpoint**: `GET /api/import/guests/{jobId}/progress`
**Response**:
```json
{
  "isRunning": true,
  "totalInserted": 50000,
  "totalRead": 50000,
  "lastBatchSize": 1000
}
```

### 5.3 Dashboard
Visit `/hangfire` to view the Hangfire Dashboard. You can see active processing jobs, retries, and succeeded jobs.

---

## 6. Performance & Optimizations
| Feature | Details |
| :--- | :--- |
| **Streaming** | Keeps memory footprint flat (~100MB) even for 10GB files. |
| **COPY Protocol** | Inserts 1000 rows in milliseconds. |
| **Staging Table** | Decouples data ingestion from constraint checking. |
| **Structured Logging** | Logs performance metrics per batch for analysis. |

## 7. Future Enhancements
*   **Docker Support**: Containerize the API and Postgres.
*   **CSV Upload**: Allow uploading files via multipart/form-data (currently reads local disk).
*   **Notification**: Email/Slack alert upon job completion.
