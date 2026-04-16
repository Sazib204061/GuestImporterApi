using Microsoft.EntityFrameworkCore;

namespace GuestImporterApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Guest> Guests => Set<Guest>();
    //public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Guest>(b =>
        {
            b.ToTable("Guest");
            b.HasKey(x => x.Id);
            //b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        });
    }
}

public class Guest
{
    public Guid? Id { get; set; }
    public string? guestName { get; set; }
    public string? IDNumber { get; set; }
    public string? IDIssuePlace { get; set; }
    public string? Phone { get; set; }
    public string? PassportNumber { get; set; }
    public string? Name_ar { get; set; }
}

//public class ImportJob
//{
//    public Guid JobId { get; set; }
//    public string Status { get; set; } = "Running";
//    public long TotalRead { get; set; }
//    public long TotalInserted { get; set; }
//    public int LastBatchSize { get; set; }
//    public DateTimeOffset LastUpdateUtc { get; set; }
//    public string? Error { get; set; }
//}
