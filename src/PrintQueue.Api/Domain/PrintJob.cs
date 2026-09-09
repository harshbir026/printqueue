namespace PrintQueue.Api.Domain;

public class PrintJob
{
    public const int MaxRetries = 3;

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid PrinterId { get; private set; }
    public string DocumentName { get; private set; } = string.Empty;
    public int PageCount { get; private set; }
    public PrintJobStatus Status { get; private set; } = PrintJobStatus.Queued;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public int RetryCount { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public Printer? Printer { get; private set; }

    private PrintJob()
    {
    }

    public static PrintJob Create(
        Guid printerId,
        string documentName,
        int pageCount,
        string idempotencyKey,
        DateTime utcNow)
    {
        if (pageCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount), "Page count must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(documentName))
        {
            throw new ArgumentException("Document name is required.", nameof(documentName));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new MissingIdempotencyKeyException();
        }

        return new PrintJob
        {
            PrinterId = printerId,
            DocumentName = documentName.Trim(),
            PageCount = pageCount,
            IdempotencyKey = idempotencyKey.Trim(),
            Status = PrintJobStatus.Queued,
            RetryCount = 0,
            CreatedAt = utcNow,
            UpdatedAt = utcNow
        };
    }

    public void TransitionTo(PrintJobStatus next, DateTime utcNow)
    {
        if (!PrintJobStateMachine.CanTransition(Status, next))
        {
            throw new InvalidStateTransitionException(Status, next);
        }

        if (Status == PrintJobStatus.Failed && next == PrintJobStatus.Queued)
        {
            if (RetryCount >= MaxRetries)
            {
                throw new MaxRetriesExceededException(Id, RetryCount, MaxRetries);
            }

            RetryCount++;
        }

        Status = next;
        UpdatedAt = utcNow;
    }
}
