using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Data;
using PrintQueue.Api.Domain;

namespace PrintQueue.Api.Services;

public class PrintJobService
{
    private readonly PrintQueueContext _db;

    public PrintJobService(PrintQueueContext db)
    {
        _db = db;
    }

    public async Task<Printer> RegisterPrinterAsync(RegisterPrinterRequest request, CancellationToken cancellationToken = default)
    {
        var printer = Printer.Create(request.Name, request.Location, DateTime.UtcNow);
        _db.Printers.Add(printer);
        await _db.SaveChangesAsync(cancellationToken);
        return printer;
    }

    public Task<List<Printer>> ListPrintersAsync(CancellationToken cancellationToken = default) =>
        _db.Printers.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);

    public async Task<Printer> GetPrinterAsync(Guid printerId, CancellationToken cancellationToken = default)
    {
        var printer = await _db.Printers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken);
        return printer ?? throw new PrinterNotFoundException(printerId);
    }

    public async Task SetPrinterOnlineAsync(Guid printerId, bool isOnline, CancellationToken cancellationToken = default)
    {
        var printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken)
            ?? throw new PrinterNotFoundException(printerId);

        printer.SetOnline(isOnline);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SubmitJobResult> SubmitJobAsync(
        SubmitJobRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new MissingIdempotencyKeyException();
        }

        var key = idempotencyKey.Trim();

        var existing = await _db.PrintJobs.FirstOrDefaultAsync(
            j => j.PrinterId == request.PrinterId && j.IdempotencyKey == key,
            cancellationToken);
        if (existing is not null)
        {
            return new SubmitJobResult(existing, Replayed: true);
        }

        var printer = await _db.Printers.FirstOrDefaultAsync(p => p.Id == request.PrinterId, cancellationToken)
            ?? throw new PrinterNotFoundException(request.PrinterId);

        if (!printer.IsOnline)
        {
            throw new PrinterOfflineException(printer.Id);
        }

        var job = PrintJob.Create(
            printer.Id,
            request.DocumentName,
            request.PageCount,
            key,
            DateTime.UtcNow);

        _db.PrintJobs.Add(job);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return new SubmitJobResult(job, Replayed: false);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _db.Entry(job).State = EntityState.Detached;

            var raced = await _db.PrintJobs.FirstOrDefaultAsync(
                j => j.PrinterId == request.PrinterId && j.IdempotencyKey == key,
                cancellationToken);

            if (raced is not null)
            {
                return new SubmitJobResult(raced, Replayed: true);
            }

            throw;
        }
    }

    public async Task<PrintJob> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.PrintJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        return job ?? throw new PrintJobNotFoundException(jobId);
    }

    public async Task<List<PrintJob>> ListJobsAsync(
        Guid? printerId,
        PrintJobStatus? status,
        CancellationToken cancellationToken = default)
    {
        var query = _db.PrintJobs.AsNoTracking().AsQueryable();

        if (printerId is not null)
        {
            query = query.Where(j => j.PrinterId == printerId.Value);
        }

        if (status is not null)
        {
            query = query.Where(j => j.Status == status.Value);
        }

        return await query
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<PrintJob> TransitionAsync(
        Guid jobId,
        PrintJobStatus next,
        CancellationToken cancellationToken = default)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new PrintJobNotFoundException(jobId);

        job.TransitionTo(next, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<PrintJob> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken)
            ?? throw new PrintJobNotFoundException(jobId);

        if (job.Status == PrintJobStatus.Cancelled)
        {
            return job;
        }

        job.TransitionTo(PrintJobStatus.Cancelled, DateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        if (exception.InnerException is SqliteException sqlite)
        {
            return sqlite.SqliteErrorCode == 19;
        }

        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique index", StringComparison.OrdinalIgnoreCase);
    }
}
