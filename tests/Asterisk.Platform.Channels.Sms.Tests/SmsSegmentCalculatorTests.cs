using Asterisk.Platform.Channels.Sms;

namespace Asterisk.Platform.Channels.Sms.Tests;

public class SmsSegmentCalculatorTests
{
    // ---- GSM-7 segment counting ----

    [Fact]
    public void CalculateSegments_ShouldReturn1_WhenGsm7MessageIs160Chars()
    {
        var body = new string('A', 160);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(1);
    }

    [Fact]
    public void CalculateSegments_ShouldReturn2_WhenGsm7MessageIs161Chars()
    {
        var body = new string('A', 161);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(2);
    }

    [Fact]
    public void CalculateSegments_ShouldReturn2_WhenGsm7MessageIs306Chars()
    {
        // 2 * 153 = 306 — exactly 2 concat segments
        var body = new string('A', 306);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(2);
    }

    [Fact]
    public void CalculateSegments_ShouldReturn3_WhenGsm7MessageIs307Chars()
    {
        var body = new string('A', 307);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(3);
    }

    // ---- UCS-2 segment counting ----

    [Fact]
    public void CalculateSegments_ShouldReturn1_WhenUcs2MessageIs70Chars()
    {
        // Use a BMP emoji (single UTF-16 code unit) so body.Length == char count.
        // U+263A WHITE SMILING FACE is non-GSM-7, forces UCS-2, and is 1 char wide.
        var body = "\u263a" + new string('A', 69); // 1 + 69 = 70 chars

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(1);
    }

    [Fact]
    public void CalculateSegments_ShouldReturn2_WhenUcs2MessageIs71Chars()
    {
        var body = "\u263a" + new string('A', 70); // 1 + 70 = 71 chars

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(2);
    }

    // ---- GSM-7 vs UCS-2 detection ----

    [Fact]
    public void RequiresUcs2_ShouldReturnFalse_WhenMessageIsAllGsm7()
    {
        var body = "Hello, World! 123";

        var result = SmsSegmentCalculator.RequiresUcs2(body);

        result.Should().BeFalse();
    }

    [Fact]
    public void RequiresUcs2_ShouldReturnTrue_WhenMessageContainsEmoji()
    {
        var body = "Hello 😊";

        var result = SmsSegmentCalculator.RequiresUcs2(body);

        result.Should().BeTrue();
    }

    [Fact]
    public void RequiresUcs2_ShouldReturnTrue_WhenMessageContainsNonGsm7SpecialChars()
    {
        // U+00C1 LATIN CAPITAL LETTER A WITH ACUTE is outside GSM-7 basic and extension sets
        var body = "\u00c1ccentuated";

        var result = SmsSegmentCalculator.RequiresUcs2(body);

        result.Should().BeTrue();
    }

    [Fact]
    public void RequiresUcs2_ShouldReturnFalse_WhenMessageContainsGsm7ExtendedChars()
    {
        // Euro sign is in GSM-7 extension table
        var body = "Cost: 10\u20ac";

        var result = SmsSegmentCalculator.RequiresUcs2(body);

        result.Should().BeFalse();
    }

    [Fact]
    public void CalculateSegments_ShouldCountExtendedGsm7CharsAsTwo()
    {
        // 80 x '€' each counts as 2 encoded chars → 160 total → 1 segment
        var body = new string('\u20ac', 80);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(1);
    }

    [Fact]
    public void CalculateSegments_ShouldReturn2_When81ExtendedGsm7Chars()
    {
        // 81 x '€' → 162 encoded chars → 2 segments (153 per concat segment)
        var body = new string('\u20ac', 81);

        var result = SmsSegmentCalculator.CalculateSegments(body);

        result.Should().Be(2);
    }

    // ---- Truncation ----

    [Fact]
    public void Truncate_ShouldReturnBodyUnchanged_WhenAlreadyWithinMaxSegments()
    {
        var body = new string('A', 100);

        var result = SmsSegmentCalculator.Truncate(body, 1);

        result.Should().Be(body);
    }

    [Fact]
    public void Truncate_ShouldTruncateToMaxSegments_WhenBodyExceedsLimit()
    {
        // 400 chars GSM-7 → ceil(400/153)=3 segments; MaxSegments=2 → truncate to 2*153=306 chars
        var body = new string('A', 400);

        var result = SmsSegmentCalculator.Truncate(body, 2);

        SmsSegmentCalculator.CalculateSegments(result).Should().BeLessThanOrEqualTo(2);
        result.Length.Should().Be(306);
    }

    [Fact]
    public void Truncate_ShouldProduceEmptyString_WhenMaxSegmentsIsZero()
    {
        var body = "Hello";

        var result = SmsSegmentCalculator.Truncate(body, 0);

        result.Should().BeEmpty();
    }
}
