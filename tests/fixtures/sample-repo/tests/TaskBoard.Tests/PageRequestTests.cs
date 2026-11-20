using TaskBoard.Api.Tasks;
using Xunit;

namespace TaskBoard.Tests;

public class PageRequestTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(7, 100)]
    public void Accepts_pages_within_the_cap(int page, int pageSize)
    {
        Assert.True(new PageRequest(page, pageSize).IsValid(out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public void Rejects_pages_outside_the_allowed_range(int page, int pageSize)
    {
        Assert.False(new PageRequest(page, pageSize).IsValid(out var error));
        Assert.NotNull(error);
    }
}
