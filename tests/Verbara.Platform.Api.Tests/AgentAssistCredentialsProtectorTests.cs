using Verbara.Platform.Api.Services.AgentAssist;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public sealed class AgentAssistCredentialsProtectorTests
{
    private static AgentAssistCredentialsProtector CreateProtector()
    {
        // EphemeralDataProtectionProvider is safe for unit tests — keys live only for
        // the duration of the provider instance, so every test starts from a clean slate.
        return new AgentAssistCredentialsProtector(new EphemeralDataProtectionProvider());
    }

    [Fact]
    public void Protect_ShouldRoundtrip_WhenUnprotectedWithSameProvider()
    {
        var protector = CreateProtector();
        var plain = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = "dg_ultra_secret_abc123",
        };

        var encrypted = protector.Protect(plain);
        encrypted["apiKey"].Should().NotBe("dg_ultra_secret_abc123",
            because: "Protect must return ciphertext, never the original plaintext");

        var decrypted = protector.Unprotect(encrypted);
        decrypted["apiKey"].Should().Be("dg_ultra_secret_abc123");
    }

    [Fact]
    public void Protect_ShouldPreserveAllKeys_WhenMultiValueInput()
    {
        var protector = CreateProtector();
        var plain = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apiKey"] = "sk-whisper-123",
            ["endpoint"] = "https://eastus.api.cognitive.microsoft.com",
        };

        var encrypted = protector.Protect(plain);
        encrypted.Keys.Should().BeEquivalentTo(["apiKey", "endpoint"]);
        encrypted.Values.Should().OnlyContain(v => v != "sk-whisper-123" && v != "https://eastus.api.cognitive.microsoft.com");

        var decrypted = protector.Unprotect(encrypted);
        decrypted["apiKey"].Should().Be("sk-whisper-123");
        decrypted["endpoint"].Should().Be("https://eastus.api.cognitive.microsoft.com");
    }

    [Fact]
    public void Protect_ShouldReturnEmptyDictionary_WhenEmptyInput()
    {
        var protector = CreateProtector();
        var plain = new Dictionary<string, string>(StringComparer.Ordinal);

        var encrypted = protector.Protect(plain);
        encrypted.Should().BeEmpty();

        var decrypted = protector.Unprotect(encrypted);
        decrypted.Should().BeEmpty();
    }
}
