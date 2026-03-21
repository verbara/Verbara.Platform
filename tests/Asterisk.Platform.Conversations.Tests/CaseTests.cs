using Asterisk.Platform.Core;

namespace Asterisk.Platform.Conversations.Tests;

public class CaseTests
{
    [Fact]
    public void Constructor_ShouldCreateCase_WhenValidInput()
    {
        var caseEntity = new Case
        {
            CaseId = EntityId.From("case-001"),
            TenantId = new TenantId("t1"),
            CaseNumber = "CS-0001",
            Subject = "Billing issue",
            Priority = CasePriority.High,
            Status = CaseStatus.Open,
            ContactId = EntityId.From("c-001"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        caseEntity.CaseNumber.Should().Be("CS-0001");
        caseEntity.Priority.Should().Be(CasePriority.High);
        caseEntity.ConversationIds.Should().BeEmpty();
    }

    [Fact]
    public void AddConversation_ShouldLinkConversation()
    {
        var caseEntity = new Case
        {
            CaseId = EntityId.From("case-001"),
            TenantId = new TenantId("t1"),
            CaseNumber = "CS-0001",
            Subject = "Issue",
            Priority = CasePriority.Normal,
            Status = CaseStatus.Open,
            ContactId = EntityId.From("c-001"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        caseEntity.AddConversation(EntityId.From("conv-001"));
        caseEntity.AddConversation(EntityId.From("conv-002"));

        caseEntity.ConversationIds.Should().HaveCount(2);
    }

    [Fact]
    public void AddConversation_ShouldNotDuplicate_WhenSameIdExists()
    {
        var caseEntity = new Case
        {
            CaseId = EntityId.From("case-001"),
            TenantId = new TenantId("t1"),
            CaseNumber = "CS-0001",
            Subject = "Issue",
            Priority = CasePriority.Normal,
            Status = CaseStatus.Open,
            ContactId = EntityId.From("c-001"),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        caseEntity.AddConversation(EntityId.From("conv-001"));
        caseEntity.AddConversation(EntityId.From("conv-001"));

        caseEntity.ConversationIds.Should().HaveCount(1);
    }
}
