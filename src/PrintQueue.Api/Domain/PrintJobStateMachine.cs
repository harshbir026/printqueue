namespace PrintQueue.Api.Domain;

/// <summary>
/// Declares every legal print-job transition in one table so controllers and
/// services never scatter <c>if (status == ...)</c> checks. Illegal moves throw
/// <see cref="InvalidStateTransitionException"/>.
/// </summary>
public static class PrintJobStateMachine
{
    private static readonly IReadOnlyDictionary<PrintJobStatus, HashSet<PrintJobStatus>> Transitions =
        new Dictionary<PrintJobStatus, HashSet<PrintJobStatus>>
        {
            [PrintJobStatus.Queued] = [PrintJobStatus.Processing, PrintJobStatus.Cancelled],
            [PrintJobStatus.Processing] =
                [PrintJobStatus.Completed, PrintJobStatus.Failed, PrintJobStatus.Cancelled],
            [PrintJobStatus.Failed] = [PrintJobStatus.Queued],
            [PrintJobStatus.Completed] = [],
            [PrintJobStatus.Cancelled] = []
        };

    public static bool CanTransition(PrintJobStatus from, PrintJobStatus to) =>
        Transitions[from].Contains(to);

    public static bool IsTerminal(PrintJobStatus status) =>
        Transitions[status].Count == 0;

    public static IReadOnlyCollection<PrintJobStatus> GetAllowedNextStates(PrintJobStatus from) =>
        Transitions[from];

    /// <summary>
    /// Allowed next states for a concrete job, including the retry bound.
    /// Failed jobs that have already used <see cref="PrintJob.MaxRetries"/> cannot re-queue.
    /// </summary>
    public static IReadOnlyCollection<PrintJobStatus> GetAllowedNextStates(PrintJob job)
    {
        var next = GetAllowedNextStates(job.Status).ToList();
        if (job.Status == PrintJobStatus.Failed && job.RetryCount >= PrintJob.MaxRetries)
        {
            next.Remove(PrintJobStatus.Queued);
        }

        return next;
    }
}
