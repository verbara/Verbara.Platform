namespace Verbara.Platform.Core.Tests;

public class PagedResultTests
{
    [Fact]
    public void Constructor_ShouldCalculateTotalPages_WhenValidInput()
    {
        var result = new PagedResult<string>(["a", "b"], totalCount: 5, page: 1, pageSize: 2);

        result.Items.Should().HaveCount(2);
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(2);
        result.TotalPages.Should().Be(3);
        result.HasNextPage.Should().BeTrue();
        result.HasPreviousPage.Should().BeFalse();
    }

    [Fact]
    public void HasPreviousPage_ShouldBeTrue_WhenNotFirstPage()
    {
        var result = new PagedResult<string>(["c"], totalCount: 5, page: 3, pageSize: 2);

        result.HasPreviousPage.Should().BeTrue();
        result.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public void Empty_ShouldReturnEmptyResult()
    {
        var result = PagedResult<string>.Empty(page: 1, pageSize: 10);

        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        result.TotalPages.Should().Be(0);
    }
}
