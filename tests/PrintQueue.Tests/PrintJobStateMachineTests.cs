using PrintQueue.Api.Domain;

namespace PrintQueue.Tests;

public class PrintJobStateMachineTests
{
    public static readonly (PrintJobStatus From, PrintJobStatus To)[] Legal =
    [
        (PrintJobStatus.Queued, PrintJobStatus.Processing),
        (PrintJobStatus.Queued, PrintJobStatus.Cancelled),
        (PrintJobStatus.Processing, PrintJobStatus.Completed),
        (PrintJobStatus.Processing, PrintJobStatus.Failed),
        (PrintJobStatus.Processing, PrintJobStatus.Cancelled),
        (PrintJobStatus.Failed, PrintJobStatus.Queued)
    ];

    public static IEnumerable<object[]> LegalTransitions() =>
        Legal.Select(pair => new object[] { pair.From, pair.To });

    public static IEnumerable<object[]> IllegalTransitions()
    {
        var legal = Legal.ToHashSet();
        foreach (var from in Enum.GetValues<PrintJobStatus>())
        {
            foreach (var to in Enum.GetValues<PrintJobStatus>())
            {
                if (!legal.Contains((from, to)))
                {
                    yield return new object[] { from, to };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void CanTransition_allows_documented_moves(PrintJobStatus from, PrintJobStatus to)
    {
        Assert.True(PrintJobStateMachine.CanTransition(from, to));
    }

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void CanTransition_rejects_illegal_moves(PrintJobStatus from, PrintJobStatus to)
    {
        Assert.False(PrintJobStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Completed_is_terminal()
    {
        Assert.True(PrintJobStateMachine.IsTerminal(PrintJobStatus.Completed));
        Assert.Empty(PrintJobStateMachine.GetAllowedNextStates(PrintJobStatus.Completed));
    }

    [Fact]
    public void Cancelled_is_terminal()
    {
        Assert.True(PrintJobStateMachine.IsTerminal(PrintJobStatus.Cancelled));
        Assert.Empty(PrintJobStateMachine.GetAllowedNextStates(PrintJobStatus.Cancelled));
    }

    [Fact]
    public void Queued_and_Processing_and_Failed_are_not_terminal()
    {
        Assert.False(PrintJobStateMachine.IsTerminal(PrintJobStatus.Queued));
        Assert.False(PrintJobStateMachine.IsTerminal(PrintJobStatus.Processing));
        Assert.False(PrintJobStateMachine.IsTerminal(PrintJobStatus.Failed));
    }

    [Fact]
    public void Allowed_next_states_match_the_transition_table()
    {
        Assert.Equal(
            [PrintJobStatus.Processing, PrintJobStatus.Cancelled],
            PrintJobStateMachine.GetAllowedNextStates(PrintJobStatus.Queued));
        Assert.Equal(
            [PrintJobStatus.Completed, PrintJobStatus.Failed, PrintJobStatus.Cancelled],
            PrintJobStateMachine.GetAllowedNextStates(PrintJobStatus.Processing));
        Assert.Equal(
            [PrintJobStatus.Queued],
            PrintJobStateMachine.GetAllowedNextStates(PrintJobStatus.Failed));
    }

    [Fact]
    public void Job_transition_follows_the_table_and_updates_timestamp()
    {
        var created = new DateTime(2026, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        var job = PrintJob.Create(Guid.NewGuid(), "payroll.pdf", 10, "k1", created);

        var moved = created.AddMinutes(1);
        job.TransitionTo(PrintJobStatus.Processing, moved);

        Assert.Equal(PrintJobStatus.Processing, job.Status);
        Assert.Equal(moved, job.UpdatedAt);
        Assert.Equal(created, job.CreatedAt);
    }

    [Fact]
    public void Job_transition_throws_on_illegal_move()
    {
        var job = PrintJob.Create(Guid.NewGuid(), "payroll.pdf", 10, "k1", DateTime.UtcNow);

        var ex = Assert.Throws<InvalidStateTransitionException>(
            () => job.TransitionTo(PrintJobStatus.Completed, DateTime.UtcNow));

        Assert.Equal(PrintJobStatus.Queued, ex.From);
        Assert.Equal(PrintJobStatus.Completed, ex.To);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public void Failed_to_Queued_increments_retry_count_until_the_bound()
    {
        var job = Fail(PrintJob.Create(Guid.NewGuid(), "payroll.pdf", 10, "k1", DateTime.UtcNow));

        job.TransitionTo(PrintJobStatus.Queued, DateTime.UtcNow);
        Assert.Equal(1, job.RetryCount);
        Fail(job);

        job.TransitionTo(PrintJobStatus.Queued, DateTime.UtcNow);
        Assert.Equal(2, job.RetryCount);
        Fail(job);

        job.TransitionTo(PrintJobStatus.Queued, DateTime.UtcNow);
        Assert.Equal(3, job.RetryCount);
        Assert.Equal(PrintJob.MaxRetries, job.RetryCount);
        Fail(job);

        Assert.Throws<MaxRetriesExceededException>(
            () => job.TransitionTo(PrintJobStatus.Queued, DateTime.UtcNow));
        Assert.Empty(PrintJobStateMachine.GetAllowedNextStates(job));
    }

    [Fact]
    public void Create_requires_a_positive_page_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrintJob.Create(Guid.NewGuid(), "doc.pdf", 0, "k1", DateTime.UtcNow));
    }

    [Fact]
    public void Create_requires_a_document_name()
    {
        Assert.Throws<ArgumentException>(
            () => PrintJob.Create(Guid.NewGuid(), "  ", 1, "k1", DateTime.UtcNow));
    }

    [Fact]
    public void Create_requires_an_idempotency_key()
    {
        Assert.Throws<MissingIdempotencyKeyException>(
            () => PrintJob.Create(Guid.NewGuid(), "doc.pdf", 1, " ", DateTime.UtcNow));
    }

    private static PrintJob Fail(PrintJob job)
    {
        if (job.Status == PrintJobStatus.Queued)
        {
            job.TransitionTo(PrintJobStatus.Processing, DateTime.UtcNow);
        }

        job.TransitionTo(PrintJobStatus.Failed, DateTime.UtcNow);
        return job;
    }
}
