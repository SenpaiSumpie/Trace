using TaskBoard.Api.Tasks;

namespace TaskBoard.Api.Reports;

/// <summary>Background service that turns queued report requests into summary reports.</summary>
public sealed class ReportWorker(ReportQueue queue, TaskRepository tasks, ILogger<ReportWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RenderTime = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!queue.TryDequeue(out var reportId))
            {
                await Task.Delay(IdleDelay, stoppingToken);
                continue;
            }

            await GenerateAsync(reportId, stoppingToken);
        }
    }

    private async Task GenerateAsync(Guid reportId, CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating report {ReportId}", reportId);

        // Rendering is slow; simulate the work.
        await Task.Delay(RenderTime);

        var openTasks = tasks.CountOpen();
        logger.LogInformation("Report {ReportId}: {OpenTasks} open tasks", reportId, openTasks);
    }
}
