using System.Text.Json.Serialization;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Audit;
using Verbara.Platform.Api.Endpoints.Mfa;
using Verbara.Platform.Api.Endpoints.Profile;
using Verbara.Platform.Api.Endpoints.Retention;
using Verbara.Platform.Api.Endpoints.Security;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Bot;
using Verbara.Platform.Billing;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Email;
using Verbara.Platform.Core.Reports;
using Verbara.Platform.Core.Webhooks;
using Verbara.Platform.Flows;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Switchboard;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Serialization;

[JsonSerializable(typeof(PagedResult<User>))]
[JsonSerializable(typeof(PagedResult<Queue>))]
[JsonSerializable(typeof(PagedResult<Agent>))]
[JsonSerializable(typeof(PagedResult<Team>))]
[JsonSerializable(typeof(PagedResult<TeamDto>))]
[JsonSerializable(typeof(TeamDto))]
[JsonSerializable(typeof(PagedResult<UserDto>))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(PagedResult<QueueDto>))]
[JsonSerializable(typeof(QueueDto))]
[JsonSerializable(typeof(ArticleDto[]))]
[JsonSerializable(typeof(ArticleDto))]
[JsonSerializable(typeof(ScheduledReportDto[]))]
[JsonSerializable(typeof(ScheduledReportDto))]
[JsonSerializable(typeof(SurveyDto[]))]
[JsonSerializable(typeof(SurveyDto))]
[JsonSerializable(typeof(PagedResult<Conversation>))]
[JsonSerializable(typeof(PagedResult<Contact>))]
[JsonSerializable(typeof(PagedResult<ApiKey>))]
[JsonSerializable(typeof(PagedResult<CampaignSummaryDto>))]
[JsonSerializable(typeof(User))]
[JsonSerializable(typeof(Queue))]
[JsonSerializable(typeof(Agent))]
[JsonSerializable(typeof(Team))]
[JsonSerializable(typeof(Conversation))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Contact))]
[JsonSerializable(typeof(FlowDefinition))]
[JsonSerializable(typeof(ApiKey))]
[JsonSerializable(typeof(OwnershipResult))]
[JsonSerializable(typeof(ConversationAssignedEvent))]
[JsonSerializable(typeof(ConversationMessageEvent))]
[JsonSerializable(typeof(ConversationStateChangedEvent))]
[JsonSerializable(typeof(AgentStateChangedEvent))]
[JsonSerializable(typeof(CampaignStatusChangedEvent))]
[JsonSerializable(typeof(CampaignMetricsUpdatedEvent))]
[JsonSerializable(typeof(CampaignDispositionSubmittedEvent))]
// SSE: Switchboard & Queue events
[JsonSerializable(typeof(ConversationOfferedEvent))]
[JsonSerializable(typeof(ConversationOfferExpiredEvent))]
[JsonSerializable(typeof(ConversationAbandonedEvent))]
[JsonSerializable(typeof(AgentCapacityChangedEvent))]
// SSE: Agent Assist events
[JsonSerializable(typeof(AgentAssistSuggestionEvent))]
[JsonSerializable(typeof(AgentAssistSentimentEvent))]
[JsonSerializable(typeof(AgentAssistComplianceAlertEvent))]
[JsonSerializable(typeof(AgentAssistTranscriptEvent))]
// SSE: Notification events
[JsonSerializable(typeof(NotificationEvent))]
[JsonSerializable(typeof(CampaignSummaryDto))]
[JsonSerializable(typeof(CampaignDetailDto))]
[JsonSerializable(typeof(ScheduleDayDto))]
[JsonSerializable(typeof(ContactListDto))]
[JsonSerializable(typeof(List<ContactListDto>))]
[JsonSerializable(typeof(CreateContactListRequest))]
[JsonSerializable(typeof(ImportContactsRequest))]
[JsonSerializable(typeof(ImportResultDto))]
[JsonSerializable(typeof(PagedResult<Contact>))]
[JsonSerializable(typeof(DispositionCodeDto))]
[JsonSerializable(typeof(List<DispositionCodeDto>))]
[JsonSerializable(typeof(CreateDispositionCodeRequest))]
[JsonSerializable(typeof(UpdateDispositionCodeRequest))]
[JsonSerializable(typeof(CampaignMetricsDto))]
[JsonSerializable(typeof(List<CampaignMetricsDto>))]
[JsonSerializable(typeof(WrapUpRequest))]
[JsonSerializable(typeof(CreateConversationRequest))]
// AOT (ADR-0022 Phase D): remaining ConversationEndpoints [FromBody] DTOs.
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(TransferRequest))]
[JsonSerializable(typeof(DashboardDto))]
[JsonSerializable(typeof(DashboardKpisDto))]
[JsonSerializable(typeof(TrendPointDto))]
[JsonSerializable(typeof(TrendPointDto[]))]
[JsonSerializable(typeof(ChannelDistributionDto))]
[JsonSerializable(typeof(ChannelDistributionDto[]))]
[JsonSerializable(typeof(CdrRowDto))]
[JsonSerializable(typeof(List<CdrRowDto>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CdrDetailDto))]
[JsonSerializable(typeof(CdrTimelineEventDto))]
[JsonSerializable(typeof(CdrTimelineEventDto[]))]
[JsonSerializable(typeof(CdrQaSummaryDto))]
[JsonSerializable(typeof(QaRowDto))]
[JsonSerializable(typeof(List<QaRowDto>))]
[JsonSerializable(typeof(QaDetailDto))]
[JsonSerializable(typeof(QaCriterionDto))]
[JsonSerializable(typeof(QaCriterionDto[]))]
[JsonSerializable(typeof(ComplianceViolationDto))]
[JsonSerializable(typeof(ComplianceViolationDto[]))]
[JsonSerializable(typeof(TopicDto))]
[JsonSerializable(typeof(TopicDto[]))]
[JsonSerializable(typeof(TurnSentimentDto))]
[JsonSerializable(typeof(TurnSentimentDto[]))]
[JsonSerializable(typeof(IntervalDto))]
[JsonSerializable(typeof(List<IntervalDto>))]
[JsonSerializable(typeof(AgentIntervalDto))]
[JsonSerializable(typeof(List<AgentIntervalDto>))]
[JsonSerializable(typeof(LiveStateDto))]
[JsonSerializable(typeof(List<LiveStateDto>))]
[JsonSerializable(typeof(CurrentIntervalDto))]
[JsonSerializable(typeof(QueueMetricsDto))]
[JsonSerializable(typeof(QueueMetricsDto[]))]
// AgentAssist runtime feature toggle (R5.1 Task J)
[JsonSerializable(typeof(AgentAssistFeatureDto))]
[JsonSerializable(typeof(AgentAssistFeatureUpdateRequest))]
[JsonSerializable(typeof(AgentAssistCredentialsDto))]
[JsonSerializable(typeof(TrunkDto))]
[JsonSerializable(typeof(List<TrunkDto>))]
[JsonSerializable(typeof(CreateTrunkRequest))]
[JsonSerializable(typeof(UpdateTrunkRequest))]
[JsonSerializable(typeof(DidRouteDto))]
[JsonSerializable(typeof(List<DidRouteDto>))]
[JsonSerializable(typeof(CreateDidRouteRequest))]
[JsonSerializable(typeof(UpdateDidRouteRequest))]
[JsonSerializable(typeof(OutboundRouteDto))]
[JsonSerializable(typeof(List<OutboundRouteDto>))]
[JsonSerializable(typeof(CreateOutboundRouteRequest))]
[JsonSerializable(typeof(UpdateOutboundRouteRequest))]
[JsonSerializable(typeof(long[]))]
// DNC Lists
[JsonSerializable(typeof(DncListDto))]
[JsonSerializable(typeof(List<DncListDto>))]
[JsonSerializable(typeof(CreateDncListRequest))]
[JsonSerializable(typeof(UpdateDncListRequest))]
[JsonSerializable(typeof(AddDncEntryRequest))]
[JsonSerializable(typeof(DncEntryDto))]
[JsonSerializable(typeof(List<DncEntryDto>))]
[JsonSerializable(typeof(DncImportResultDto))]
[JsonSerializable(typeof(DncCheckResultDto))]
// Caller ID Pools
[JsonSerializable(typeof(CallerIdPoolDto))]
[JsonSerializable(typeof(List<CallerIdPoolDto>))]
[JsonSerializable(typeof(CreateCallerIdPoolRequest))]
[JsonSerializable(typeof(UpdateCallerIdPoolRequest))]
[JsonSerializable(typeof(AddCallerIdEntryRequest))]
[JsonSerializable(typeof(CallerIdEntryDto))]
[JsonSerializable(typeof(List<CallerIdEntryDto>))]
// Holiday Calendars
[JsonSerializable(typeof(HolidayCalendarDto))]
[JsonSerializable(typeof(List<HolidayCalendarDto>))]
[JsonSerializable(typeof(CreateHolidayCalendarRequest))]
[JsonSerializable(typeof(UpdateHolidayCalendarRequest))]
[JsonSerializable(typeof(AddHolidayRequest))]
[JsonSerializable(typeof(HolidayDto))]
[JsonSerializable(typeof(List<HolidayDto>))]
// Recordings
[JsonSerializable(typeof(RecordingMetadataDto))]
// Queue Members
[JsonSerializable(typeof(AddQueueMemberRequest))]
[JsonSerializable(typeof(QueueMemberDto))]
[JsonSerializable(typeof(QueueMemberDto[]))]
[JsonSerializable(typeof(List<QueueMemberDto>))]
// Dialer Settings
[JsonSerializable(typeof(DialerSettingsDto))]
[JsonSerializable(typeof(UpdateDialerSettingsRequest))]
// Skills
[JsonSerializable(typeof(SkillDto))]
[JsonSerializable(typeof(List<SkillDto>))]
[JsonSerializable(typeof(AgentSkillDto))]
[JsonSerializable(typeof(List<AgentSkillDto>))]
[JsonSerializable(typeof(CreateSkillRequest))]
[JsonSerializable(typeof(UpsertSkillRequest))]
[JsonSerializable(typeof(AssignSkillRequest))]
// Call Attempts
[JsonSerializable(typeof(UpdateCallAttemptDispositionRequest))]
// Callbacks
[JsonSerializable(typeof(CallbackDto))]
[JsonSerializable(typeof(List<CallbackDto>))]
[JsonSerializable(typeof(CreateCallbackRequest))]
// Realtime Endpoint Profiles
[JsonSerializable(typeof(EndpointProfileDto))]
[JsonSerializable(typeof(List<EndpointProfileDto>))]
[JsonSerializable(typeof(CreateEndpointProfileRequest))]
[JsonSerializable(typeof(UpdateEndpointProfileRequest))]
// Management Cluster
[JsonSerializable(typeof(MgmtClusterStatusDto))]
[JsonSerializable(typeof(MgmtClusterNodeDto))]
[JsonSerializable(typeof(List<MgmtClusterNodeDto>))]
[JsonSerializable(typeof(MgmtDrainNodeRequest))]
[JsonSerializable(typeof(MgmtDrainStatusDto))]
[JsonSerializable(typeof(CreateNodeRequest))]
[JsonSerializable(typeof(UpdateNodeRequest))]
[JsonSerializable(typeof(MgmtInstanceDto))]
[JsonSerializable(typeof(List<MgmtInstanceDto>))]
// Impersonation
[JsonSerializable(typeof(ImpersonateRequest))]
[JsonSerializable(typeof(ImpersonateResponse))]
// R5.2 PB.2 — admin impersonation session-management DTOs.
[JsonSerializable(typeof(ImpersonationSessionDto))]
[JsonSerializable(typeof(PagedResult<ImpersonationSessionDto>))]
[JsonSerializable(typeof(RevokeImpersonationSessionRequest))]
// Management Tenants
[JsonSerializable(typeof(MgmtTenantDto))]
[JsonSerializable(typeof(List<MgmtTenantDto>))]
[JsonSerializable(typeof(CreateMgmtTenantRequest))]
[JsonSerializable(typeof(UpdateMgmtTenantRequest))]
// Partner Customers
[JsonSerializable(typeof(PartnerCustomerDto))]
[JsonSerializable(typeof(List<PartnerCustomerDto>))]
[JsonSerializable(typeof(CreatePartnerCustomerRequest))]
[JsonSerializable(typeof(UpdatePartnerCustomerRequest))]
[JsonSerializable(typeof(SuspendCustomerRequest))]
// Partner Billing
[JsonSerializable(typeof(PartnerGenerateInvoiceResponse))]
[JsonSerializable(typeof(PartnerRevenueSnapshotDto))]
// Partner Revenue
[JsonSerializable(typeof(PartnerRevenueSummaryDto))]
[JsonSerializable(typeof(PartnerRevenueDetailDto))]
[JsonSerializable(typeof(List<PartnerRevenueDetailDto>))]
// MFA admin (R5.2 PA.1)
[JsonSerializable(typeof(MfaUserSummary))]
[JsonSerializable(typeof(PagedResult<MfaUserSummary>))]
[JsonSerializable(typeof(MfaPolicyDto))]
[JsonSerializable(typeof(UserMeResponseDto))]
// Retention admin (R5.2 PC.1)
[JsonSerializable(typeof(RetentionTargetDto))]
[JsonSerializable(typeof(IReadOnlyList<RetentionTargetDto>))]
[JsonSerializable(typeof(List<RetentionTargetDto>))]
[JsonSerializable(typeof(RetentionConfigDto))]
[JsonSerializable(typeof(RetentionConfigPatchDto))]
[JsonSerializable(typeof(RetentionRunResultDto))]
[JsonSerializable(typeof(RetentionRunTargetOutcomeDto))]
// Management API Keys
[JsonSerializable(typeof(MgmtApiKeyDto))]
[JsonSerializable(typeof(List<MgmtApiKeyDto>))]
[JsonSerializable(typeof(CreateMgmtApiKeyRequest))]
[JsonSerializable(typeof(CreateMgmtApiKeyResponse))]
// Setup
[JsonSerializable(typeof(SetupRequest))]
[JsonSerializable(typeof(SetupResponse))]
// System Settings
[JsonSerializable(typeof(SystemSettingsRequest))]
// Auth
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(TokenUserDto))]
// Agent Assist
[JsonSerializable(typeof(KeywordRuleDto))]
[JsonSerializable(typeof(KeywordRuleDto[]))]
[JsonSerializable(typeof(ComplianceRuleDto))]
[JsonSerializable(typeof(ComplianceRuleDto[]))]
[JsonSerializable(typeof(AgentAssistConfigSnapshot))]
[JsonSerializable(typeof(AgentAssistSessionDto))]
[JsonSerializable(typeof(SuggestionLogRowDto))]
[JsonSerializable(typeof(SuggestionLogRowDto[]))]
[JsonSerializable(typeof(ComplianceAlertRowDto))]
[JsonSerializable(typeof(ComplianceAlertRowDto[]))]
// Cases
[JsonSerializable(typeof(Case))]
[JsonSerializable(typeof(PagedResult<Case>))]
[JsonSerializable(typeof(CreateCaseRequest))]
[JsonSerializable(typeof(UpdateCaseRequest))]
// Bot Analytics
[JsonSerializable(typeof(BotAnalyticsSummary))]
// Bots
[JsonSerializable(typeof(BotDto))]
[JsonSerializable(typeof(BotDto[]))]
[JsonSerializable(typeof(CreateBotRequest))]
[JsonSerializable(typeof(UpdateBotRequest))]
// Call Analytics
[JsonSerializable(typeof(TopicTrendDto))]
[JsonSerializable(typeof(TopicTrendDto[]))]
[JsonSerializable(typeof(TopicTrendsResponse))]
[JsonSerializable(typeof(SentimentTrendPointDto))]
[JsonSerializable(typeof(SentimentTrendPointDto[]))]
[JsonSerializable(typeof(SentimentTrendsResponse))]
[JsonSerializable(typeof(ComplianceRuleSummaryDto))]
[JsonSerializable(typeof(ComplianceRuleSummaryDto[]))]
[JsonSerializable(typeof(ComplianceSeverityBreakdownDto))]
[JsonSerializable(typeof(ComplianceSummaryResponse))]
// Shared response DTOs
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(TenantTypeMismatchProblem))]  // ADR-0027 — operational tenant-type gate response
[JsonSerializable(typeof(ErrorDetailResponse))]
[JsonSerializable(typeof(MessageResponse))]
[JsonSerializable(typeof(StatusUpdateResponse))]
[JsonSerializable(typeof(PagedDataResponse<CdrRowDto>))]
[JsonSerializable(typeof(PagedDataResponse<QaRowDto>))]
// Auth
// AOT (ADR-0022 Phase D): request bodies deserialized by AuthEndpoints. Without
// these the AOT host throws NotSupported(JsonTypeInfo metadata not provided) on
// /auth/login etc. since STJ has no reflection fallback under PublishAot.
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(ApiKeyLoginRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(ForgotPasswordRequest))]
[JsonSerializable(typeof(ResetPasswordRequest))]
[JsonSerializable(typeof(MfaVerifyRequest))]
[JsonSerializable(typeof(MfaConfirmRequest))]
[JsonSerializable(typeof(MfaDisableRequest))]
[JsonSerializable(typeof(MfaSetupResponse))]
[JsonSerializable(typeof(RevokedSessionsResponse))]
// ADMIN-001 (PREPUB-2026-05-09): redacted projection of TenantAuthConfig
// returned by /admin/auth/config — emits OidcClientSecretSet + fingerprint
// instead of the raw OIDC client secret.
[JsonSerializable(typeof(TenantAuthConfigResponse))]
[JsonSerializable(typeof(MfaChallengeResponse))]
[JsonSerializable(typeof(MfaEnrollmentRequiredResponse))]
[JsonSerializable(typeof(MfaStepUpRequiredResponse))]
[JsonSerializable(typeof(PasswordResetMfaRequiredResponse))]
[JsonSerializable(typeof(AuthEventDetail))]
[JsonSerializable(typeof(UserSessionDto))]
[JsonSerializable(typeof(UserSessionDto[]))]
[JsonSerializable(typeof(ActiveSession))]
[JsonSerializable(typeof(IReadOnlyList<ActiveSession>))]
[JsonSerializable(typeof(RegenerateRecoveryCodesRequest))]
[JsonSerializable(typeof(RecoveryCodesResponse))]
[JsonSerializable(typeof(PasswordPolicyDto))]
// R5.2 PA.2 — Profile-scoped MFA wizard / sessions / recovery-codes.
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.MfaEnrollInitResponse))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.MfaEnrollVerifyRequest))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.MfaEnrollCompleteRequest))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.RecoveryCodesPayload))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.ProfileUserSessionDto))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.ProfileUserSessionDto[]))]
[JsonSerializable(typeof(Verbara.Platform.Api.Endpoints.Profile.ProfileRegenerateRecoveryCodesRequest))]
// RBAC
[JsonSerializable(typeof(PermissionGroupDto))]
[JsonSerializable(typeof(List<PermissionGroupDto>))]
[JsonSerializable(typeof(UserPermissionsDto))]
[JsonSerializable(typeof(PermissionDefinition))]
[JsonSerializable(typeof(List<PermissionDefinition>))]
// Channel Config
[JsonSerializable(typeof(ChannelStatusDto))]
[JsonSerializable(typeof(ChannelTestResponse))]
[JsonSerializable(typeof(TenantChannelConfig))]  // ADR-0026 Phase A.2 — fix AOT bug surfaced by living-docs (GET /admin/channels/{channel} returned 500 for enabled channels)
[JsonSerializable(typeof(ChannelChangeAudit))]  // ADR-0026 Phase A.2 — typed audit DTO replacing anonymous types in UpdateChannelConfig
[JsonSerializable(typeof(IReadOnlyDictionary<string, bool>))]
// Management System
[JsonSerializable(typeof(SystemInfoDto))]
[JsonSerializable(typeof(LicenseInfoDto))]
// Pro v2.4.0-pro — License status snapshot for GET /management/system/license/status
[JsonSerializable(typeof(Verbara.Sdk.Pro.Licensing.LicenseStatusSnapshot))]
[JsonSerializable(typeof(SystemSettingsDto))]
// GDPR
[JsonSerializable(typeof(GdprExportRequest))]
[JsonSerializable(typeof(GdprPurgeRequest))]
[JsonSerializable(typeof(GdprExportResult))]
[JsonSerializable(typeof(GdprSubjectInfo))]
[JsonSerializable(typeof(GdprExportData))]
[JsonSerializable(typeof(GdprConversationRecord))]
[JsonSerializable(typeof(GdprRecordingRecord))]
[JsonSerializable(typeof(GdprCallDetailRecord))]
[JsonSerializable(typeof(GdprSurveyResponseRecord))]
[JsonSerializable(typeof(GdprAuditRecord))]
[JsonSerializable(typeof(List<GdprConversationRecord>))]
[JsonSerializable(typeof(List<GdprRecordingRecord>))]
[JsonSerializable(typeof(List<GdprCallDetailRecord>))]
[JsonSerializable(typeof(List<GdprSurveyResponseRecord>))]
[JsonSerializable(typeof(List<GdprAuditRecord>))]
[JsonSerializable(typeof(PurgeResult))]
[JsonSerializable(typeof(PagedResult<PurgeEntry>))]
[JsonSerializable(typeof(PurgeEntry))]
[JsonSerializable(typeof(RetentionPolicyDto))]
[JsonSerializable(typeof(UpdateRetentionPolicyRequest))]
[JsonSerializable(typeof(TenantRetentionPolicy))]
[JsonSerializable(typeof(Dictionary<string, int>))]
// TenantSettings Facade
[JsonSerializable(typeof(TenantSettingsDto))]
[JsonSerializable(typeof(DunningStatusDto))]
[JsonSerializable(typeof(DunningRecordDto))]
[JsonSerializable(typeof(TenantPlan))]
[JsonSerializable(typeof(PlanFeature))]
[JsonSerializable(typeof(PaymentStatus))]
[JsonSerializable(typeof(IReadOnlyList<PlanFeature>))]
[JsonSerializable(typeof(UpdateTenantSettingsRequest))]
[JsonSerializable(typeof(OperationalSettingsDto))]
[JsonSerializable(typeof(AuthSettingsDto))]
[JsonSerializable(typeof(QuotaSettingsDto))]
[JsonSerializable(typeof(RetentionSettingsDto))]
[JsonSerializable(typeof(UpdateOperationalSettingsDto))]
[JsonSerializable(typeof(UpdateAuthSettingsDto))]
[JsonSerializable(typeof(UpdateQuotaSettingsDto))]
[JsonSerializable(typeof(UpdateRetentionSettingsDto))]
[JsonSerializable(typeof(BrandingSettingsDto))]
[JsonSerializable(typeof(UpdateBrandingSettingsDto))]
[JsonSerializable(typeof(UpdateManagementBrandingSettingsDto))]
[JsonSerializable(typeof(ManagementUpdateTenantSettingsRequest))]
// Webhooks
[JsonSerializable(typeof(WebhookSubscription))]
[JsonSerializable(typeof(List<WebhookSubscription>))]
[JsonSerializable(typeof(WebhookDelivery))]
[JsonSerializable(typeof(List<WebhookDelivery>))]
[JsonSerializable(typeof(PagedResult<WebhookDelivery>))]
[JsonSerializable(typeof(WebhookEventPayload))]
[JsonSerializable(typeof(WebhookDeliveryStatus))]
[JsonSerializable(typeof(CreateWebhookSubscriptionRequest))]
[JsonSerializable(typeof(UpdateWebhookSubscriptionRequest))]
[JsonSerializable(typeof(WebhookEventTypeDto))]
[JsonSerializable(typeof(List<WebhookEventTypeDto>))]
// Notifications
[JsonSerializable(typeof(NotificationEndpoints.NotificationDto))]
[JsonSerializable(typeof(List<NotificationEndpoints.NotificationDto>))]
[JsonSerializable(typeof(NotificationEndpoints.UnreadCountDto))]
// Branding (public)
[JsonSerializable(typeof(BrandingEndpoints.PublicBrandingDto))]
// Supervisor Digital
[JsonSerializable(typeof(SupervisorCloseRequest))]
[JsonSerializable(typeof(CoachingNoteRequest))]
// Canned Responses
[JsonSerializable(typeof(CannedResponseEndpoints.CreateCannedResponseRequest))]
[JsonSerializable(typeof(CannedResponseEndpoints.UpdateCannedResponseRequest))]
[JsonSerializable(typeof(CannedResponseEndpoints.CannedResponseDto))]
[JsonSerializable(typeof(List<CannedResponseEndpoints.CannedResponseDto>))]
// WebChat
[JsonSerializable(typeof(WebChatEndpoints.CreateSessionRequest))]
[JsonSerializable(typeof(WebChatEndpoints.CreateSessionResponse))]
[JsonSerializable(typeof(WebChatEndpoints.WebChatMessageRequest))]
// Onboarding
[JsonSerializable(typeof(OnboardingStatusDto))]
[JsonSerializable(typeof(ChecklistItemDto))]
[JsonSerializable(typeof(List<ChecklistItemDto>))]
[JsonSerializable(typeof(ApplyTemplateRequest))]
// IP Allowlist management
[JsonSerializable(typeof(IpAllowlistEntryDto))]
[JsonSerializable(typeof(IpAllowlistListResponse))]
[JsonSerializable(typeof(AddIpAllowlistEntryRequest))]
// AOT: ProblemDetails, Email, Reports
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(EmailMessage))]
[JsonSerializable(typeof(EmailRecipient))]
[JsonSerializable(typeof(EmailAttachment))]
[JsonSerializable(typeof(ReportData))]
[JsonSerializable(typeof(ReportDataRow))]
// ─── AOT (ADR-0022 Phase D): endpoint response collection types ──────────────
// Results.Ok(collection) serializes the static (compile-time) type, so the exact
// collection type returned by each handler needs a source-gen JsonTypeInfo, not
// just its element type. These were emitted by GET endpoints that returned
// store collections directly.
[JsonSerializable(typeof(RoleTemplate))]
[JsonSerializable(typeof(IReadOnlyList<RoleTemplate>))]
[JsonSerializable(typeof(UserRoleAssignment))]
[JsonSerializable(typeof(IReadOnlyList<UserRoleAssignment>))]
[JsonSerializable(typeof(TenantRole))]
[JsonSerializable(typeof(IReadOnlyList<TenantRole>))]
[JsonSerializable(typeof(Disposition))]
[JsonSerializable(typeof(IReadOnlyList<Disposition>))]
[JsonSerializable(typeof(RateCardDto))]
[JsonSerializable(typeof(List<RateCardDto>))]
[JsonSerializable(typeof(IntervalDto[]))]
[JsonSerializable(typeof(AuthEvent))]
[JsonSerializable(typeof(PagedResult<AuthEvent>))]
[JsonSerializable(typeof(IReadOnlyList<Message>))]
[JsonSerializable(typeof(UsageSummaryDto))]
[JsonSerializable(typeof(List<UsageSummaryDto>))]
[JsonSerializable(typeof(JwtKeyListResponse))]
[JsonSerializable(typeof(RotateKeyResponse))]
[JsonSerializable(typeof(AuditEventDto))]
[JsonSerializable(typeof(PagedResult<AuditEventDto>))]
[JsonSerializable(typeof(Verbara.Platform.Media.MediaFile))]
// ─── AOT (ADR-0022 Phase D): endpoint [FromBody] request DTOs ────────────────
// Under PublishAot, System.Text.Json has NO reflection fallback, so every
// request body deserialized by a Minimal API handler MUST have a source-gen
// JsonTypeInfo. The DTOs below were missing from the manifest and threw
// NotSupportedException at request time under the no-reflection contract. Grouped
// by owning endpoint file.
// Admin (users / agents / queues / teams)
[JsonSerializable(typeof(CreateUserRequest))]
[JsonSerializable(typeof(UpdateUserRequest))]
[JsonSerializable(typeof(CreateQueueRequest))]
[JsonSerializable(typeof(UpdateQueueRequest))]
[JsonSerializable(typeof(CreateAgentRequest))]
[JsonSerializable(typeof(QueueMembershipRequest))]  // ADR-0026 Phase A.1
[JsonSerializable(typeof(AgentQueueMembershipDto))]  // ADR-0026 Phase A.6
[JsonSerializable(typeof(List<AgentQueueMembershipDto>))]  // ADR-0026 Phase A.6
[JsonSerializable(typeof(UpdateAgentRequest))]
[JsonSerializable(typeof(CreateTeamRequest))]
[JsonSerializable(typeof(UpdateTeamRequest))]
// Agent / Supervisor
[JsonSerializable(typeof(UpdateAgentStateRequest))]
[JsonSerializable(typeof(WhisperRequest))]
[JsonSerializable(typeof(ListenRequest))]
// Queue members
[JsonSerializable(typeof(AddMemberBody))]
[JsonSerializable(typeof(UpdateMemberBody))]
[JsonSerializable(typeof(PauseMemberBody))]
[JsonSerializable(typeof(QueueMemberUpdateAudit))]  // ADR-0026 Phase A.6 audit payload
// Contacts
[JsonSerializable(typeof(CreateContactRequest))]
[JsonSerializable(typeof(UpdateContactRequest))]
// RBAC (tenant roles)
[JsonSerializable(typeof(CreateTenantRoleRequest))]
[JsonSerializable(typeof(UpdateTenantRoleRequest))]
[JsonSerializable(typeof(CloneTenantRoleRequest))]
[JsonSerializable(typeof(ReplaceUserRolesRequest))]
// KnowledgeBase
[JsonSerializable(typeof(CreateArticleRequest))]
[JsonSerializable(typeof(UpdateArticleRequest))]
// Surveys
[JsonSerializable(typeof(CreateSurveyRequest))]
[JsonSerializable(typeof(UpdateSurveyRequest))]
[JsonSerializable(typeof(ActivateSurveyRequest))]
// Flows
[JsonSerializable(typeof(CreateFlowRequest))]
[JsonSerializable(typeof(UpdateFlowRequest))]
// Campaigns (Dialer)
[JsonSerializable(typeof(CreateCampaignRequest))]
[JsonSerializable(typeof(UpdateCampaignRequest))]
// Dispositions
[JsonSerializable(typeof(CreateDispositionRequest))]
// Scheduled reports
[JsonSerializable(typeof(CreateScheduledReportRequest))]
[JsonSerializable(typeof(UpdateScheduledReportRequest))]
// Channel config
[JsonSerializable(typeof(UpdateChannelConfigRequest))]
// Management — billing
[JsonSerializable(typeof(CreateRateCardRequest))]
[JsonSerializable(typeof(GenerateInvoiceRequest))]
[JsonSerializable(typeof(UpdateQuotaRequest))]
// Management — system / license
[JsonSerializable(typeof(UpdateLicenseRequest))]
// Auth admin (tenant auth config)
[JsonSerializable(typeof(UpdateTenantAuthConfigRequest))]
// GDPR
[JsonSerializable(typeof(GdprUserPurgeRequest))]
// Profile-scoped MFA enrolment + recovery codes
[JsonSerializable(typeof(MfaEnrollVerifyRequest))]
[JsonSerializable(typeof(MfaEnrollCompleteRequest))]
[JsonSerializable(typeof(ProfileRegenerateRecoveryCodesRequest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
internal partial class ApiJsonContext : JsonSerializerContext;
