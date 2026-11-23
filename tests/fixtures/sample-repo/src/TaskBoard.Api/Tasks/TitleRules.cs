namespace TaskBoard.Api.Tasks;

/// <summary>Rules a task title must meet before the task is stored.</summary>
public static class TitleRules
{
    public const int MaxLength = 200;

    public static bool TryNormalize(string? raw, out string title, out string? error)
    {
        title = (raw ?? string.Empty).Trim();

        if (title.Length == 0)
        {
            error = "Title is required.";
            return false;
        }

        if (title.Length > MaxLength)
        {
            error = $"Title must be at most {MaxLength} characters.";
            return false;
        }

        // Titles are shown on one line in the client, so line breaks and tabs are refused.
        if (title.Any(char.IsControl))
        {
            error = "Title must not contain control characters.";
            return false;
        }

        error = null;
        return true;
    }
}
