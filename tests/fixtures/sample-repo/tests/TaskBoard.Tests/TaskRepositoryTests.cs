using TaskBoard.Api.Tasks;
using Xunit;

namespace TaskBoard.Tests;

public class TaskRepositoryTests
{
    [Fact]
    public void Add_assigns_increasing_ids_starting_at_one()
    {
        var repository = new TaskRepository();

        var first = repository.Add("Write the report");
        var second = repository.Add("Review the report");

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
    }

    [Fact]
    public void Complete_returns_false_for_an_unknown_id()
    {
        var repository = new TaskRepository();

        Assert.False(repository.Complete(42));
    }

    [Fact]
    public void Completed_tasks_are_not_counted_as_open()
    {
        var repository = new TaskRepository();
        var task = repository.Add("Ship it");
        repository.Add("Celebrate");

        Assert.True(repository.Complete(task.Id));

        Assert.Equal(1, repository.CountOpen());
    }

    // TODO: cover List. Nothing tests paging through the repository yet.
}
