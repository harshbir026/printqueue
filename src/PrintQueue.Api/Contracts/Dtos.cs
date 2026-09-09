using System.ComponentModel.DataAnnotations;
using PrintQueue.Api.Domain;

namespace PrintQueue.Api.Contracts;

public record RegisterPrinterRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(400)]
    public string Location { get; init; } = string.Empty;
}

public record PrinterResponse(
    Guid Id,
    string Name,
    string Location,
    bool IsOnline,
    DateTime CreatedAt);

public record SubmitJobRequest
{
    [Required]
    public Guid PrinterId { get; init; }

    [Required]
    [MinLength(1)]
    [MaxLength(400)]
    public string DocumentName { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int PageCount { get; init; }
}

public record TransitionJobRequest
{
    [Required]
    public PrintJobStatus Status { get; init; }
}

public record PrintJobResponse(
    Guid Id,
    Guid PrinterId,
    string DocumentName,
    int PageCount,
    PrintJobStatus Status,
    string IdempotencyKey,
    int RetryCount,
    int MaxRetries,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyCollection<PrintJobStatus> AllowedNextStates);

public record SubmitJobResult(PrintJob Job, bool Replayed);
