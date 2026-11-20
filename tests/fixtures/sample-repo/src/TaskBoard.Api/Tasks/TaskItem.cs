namespace TaskBoard.Api.Tasks;

public sealed record TaskItem(int Id, string Title, DateTimeOffset CreatedAt, bool Completed);

public sealed record TaskPage(IReadOnlyList<TaskItem> Items, int Page, int PageSize, int TotalCount);
