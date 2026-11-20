namespace TaskBoard.Api.Reports;

/// <summary>Holds the IDs of reports waiting to be generated.</summary>
public sealed class ReportQueue
{
    private readonly Queue<Guid> pending = new();

    public int Count => pending.Count;

    public Guid Enqueue()
    {
        var reportId = Guid.NewGuid();
        pending.Enqueue(reportId);
        return reportId;
    }

    public bool TryDequeue(out Guid reportId) => pending.TryDequeue(out reportId);
}
