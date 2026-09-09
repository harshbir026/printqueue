namespace PrintQueue.Api.Domain;

public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }

    public abstract int StatusCode { get; }
    public abstract string Title { get; }
}

public sealed class InvalidStateTransitionException : DomainException
{
    public PrintJobStatus From { get; }
    public PrintJobStatus To { get; }

    public InvalidStateTransitionException(PrintJobStatus from, PrintJobStatus to)
        : base($"Cannot transition a print job from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public override int StatusCode => StatusCodes.Status409Conflict;
    public override string Title => "Invalid state transition";
}

public sealed class PrinterNotFoundException : DomainException
{
    public PrinterNotFoundException(Guid printerId)
        : base($"Printer '{printerId}' was not found.")
    {
    }

    public override int StatusCode => StatusCodes.Status404NotFound;
    public override string Title => "Printer not found";
}

public sealed class PrintJobNotFoundException : DomainException
{
    public PrintJobNotFoundException(Guid jobId)
        : base($"Print job '{jobId}' was not found.")
    {
    }

    public override int StatusCode => StatusCodes.Status404NotFound;
    public override string Title => "Print job not found";
}

public sealed class PrinterOfflineException : DomainException
{
    public PrinterOfflineException(Guid printerId)
        : base($"Printer '{printerId}' is offline and cannot accept jobs.")
    {
    }

    public override int StatusCode => StatusCodes.Status409Conflict;
    public override string Title => "Printer is offline";
}

public sealed class MissingIdempotencyKeyException : DomainException
{
    public MissingIdempotencyKeyException()
        : base("The Idempotency-Key header is required when submitting a print job.")
    {
    }

    public override int StatusCode => StatusCodes.Status400BadRequest;
    public override string Title => "Missing idempotency key";
}

public sealed class MaxRetriesExceededException : DomainException
{
    public MaxRetriesExceededException(Guid jobId, int retryCount, int maxRetries)
        : base($"Print job '{jobId}' has used {retryCount} of {maxRetries} retries and cannot be re-queued.")
    {
    }

    public override int StatusCode => StatusCodes.Status409Conflict;
    public override string Title => "Retry limit reached";
}
