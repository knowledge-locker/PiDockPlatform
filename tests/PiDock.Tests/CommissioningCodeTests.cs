using System.Text;
using PiDock.Web.Shared;

namespace PiDock.Tests;

public sealed class CommissioningCodeTests
{
    private static readonly string Secret = Totp.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1234567890);
    private static readonly long Step = Now.ToUnixTimeSeconds() / 30;

    [Fact]
    public void NormalCommissioningDoesNotDisplayCodesOrDebugTimestamp()
    {
        var output = new StringBuilder();
        var code = Totp.Code(Secret, Step);
        Assert.Equal(Step, Authentication.ReadCommissioningCode(Secret, "Code: ", -1, () => code, () => Now, text => output.Append(text)));
        Assert.Equal("Code: ", output.ToString());
    }

    [Fact]
    public void DebugUsesTheSamePostInputTimestampAsValidation()
    {
        var output = new StringBuilder();
        var clock = Now;
        var code = Totp.Code(Secret, Step + 2);
        var result = Authentication.ReadCommissioningCode(Secret, "Code: ", Step + 1,
            () => { clock = Now.AddSeconds(60); return code; }, () => clock, text => output.Append(text), debugCodes: true);
        Assert.Equal(Step + 2, result);
        var text = output.ToString();
        Assert.Contains($"submission UTC: {clock:O}", text);
        Assert.Contains($"Unix seconds: {clock.ToUnixTimeSeconds()}", text);
        Assert.Contains($"submitted code: \"{code}\"; characters: 6", text);
        foreach (long counter in new[] { Step + 1, Step + 2, Step + 3 })
            Assert.Contains($"expected code: {Totp.Code(Secret, counter)}", text);
        Assert.Contains($"second step requires counter {Step + 2}", text);
        Assert.DoesNotContain(Secret, text);
    }

    [Fact]
    public void AcceptsValidCodeOnThirdAttemptWithoutChangingSecret()
    {
        var codes = new Queue<string>(["invalid", "123", Totp.Code(Secret, Step)]);
        var output = new StringBuilder();
        var result = Authentication.ReadCommissioningCode(Secret, "Code: ", -1, codes.Dequeue, () => Now, text => output.Append(text));
        Assert.Equal(Step, result);
        Assert.Empty(codes);
        Assert.Contains("2 attempt(s) remaining", output.ToString());
        Assert.Contains("1 attempt(s) remaining", output.ToString());
        Assert.DoesNotContain(Secret, output.ToString());
    }

    [Fact]
    public void StopsAfterThreeFailuresWithoutReadingFourthCode()
    {
        var codes = new Queue<string>(["bad", "bad", "bad", Totp.Code(Secret, Step)]);
        Assert.Null(Authentication.ReadCommissioningCode(Secret, "Code: ", -1, codes.Dequeue, () => Now, _ => { }));
        Assert.Single(codes);
    }

    [Fact]
    public void SecondStepRejectsReplayAndStillAcceptsNextCode()
    {
        var codes = new Queue<string>([Totp.Code(Secret, Step), "bad", Totp.Code(Secret, Step + 1)]);
        Assert.Equal(Step + 1, Authentication.ReadCommissioningCode(Secret, "Next: ", Step, codes.Dequeue, () => Now.AddSeconds(30), _ => { }));
        Assert.Empty(codes);
    }

    [Fact]
    public void SecondStepCannotSkipAnInterval()
    {
        var reads = 0;
        Assert.Null(Authentication.ReadCommissioningCode(Secret, "Next: ", Step,
            () => { reads++; return Totp.Code(Secret, Step + 2); }, () => Now.AddSeconds(60), _ => { }));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void SamplesClockAfterInputAndAcceptsCodeThatJustRolledOver()
    {
        var clock = Now;
        var result = Authentication.ReadCommissioningCode(Secret, "Code: ", -1,
            () => { clock = Now.AddSeconds(60); return Totp.Code(Secret, Step + 1); }, () => clock, _ => { });
        Assert.Equal(Step + 1, result);
    }
}
