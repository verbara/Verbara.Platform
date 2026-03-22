using Asterisk.Platform.Flows;
using FluentAssertions;

namespace Asterisk.Platform.Flows.Tests;

public sealed class TemplateRendererTests
{
    private readonly TemplateRenderer _renderer = new();

    [Fact]
    public void Render_ShouldSubstituteSimpleVariable()
    {
        var result = _renderer.Render("Hello, {{name}}!", new Dictionary<string, string> { ["name"] = "Alice" });

        result.Should().Be("Hello, Alice!");
    }

    [Fact]
    public void Render_ShouldSubstituteMultipleVariables()
    {
        var vars = new Dictionary<string, string> { ["first"] = "John", ["last"] = "Doe" };
        var result = _renderer.Render("{{first}} {{last}}", vars);

        result.Should().Be("John Doe");
    }

    [Fact]
    public void Render_ShouldLeaveUnknownPlaceholderAsIs()
    {
        var result = _renderer.Render("Hello {{unknown}}!", new Dictionary<string, string>());

        result.Should().Be("Hello {{unknown}}!");
    }

    [Fact]
    public void Render_ShouldHandleTemplateWithNoPlaceholders()
    {
        var result = _renderer.Render("No placeholders here.", new Dictionary<string, string> { ["x"] = "y" });

        result.Should().Be("No placeholders here.");
    }

    [Fact]
    public void Render_ShouldHandleEmptyTemplate()
    {
        var result = _renderer.Render(string.Empty, new Dictionary<string, string> { ["x"] = "y" });

        result.Should().BeEmpty();
    }
}
