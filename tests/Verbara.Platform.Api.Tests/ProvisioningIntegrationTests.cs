using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

public sealed class ProvisioningIntegrationTests
{
    [Theory]
    [InlineData("support")]
    [InlineData("sales")]
    [InlineData("blended")]
    public void ValidTemplateNames_ShouldBeAccepted(string template)
    {
        TenantProvisioningTemplates.ValidTemplateNames.Contains(template).Should().BeTrue();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("enterprise")]
    [InlineData("")]
    public void InvalidTemplateNames_ShouldBeRejected(string template)
    {
        TenantProvisioningTemplates.ValidTemplateNames.Contains(template).Should().BeFalse();
    }

    [Fact]
    public void SupportTemplate_ShouldCreateOneQueue()
    {
        var queues = TenantProvisioningTemplates.GetQueues("support",
            new TenantId("t1"), "UTC");
        queues.Should().HaveCount(1);
        queues[0].Name.Should().Be("General Support");
    }
}
