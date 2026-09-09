using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using PrintQueue.Api.Contracts;
using PrintQueue.Api.Domain;

namespace PrintQueue.Tests;

public class ApiIntegrationTests : IClassFixture<PrintQueueApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;

    public ApiIntegrationTests(PrintQueueApiFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost:5000")
        });
    }

    [Fact]
    public async Task Register_printer_returns_201_with_location()
    {
        var response = await _client.PostAsJsonAsync("/api/printers", new
        {
            name = Unique("HP-LaserJet"),
            location = "Floor 3 / Hyderabad"
        }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await Read<PrinterResponse>(response);
        Assert.True(body.IsOnline);
        Assert.Equal("Floor 3 / Hyderabad", body.Location);
    }

    [Fact]
    public async Task Get_printers_includes_the_registered_printer()
    {
        var created = await RegisterPrinterAsync();

        var listed = await _client.GetFromJsonAsync<List<PrinterResponse>>("/api/printers", Json);

        Assert.Contains(listed!, p => p.Id == created.Id);
    }

    [Fact]
    public async Task Get_printer_by_id_returns_200_and_missing_returns_404_problem()
    {
        var created = await RegisterPrinterAsync();

        var found = await _client.GetAsync($"/api/printers/{created.Id}");
        var missing = await _client.GetAsync($"/api/printers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        var problem = await missing.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal(404, problem!.Status);
        Assert.Equal("Printer not found", problem.Title);
    }

    [Fact]
    public async Task Put_online_returns_204_and_blocks_new_jobs()
    {
        var printer = await RegisterPrinterAsync();

        var offline = await _client.PutAsync($"/api/printers/{printer.Id}/online?isOnline=false", null);
        Assert.Equal(HttpStatusCode.NoContent, offline.StatusCode);

        var rejected = await SubmitJobRawAsync(printer.Id, Unique("key"));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var online = await _client.PutAsync($"/api/printers/{printer.Id}/online?isOnline=true", null);
        Assert.Equal(HttpStatusCode.NoContent, online.StatusCode);
    }

    [Fact]
    public async Task Submit_job_returns_201_then_200_on_idempotent_replay()
    {
        var printer = await RegisterPrinterAsync();
        var key = Unique("payroll");

        var created = await SubmitJobRawAsync(printer.Id, key);
        var replayed = await SubmitJobRawAsync(printer.Id, key);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

        var first = await Read<PrintJobResponse>(created);
        var second = await Read<PrintJobResponse>(replayed);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(PrintJobStatus.Queued, first.Status);
        Assert.Contains(PrintJobStatus.Processing, first.AllowedNextStates);
        Assert.Contains(PrintJobStatus.Cancelled, first.AllowedNextStates);
    }

    [Fact]
    public async Task Submit_job_without_idempotency_key_returns_400_problem()
    {
        var printer = await RegisterPrinterAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(new
            {
                printerId = printer.Id,
                documentName = "payroll.pdf",
                pageCount = 10
            }, options: Json)
        };

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Missing idempotency key", problem!.Title);
    }

    [Fact]
    public async Task Get_job_includes_allowed_next_states()
    {
        var job = await SubmitJobAsync();

        var response = await _client.GetFromJsonAsync<PrintJobResponse>($"/api/jobs/{job.Id}", Json);

        Assert.Equal(job.Id, response!.Id);
        Assert.Equal(PrintJob.MaxRetries, response.MaxRetries);
        Assert.Equal(2, response.AllowedNextStates.Count);
    }

    [Fact]
    public async Task List_jobs_filters_by_printer_and_status()
    {
        var printer = await RegisterPrinterAsync();
        var queued = await SubmitJobAsync(printer.Id, Unique("queued"));
        var processing = await SubmitJobAsync(printer.Id, Unique("proc"));
        await TransitionAsync(processing.Id, PrintJobStatus.Processing);

        var filtered = await _client.GetFromJsonAsync<List<PrintJobResponse>>(
            $"/api/jobs?printerId={printer.Id}&status=Processing", Json);

        Assert.NotNull(filtered);
        Assert.Single(filtered);
        Assert.Equal(processing.Id, filtered[0].Id);
        Assert.DoesNotContain(filtered, j => j.Id == queued.Id);
    }

    [Fact]
    public async Task Transition_moves_the_job_and_illegal_moves_return_409()
    {
        var job = await SubmitJobAsync();

        var processing = await TransitionAsync(job.Id, PrintJobStatus.Processing);
        Assert.Equal(PrintJobStatus.Processing, processing.Status);

        var illegal = await _client.PostAsJsonAsync(
            $"/api/jobs/{job.Id}/transitions",
            new { status = "Queued" },
            Json);

        Assert.Equal(HttpStatusCode.Conflict, illegal.StatusCode);
        var problem = await illegal.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Invalid state transition", problem!.Title);
        Assert.Equal(409, problem.Status);
    }

    [Fact]
    public async Task Delete_cancels_a_queued_job()
    {
        var job = await SubmitJobAsync();

        var response = await _client.DeleteAsync($"/api/jobs/{job.Id}");
        var body = await Read<PrintJobResponse>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrintJobStatus.Cancelled, body.Status);
        Assert.Empty(body.AllowedNextStates);
    }

    [Fact]
    public async Task Full_lifecycle_queued_processing_completed()
    {
        var job = await SubmitJobAsync();

        await TransitionAsync(job.Id, PrintJobStatus.Processing);
        var completed = await TransitionAsync(job.Id, PrintJobStatus.Completed);

        Assert.Equal(PrintJobStatus.Completed, completed.Status);
        Assert.Empty(completed.AllowedNextStates);

        var cancelCompleted = await _client.DeleteAsync($"/api/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.Conflict, cancelCompleted.StatusCode);
    }

    [Fact]
    public async Task Missing_job_returns_404()
    {
        var response = await _client.GetAsync($"/api/jobs/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Model_validation_returns_400_for_empty_printer_name()
    {
        var response = await _client.PostAsJsonAsync("/api/printers", new
        {
            name = "",
            location = "Floor 3"
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Model_validation_returns_400_for_non_positive_page_count()
    {
        var printer = await RegisterPrinterAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(new
            {
                printerId = printer.Id,
                documentName = "payroll.pdf",
                pageCount = 0
            }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Unique("pages"));

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_database_connectivity()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", payload.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            payload.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "database");
    }

    [Fact]
    public async Task Swagger_is_served_in_development()
    {
        var response = await _client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<PrinterResponse> RegisterPrinterAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/printers", new
        {
            name = Unique("HP-LaserJet"),
            location = "Floor 3 / Hyderabad"
        }, Json);
        response.EnsureSuccessStatusCode();
        return await Read<PrinterResponse>(response);
    }

    private async Task<PrintJobResponse> SubmitJobAsync(Guid? printerId = null, string? key = null)
    {
        printerId ??= (await RegisterPrinterAsync()).Id;
        var response = await SubmitJobRawAsync(printerId.Value, key ?? Unique("job"));
        response.EnsureSuccessStatusCode();
        return await Read<PrintJobResponse>(response);
    }

    private async Task<HttpResponseMessage> SubmitJobRawAsync(Guid printerId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(new
            {
                printerId,
                documentName = "payroll.pdf",
                pageCount = 10
            }, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private async Task<PrintJobResponse> TransitionAsync(Guid jobId, PrintJobStatus status)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/jobs/{jobId}/transitions",
            new { status },
            Json);
        response.EnsureSuccessStatusCode();
        return await Read<PrintJobResponse>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<T>(Json);
        Assert.NotNull(body);
        return body!;
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
