using Microsoft.EntityFrameworkCore;
using PrintQueue.Api.Domain;

namespace PrintQueue.Api.Data;

public class PrintQueueContext : DbContext
{
    public PrintQueueContext(DbContextOptions<PrintQueueContext> options) : base(options)
    {
    }

    public DbSet<Printer> Printers => Set<Printer>();
    public DbSet<PrintJob> PrintJobs => Set<PrintJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Printer>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
            entity.Property(p => p.Location).IsRequired().HasMaxLength(400);
            entity.Property(p => p.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.DocumentName).IsRequired().HasMaxLength(400);
            entity.Property(j => j.IdempotencyKey).IsRequired().HasMaxLength(200);
            entity.Property(j => j.Status).HasConversion<string>().IsRequired();
            entity.Property(j => j.CreatedAt).IsRequired();
            entity.Property(j => j.UpdatedAt).IsRequired();

            entity.HasOne(j => j.Printer)
                .WithMany(p => p.Jobs)
                .HasForeignKey(j => j.PrinterId)
                .OnDelete(DeleteBehavior.Restrict);

            // Same key against a different printer is a different job.
            entity.HasIndex(j => new { j.PrinterId, j.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("IX_PrintJobs_PrinterId_IdempotencyKey");
        });
    }
}
