using Microsoft.EntityFrameworkCore;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Data;
using PrintQueue.Api.Domain;
using PrintQueue.Api.Services;

namespace PrintQueue.Tests;

public class PrintJobServiceTests : IDisposable
{
    private readonly PrintQueueContext _db;
    private readonly PrintJobService _service;

    public PrintJobServiceTests()
    {
        var options = new DbContextOptionsBuilder<PrintQueueContext>()
            .UseInMemoryDatabase($"service-{Guid.NewGuid()}")
            .Options;
        _db = new PrintQueueContext(options);
        _db.Database.EnsureCreated();
        _service = new PrintJobService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RegisterPrinter_persists_an_online_printer()
    {
        var printer = await SeedPrinterAsync();

        Assert.True(printer.IsOnline);
        Assert.Equal("HP-LaserJet-01", printer.Name);
        Assert.Equal("Floor 3 / Hyderabad", printer.Location);
        Assert.NotEqual(Guid.Empty, printer.Id);
    }

    [Fact]
    public async Task GetPrinter_throws_when_missing()
    {
        await Assert.ThrowsAsync<PrinterNotFoundException>(
            () => _service.GetPrinterAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Submit_creates_a_queued_job()
    {
        var printer = await SeedPrinterAsync();

        var result = await SubmitAsync(printer.Id, "payroll.pdf", "payroll-2026-08-15");

        Assert.False(result.Replayed);
        Assert.Equal(PrintJobStatus.Queued, result.Job.Status);
        Assert.Equal("payroll.pdf", result.Job.DocumentName);
        Assert.Equal(10, result.Job.PageCount);
        Assert.Equal(0, result.Job.RetryCount);
    }

    [Fact]
    public async Task Submit_replays_the_same_idempotency_key_for_the_same_printer()
    {
        var printer = await SeedPrinterAsync();

        var first = await SubmitAsync(printer.Id, "payroll.pdf", "same-key");
        var second = await SubmitAsync(printer.Id, "other.pdf", "same-key");

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Job.Id, second.Job.Id);
        Assert.Equal("payroll.pdf", second.Job.DocumentName);
        Assert.Equal(1, await _db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task Submit_scopes_idempotency_keys_per_printer()
    {
        var floor3 = await SeedPrinterAsync();
        var floor4 = await _service.RegisterPrinterAsync(new RegisterPrinterRequest
        {
            Name = "HP-LaserJet-02",
            Location = "Floor 4"
        });

        var a = await SubmitAsync(floor3.Id, "a.pdf", "shared-key");
        var b = await SubmitAsync(floor4.Id, "b.pdf", "shared-key");

        Assert.NotEqual(a.Job.Id, b.Job.Id);
        Assert.Equal(2, await _db.PrintJobs.CountAsync());
    }

    [Fact]
    public async Task Submit_rejects_an_offline_printer()
    {
        var printer = await SeedPrinterAsync();
        await _service.SetPrinterOnlineAsync(printer.Id, false);

        await Assert.ThrowsAsync<PrinterOfflineException>(
            () => SubmitAsync(printer.Id, "payroll.pdf", "offline-key"));
    }

    [Fact]
    public async Task Submit_rejects_a_missing_printer()
    {
        await Assert.ThrowsAsync<PrinterNotFoundException>(
            () => SubmitAsync(Guid.NewGuid(), "payroll.pdf", "missing-printer"));
    }

    [Fact]
    public async Task Submit_requires_an_idempotency_key()
    {
        var printer = await SeedPrinterAsync();

        await Assert.ThrowsAsync<MissingIdempotencyKeyException>(
            () => _service.SubmitJobAsync(new SubmitJobRequest
            {
                PrinterId = printer.Id,
                DocumentName = "payroll.pdf",
                PageCount = 10
            }, "  "));
    }

    [Fact]
    public async Task ListJobs_filters_by_printer_and_status()
    {
        var printerA = await SeedPrinterAsync();
        var printerB = await _service.RegisterPrinterAsync(new RegisterPrinterRequest
        {
            Name = "B",
            Location = "Lab"
        });

        await SubmitAsync(printerA.Id, "a1.pdf", "a1");
        var a2 = await SubmitAsync(printerA.Id, "a2.pdf", "a2");
        await SubmitAsync(printerB.Id, "b1.pdf", "b1");
        await _service.TransitionAsync(a2.Job.Id, PrintJobStatus.Processing);

        var queuedOnA = await _service.ListJobsAsync(printerA.Id, PrintJobStatus.Queued);
        var allOnA = await _service.ListJobsAsync(printerA.Id, null);
        var processing = await _service.ListJobsAsync(null, PrintJobStatus.Processing);

        Assert.Single(queuedOnA);
        Assert.Equal(2, allOnA.Count);
        Assert.Single(processing);
        Assert.Equal(a2.Job.Id, processing[0].Id);
    }

    [Fact]
    public async Task ListJobs_returns_fifo_order()
    {
        var printer = await SeedPrinterAsync();
        var first = await SubmitAsync(printer.Id, "first.pdf", "first");
        var second = await SubmitAsync(printer.Id, "second.pdf", "second");

        var listed = await _service.ListJobsAsync(null, null);

        Assert.Equal(new[] { first.Job.Id, second.Job.Id }, listed.Select(j => j.Id));
    }

    [Fact]
    public async Task Transition_persists_a_legal_move_and_rejects_an_illegal_one()
    {
        var printer = await SeedPrinterAsync();
        var submitted = await SubmitAsync(printer.Id, "payroll.pdf", "lifecycle");

        var processing = await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Processing);
        Assert.Equal(PrintJobStatus.Processing, processing.Status);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            () => _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Queued));
    }

    [Fact]
    public async Task Cancel_is_idempotent_once_already_cancelled()
    {
        var printer = await SeedPrinterAsync();
        var submitted = await SubmitAsync(printer.Id, "payroll.pdf", "cancel-me");

        var cancelled = await _service.CancelAsync(submitted.Job.Id);
        var again = await _service.CancelAsync(submitted.Job.Id);

        Assert.Equal(PrintJobStatus.Cancelled, cancelled.Status);
        Assert.Equal(PrintJobStatus.Cancelled, again.Status);
    }

    [Fact]
    public async Task Cancel_of_a_completed_job_is_rejected()
    {
        var printer = await SeedPrinterAsync();
        var submitted = await SubmitAsync(printer.Id, "payroll.pdf", "done");
        await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Processing);
        await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Completed);

        await Assert.ThrowsAsync<InvalidStateTransitionException>(
            () => _service.CancelAsync(submitted.Job.Id));
    }

    [Fact]
    public async Task GetJob_throws_when_missing()
    {
        await Assert.ThrowsAsync<PrintJobNotFoundException>(
            () => _service.GetJobAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Retry_from_failed_requeues_until_max_retries()
    {
        var printer = await SeedPrinterAsync();
        var submitted = await SubmitAsync(printer.Id, "payroll.pdf", "retry");

        for (var attempt = 1; attempt <= PrintJob.MaxRetries; attempt++)
        {
            await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Processing);
            await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Failed);
            var requeued = await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Queued);
            Assert.Equal(attempt, requeued.RetryCount);
        }

        await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Processing);
        await _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Failed);

        await Assert.ThrowsAsync<MaxRetriesExceededException>(
            () => _service.TransitionAsync(submitted.Job.Id, PrintJobStatus.Queued));
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
