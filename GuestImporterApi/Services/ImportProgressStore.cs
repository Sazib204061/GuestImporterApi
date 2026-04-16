using System.Collections.Concurrent;

namespace GuestImporterApi.Services;

public class ImportProgressStore
{
    private readonly ConcurrentDictionary<Guid, ImportProgress> _jobs = new();
    private readonly ConcurrentDictionary<Guid, string> _hangfireJobs = new();

    public ImportProgress GetOrCreate(Guid jobId)
        => _jobs.GetOrAdd(jobId, _ => new ImportProgress());

    public ImportProgress? Get(Guid jobId)
        => _jobs.TryGetValue(jobId, out var p) ? p : null;

    public void SetHangfireJobId(Guid jobId, string hangfireJobId)
        => _hangfireJobs[jobId] = hangfireJobId;

    public IReadOnlyCollection<string> GetAllHangfireJobIds()
        => _hangfireJobs.Values.ToArray();

    public void Remove(Guid jobId)
    {
        _jobs.TryRemove(jobId, out _);
        _hangfireJobs.TryRemove(jobId, out _);
    }

    public void ClearAll()
    {
        _jobs.Clear();
        _hangfireJobs.Clear();
    }
}

public class ImportProgress
{
    public bool IsRunning { get; set; }
    public bool IsCompleted { get; set; }
    public string? Error { get; set; }
    public string? HangfireJobId { get; set; }

    public long TotalInserted { get; set; }
    public long TotalRead { get; set; }
    public int LastBatchSize { get; set; }
    public DateTimeOffset LastUpdateUtc { get; set; } = DateTimeOffset.UtcNow;
}
