using System.Text.Json;
using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Api.Serialization;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests.Endpoints;

public sealed class AuthEndpointsTests
{
    [Fact]
    public void MfaChallengeResponse_ShouldSerializeWithFrontendFieldNames_WhenSerialized()
    {
        var response = new MfaChallengeResponse(true, "abc123");

        var json = JsonSerializer.Serialize(response, ApiJsonContext.Default.MfaChallengeResponse);

        json.Should().Contain("\"requiresMfa\":true");
        json.Should().Contain("\"mfaToken\":\"abc123\"");
    }
}
