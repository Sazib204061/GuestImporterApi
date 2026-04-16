using GuestImporterApi.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using Microsoft.AspNetCore.Mvc;

namespace GuestImporterApi.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController : ControllerBase
{
    private static readonly Dictionary<Guid, CancellationTokenSource> _ctsMap = new();
    private static readonly object _lock = new();

    private readonly GuestCsvImporter _importer;
    private readonly ImportProgressStore _store;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly JobStorage _jobStorage;

    public ImportController(
        GuestCsvImporter importer,
        ImportProgressStore store,
        IBackgroundJobClient backgroundJobClient,
        JobStorage jobStorage)
    {
        _importer = importer;
        _store = store;
        _backgroundJobClient = backgroundJobClient;
        _jobStorage = jobStorage;
    }

    [HttpPost("guests")]
    public IActionResult StartImport([FromBody] StartImportRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.CsvFilePath) || !System.IO.File.Exists(req.CsvFilePath))
            return BadRequest("CsvFilePath not found.");

        var jobId = Guid.NewGuid();
        // Pre-create progress obj so it exists before Hangfire job starts
        var progress = _store.GetOrCreate(jobId);

        // Enqueue the import job - pass jobId instead of progress
        var hangfireJobId = BackgroundJob.Enqueue<GuestCsvImporter>(importer =>
            importer.ImportAsync(
                jobId,  // Passing jobId
                req.CsvFilePath,
                req.BatchSize <= 0 ? 1000 : req.BatchSize,
                //progress,
                CancellationToken.None));

        progress.HangfireJobId = hangfireJobId;
        _store.SetHangfireJobId(jobId, hangfireJobId);

        return Ok(new { jobId, HangfireJobId = hangfireJobId });
    }

    // Start import (server reads a file path from disk)
    // For 2GB, better to place file on server disk and pass the path.
    //[HttpPost("guests")]
    //public ActionResult StartImport([FromBody] StartImportRequest req)
    //{
    //    if (string.IsNullOrWhiteSpace(req.CsvFilePath) || !System.IO.File.Exists(req.CsvFilePath))
    //        return BadRequest("CsvFilePath not found.");

    //    var jobId = Guid.NewGuid();
    //    var progress = _store.GetOrCreate(jobId);

    //    var cts = new CancellationTokenSource();
    //    lock (_lock) _ctsMap[jobId] = cts;

    //    _ = Task.Run(async () =>
    //    {
    //        try
    //        {
    //            await _importer.ImportAsync(
    //                req.CsvFilePath,
    //                req.BatchSize <= 0 ? 1000 : req.BatchSize,
    //                progress,
    //                cts.Token);
    //        }
    //        catch (Exception ex)
    //        {
    //            progress.Error = ex.Message;
    //            progress.IsRunning = false;
    //        }
    //        finally
    //        {
    //            lock (_lock) _ctsMap.Remove(jobId);
    //        }
    //    });

    //    return Ok(new { jobId });
    //}

    [HttpGet("guests/{jobId:guid}/progress")]
    public ActionResult GetProgress(Guid jobId)
    {
        var progress = _store.Get(jobId);
        return progress is null ? NotFound() : Ok(progress);
    }

    [HttpPost("guests/{jobId:guid}/cancel")]
    public ActionResult Cancel(Guid jobId)
    {
        var progress = _store.Get(jobId);
        if (progress is null)
            return NotFound(new { jobId, cancelled = false, reason = "Job id not found." });

        // In the Hangfire flow, local CTS map is not used. Cancel by deleting the Hangfire job.
        var hangfireJobId = progress.HangfireJobId;
        if (!string.IsNullOrWhiteSpace(hangfireJobId))
        {
            var deleted = _backgroundJobClient.Delete(hangfireJobId);
            if (deleted)
            {
                progress.IsRunning = false;
                progress.Error = "Import cancelled by user.";
                progress.LastUpdateUtc = DateTimeOffset.UtcNow;
                return Ok(new { jobId, cancelled = true, hangfireJobId });
            }
        }

        lock (_lock)
        {
            if (_ctsMap.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _ctsMap.Remove(jobId);

                progress.IsRunning = false;
                progress.Error = "Import cancelled by user.";
                progress.LastUpdateUtc = DateTimeOffset.UtcNow;

                return Ok(new { jobId, cancelled = true, fallback = "local-cts" });
            }
        }

        return NotFound(new { jobId, cancelled = false, reason = "No active Hangfire/local token job found." });
    }

    [HttpPost("guests/stop-all")]
    public ActionResult StopAllImports()
    {
        var cancelledLocalTokens = 0;
        lock (_lock)
        {
            foreach (var cts in _ctsMap.Values)
            {
                cts.Cancel();
                cts.Dispose();
                cancelledLocalTokens++;
            }

            _ctsMap.Clear();
        }

        var deletedIds = new HashSet<string>(StringComparer.Ordinal);
        var deletedJobs = 0;

        // Delete any tracked Hangfire jobs first.
        foreach (var jobId in _store.GetAllHangfireJobIds())
        {
            if (string.IsNullOrWhiteSpace(jobId) || !deletedIds.Add(jobId))
                continue;

            if (_backgroundJobClient.Delete(jobId))
                deletedJobs++;
        }

        // Also scan queues/processing/scheduled states and delete importer jobs.
        var monitoring = _jobStorage.GetMonitoringApi();

        foreach (var queue in monitoring.Queues())
        {
            var from = 0;
            const int pageSize = 100;

            while (true)
            {
                var jobs = monitoring.EnqueuedJobs(queue.Name, from, pageSize);
                if (jobs.Count == 0)
                    break;

                foreach (var kv in jobs)
                {
                    if (!IsImporterJob(kv.Value.Job) || !deletedIds.Add(kv.Key))
                        continue;

                    if (_backgroundJobClient.Delete(kv.Key))
                        deletedJobs++;
                }

                from += pageSize;
            }
        }

        {
            var from = 0;
            const int pageSize = 100;

            while (true)
            {
                var jobs = monitoring.ProcessingJobs(from, pageSize);
                if (jobs.Count == 0)
                    break;

                foreach (var kv in jobs)
                {
                    if (!IsImporterJob(kv.Value.Job) || !deletedIds.Add(kv.Key))
                        continue;

                    if (_backgroundJobClient.Delete(kv.Key))
                        deletedJobs++;
                }

                from += pageSize;
            }
        }

        {
            var from = 0;
            const int pageSize = 100;

            while (true)
            {
                var jobs = monitoring.ScheduledJobs(from, pageSize);
                if (jobs.Count == 0)
                    break;

                foreach (var kv in jobs)
                {
                    if (!IsImporterJob(kv.Value.Job) || !deletedIds.Add(kv.Key))
                        continue;

                    if (_backgroundJobClient.Delete(kv.Key))
                        deletedJobs++;
                }

                from += pageSize;
            }
        }

        _store.ClearAll();

        return Ok(new
        {
            cancelledLocalTokens,
            deletedJobs,
            trackingCleared = true
        });
    }

    private static bool IsImporterJob(Job? job)
    {
        if (job is null)
            return false;

        return job.Type == typeof(GuestCsvImporter)
            && job.Method.Name == nameof(GuestCsvImporter.ImportAsync);
    }

    public sealed class StartImportRequest
    {
        public string CsvFilePath { get; set; } = "";
        public int BatchSize { get; set; } = 1000;
    }
}
