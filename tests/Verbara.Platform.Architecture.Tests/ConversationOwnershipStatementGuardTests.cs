namespace Verbara.Platform.Architecture.Tests;

/// <summary>
/// contact-history-opens-by-assignment task 1.6 (design D7, fable P3): every statement of
/// <c>PostgresConversationStore</c> that writes a conversation's owner also writes <c>queue_id</c> with the shared
/// CASE constant, so a future conditional move to a queue (<c>conditional-ownership-moves</c>) cannot leave the old
/// queue behind. The real-tree test runs <see cref="ConversationOwnershipStatementScanner"/> over the store; the
/// mutation removes the constant from a copy of the upsert and must be caught; the probes pin each arm.
/// </summary>
public sealed class ConversationOwnershipStatementGuardTests
{
    private static string StoreSource() =>
        File.ReadAllText(Path.Join(
            SourceTreeSource.SrcRoot(), "Verbara.Platform.Storage.Postgres", "Stores",
            ConversationOwnershipStatementScanner.StoreFile));

    [Fact]
    public void OwnershipStatements_ShouldWriteQueueIdWithTheSharedCase_WhenTheyWriteOwnerKind()
    {
        var source = StoreSource();

        ConversationOwnershipStatementScanner.OwnershipStatements(source).Should().NotBeEmpty(
            "the scan must reach the whole-row upsert, which writes owner_kind");
        ConversationOwnershipStatementScanner.Scan(source).Should().BeEmpty();
        source.Should().Contain($"const string {ConversationOwnershipStatementScanner.CaseConstant} =",
            "the CASE lives in one constant of the store");
    }

    [Fact]
    public void OwnershipStatements_ShouldBeFlagged_WhenTheCaseIsRemovedFromACopyOfTheUpsert()
    {
        var source = StoreSource();
        // Every reference to the constant (not its declaration) becomes a literal NULL: the upsert's SQL text still
        // writes queue_id, but no longer through the shared CASE.
        var reference = new System.Text.RegularExpressions.Regex(
            $@"\b{ConversationOwnershipStatementScanner.CaseConstant}\b(?!\s*=)");
        reference.Count(source).Should().BeGreaterThan(0, "the upsert names the constant");

        var mutated = reference.Replace(source, "\"NULL\"");

        ConversationOwnershipStatementScanner.Scan(mutated).Should().NotBeEmpty(
            "an upsert that writes owner_kind without the shared queue_id CASE must fail the guard");
    }

    [Theory]
    [InlineData("\"UPDATE conversations SET owner_kind = @OwnerKind, owner_id = @OwnerId \" + \"WHERE tenant_id = @T\"")]
    [InlineData("\"INSERT INTO conversations (conversation_id, owner_kind, owner_id) VALUES (@Id, @K, @O)\"")]
    [InlineData("\"INSERT INTO conversations (id) VALUES (@Id) ON CONFLICT (id) DO UPDATE SET owner_kind = EXCLUDED.owner_kind\"")]
    [InlineData("\"UPDATE conversations SET owner_kind = @K, queue_id = CASE WHEN @OwnerKind = 3 THEN @OwnerId END WHERE x\"")]
    public void Scan_ShouldFlagTheStatement_WhenItWritesTheOwnerWithoutTheConstant(string statement)
    {
        ConversationOwnershipStatementScanner.Scan($"var sql = {statement};").Should().ContainSingle(
            "a copy of the CASE text is not the constant");
    }

    [Theory]
    [InlineData("\"UPDATE conversations SET owner_kind = @K, owner_id = @O, queue_id = \" + QueueIdWriteCase + \" WHERE x\"")]
    [InlineData("\"SELECT owner_kind, owner_id FROM conversations WHERE owner_kind = @K\"")]
    [InlineData("\"UPDATE conversations SET state = @S WHERE owner_kind = @K AND owner_id = @O\"")]
    public void Scan_ShouldPassTheStatement_WhenItCarriesTheConstantOrWritesNoOwner(string statement)
    {
        ConversationOwnershipStatementScanner.Scan($"var sql = {statement};").Should().BeEmpty();
    }
}
