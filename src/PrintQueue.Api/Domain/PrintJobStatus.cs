namespace PrintQueue.Api.Domain;

public enum PrintJobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}
