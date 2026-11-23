using TaskBoard.Api.Auth;
using TaskBoard.Api.Notifications;
using TaskBoard.Api.RateLimiting;
using TaskBoard.Api.Reports;
using TaskBoard.Api.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<ReportQueue>();
builder.Services.AddHostedService<ReportWorker>();
builder.Services.AddHttpClient<WebhookNotifier>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RequestThrottle>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/tasks", (int? page, int? pageSize, TaskRepository tasks) =>
{
    var request = new PageRequest(page ?? 1, pageSize ?? PageRequest.DefaultPageSize);
    if (!request.IsValid(out var error))
    {
        return Results.BadRequest(new { error });
    }

    return Results.Ok(tasks.List(request));
});

app.MapPost("/tasks", (CreateTaskRequest body, TaskRepository tasks) =>
{
    if (!TitleRules.TryNormalize(body.Title, out var title, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var created = tasks.Add(title);
    return Results.Created($"/tasks/{created.Id}", created);
});

app.MapPost("/tasks/{id:int}/complete", async (int id, TaskRepository tasks, WebhookNotifier notifier, CancellationToken cancellationToken) =>
{
    if (!tasks.Complete(id))
    {
        return Results.NotFound(new { error = "No task has that ID." });
    }

    await notifier.TaskCompletedAsync(id, cancellationToken);
    return Results.NoContent();
});

app.MapPost("/reports", (ReportQueue queue) =>
{
    var reportId = queue.Enqueue();
    return Results.Accepted($"/reports/{reportId}", new { reportId });
});

app.Run();

public sealed record CreateTaskRequest(string? Title);
