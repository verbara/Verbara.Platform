using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Endpoints;
using Verbara.Sdk.Pro.EventStore;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The event store's recording resolver (call-records-are-written-once design D3, audit F3). Pro asks it at projection,
/// with the record's tenant and its <c>CallStarted</c> session id, whether the call was recorded: a call names a
/// recording only when its file exists under <see cref="RecordingOptions.BasePath"/>, where the recording stream reads
/// it (<see cref="RecordingEndpoints.FindRecordingFile"/>), so a record's recording name and its stream agree. The
/// file is named by <see cref="EventStoreOptions.RecordingNamePattern"/> (default <c>{sessionId}</c>); the pattern names
/// the file, the file's presence decides.
/// </summary>
internal sealed class RecordingFileCallRecordingResolver(
    IOptions<RecordingOptions> recordings,
    IOptions<EventStoreOptions> eventStore) : ICallRecordingResolver
{
    /// <inheritdoc />
    public ValueTask<string?> ResolveRecordingNameAsync(
        string tenantId, string sessionId, CancellationToken cancellationToken = default)
    {
        var recordingName = eventStore.Value.RecordingNamePattern.Replace("{sessionId}", sessionId, StringComparison.Ordinal);
        var file = RecordingEndpoints.FindRecordingFile(recordings.Value.BasePath, tenantId, recordingName);
        return ValueTask.FromResult(file is null ? null : recordingName);
    }
}
