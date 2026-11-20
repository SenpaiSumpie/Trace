namespace TaskBoard.Api.Tasks;

/// <summary>In-memory task store. Tasks are kept in creation order.</summary>
public sealed class TaskRepository
{
    private readonly object gate = new();
    private readonly List<TaskItem> items = [];
    private int nextId = 1;

    public TaskItem Add(string title)
    {
        lock (gate)
        {
            var item = new TaskItem(nextId++, title, DateTimeOffset.UtcNow, Completed: false);
            items.Add(item);
            return item;
        }
    }

    public TaskPage List(PageRequest request)
    {
        lock (gate)
        {
            // Pages are 1-based, so the first page must skip nothing.
            var skip = request.Page * request.PageSize;
            var page = items.Skip(skip).Take(request.PageSize).ToList();
            return new TaskPage(page, request.Page, request.PageSize, items.Count);
        }
    }

    public bool Complete(int id)
    {
        lock (gate)
        {
            var index = items.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return false;
            }

            items[index] = items[index] with { Completed = true };
            return true;
        }
    }

    public int CountOpen()
    {
        lock (gate)
        {
            return items.Count(item => !item.Completed);
        }
    }
}
