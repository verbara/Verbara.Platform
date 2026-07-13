using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;
using Verbara.Sdk.Pro.CsatRunner.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// csat-runner Phase E — CSAT admin template CRUD surface
/// (<c>/api/v1/admin/csat/templates/*</c>) + the <c>CsatTemplateProvider</c> fallback chain.
/// Covers upsert+readback, list, delete, get-not-found, the deferred <c>preview-voice</c> 501,
/// the <c>AdminOnly</c> 403 path, and provider resolution
/// (tenant-locale → tenant-default-locale → global-default-locale → global-default-en-US).
/// </summary>
public sealed class CsatTemplateAdminEndpointsTests : IClassFixture<UnifiedPlatformApiFactory>
{
    private readonly UnifiedPlatformApiFactory _factory;
    private readonly HttpClient _client;

    public CsatTemplateAdminEndpointsTests(UnifiedPlatformApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Upsert + read-back ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertTemplate_ShouldReturn200WithProjection_WhenValidEmail()
    {
        var id = $"tmpl-{Guid.NewGuid():N}";
        var response = await _client.PutAsync(
            $"/api/admin/csat/templates/{id}",
            JsonContent.Create(new
            {
                channel = "email",
                locale = "en-US",
                subject = "Rate us",
                body = "Reply 1-5 to rate your support.",
                isDefault = false,
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["templateId"]!.GetValue<string>().Should().Be(id);
        node["channel"]!.GetValue<string>().Should().Be("email");
        node["locale"]!.GetValue<string>().Should().Be("en-US");
        node["subject"]!.GetValue<string>().Should().Be("Rate us");
        node["body"]!.GetValue<string>().Should().Be("Reply 1-5 to rate your support.");
    }

    [Fact]
    public async Task GetTemplate_ShouldReturnUpsertedTemplate_WhenExists()
    {
        var id = $"tmpl-{Guid.NewGuid():N}";
        await UpsertAsync(id, "sms", "es-419", body: "Responde 1-5.");

        var response = await _client.GetAsync($"/api/admin/csat/templates/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["channel"]!.GetValue<string>().Should().Be("sms");
        node["locale"]!.GetValue<string>().Should().Be("es-419");
        node["subject"]?.GetValue<string?>().Should().BeNull();
    }

    [Fact]
    public async Task ListTemplates_ShouldIncludeUpsertedTemplate()
    {
        var id = $"tmpl-{Guid.NewGuid():N}";
        await UpsertAsync(id, "email", "pt-BR", body: "Avalie de 1 a 5.");

        var response = await _client.GetAsync("/api/admin/csat/templates/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var arr = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        arr.Select(n => n!["templateId"]!.GetValue<string>()).Should().Contain(id);
    }

    // ─── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTemplate_ShouldReturn204ThenGet404_WhenExists()
    {
        var id = $"tmpl-{Guid.NewGuid():N}";
        await UpsertAsync(id, "voice", "en-US", body: "Rate one to five.");

        var del = await _client.DeleteAsync($"/api/admin/csat/templates/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await _client.GetAsync($"/api/admin/csat/templates/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteTemplate_ShouldReturn404_WhenAbsent()
    {
        var response = await _client.DeleteAsync($"/api/admin/csat/templates/tmpl-{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTemplate_ShouldReturn404_WhenAbsent()
    {
        var response = await _client.GetAsync($"/api/admin/csat/templates/tmpl-{Guid.NewGuid():N}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Validation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertTemplate_ShouldReturn400_WhenChannelInvalid()
    {
        var response = await _client.PutAsync(
            $"/api/admin/csat/templates/tmpl-{Guid.NewGuid():N}",
            JsonContent.Create(new { channel = "webchat", locale = "en-US", body = "x" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── Preview voice (csat-completion — synthesized) ───────────────────────────

    [Fact]
    public async Task PreviewVoice_ShouldReturnSynthesizedAudio_WhenTemplateExists()
    {
        // csat-completion — the voice channel ships, so preview-voice synthesizes the resolved template
        // body through the Pro TTS seam (TtsPromptCache → SpeechSynthesizer) instead of returning 501.
        var id = $"tmpl-{Guid.NewGuid():N}";
        await UpsertAsync(id, "voice", "en-US", body: "Rate one to five.");

        var response = await _client.PostAsync($"/api/admin/csat/templates/{id}/preview-voice", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("audio/L16");
        var audio = await response.Content.ReadAsByteArrayAsync();
        audio.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PreviewVoice_ShouldReturn404_WhenTemplateAbsent()
    {
        var response = await _client.PostAsync(
            $"/api/admin/csat/templates/tmpl-{Guid.NewGuid():N}/preview-voice", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── AdminOnly RBAC ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTemplates_ShouldReturn403_WhenCallerNotAdmin()
    {
        using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        var client = nonAdmin.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/admin/csat/templates/");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpsertTemplate_ShouldReturn403_WhenCallerNotAdmin()
    {
        using var nonAdmin = new NonAdminAuthenticatedApiFactory();
        var client = nonAdmin.CreateAuthenticatedClient();

        var response = await client.PutAsync(
            $"/api/admin/csat/templates/tmpl-{Guid.NewGuid():N}",
            JsonContent.Create(new { channel = "email", locale = "en-US", body = "x" }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Provider fallback chain (in-process, resolved through the store) ─────────

    [Fact]
    public async Task Provider_ShouldResolveTenantLocale_WhenExactMatchExists()
    {
        var tenant = new TenantId(UnifiedPlatformApiFactory.TestTenantId);
        var store = _factory.Services.GetRequiredService<ICsatTemplateStore>();
        await store.SaveAsync(Entry(tenant, "prov-exact-email-fr", "email", "fr-FR", "French body"), CancellationToken.None);

        var provider = _factory.Services.GetRequiredService<ICsatTemplateProvider>();
        var template = await provider.GetTemplateAsync(tenant.Value, "email", "fr-FR");

        template.Should().NotBeNull();
        template!.Locale.Should().Be("fr-FR");
        template.Body.Should().Be("French body");
    }

    [Fact]
    public async Task Provider_ShouldFallBackToEnUsDefault_WhenLocaleMissing()
    {
        var tenant = new TenantId(UnifiedPlatformApiFactory.TestTenantId);
        var store = _factory.Services.GetRequiredService<ICsatTemplateStore>();
        // Only an en-US default exists for this channel; request an unseeded locale.
        await store.SaveAsync(
            Entry(tenant, "prov-default-sms-enus", "sms", "en-US", "English default", isDefault: true),
            CancellationToken.None);

        var provider = _factory.Services.GetRequiredService<ICsatTemplateProvider>();
        var template = await provider.GetTemplateAsync(tenant.Value, "sms", "ja-JP");

        template.Should().NotBeNull();
        template!.Locale.Should().Be("en-US");
        template.Body.Should().Be("English default");
    }

    [Fact]
    public async Task Provider_ShouldReturnNull_WhenNoTemplateForChannel()
    {
        var tenant = new TenantId($"tenant-empty-{Guid.NewGuid():N}");
        var provider = _factory.Services.GetRequiredService<ICsatTemplateProvider>();

        var template = await provider.GetTemplateAsync(tenant.Value, "voice", "en-US");

        template.Should().BeNull();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private async Task UpsertAsync(string id, string channel, string locale, string body)
    {
        var response = await _client.PutAsync(
            $"/api/admin/csat/templates/{id}",
            JsonContent.Create(new { channel, locale, body }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static CsatTemplateEntry Entry(
        TenantId tenant, string id, string channel, string locale, string body, bool isDefault = false) => new()
    {
        TemplateId = EntityId.From(id),
        TenantId = tenant,
        Channel = channel,
        Locale = locale,
        Subject = null,
        Body = body,
        IsDefault = isDefault,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
