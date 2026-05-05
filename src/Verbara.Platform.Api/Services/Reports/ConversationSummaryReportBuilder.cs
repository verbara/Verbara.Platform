using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Reports;

namespace Verbara.Platform.Api.Services.Reports;

internal sealed class ConversationSummaryReportBuilder : IReportDataBuilder
{
    private readonly IConversationStore _conversationStore;
    private readonly IMessageStore _messageStore;

    public ConversationSummaryReportBuilder(
        IConversationStore conversationStore,
        IMessageStore messageStore)
    {
        _conversationStore = conversationStore;
        _messageStore = messageStore;
    }

    public string ReportType => "conversation_summary";

    public async Task<ReportData> BuildAsync(
        string tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        string? filters, CancellationToken ct)
    {
        var tid = new TenantId(tenantId);

        // Fetch conversations across all states, filter by period
        var allConversations = new List<Conversation>();
        var page = 1;
        const int pageSize = 200;
        while (true)
        {
            var result = await _conversationStore.ListAsync(tid, new ConversationQuery { Page = page, PageSize = pageSize }, ct);
            allConversations.AddRange(result.Items);
            if (result.Items.Count < pageSize) break;
            page++;
        }

        var inPeriod = allConversations
            .Where(c => c.CreatedAt >= periodStart && c.CreatedAt <= periodEnd)
            .ToList();

        // Group by channel
        var byChannel = inPeriod.GroupBy(c => c.Channel);

        var rows = new List<ReportDataRow>();
        int totalConversations = 0, totalNew = 0, totalClosed = 0;
        double totalMessages = 0;

        foreach (var group in byChannel)
        {
            var channel = group.Key.ToString();
            var total = group.Count();
            var newCount = total; // All in period are "new" for this period
            var closedCount = group.Count(c => c.State == ConversationState.Closed);

            // Sample message count (up to 100 conversations to avoid overload)
            var sampleConversations = group.Take(100).ToList();
            var totalMsgCount = 0;
            foreach (var conv in sampleConversations)
            {
                var messages = await _messageStore.GetConversationMessagesAsync(
                    tid, conv.ConversationId, 1000, 0, ct);
                totalMsgCount += messages.Count;
            }
            var avgMessages = sampleConversations.Count > 0 ? (double)totalMsgCount / sampleConversations.Count : 0;

            // Avg resolution time for closed conversations
            var closedConvs = group.Where(c => c.ClosedAt.HasValue).ToList();
            var avgResolutionHours = closedConvs.Count > 0
                ? closedConvs.Average(c => (c.ClosedAt!.Value - c.CreatedAt).TotalHours)
                : 0.0;

            rows.Add(new ReportDataRow(channel, new Dictionary<string, object>
            {
                ["Total Conversations"] = total,
                ["New Conversations"] = newCount,
                ["Closed Conversations"] = closedCount,
                ["Avg Messages per Conversation"] = Math.Round(avgMessages, 1),
                ["Avg Resolution Time (h)"] = Math.Round(avgResolutionHours, 1),
            }));

            totalConversations += total;
            totalNew += newCount;
            totalClosed += closedCount;
            totalMessages += avgMessages * total;
        }

        var summary = new Dictionary<string, double>
        {
            ["Total Conversations"] = totalConversations,
            ["Total New"] = totalNew,
            ["Total Closed"] = totalClosed,
            ["Avg Messages per Conversation"] = totalConversations > 0
                ? Math.Round(totalMessages / totalConversations, 1) : 0,
        };

        return new ReportData
        {
            ReportName = "Conversation Summary",
            TenantName = tenantId,
            ReportType = ReportType,
            From = periodStart,
            To = periodEnd,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = rows,
            Summary = summary,
        };
    }
}
