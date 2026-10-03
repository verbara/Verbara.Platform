using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// GET /admin/users/{id} and PUT /admin/users/{id} carry an entity tag for the fields an administrator
/// edits (display name, role, status), and PUT honours an <c>If-Match</c> naming it: an edit made from
/// a form loaded before someone else changed the user is refused with 412 instead of putting the values
/// that form showed back. <c>If-Match</c> is optional; without it the edit is applied as before.
/// </summary>
public sealed class UpdateUserPreconditionEndpointTests : IClassFixture<AccountStatusApiFactory>
{
    private const string Customer = AccountStatusApiFactory.CustomerTenantId;

    private readonly AccountStatusApiFactory _factory;

    public UpdateUserPreconditionEndpointTests(AccountStatusApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetUser_ShouldReturnAStrongEntityTag_WhenTheUserExists()
    {
        var target = NewTargetUser();

        var etag = await GetEntityTagAsync(target);

        etag.Should().NotBeNull(because: "the edit form needs a tag to send back in If-Match");
        etag!.IsWeak.Should().BeFalse(because: "If-Match uses the strong comparison, which a weak tag never passes");
    }

    [Fact]
    public async Task GetUser_ShouldReturnTheSameEntityTag_WhenNothingChangedBetweenTwoReads()
    {
        var target = NewTargetUser();

        var first = await GetEntityTagAsync(target);
        var second = await GetEntityTagAsync(target);

        first.Should().NotBeNull();
        second.Should().Be(first);
    }

    [Fact]
    public async Task UpdateUser_ShouldReturnTheEntityTagOfTheStoredValues_WhenTheChangeIsWritten()
    {
        var target = NewTargetUser();
        var before = await GetEntityTagAsync(target);

        using var response = await PutAsync(target, new { displayName = "Renamed" }, ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.ETag.Should().NotBe(before, because: "the display name it covers changed");
        response.Headers.ETag.Should().Be(await GetEntityTagAsync(target),
            because: "the tag the edit returns is the one the next read returns");
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn412AndKeepTheRole_WhenIfMatchNamesTheUserAsItWasBeforeAnotherAdminChangedIt()
    {
        // Admin A opens the edit form; admin B demotes the user; A saves the form, which still shows
        // Admin, with the tag of what it showed.
        var target = NewTargetUser(role: UserRole.Admin);
        var formTag = await GetEntityTagAsync(target);
        formTag.Should().NotBeNull();

        using (var demotion = await PutAsync(target, new { role = "Agent" }, ifMatch: null))
            demotion.StatusCode.Should().Be(HttpStatusCode.OK);

        using var staleSave = await PutAsync(
            target, new { displayName = target.DisplayName, role = "Admin" }, ifMatch: formTag!.ToString());

        staleSave.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Agent,
            because: "a form loaded before the demotion must not put the old role back");
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn412AndChangeNothing_WhenIfMatchNamesNoCurrentState()
    {
        var target = NewTargetUser(role: UserRole.Agent);

        using var response = await PutAsync(target, new { role = "Admin" }, ifMatch: "\"an-earlier-state\"");

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Agent);
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn412_WhenIfMatchIsStaleEvenIfTheBodyChangesNothing()
    {
        var target = NewTargetUser();

        using var response = await PutAsync(
            target, new { displayName = target.DisplayName }, ifMatch: "\"an-earlier-state\"");

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Fact]
    public async Task UpdateUser_ShouldApplyTheChange_WhenIfMatchNamesTheCurrentState()
    {
        var target = NewTargetUser(role: UserRole.Agent);
        var current = await GetEntityTagAsync(target);
        current.Should().NotBeNull();

        using var response = await PutAsync(target, new { role = "Supervisor" }, ifMatch: current!.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task UpdateUser_ShouldApplyTheChange_WhenOneOfSeveralEntityTagsInIfMatchIsCurrent()
    {
        var target = NewTargetUser(role: UserRole.Agent);
        var current = await GetEntityTagAsync(target);
        current.Should().NotBeNull();

        using var response = await PutAsync(
            target, new { role = "Supervisor" }, ifMatch: $"\"an-earlier-state\", {current}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn412_WhenIfMatchIsTheWeakFormOfTheCurrentEntityTag()
    {
        var target = NewTargetUser(role: UserRole.Agent);
        var current = await GetEntityTagAsync(target);
        current.Should().NotBeNull();

        using var response = await PutAsync(target, new { role = "Supervisor" }, ifMatch: $"W/{current!.Tag}");

        response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Agent);
    }

    [Fact]
    public async Task UpdateUser_ShouldApplyTheChange_WhenIfMatchIsTheWildcard()
    {
        var target = NewTargetUser(role: UserRole.Agent);

        using var response = await PutAsync(target, new { role = "Supervisor" }, ifMatch: "*");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task UpdateUser_ShouldApplyTheChange_WhenIfMatchIsAbsent()
    {
        // Platform.Web 3.20.0 sends no If-Match: its edits keep working.
        var target = NewTargetUser(role: UserRole.Agent);

        using var response = await PutAsync(
            target, new { displayName = target.DisplayName, role = "Supervisor", status = "Active" }, ifMatch: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.GetUser(target.UserId.Value, Customer)!.Role.Should().Be(UserRole.Supervisor);
    }

    [Fact]
    public async Task UpdateUser_ShouldReturn404_WhenTheUserDoesNotExistAndIfMatchIsSent()
    {
        using var client = AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/users/no-such-user-{Guid.NewGuid():N}")
        {
            Content = JsonContent.Create(new { role = "Agent" }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"an-earlier-state\"");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private User NewTargetUser(UserRole role = UserRole.Agent) =>
        _factory.SaveUser($"precondition-target-{Guid.NewGuid():N}", Customer, role, UserStatus.Active);

    private HttpClient AdminClient()
    {
        var admin = _factory.GetUser(AccountStatusApiFactory.CustomerAdminUserId, Customer)!;
        return _factory.CreateBearerClient(_factory.MintAccessToken(admin));
    }

    private async Task<EntityTagHeaderValue?> GetEntityTagAsync(User target)
    {
        using var client = AdminClient();
        using var response = await client.GetAsync($"/api/v1/admin/users/{target.UserId.Value}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.ETag;
    }

    private async Task<HttpResponseMessage> PutAsync(User target, object body, string? ifMatch)
    {
        using var client = AdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/users/{target.UserId.Value}")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await client.SendAsync(request);
    }
}
