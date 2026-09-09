using Microsoft.AspNetCore.Mvc;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Domain;
using PrintQueue.Api.Services;

namespace PrintQueue.Api.Controllers;

[ApiController]
[Route("api/printers")]
public class PrintersController : ControllerBase
{
    private readonly PrintJobService _service;

    public PrintersController(PrintJobService service)
    {
        _service = service;
    }

    [HttpPost]
    [ProducesResponseType(typeof(PrinterResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<PrinterResponse>> Register(RegisterPrinterRequest request, CancellationToken cancellationToken)
    {
        var printer = await _service.RegisterPrinterAsync(request, cancellationToken);
        var body = Map(printer);
        return CreatedAtAction(nameof(GetById), new { printerId = printer.Id }, body);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PrinterResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<PrinterResponse>>> List(CancellationToken cancellationToken)
    {
        var printers = await _service.ListPrintersAsync(cancellationToken);
        return Ok(printers.Select(Map));
    }

    [HttpGet("{printerId:guid}")]
    [ProducesResponseType(typeof(PrinterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrinterResponse>> GetById(Guid printerId, CancellationToken cancellationToken)
    {
        var printer = await _service.GetPrinterAsync(printerId, cancellationToken);
        return Ok(Map(printer));
    }

    [HttpPut("{printerId:guid}/online")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetOnline(Guid printerId, [FromQuery] bool isOnline, CancellationToken cancellationToken)
    {
        await _service.SetPrinterOnlineAsync(printerId, isOnline, cancellationToken);
        return NoContent();
    }

    private static PrinterResponse Map(Printer printer) =>
        new(printer.Id, printer.Name, printer.Location, printer.IsOnline, printer.CreatedAt);
}
