namespace TaskBoard.Api.Tasks;

/// <summary>A 1-based page request for the task list.</summary>
public readonly record struct PageRequest(int Page, int PageSize)
{
    public const int DefaultPageSize = 20;

    // See docs/adr/0001-page-size-cap.md before changing this.
    public const int MaxPageSize = 100;

    public bool IsValid(out string? error)
    {
        if (Page < 1)
        {
            error = "page must be 1 or greater.";
            return false;
        }

        if (PageSize < 1 || PageSize > MaxPageSize)
        {
            error = $"pageSize must be between 1 and {MaxPageSize}.";
            return false;
        }

        error = null;
        return true;
    }
}
