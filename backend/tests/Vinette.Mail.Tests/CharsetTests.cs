using System.Text;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>
/// Charsets, RFC 2047 / 2231 and transfer encodings in headers, bodies and file names, built byte by byte: what real mail
/// from real clients looks like, not only what the specifications allow.
/// </summary>
public class CharsetTests
{
    private const string Russian = "Привет, мир! Проверка кодировок: ёЁ.";

    private static readonly MimeMessageParser Parser = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Encoding Charset(string name) => Encoding.GetEncoding(name);

    /// <summary>A mail from header lines (ASCII) and a body in raw bytes; lines end with CRLF.</summary>
    private static Task<ParsedMessage> ParseAsync(string headers, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(headers.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n\r\n");
        return Parser.ParseAsync(new MemoryStream([.. head, .. body]), Token);
    }

    private static Task<ParsedMessage> ParseRawAsync(byte[] mail) => Parser.ParseAsync(new MemoryStream(mail), Token);

    public static TheoryData<string, string> CyrillicCharsets() => new()
    {
        { "koi8-r", Russian },
        { "windows-1251", "Привет, мир! Проверка кодировок: № ёЁ." }, // cp1251 has "№"
        { "utf-8", Russian },
        { "iso-8859-5", "Привет, мир! Проверка кодировок: ёЁ." }, // no "№" in ISO 8859-5
        { "ibm866", Russian },
        { "koi8-u", "Привіт, світе! Є ї ґ." },
        { "windows-1251", "Привіт, світе! Є ї ґ." },
    };

    [Theory]
    [MemberData(nameof(CyrillicCharsets))]
    public async Task A_Body_In_Any_Cyrillic_Charset_Is_Read_Whatever_The_Transfer_Encoding(string charset, string text)
    {
        var bytes = Charset(charset).GetBytes(text);
        var encodings = new (string Name, byte[] Body)[]
        {
            ("8bit", bytes),
            ("base64", Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks))),
            ("quoted-printable", Encoding.ASCII.GetBytes(string.Concat(bytes.Select(b => $"={b:X2}")))),
        };

        foreach (var (name, body) in encodings)
        {
            var parsed = await ParseAsync($"From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain; charset={charset}\nContent-Transfer-Encoding: {name}", body);

            Assert.Equal(text, parsed.Text?.Trim());
        }
    }

    [Theory]
    [InlineData("iso-8859-1", "Café déjà vu, Müller, naïve, ñ")]
    [InlineData("windows-1252", "“Smart quotes” — and the € sign")]
    [InlineData("iso-8859-15", "Euro € and œ")]
    [InlineData("windows-1250", "Zażółć gęślą jaźń")]
    [InlineData("iso-8859-2", "Příliš žluťoučký kůň")]
    [InlineData("iso-8859-7", "Ελληνικά")]
    [InlineData("windows-1253", "Ελληνικά")]
    [InlineData("iso-8859-9", "Işığı çok güzel")]
    [InlineData("windows-1255", "שלום עולם")]
    [InlineData("windows-1256", "مرحبا بالعالم")]
    [InlineData("shift_jis", "日本語のメール")]
    [InlineData("euc-jp", "日本語のメール")]
    [InlineData("iso-2022-jp", "日本語のメール")]
    [InlineData("gb2312", "中文邮件")]
    [InlineData("gbk", "中文邮件")]
    [InlineData("big5", "中文郵件")]
    [InlineData("euc-kr", "한국어 메일")]
    [InlineData("utf-16", "Привет, мир ✓")]
    public async Task A_Body_In_Other_Common_Charsets_Is_Read(string charset, string text)
    {
        var bytes = Charset(charset).GetBytes(text);

        var parsed = await ParseAsync($"From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain; charset={charset}\nContent-Transfer-Encoding: base64", Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes)));

        Assert.Equal(text, parsed.Text?.Trim().TrimStart('﻿'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("; charset=x-unknown-charset")]
    [InlineData("; charset=\"\"")]
    [InlineData("; charset=utf8")]
    [InlineData("; charset=UTF-8; format=flowed")]
    public async Task An_Unknown_Missing_Or_Oddly_Written_Charset_Still_Gives_The_Text(string parameter)
    {
        var parsed = await ParseAsync($"From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain{parameter}", Encoding.UTF8.GetBytes("Plain ASCII text, nothing special."));

        Assert.Equal("Plain ASCII text, nothing special.", parsed.Text?.Trim());
    }

    [Fact]
    public async Task A_Body_Without_A_Charset_That_Is_Really_Utf8_Is_Read_As_Utf8()
    {
        var parsed = await ParseAsync("From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain\nContent-Transfer-Encoding: 8bit", Encoding.UTF8.GetBytes(Russian));

        Assert.Equal(Russian, parsed.Text?.Trim());
    }

    [Fact]
    public async Task A_Body_Labelled_Ascii_But_Really_Utf8_Is_Not_Turned_Into_Garbage()
    {
        // Old and careless programs label everything us-ascii.
        var parsed = await ParseAsync("From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain; charset=us-ascii\nContent-Transfer-Encoding: 8bit", Encoding.UTF8.GetBytes(Russian));

        Assert.Equal(Russian, parsed.Text?.Trim());
    }

    [Theory]
    [InlineData("koi8-r", "B")]
    [InlineData("koi8-r", "Q")]
    [InlineData("windows-1251", "B")]
    [InlineData("windows-1251", "Q")]
    [InlineData("utf-8", "B")]
    [InlineData("utf-8", "Q")]
    [InlineData("iso-8859-5", "B")]
    public async Task A_Subject_In_An_Encoded_Word_Is_Decoded(string charset, string encoding)
    {
        const string Subject = "Отчёт за квартал ёЁ";
        var bytes = Charset(charset).GetBytes(Subject);
        var payload = encoding == "B" ? Convert.ToBase64String(bytes) : string.Concat(bytes.Select(b => $"={b:X2}"));

        var parsed = await ParseAsync($"From: a@example.org\nSubject: =?{charset}?{encoding}?{payload}?=\nContent-Type: text/plain", []);

        Assert.Equal(Subject, parsed.Subject);
    }

    [Fact]
    public async Task A_Subject_Split_Into_Several_Encoded_Words_Is_Joined_Even_Across_A_Character()
    {
        // The two halves of one UTF-8 character sit in two different words (RFC 2047 forbids it; clients do it).
        var bytes = Encoding.UTF8.GetBytes("Длинная тема");
        var cut = 3; // inside the second character
        var first = Convert.ToBase64String(bytes[..cut]);
        var second = Convert.ToBase64String(bytes[cut..]);

        var parsed = await ParseAsync($"From: a@example.org\nSubject: =?UTF-8?B?{first}?=\n =?UTF-8?B?{second}?=\nContent-Type: text/plain", []);

        Assert.Equal("Длинная тема", parsed.Subject);
    }

    [Fact]
    public async Task A_Subject_Written_In_Raw_Utf8_Without_Encoding_Is_Read()
    {
        var mail = Encoding.ASCII.GetBytes("From: a@example.org\r\nSubject: ").Concat(Encoding.UTF8.GetBytes(Russian)).Concat(Encoding.ASCII.GetBytes("\r\nContent-Type: text/plain\r\n\r\nx")).ToArray();

        var parsed = await ParseRawAsync(mail);

        Assert.Equal(Russian, parsed.Subject);
    }

    [Fact]
    public async Task A_Subject_Written_In_Raw_Koi8r_Without_Encoding_Is_Readable_Not_Question_Marks()
    {
        const string Subject = "Тест отчёта";
        var mail = Encoding.ASCII.GetBytes("From: a@example.org\r\nSubject: ").Concat(Charset("koi8-r").GetBytes(Subject)).Concat(Encoding.ASCII.GetBytes("\r\nContent-Type: text/plain\r\n\r\nx")).ToArray();

        var parsed = await ParseRawAsync(mail);

        // Nothing says which charset it is: the aim is a subject that is not lost and does not break anything.
        Assert.False(string.IsNullOrWhiteSpace(parsed.Subject));
        Assert.DoesNotContain("\0", parsed.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Name_In_The_From_Header_Is_Decoded_And_The_Address_Is_Kept()
    {
        var name = Convert.ToBase64String(Charset("windows-1251").GetBytes("Анна Смирнова"));

        var parsed = await ParseAsync($"From: =?windows-1251?B?{name}?= <anna@example.org>\nSubject: x\nContent-Type: text/plain", []);

        Assert.Equal("Анна Смирнова", parsed.From?.Name);
        Assert.Equal("anna@example.org", parsed.From?.Address);
    }

    [Theory]
    [InlineData("Content-Disposition: attachment; filename*=UTF-8''%D0%9E%D1%82%D1%87%D1%91%D1%82%20%E2%84%961.pdf", "Отчёт №1.pdf")]
    [InlineData("Content-Disposition: attachment; filename*=koi8-r''%F0%D2%C9%D7%C5%D4.txt", "Привет.txt")]
    [InlineData("Content-Disposition: attachment; filename*0*=UTF-8''%D0%9E%D1%82; filename*1*=%D1%87%D1%91%D1%82.pdf", "Отчёт.pdf")]
    [InlineData("Content-Disposition: attachment; filename*0=\"very-long-\"; filename*1=\"name.pdf\"", "very-long-name.pdf")]
    [InlineData("Content-Disposition: attachment; filename=\"=?UTF-8?B?0J7RgtGH0ZHRgi5wZGY=?=\"", "Отчёт.pdf")]
    [InlineData("Content-Disposition: attachment; filename=\"=?windows-1251?Q?=CE=F2=F7=B8=F2.pdf?=\"", "Отчёт.pdf")]
    [InlineData("Content-Type: application/pdf; name*=UTF-8''%D0%9E%D1%82%D1%87%D1%91%D1%82.pdf\nContent-Disposition: attachment", "Отчёт.pdf")]
    [InlineData("Content-Disposition: attachment; filename=plain.pdf", "plain.pdf")]
    public async Task A_File_Name_In_Any_Of_The_Ways_Mail_Programs_Write_It_Is_Decoded(string header, string expected)
    {
        var mail = $"From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: multipart/mixed; boundary=\"b\"\n\n--b\nContent-Type: text/plain\n\nhello\n--b\n{(header.StartsWith("Content-Type", StringComparison.Ordinal) ? string.Empty : "Content-Type: application/octet-stream\n")}{header}\nContent-Transfer-Encoding: base64\n\nAAEC\n--b--\n";
        var result = await ParseRawAsync(Encoding.ASCII.GetBytes(mail.Replace("\n", "\r\n", StringComparison.Ordinal)));

        Assert.Equal(expected, Assert.Single(result.Attachments).FileName);
    }

    [Theory]
    [InlineData("AAEC", 3)]
    [InlineData("AAEC\r\nAwQF", 6)] // line breaks inside the base64
    [InlineData("AAECAw", 3)] // padding missing: the incomplete last group is dropped
    [InlineData("AAEC!!!!AwQF", 6)] // junk between the groups is skipped
    public async Task Sloppy_Base64_Is_Decoded_As_Far_As_It_Goes(string base64, int size)
    {
        var mail = $"From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: application/octet-stream; name=\"a.bin\"\nContent-Transfer-Encoding: base64\n\n{base64}\n";

        var parsed = await ParseRawAsync(Encoding.ASCII.GetBytes(mail.Replace("\n", "\r\n", StringComparison.Ordinal)));

        Assert.Equal(size, Assert.Single(parsed.Attachments).Size);
    }

    [Fact]
    public async Task Quoted_Printable_With_Soft_Line_Breaks_And_Broken_Escapes_Is_Read()
    {
        var parsed = await ParseAsync(
            "From: a@example.org\nSubject: x\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8\nContent-Transfer-Encoding: quoted-printable",
            Encoding.ASCII.GetBytes("=D0=9F=D1=80=D0=B8=D0=B2=D0=B5=D1=82, soft=\r\nbreak, a bad escape =ZZ and 100% sure=\r\n"));

        Assert.StartsWith("Привет, softbreak", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("100% sure", parsed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Fixtures_With_Legacy_Charsets_Read_Without_Garbage()
    {
        foreach (var file in new[] { "03-koi8r-8bit.eml", "04-windows1251-qp.eml" })
        {
            await using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "fixtures", file));

            var parsed = await Parser.ParseAsync(stream, Token);

            Assert.DoesNotContain("�", parsed.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("�", parsed.Subject, StringComparison.Ordinal);
            Assert.Matches("[А-Яа-я]", parsed.Text);
            Assert.Matches("[А-Яа-я]", parsed.Subject);
        }
    }
}
