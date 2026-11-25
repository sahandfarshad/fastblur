using FastBlur.Core.Detect;
using FastBlur.Core.Models;

namespace FastBlur.Core.Tests;

public sealed class ClassifierTests
{
    private static Dictionary<string, bool> AllOn() => new()
    {
        ["url"] = true, ["email"] = true, ["password"] = true,
        ["apikey"] = true, ["crypto"] = true, ["suspicious"] = false,
    };

    private static string Classify(string text, Dictionary<string, bool>? patterns = null, List<string>? keywords = null) =>
        SensitiveTextClassifier.Classify(text, keywords ?? new List<string>(), patterns ?? AllOn());

    [Fact]
    public void Url_Https_IsDetected()
    {
        Assert.Equal("url", Classify("see https://example.com/page for details"));
        Assert.Equal("url", Classify("www.youtube.com/watch?v=abc"));
        Assert.Equal("url", Classify("visit mysite.dev/docs"));
    }

    [Fact]
    public void Email_IsDetected()
    {
        Assert.Equal("email", Classify("contact me at sahand@example.com"));
    }

    [Fact]
    public void Password_BareLabel_IsDetected()
    {
        // Login forms show "password" with no : or = after it.
        Assert.Equal("password", Classify("password"));
        Assert.Equal("password", Classify("enter your password here"));
        Assert.Equal("password", Classify("pwd"));
    }

    [Fact]
    public void Password_Value_NotMatched()
    {
        // "password123" is a value, not a label — the \b boundary keeps it unmatched.
        Assert.Equal("", Classify("password123"));
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghij1234567890")]
    [InlineData("sk-proj-abcdefghij1234567890")]
    [InlineData("api_key = abc123")]
    [InlineData("API-KEY: xyz")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void ApiKey_Patterns_AreDetected(string text)
    {
        Assert.Equal("apikey", Classify(text));
    }

    [Fact]
    public void ApiKey_KeyValue_IsDetected()
    {
        Assert.Equal("apikey", Classify("secret: hunter2"));
        Assert.Equal("apikey", Classify("token=abc"));
    }

    [Theory]
    [InlineData("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq")]
    [InlineData("1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa")]
    [InlineData("3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy")]
    [InlineData("0xde0B295669a9FD93d5F28D9Ec85E40f4cb697BAe")]
    public void Crypto_Addresses_AreDetected(string text)
    {
        Assert.Equal("crypto", Classify("send to " + text));
    }

    [Fact]
    public void PossibleKey_OptIn_OffByDefault()
    {
        const string token = "aB3dE5fG7hJ9kL2mN4pQ6";
        Assert.Equal("", Classify(token)); // suspicious OFF
        var on = AllOn();
        on["suspicious"] = true;
        Assert.Equal("possible-key", Classify(token, on));
    }

    [Fact]
    public void PossibleKey_NeedsLetterAndDigit()
    {
        var on = AllOn();
        on["suspicious"] = true;
        Assert.Equal("", Classify("abcdefghijklmnopqrstuvwx", on)); // letters only
        Assert.Equal("", Classify("1234567890123456789012345", on)); // digits only
        Assert.Equal("", Classify("short1", on)); // too short
    }

    [Fact]
    public void Keyword_IsDetected()
    {
        Assert.Equal("keyword", Classify("my secret project", keywords: new List<string> { "secret project" }));
        Assert.Equal("", Classify("nothing here", keywords: new List<string> { "secret project" }));
    }

    [Fact]
    public void Email_WinsOverUrl_WhenBothPresent()
    {
        // Check order: email is tested before url.
        Assert.Equal("email", Classify("mail me at a@b.com or see https://x.com"));
    }

    [Fact]
    public void PlainText_IsNotSensitive()
    {
        Assert.Equal("", Classify("hello world, this is a tutorial"));
        Assert.Equal("", Classify(""));
        Assert.Equal("", Classify("   "));
    }

    [Fact]
    public void FindSensitive_JoinedText_FirstThenPerWord()
    {
        var words = new[]
        {
            new OcrWord { Text = "password:", X = 0, Y = 0, W = 50, H = 12, Conf = 90 },
            new OcrWord { Text = "hunter2", X = 60, Y = 0, W = 40, H = 12, Conf = 88 },
        };
        var (label, text) = SensitiveTextClassifier.FindSensitive(words, new List<string>(), AllOn());
        Assert.Equal("password", label);
        Assert.Contains("password", text);
    }

    [Fact]
    public void DisabledPattern_IsIgnored()
    {
        var patterns = AllOn();
        patterns["url"] = false;
        Assert.Equal("", Classify("https://example.com", patterns));
    }
}
