using System.Text.RegularExpressions;

namespace FastBlur.Core.Detect;

/// <summary>
/// Regex matching of OCR text to sensitivity labels.
/// Port of BlurCut's detect.go classifyText / FindSensitive, same patterns
/// and same check order — the order matters: real URLs/emails/keys must
/// never be relabelled as "possible-key".
/// </summary>
public static class SensitiveTextClassifier
{
    private static readonly Regex UrlRegex = new(
        @"(https?://\S+|www\.\S+|[a-z0-9-]+\.(com|net|io|dev|org|app|gg|me|co|tv)\S*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex EmailRegex = new(
        @"[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Bare label words too: login forms show "password" with no : or =
    // after it. \b keeps "password123" (a value, not a label) unmatched.
    private static readonly Regex PasswordRegex = new(
        @"\b(password|passwd|pwd)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // sk- allows - and _ inside the key: modern OpenAI keys are
    // "sk-proj-...", which the Go pattern (letters/digits only) missed.
    private static readonly Regex ApiKeyRegex = new(
        @"(api[_-]?key|apikey|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{10,}|-----BEGIN.*PRIVATE KEY-----)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex KeyValueRegex = new(
        @"(api[_-]?key|secret|token|bearer)\s*[:=]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Crypto addresses: Bitcoin bech32 (bc1…), legacy (1…/3…, base58),
    // Ethereum (0x + 40 hex). No dot+TLD, so they never look like URLs.
    private static readonly Regex CryptoRegex = new(
        @"(bc1[a-z0-9]{25,}|[13][a-km-zA-HJ-NP-Z1-9]{25,34}|0x[a-f0-9]{40})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Suspicious token: long run of letters/digits/_/- only (checked per
    // token in Classify, with a letter+digit requirement).
    private static readonly Regex SuspiciousRegex = new(
        @"^[A-Za-z0-9_-]{20,}$",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Returns the label for a word/phrase, or "" if not sensitive.</summary>
    public static string Classify(string text, IReadOnlyList<string> keywords, IReadOnlyDictionary<string, bool> patterns)
    {
        var t = text.Trim();
        if (t.Length == 0) return "";

        bool On(string key) => patterns.TryGetValue(key, out var v) && v;

        if (On("email") && EmailRegex.IsMatch(t)) return "email";
        if (On("apikey") && (ApiKeyRegex.IsMatch(t) || KeyValueRegex.IsMatch(t))) return "apikey";
        if (On("password") && PasswordRegex.IsMatch(t)) return "password";
        if (On("crypto") && CryptoRegex.IsMatch(t)) return "crypto";
        if (On("url") && UrlRegex.IsMatch(t)) return "url";

        var lower = t.ToLowerInvariant();
        foreach (var kw in keywords)
        {
            if (kw.Length > 0 && lower.Contains(kw.ToLowerInvariant()))
                return "keyword";
        }

        // Last, so real URLs/emails/keys are never relabelled: a long
        // token (letters/digits/_/-, at least one letter AND one digit).
        if (On("suspicious"))
        {
            foreach (var tok in t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!SuspiciousRegex.IsMatch(tok)) continue;
                bool hasLetter = false, hasDigit = false;
                foreach (var c in tok)
                {
                    if (char.IsLetter(c)) hasLetter = true;
                    if (char.IsDigit(c)) hasDigit = true;
                }
                if (hasLetter && hasDigit) return "possible-key";
            }
        }
        return "";
    }

    /// <summary>
    /// Scans a list of OCR words and returns (label, matchedText) for the
    /// first sensitive hit in the joined text, else per word, else ("", "").
    /// </summary>
    public static (string Label, string Text) FindSensitive(
        IReadOnlyList<OcrWord> words, IReadOnlyList<string> keywords, IReadOnlyDictionary<string, bool> patterns)
    {
        var joined = string.Join(" ", words.Select(w => w.Text));
        var label = Classify(joined, keywords, patterns);
        if (label.Length > 0) return (label, joined);

        foreach (var w in words)
        {
            label = Classify(w.Text, keywords, patterns);
            if (label.Length > 0) return (label, w.Text);
        }
        return ("", "");
    }
}
