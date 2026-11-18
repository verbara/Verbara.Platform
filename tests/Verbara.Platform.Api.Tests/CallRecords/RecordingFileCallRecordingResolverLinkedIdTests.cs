using FluentAssertions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Tests.CallRecords;

/// <summary>
/// Pro <c>2.27.0-pro</c> adds the default member
/// <see cref="ICallRecordingResolver.ResolveRecordingNameAsync(string, string, string, CancellationToken)"/>, which the
/// event store asks at projection with the call's linked id. Platform's resolver keeps the default, so the linked-id member
/// answers exactly what the session-id member answers: the recording name when the file exists, <see langword="null"/>
/// when it does not.
/// </summary>
public sealed class RecordingFileCallRecordingResolverLinkedIdTests : IDisposable
{
    private const string TenantId = "acme";
    private const string SessionId = "c7a1f0e25b6d4c3e9a8f1e2d3c4b5a69";
    private const string LinkedId = "1731900000.42";

    private readonly string _basePath = Directory.CreateTempSubdirectory("recording-resolver-").FullName;

    public void Dispose() => Directory.Delete(_basePath, recursive: true);

    private RecordingFileCallRecordingResolver CreateResolver() =>
        new RecordingFileCallRecordingResolver(
            Options.Create(new RecordingOptions { BasePath = _basePath }),
            Options.Create(new EventStoreOptions()));

    [Fact]
    public async Task ResolveRecordingNameAsync_ShouldAnswerAsTheSessionIdMember_WhenTheRecordingFileIsPresent()
    {
        Directory.CreateDirectory(Path.Join(_basePath, TenantId));
        await File.WriteAllBytesAsync(Path.Join(_basePath, TenantId, SessionId + ".wav"), [0x52, 0x49, 0x46, 0x46]);
        ICallRecordingResolver resolver = CreateResolver(); // asked through the interface, as the event store asks it

        var bySession = await resolver.ResolveRecordingNameAsync(TenantId, SessionId, CancellationToken.None);
        var byLinkedId = await resolver.ResolveRecordingNameAsync(TenantId, SessionId, LinkedId, CancellationToken.None);

        bySession.Should().Be(SessionId);
        byLinkedId.Should().Be(bySession, "the linked-id member keeps Pro's default, which asks the session-id member");
    }

    [Fact]
    public async Task ResolveRecordingNameAsync_ShouldAnswerAsTheSessionIdMember_WhenTheRecordingFileIsAbsent()
    {
        ICallRecordingResolver resolver = CreateResolver(); // asked through the interface, as the event store asks it

        var bySession = await resolver.ResolveRecordingNameAsync(TenantId, SessionId, CancellationToken.None);
        var byLinkedId = await resolver.ResolveRecordingNameAsync(TenantId, SessionId, LinkedId, CancellationToken.None);

        bySession.Should().BeNull();
        byLinkedId.Should().BeNull();
    }
}
