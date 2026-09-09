using Microsoft.AspNetCore.Mvc;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Domain;
using PrintQueue.Api.Services;

namespace PrintQueue.Api.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";

    private readonly PrintJobService _service;

    public JobsController(PrintJobService service)
    {
        _service = service;
    }

    [HttpPost]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<PrintJobResponse>> Submit(
        SubmitJobRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var result = await _service.SubmitJobAsync(request, idempotencyKey, cancellationToken);
        var body = Map(result.Job);

        if (result.Replayed)
        {
            return Ok(body);
        }

        return CreatedAtAction(nameof(GetById), new { jobId = result.Job.Id }, body);
    }

    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintJobResponse>> GetById(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _service.GetJobAsync(jobId, cancellationToken);
        return Ok(Map(job));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrintJobResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PrintJobResponse>>> List(
        [FromQuery] Guid? printerId,
        [FromQuery] PrintJobStatus? status,
        CancellationToken cancellationToken)
    {
        var jobs = await _service.ListJobsAsync(printerId, status, cancellationToken);
        return Ok(jobs.Select(Map));
    }

    [HttpPost("{jobId:guid}/transitions")]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PrintJobResponse>> Transition(
        Guid jobId,
        TransitionJobRequest request,
        CancellationToken cancellationToken)
    {
        var job = await _service.TransitionAsync(jobId, request.Status, cancellationToken);
        return Ok(Map(job));
    }

    [HttpDelete("{jobId:guid}")]
    [ProducesResponseType(typeof(PrintJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PrintJobResponse>> Cancel(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _service.CancelAsync(jobId, cancellationToken);
        return Ok(Map(job));
    }

    private static PrintJobResponse Map(PrintJob job) =>
        new(
            job.Id,
            job.PrinterId,
            job.DocumentName,
            job.PageCount,
            job.Status,
            job.IdempotencyKey,
            job.RetryCount,
            PrintJob.MaxRetries,
            job.CreatedAt,
            job.UpdatedAt,
            PrintJobStateMachine.GetAllowedNextStates(job));
}
