using TaskBoard.Api.Auth;
using TaskBoard.Api.Reports;
using TaskBoard.Api.Tasks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TaskRepository>();
builder.Services.AddSingleton<ReportQueue>();
builder.Services.AddHostedService<ReportWorker>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();

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
    if (string.IsNullOrWhiteSpace(body.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    var created = tasks.Add(body.Title.Trim());
    return Results.Created($"/tasks/{created.Id}", created);
});

app.MapPost("/reports", (ReportQueue queue) =>
{
    var reportId = queue.Enqueue();
    return Results.Accepted($"/reports/{reportId}", new { reportId });
});

app.Run();

public sealed record CreateTaskRequest(string Title);
