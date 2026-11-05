using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Services;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The identity token verifier, against every vector of <c>Conversations/Fixtures/webchat-identity-token.v1.json</c>,
/// verified at the fixture's <c>now</c>, with the fixture's secrets.
/// </summary>
public sealed class WebChatIdentityTokenVerifierTests
{
    private const string Current = "identityTokenSecret";
    private const string Previous = "identityTokenSecretPrevious";
    private const string ReplacedAt = "identityTokenSecretReplacedAt";

    private static readonly JsonObject Fixture = JsonNode.Parse(File.ReadAllText(
        Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "webchat-identity-token.v1.json")))!.AsObject();

    private static readonly JsonNode Vector = Fixture["testVector"]!;
    private static readonly string TenantId = Vector["tenantId"]!.GetValue<string>();
    private static readonly string CurrentSecret = Vector["currentSecret"]!.GetValue<string>();
    private static readonly string PreviousSecret = Vector["previousSecret"]!.GetValue<string>();
    private static readonly string Stamp = Vector["previousSecretReplacedAt"]!.GetValue<string>();
    private static readonly long NowSeconds = Vector["now"]!.GetValue<long>();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(NowSeconds);

    private static readonly Dictionary<string, string> InGrace = new()
    {
        [Current] = CurrentSecret,
        [Previous] = PreviousSecret,
        [ReplacedAt] = Stamp,
    };

    private static WebChatIdentityTokenVerifier Sut(ILogger<WebChatIdentityTokenVerifier>? logger = null) =>
        new(logger ?? NullLogger<WebChatIdentityTokenVerifier>.Instance);

    private static WebChatIdentityTokenResult Verify(
        string token, IReadOnlyDictionary<string, string>? credentials = null, DateTimeOffset? now = null) =>
        Sut().Verify(token, TenantId, credentials ?? InGrace, now ?? Now);

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    private static string Sign(string payload, string? secret = null, string header = """{"alg":"HS256","typ":"JWT"}""")
    {
        var signed = $"{Segment(header)}.{Segment(payload)}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret ?? CurrentSecret), Encoding.ASCII.GetBytes(signed));
        return $"{signed}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Claims(
        string externalId = "\"cust-1\"",
        string aud = "\"t-demo-0001\"",
        long? iat = null,
        long? exp = null,
        string extra = "")
    {
        var issued = iat ?? NowSeconds - 300;
        var expires = exp ?? issued + 1800;
        return $$"""{"external_id":{{externalId}},"aud":{{aud}},"iat":{{issued}},"exp":{{expires}}{{extra}}}""";
    }

    // PostgreSQL's jsonb refuses U+0000, and external_id becomes an address verbatim, so it could never be stored.
    [Fact]
    public void Verify_ShouldAnswerMalformed_WhenExternalIdCarriesTheNulCharacter()
    {
        Verify(Sign(Claims(externalId: "\"cust\\u0000-1\""))).RefusalCode.Should().Be(WebChatIdentityTokenRefusal.Malformed);
    }

    private static string ValidPreviousToken =>
        Vector["accepted"]!.AsArray().Single(a => a!["case"]!.GetValue<string>().Contains("previous secret"))!["token"]!.GetValue<string>();

    public static TheoryData<int> AcceptedIndexes() => Indexes("accepted", _ => true);

    public static TheoryData<int> RefusedTokenIndexes() =>
        Indexes("refused", r => !r["token"]!.GetValue<string>().StartsWith('(')); // "(any token)" and the like have their own tests

    private static TheoryData<int> Indexes(string list, Func<JsonNode, bool> take)
    {
        var data = new TheoryData<int>();
        var vectors = Vector[list]!.AsArray();
        for (var i = 0; i < vectors.Count; i++)
        {
            if (take(vectors[i]!))
                data.Add(i);
        }

        return data;
    }

    // ─── The fixture's vectors ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AcceptedIndexes))]
    public void Verify_ShouldAcceptAndReturnThePayload_WhenVectorIsAccepted(int index)
    {
        var vector = Vector["accepted"]!.AsArray()[index]!;
        var payload = Vector["payload"]!;

        var result = Verify(vector["token"]!.GetValue<string>());

        result.RefusalCode.Should().BeNull();
        result.Claims.Should().NotBeNull();
        result.Claims!.ExternalId.Should().Be(payload["external_id"]!.GetValue<string>());
        result.Claims.Name.Should().Be(payload["name"]!.GetValue<string>());
        result.Claims.Email.Should().Be(payload["email"]!.GetValue<string>());
        ("webchat-external:" + result.Claims.ExternalId).Should().Be(vector["contactAddress"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(RefusedTokenIndexes))]
    public void Verify_ShouldAnswerTheVectorsCode_WhenVectorIsRefused(int index)
    {
        var vector = Vector["refused"]!.AsArray()[index]!;

        var result = Verify(vector["token"]!.GetValue<string>());

        result.Claims.Should().BeNull();
        result.RefusalCode.Should().Be(vector["identityTokenRefusal"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Verify_ShouldAnswerNotConfigured_WhenTenantHasNoSecret(string? secret)
    {
        var credentials = new Dictionary<string, string> { ["defaultQueueId"] = "q1" };
        if (secret is not null)
            credentials[Current] = secret;

        foreach (var token in new[] { "not-a-token", Vector["accepted"]![0]!["token"]!.GetValue<string>() })
        {
            Verify(token, credentials).RefusalCode.Should().Be("identity-token-not-configured");
        }
    }

    [Fact]
    public void Verify_ShouldAnswerNotConfigured_WhenThereAreNoCredentials() =>
        Sut().Verify("not-a-token", TenantId, null, Now).RefusalCode.Should().Be("identity-token-not-configured");

    [Fact]
    public void Verify_ShouldNeverUseThePreviousSecret_WhenThereIsNoCurrentOne()
    {
        var credentials = new Dictionary<string, string> { [Previous] = PreviousSecret, [ReplacedAt] = Stamp };

        Verify(ValidPreviousToken, credentials).RefusalCode.Should().Be("identity-token-not-configured");
    }

    // ─── The previous secret's grace ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_ShouldRefusePreviousSecret_WhenItsGraceHasEnded()
    {
        // The vector's own clock rule would expire this token first, so its lifetime is moved to that instant.
        var at = DateTimeOffset.Parse("2026-11-05T09:00:01Z", System.Globalization.CultureInfo.InvariantCulture);
        var token = Sign(Claims(iat: at.ToUnixTimeSeconds() - 60), PreviousSecret);

        Verify(token, now: at).RefusalCode.Should().Be("identity-token-signature-invalid");
    }

    [Fact]
    public void Verify_ShouldHoldTheGraceBoundary_WhenNowIsAt24Hours()
    {
        var end = DateTimeOffset.Parse(Stamp, System.Globalization.CultureInfo.InvariantCulture).AddHours(24);
        var token = Sign(Claims(iat: end.ToUnixTimeSeconds() - 60), PreviousSecret);

        Verify(token, now: end.AddSeconds(-1)).RefusalCode.Should().BeNull("inside the grace");
        Verify(token, now: end).RefusalCode.Should().Be("identity-token-signature-invalid", "now must be less than 24 h after the stamp");
    }

    [Fact]
    public void Verify_ShouldRefusePreviousSecret_WhenItWasRemoved()
    {
        Verify(ValidPreviousToken, new Dictionary<string, string> { [Current] = CurrentSecret })
            .RefusalCode.Should().Be("identity-token-signature-invalid");
    }

    [Fact]
    public void Verify_ShouldRefusePreviousSecret_WhenStampIsAbsent()
    {
        var credentials = new Dictionary<string, string> { [Current] = CurrentSecret, [Previous] = PreviousSecret };

        Verify(ValidPreviousToken, credentials).RefusalCode.Should().Be("identity-token-signature-invalid");
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("")]
    [InlineData("11/04/2026 09:00:00")] // a date, not an ISO 8601 instant
    [InlineData("2026-11-04T09:00:00")] // no offset, so not an instant
    [InlineData("2026-11-04")]
    public void Verify_ShouldRefusePreviousSecret_WhenStampDoesNotParse(string stamp)
    {
        var credentials = new Dictionary<string, string> { [Current] = CurrentSecret, [Previous] = PreviousSecret, [ReplacedAt] = stamp };

        Verify(ValidPreviousToken, credentials).RefusalCode.Should().Be("identity-token-signature-invalid");
    }

    [Theory]
    [InlineData("2026-11-04T09:00:00.000+00:00")]
    [InlineData("2026-11-04T09:00:00Z")]
    [InlineData("2026-11-04T09:00:00.0000000+00:00")]
    [InlineData("2026-11-04T04:00:00-05:00")]
    public void Verify_ShouldAcceptPreviousSecret_WhenStampIsAnIsoInstant(string stamp)
    {
        var credentials = new Dictionary<string, string> { [Current] = CurrentSecret, [Previous] = PreviousSecret, [ReplacedAt] = stamp };

        Verify(ValidPreviousToken, credentials).RefusalCode.Should().BeNull();
    }

    // ─── Evaluation order: each token breaks two rules, and the fixture's earlier rule answers ───────

    [Fact]
    public void Verify_ShouldFollowFixtureEvaluationOrder_WhenTokenBreaksTwoRules()
    {
        Fixture["evaluationOrder"]!.AsArray().Should().HaveCount(8);
        var soon = NowSeconds + 3600;
        var old = NowSeconds - 86_400;

        // 1 before 2: no secret, malformed token.
        Verify("not-a-token", new Dictionary<string, string>()).RefusalCode.Should().Be("identity-token-not-configured");
        // 2 before 3: a bad alg with a signature that would not verify anyway.
        Verify(Sign(Claims(), "another-secret-of-the-same-length-000000", """{"alg":"HS512","typ":"JWT"}"""))
            .RefusalCode.Should().Be("identity-token-malformed");
        // 3 before 4: a bad signature over a payload that is not valid JSON.
        Verify(Sign("{nope", "another-secret-of-the-same-length-000000"))
            .RefusalCode.Should().Be("identity-token-signature-invalid");
        // 4 before 5: no external_id, and another tenant.
        Verify(Sign("""{"aud":"t-other-0002","iat":1793800800,"exp":1793802600}"""))
            .RefusalCode.Should().Be("identity-token-malformed");
        // 5 before 6: another tenant, and a lifetime over the cap.
        Verify(Sign(Claims(aud: "\"t-other-0002\"", exp: NowSeconds + 86_400)))
            .RefusalCode.Should().Be("identity-token-wrong-tenant");
        // 6 before 7: a lifetime over the cap, issued in the future.
        Verify(Sign(Claims(iat: soon, exp: soon + 86_400)))
            .RefusalCode.Should().Be("identity-token-lifetime-too-long");
        // 6 before 8: a lifetime over the cap, long expired.
        Verify(Sign(Claims(iat: old - 86_400, exp: old)))
            .RefusalCode.Should().Be("identity-token-lifetime-too-long");
        // 7 and 8 cannot both hold for a token with exp > iat, so 7 is shown with a valid exp.
        Verify(Sign(Claims(iat: soon, exp: soon + 60))).RefusalCode.Should().Be("identity-token-not-yet-valid");
    }

    [Fact]
    public void Verify_ShouldListEveryRefusalCode_WhenCodesAreTheFixtures()
    {
        Fixture["refusalCodes"]!.AsArray().Select(c => c!.GetValue<string>()).Should().BeEquivalentTo(
            WebChatIdentityTokenRefusal.NotConfigured,
            WebChatIdentityTokenRefusal.Malformed,
            WebChatIdentityTokenRefusal.SignatureInvalid,
            WebChatIdentityTokenRefusal.WrongTenant,
            WebChatIdentityTokenRefusal.LifetimeTooLong,
            WebChatIdentityTokenRefusal.NotYetValid,
            WebChatIdentityTokenRefusal.Expired);
    }

    // ─── Shape ───────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> MalformedShapes() => new()
    {
        "",
        "a.b",
        "a.b.c.d",
        "eyJhbGciOiJIUzI1NiJ9..",
        "!!!.###.$$$",
    };

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public void Verify_ShouldAnswerMalformed_WhenShapeIsWrong(string token) =>
        Verify(token).RefusalCode.Should().Be("identity-token-malformed");

    [Fact]
    public void Verify_ShouldHoldTheLengthLimit_WhenTokenIsOver4096()
    {
        var header = """{"alg":"HS256"}""";
        var fits = Sign(Claims(extra: $",\"x\":\"{new string('y', 2000)}\""), header: header);
        var over = Sign(Claims(extra: $",\"x\":\"{new string('y', 4000)}\""), header: header);

        fits.Length.Should().BeLessThanOrEqualTo(4096);
        over.Length.Should().BeGreaterThan(4096);
        Verify(fits).RefusalCode.Should().BeNull("an unknown claim is ignored");
        Verify(over).RefusalCode.Should().Be("identity-token-malformed");
    }

    [Theory]
    [InlineData("""{"alg":"HS256"}""", null)]
    [InlineData("""{"alg":"HS256","typ":"JWT","kid":"anything"}""", null)]
    [InlineData("""{"alg":"HS256","typ":"at+jwt"}""", "identity-token-malformed")]
    [InlineData("""{"typ":"JWT"}""", "identity-token-malformed")]
    [InlineData("""{"alg":"hs256","typ":"JWT"}""", "identity-token-malformed")]
    [InlineData("""{"alg":"RS256","typ":"JWT"}""", "identity-token-malformed")]
    [InlineData("""{"alg":1}""", "identity-token-malformed")]
    [InlineData("""[]""", "identity-token-malformed")]
    [InlineData("""nope""", "identity-token-malformed")]
    public void Verify_ShouldApplyHeaderRules_WhenHeaderIsThis(string header, string? code) =>
        Verify(Sign(Claims(), header: header)).RefusalCode.Should().Be(code);

    [Fact]
    public void Verify_ShouldAnswerMalformed_WhenASegmentHoldsWhitespace()
    {
        var token = Sign(Claims());

        Verify(token.Insert(token.LastIndexOf('.') + 3, " ")).RefusalCode.Should().Be("identity-token-malformed");
    }

    [Fact]
    public void Verify_ShouldAnswerSignatureInvalid_WhenSignatureHasTheWrongLength()
    {
        var token = Sign(Claims());
        var unsigned = token[..token.LastIndexOf('.')];

        Verify($"{unsigned}.{Base64Url.EncodeToString(new byte[16])}").RefusalCode.Should().Be("identity-token-signature-invalid");
    }

    // ─── Claims ──────────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> MalformedPayloads() => new()
    {
        "[]",
        "{nope",
        """{"aud":"t-demo-0001","iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":"t-demo-0001","exp":1793802600}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":1793800800}""",
        """{"external_id":1,"aud":"t-demo-0001","iat":1793800800,"exp":1793802600}""",
        """{"external_id":null,"aud":"t-demo-0001","iat":1793800800,"exp":1793802600}""",
        """{"external_id":"   ","aud":"t-demo-0001","iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":null,"iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":7,"iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":[],"iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":["t-demo-0001",7],"iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":["t-other-0002",null],"iat":1793800800,"exp":1793802600}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":"1793800800","exp":1793802600}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":1793800800.5,"exp":1793802600}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":1793800800,"exp":true}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":1793800800,"exp":1793802600,"name":1}""",
        """{"external_id":"c","aud":"t-demo-0001","iat":1793800800,"exp":1793802600,"email":{}}""",
    };

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void Verify_ShouldAnswerMalformed_WhenClaimsAreNotWellTyped(string payload) =>
        Verify(Sign(payload)).RefusalCode.Should().Be("identity-token-malformed");

    [Fact]
    public void Verify_ShouldAnswerMalformed_WhenPayloadIsNotUtf8()
    {
        var header = Segment("""{"alg":"HS256"}""");
        // {"external_id":"<invalid byte>", ...}
        var bytes = Encoding.ASCII.GetBytes("""{"external_id":"x","aud":"t-demo-0001","iat":1793800800,"exp":1793802600}""");
        bytes[16] = 0xFF;
        var signed = $"{header}.{Base64Url.EncodeToString(bytes)}";
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(CurrentSecret), Encoding.ASCII.GetBytes(signed));

        Verify($"{signed}.{Base64Url.EncodeToString(signature)}").RefusalCode.Should().Be("identity-token-malformed");
    }

    [Theory]
    [InlineData(256, null)]
    [InlineData(257, "identity-token-malformed")]
    public void Verify_ShouldBoundExternalId_WhenItHasThisLength(int length, string? code) =>
        Verify(Sign(Claims(externalId: $"\"{new string('c', length)}\""))).RefusalCode.Should().Be(code);

    [Fact]
    public void Verify_ShouldBoundExternalIdAfterTrimming_AndKeepItVerbatim_WhenItIsPadded()
    {
        var padded = "  " + new string('c', 256) + "  ";

        var result = Verify(Sign(Claims(externalId: $"\"{padded}\"")));

        result.RefusalCode.Should().BeNull();
        result.Claims!.ExternalId.Should().Be(padded);
    }

    [Theory]
    [InlineData("name", 200, null)]
    [InlineData("name", 201, "identity-token-malformed")]
    [InlineData("email", 320, null)]
    [InlineData("email", 321, "identity-token-malformed")]
    public void Verify_ShouldBoundDisplayClaims_WhenTheyHaveThisLength(string claim, int length, string? code) =>
        Verify(Sign(Claims(extra: $",\"{claim}\":\"{new string('n', length)}\""))).RefusalCode.Should().Be(code);

    [Fact]
    public void Verify_ShouldLeaveDisplayClaimsNull_WhenAbsent()
    {
        var result = Verify(Sign(Claims()));

        result.RefusalCode.Should().BeNull();
        result.Claims!.Name.Should().BeNull();
        result.Claims.Email.Should().BeNull();
    }

    [Theory]
    [InlineData("\"\"", "identity-token-wrong-tenant")]
    [InlineData("\"T-DEMO-0001\"", "identity-token-wrong-tenant")]
    [InlineData("""["t-other-0002"]""", "identity-token-wrong-tenant")]
    [InlineData("""["t-demo-0001","t-demo-0001"]""", null)]
    public void Verify_ShouldCompareAudOrdinally_WhenAudIsThis(string aud, string? code) =>
        Verify(Sign(Claims(aud: aud))).RefusalCode.Should().Be(code);

    // ─── The clock ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(3600, null)]
    [InlineData(3601, "identity-token-lifetime-too-long")]
    [InlineData(0, "identity-token-lifetime-too-long")]
    [InlineData(-1, "identity-token-lifetime-too-long")]
    public void Verify_ShouldHoldTheLifetimeCap_WhenLifetimeIsThis(int lifetime, string? code)
    {
        var iat = NowSeconds - 60;

        Verify(Sign(Claims(iat: iat, exp: iat + lifetime))).RefusalCode.Should().Be(code);
    }

    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(long.MinValue, 1793802600)]
    [InlineData(-1, long.MaxValue)]
    public void Verify_ShouldAnswerLifetimeTooLong_WhenTheClaimsWouldOverflow(long iat, long exp) =>
        Verify(Sign(Claims(iat: iat, exp: exp))).RefusalCode.Should().Be("identity-token-lifetime-too-long");

    [Fact]
    public void Verify_ShouldHoldTheIssuedSkew_WhenIatIsAtTheBoundary()
    {
        Verify(Sign(Claims(iat: NowSeconds + 300))).RefusalCode.Should().BeNull();
        Verify(Sign(Claims(iat: NowSeconds + 301))).RefusalCode.Should().Be("identity-token-not-yet-valid");
    }

    [Fact]
    public void Verify_ShouldHoldTheExpirySkew_WhenExpIsAtTheBoundary()
    {
        Verify(Sign(Claims(iat: NowSeconds - 400, exp: NowSeconds - 300))).RefusalCode.Should().BeNull();
        Verify(Sign(Claims(iat: NowSeconds - 400, exp: NowSeconds - 301))).RefusalCode.Should().Be("identity-token-expired");
    }

    // ─── Logging ─────────────────────────────────────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger<WebChatIdentityTokenVerifier>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    [Theory]
    [MemberData(nameof(RefusedTokenIndexes))]
    public void Verify_ShouldLogTheTenantAndCodeOnly_WhenTokenIsRefused(int index)
    {
        var vector = Vector["refused"]!.AsArray()[index]!;
        var token = vector["token"]!.GetValue<string>();
        var logger = new CapturingLogger();

        Sut(logger).Verify(token, TenantId, InGrace, Now);

        logger.Lines.Should().ContainSingle();
        var line = logger.Lines[0];
        line.Should().Contain(TenantId).And.Contain(vector["identityTokenRefusal"]!.GetValue<string>());
        foreach (var segment in token.Split('.').Where(s => s.Length > 0))
            line.Should().NotContain(segment);
        line.Should().NotContain("cust-48151623").And.NotContain("ana.perez@example.com").And.NotContain("Ana");
    }

    [Fact]
    public void Verify_ShouldLogNothing_WhenTokenIsAccepted()
    {
        var logger = new CapturingLogger();

        Sut(logger).Verify(Vector["accepted"]![0]!["token"]!.GetValue<string>(), TenantId, InGrace, Now).RefusalCode.Should().BeNull();

        logger.Lines.Should().BeEmpty();
    }
}
