using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public class SenderVerifierTests
{
    private static readonly SenderVerifier Verifier = new(["mail.example.com"]);

    private static SenderVerdict Verdict(string[] headers, string from = "anna@example.org", SenderVerifier? verifier = null) =>
        (verifier ?? Verifier).Check(headers, from).Verdict;

    // ---- Headers as real servers write them ----

    [Theory]
    [InlineData("mail.example.com; dkim=pass header.d=example.org header.s=sel; spf=pass smtp.mailfrom=example.org; dmarc=pass header.from=example.org")]
    [InlineData("mail.example.com; dkim=pass (1024-bit key; unprotected) header.d=example.org header.i=@example.org header.b=abc123 header.a=rsa-sha256; dmarc=pass (p=none dis=none) header.from=example.org")]
    [InlineData("mail.example.com; dkim=pass header.d=example.org header.s=20230601 header.b=xyz; spf=pass (mail.example.com: domain of anna@example.org designates 192.0.2.1 as permitted sender) smtp.mailfrom=anna@example.org; dmarc=pass (policy=none) header.from=example.org")]
    [InlineData("MAIL.EXAMPLE.COM; DKIM=PASS header.d=example.org; DMARC=PASS header.from=EXAMPLE.ORG")]
    [InlineData("mail.example.com 1; dmarc=pass header.from=example.org")]
    [InlineData("mail.example.com;\r\n\tdkim=pass header.d=example.org\r\n\theader.s=sel;\r\n\tdmarc=pass header.from=example.org")]
    [InlineData("mail.example.com; dkim=pass header.d=example.org; spf=fail smtp.mailfrom=example.org")] // as in DMARC, one aligned pass is enough
    [InlineData("mail.example.com; dmarc=pass reason=\"all; good\" header.from=example.org")]
    [InlineData("mail.example.com; dmarc=pass (comment; with; semicolons) header.from=example.org")]
    public void A_Pass_From_Our_Own_Server_On_Top_Is_Verified(string header)
    {
        Assert.Equal(SenderVerdict.Verified, Verdict([header]));
    }

    [Theory]
    [InlineData("mail.example.com; dkim=none; spf=fail smtp.mailfrom=example.org; dmarc=fail (p=reject) header.from=example.org")]
    [InlineData("mail.example.com; dmarc=fail (p=quarantine dis=none) header.from=example.org")]
    [InlineData("mail.example.com; dmarc=fail header.from=example.org")]
    [InlineData("mail.example.com; spf=fail smtp.mailfrom=example.org; dkim=none")]
    [InlineData("mail.example.com; dkim=fail (bad signature) header.d=example.org")]
    [InlineData("mail.example.com; spf=fail smtp.mailfrom=example.org; dkim=fail header.d=example.org")]
    public void A_Failed_Check_Is_Suspicious(string header)
    {
        Assert.Equal(SenderVerdict.Suspicious, Verdict([header]));
    }

    [Theory]
    [InlineData("mail.example.com; none")]
    [InlineData("mail.example.com; dkim=none; spf=none; dmarc=none")]
    [InlineData("mail.example.com; spf=softfail smtp.mailfrom=example.org")]
    [InlineData("mail.example.com; spf=neutral smtp.mailfrom=example.org")]
    [InlineData("mail.example.com; dkim=temperror; dmarc=temperror")]
    [InlineData("mail.example.com; dkim=permerror header.d=example.org")]
    [InlineData("mail.example.com; dmarc=fail (p=none dis=none) header.from=example.org")]
    [InlineData("mail.example.com; dmarc=fail (policy=none) header.from=example.org")]
    public void Inconclusive_Results_Say_Nothing(string header)
    {
        Assert.Equal(SenderVerdict.Unknown, Verdict([header]));
    }

    [Fact]
    public void No_Header_Says_Nothing()
    {
        Assert.Equal(SenderVerdict.Unknown, Verdict([]));
    }

    // ---- Whom to believe ----

    [Fact]
    public void A_Pass_Written_By_Somebody_Else_Is_Not_Believed()
    {
        Assert.Equal(SenderVerdict.Unknown, Verdict(["evil.example; dmarc=pass header.from=example.org"]));
    }

    [Fact]
    public void A_Forged_Pass_Under_The_Real_Header_Is_Not_Believed_Because_Only_The_Top_One_Counts()
    {
        var verdict = Verdict([
            "mail.example.com; dmarc=none header.from=example.org", // the real one, put on top by our server
            "mail.example.com; dmarc=pass header.from=example.org", // the sender's own, further down
        ]);

        Assert.Equal(SenderVerdict.Unknown, verdict);
    }

    [Fact]
    public void A_Forged_Header_On_Top_Written_By_Somebody_Else_Cannot_Make_A_Message_Verified_Even_If_Ours_Is_Below()
    {
        var verdict = Verdict([
            "attacker.example; dmarc=pass header.from=example.org",
            "mail.example.com; dmarc=pass header.from=example.org",
        ]);

        Assert.Equal(SenderVerdict.Unknown, verdict);
    }

    [Fact]
    public void A_Failure_Is_Believed_From_Any_Header_Because_A_Forged_Failure_Only_Hurts_The_Message_That_Has_It()
    {
        var verdict = Verdict([
            "mail.example.com; dmarc=pass header.from=example.org",
            "relay.example.net; dmarc=fail (p=reject) header.from=example.org",
        ]);

        Assert.Equal(SenderVerdict.Suspicious, verdict);
    }

    [Fact]
    public void A_Failure_Needs_No_Trusted_Name()
    {
        Assert.Equal(SenderVerdict.Suspicious, Verdict(["whoever.example; dmarc=fail (p=reject) header.from=example.org"]));
    }

    [Fact]
    public void Several_Trusted_Names_Are_All_Accepted()
    {
        var verifier = new SenderVerifier(["mx1.example.com", " MX2.example.com "]);

        Assert.Equal(SenderVerdict.Verified, Verdict(["mx2.example.com; dmarc=pass header.from=example.org"], verifier: verifier));
        Assert.Equal(SenderVerdict.Unknown, Verdict(["mx3.example.com; dmarc=pass header.from=example.org"], verifier: verifier));
    }

    [Fact]
    public void Nobody_Trusted_Means_Nobody_Is_Verified()
    {
        var verifier = new SenderVerifier([]);

        Assert.Equal(SenderVerdict.Unknown, Verdict(["mail.example.com; dmarc=pass header.from=example.org"], verifier: verifier));
        Assert.Equal(SenderVerdict.Suspicious, Verdict(["mail.example.com; dmarc=fail (p=reject) header.from=example.org"], verifier: verifier));
    }

    // ---- Which domain the result is about ----

    [Fact]
    public void A_Pass_For_Another_Domain_Than_The_From_Line_Is_Not_A_Pass_For_This_Sender()
    {
        Assert.Equal(SenderVerdict.Unknown, Verdict(["mail.example.com; dmarc=pass header.from=other.example"]));
        Assert.Equal(SenderVerdict.Unknown, Verdict(["mail.example.com; dmarc=pass header.from=example.org"], from: "ceo@lookalike.example"));
    }

    [Theory]
    [InlineData("mail.example.com; dkim=pass header.d=example.org", "anna@example.org", SenderVerdict.Verified)]
    [InlineData("mail.example.com; dkim=pass header.d=mail.example.org", "anna@example.org", SenderVerdict.Verified)]
    [InlineData("mail.example.com; dkim=pass header.d=example.org", "anna@news.example.org", SenderVerdict.Verified)]
    [InlineData("mail.example.com; dkim=pass header.d=other.example", "anna@example.org", SenderVerdict.Unknown)]
    [InlineData("mail.example.com; dkim=pass header.d=notexample.org", "anna@example.org", SenderVerdict.Unknown)]
    [InlineData("mail.example.com; spf=pass smtp.mailfrom=bounce@mail.example.org", "anna@example.org", SenderVerdict.Verified)]
    [InlineData("mail.example.com; spf=pass smtp.mailfrom=bounce@mailer.example", "anna@example.org", SenderVerdict.Unknown)]
    [InlineData("mail.example.com; spf=pass", "anna@example.org", SenderVerdict.Unknown)]
    [InlineData("mail.example.com; dkim=pass", "anna@example.org", SenderVerdict.Unknown)]
    public void Without_A_Dmarc_Result_A_Signature_Or_Envelope_Must_Belong_To_The_Domain_Of_The_From_Line(string header, string from, SenderVerdict expected)
    {
        Assert.Equal(expected, Verdict([header], from));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-domain")]
    public void A_Message_Without_A_Usable_From_Domain_Is_Never_Verified(string? from)
    {
        Assert.Equal(SenderVerdict.Unknown, Verifier.Check(["mail.example.com; dmarc=pass header.from=example.org"], from).Verdict);
    }

    [Fact]
    public void The_Domain_Of_The_Verdict_Is_The_From_Domain_In_Lower_Case()
    {
        var check = Verifier.Check(["mail.example.com; dmarc=pass header.from=example.org"], "Anna@Example.ORG");

        Assert.Equal("example.org", check.Domain);
        Assert.Equal(SenderVerdict.Verified, check.Verdict);
    }

    // ---- Odd and hostile input ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData(";;;")]
    [InlineData("dkim=pass header.d=example.org")]
    [InlineData("(unterminated comment; dkim=pass")]
    [InlineData("mail.example.com; \"unterminated quote; dkim=pass")]
    [InlineData("mail.example.com; =pass")]
    [InlineData("mail.example.com; dkim=")]
    [InlineData("mail.example.com; dkim==pass header.d=example.org")]
    [InlineData("mail.example.com; ((((((((((((((((((((dkim=pass")]
    [InlineData("\u0000\u0001\u0002")]
    public void Broken_Headers_Say_Nothing_And_Never_Throw(string header)
    {
        Assert.NotEqual(SenderVerdict.Verified, Verdict([header]));
    }

    [Fact]
    public void A_Huge_Header_Is_Ignored()
    {
        var huge = "mail.example.com; " + string.Concat(Enumerable.Repeat("dmarc=pass header.from=example.org; ", 5000));

        Assert.Equal(SenderVerdict.Unknown, Verdict([huge]));
    }

    [Fact]
    public void Hostile_Input_Is_Handled_In_Linear_Time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var inputs = new[]
        {
            "mail.example.com; " + new string('(', 10_000) + "dmarc=pass",
            "mail.example.com; " + new string('"', 10_000),
            "mail.example.com; " + string.Concat(Enumerable.Repeat("\\", 10_000)),
            "mail.example.com; " + string.Concat(Enumerable.Repeat("a=b ", 4_000)),
            string.Concat(Enumerable.Repeat(";", 10_000)),
        };

        foreach (var input in inputs)
        {
            _ = Verifier.Check([input], "anna@example.org");
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void Headers_Are_Read_In_The_Order_Given_The_First_One_Is_The_Top()
    {
        var top = Verifier.Check(["mail.example.com; dmarc=pass header.from=example.org", "other.example; dmarc=none"], "anna@example.org");
        var swapped = Verifier.Check(["other.example; dmarc=none", "mail.example.com; dmarc=pass header.from=example.org"], "anna@example.org");

        Assert.Equal(SenderVerdict.Verified, top.Verdict);
        Assert.Equal(SenderVerdict.Unknown, swapped.Verdict);
    }
}
