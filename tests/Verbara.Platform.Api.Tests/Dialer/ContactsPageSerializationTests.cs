using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using DialerContact = Verbara.Sdk.Pro.Dialer.Models.Contact;

namespace Verbara.Platform.Api.Tests.Dialer;

/// <summary>
/// contact-lists-stay-in-their-tenant: <c>GET /api/v1/admin/campaigns/{id}/contact-lists/{listId}/contacts</c> answers
/// a page of the dialer's contacts. The host serializes without reflection (Native AOT), so the page needs its own
/// source-generated metadata in <see cref="ApiJsonContext"/>; without it the route cannot write its body and the
/// OpenAPI document cannot be generated. The test project itself serializes with reflection, so only this direct
/// lookup in the context shows the gap.
/// </summary>
public sealed class ContactsPageSerializationTests
{
    [Fact]
    public void GetTypeInfo_ShouldReturnMetadata_WhenTheTypeIsThePageOfDialerContacts()
    {
        var typeInfo = ApiJsonContext.Default.GetTypeInfo(typeof(PagedResult<DialerContact>));

        typeInfo.Should().NotBeNull("the contacts route answers a PagedResult of the dialer's Contact");
    }

    [Fact]
    public void Serialize_ShouldWriteTheContactsInCamelCase_WhenUsingTheContextMetadata()
    {
        var page = new PagedResult<DialerContact>([new DialerContact { FirstName = "Ana", LastName = "Test" }], 1, 1, 25);
        var typeInfo = ApiJsonContext.Default.GetTypeInfo(typeof(PagedResult<DialerContact>));
        typeInfo.Should().NotBeNull();

        var node = JsonNode.Parse(JsonSerializer.Serialize(page, typeInfo!))!;

        node["items"]!.AsArray().Select(n => n!["firstName"]!.GetValue<string>()).Should().Equal(["Ana"]);
    }
}
