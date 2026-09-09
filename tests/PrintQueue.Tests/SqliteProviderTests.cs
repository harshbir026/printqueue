using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Data;
using PrintQueue.Api.Domain;
using PrintQueue.Api.Services;

namespace PrintQueue.Tests;

/// <summary>
/// EF Core's in-memory provider does not enforce unique indexes or SQLite's
/// type translation rules. These tests run against a real SQLite file so a
/// green in-memory suite cannot hide a 500 on the shipped database.
/// </summary>
public class SqliteProviderTests : IDisposable
{
    private readonly string _path;
    private readonly PrintQueueContext _db;
    private readonly PrintJobService _service;

    public SqliteProviderTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"printqueue-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<PrintQueueContext>()
            .UseSqlite($"Data Source={_path}")
            .Options;
        _db = new PrintQueueContext(options);
        _db.Database.EnsureCreated();
        _service = new PrintJobService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            File.Delete(_path);
            File.Delete(_path + "-shm");
            File.Delete(_path + "-wal");
        }
        catch (IOException)
        {
            // Best-effort cleanup of SQLite sidecar files.
        }
    }

    [Fact]
    public async Task EnsureCreated_persists_and_reloads_a_job()
    {
        var printer = await SeedPrinterAsync();
        var submitted = await SubmitAsync(printer.Id, "payroll.pdf", "round-trip");

        await using var reload = CreateContext();
        var loaded = await reload.PrintJobs.SingleAsync(j => j.Id == submitted.Job.Id);

        Assert.Equal("payroll.pdf", loaded.DocumentName);
        Assert.Equal(PrintJobStatus.Queued, loaded.Status);
        Assert.Equal("round-trip", loaded.IdempotencyKey);
    }

    [Fact]
    public async Task Unique_index_rejects_duplicate_printer_and_key_pairs()
    {
        var printer = await SeedPrinterAsync();
        var now = DateTime.UtcNow;
        _db.PrintJobs.Add(PrintJob.Create(printer.Id, "a.pdf", 1, "dup", now));
        await _db.SaveChangesAsync();

        _db.PrintJobs.Add(PrintJob.Create(printer.Id, "b.pdf", 1, "dup", now));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
        Assert.True(
            ex.InnerException is SqliteException sqlite && sqlite.SqliteErrorCode == 19,
            $"Expected SQLite constraint 19, got {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
    }

    [Fact]
    public async Task Unique_index_allows_the_same_key_on_a_different_printer()
    {
        var a = await SeedPrinterAsync();
        var b = await _service.RegisterPrinterAsync(new RegisterPrinterRequest
        {
            Name = "Other",
            Location = "Lab"
        });

        _db.PrintJobs.Add(PrintJob.Create(a.Id, "a.pdf", 1, "shared", DateTime.UtcNow));
        _db.PrintJobs.Add(PrintJob.Create(b.Id, "b.pdf", 1, "shared", DateTime.UtcNow));

        await _db.SaveChangesAsync();
        Assert.Equal(2, await _db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task Ordering_by_DateTime_translates_on_sqlite()
    {
        var printer = await SeedPrinterAsync();
        var older = PrintJob.Create(printer.Id, "older.pdf", 1, "older", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = PrintJob.Create(printer.Id, "newer.pdf", 1, "newer", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        _db.PrintJobs.AddRange(newer, older);
        await _db.SaveChangesAsync();

        var ordered = await _db.PrintJobs.AsNoTracking()
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.DocumentName)
            .ToListAsync();

        Assert.Equal(["older.pdf", "newer.pdf"], ordered);
    }

    [Fact]
    public async Task Status_and_printer_filters_translate_on_sqlite()
    {
        var printer = await SeedPrinterAsync();
        var queued = await SubmitAsync(printer.Id, "queued.pdf", "q");
        var processing = await SubmitAsync(printer.Id, "proc.pdf", "p");
        await _service.TransitionAsync(processing.Job.Id, PrintJobStatus.Processing);

        var filtered = await _service.ListJobsAsync(printer.Id, PrintJobStatus.Processing);

        Assert.Single(filtered);
        Assert.Equal(processing.Job.Id, filtered[0].Id);
        Assert.DoesNotContain(filtered, j => j.Id == queued.Job.Id);
    }

    [Fact]
    public async Task Concurrent_duplicate_submits_resolve_to_one_job()
    {
        var printer = await SeedPrinterAsync();
        var request = new SubmitJobRequest
        {
            PrinterId = printer.Id,
            DocumentName = "payroll.pdf",
            PageCount = 10
        };

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var serviceA = new PrintJobService(dbA);
        var serviceB = new PrintJobService(dbB);

        var taskA = serviceA.SubmitJobAsync(request, "race-key");
        var taskB = serviceB.SubmitJobAsync(request, "race-key");
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results.Select(r => r.Job.Id).Distinct());
        Assert.Equal(1, await _db.PrintJobs.CountAsync(j => j.IdempotencyKey == "race-key"));
    }

    private PrintQueueContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrintQueueContext>()
            .UseSqlite($"Data Source={_path}")
            .Options;
        return new PrintQueueContext(options);
    }

    private Task<Printer> SeedPrinterAsync() =>
        _service.RegisterPrinterAsync(new RegisterPrinterRequest
        {
            Name = "HP-LaserJet-01",
            Location = "Floor 3 / Hyderabad"
        });

    private Task<SubmitJobResult> SubmitAsync(Guid printerId, string document, string key) =>
        _service.SubmitJobAsync(new SubmitJobRequest
        {
            PrinterId = printerId,
            DocumentName = document,
            PageCount = 10
        }, key);
}
