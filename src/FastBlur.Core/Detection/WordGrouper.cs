namespace FastBlur.Core.Detect;

/// <summary>Groups adjacent OCR words on the same line into one box (port of BlurCut's groupWords).</summary>
public static class WordGrouper
{
    /// <summary>
    /// Words within 40px horizontally and 12px vertically of the current box join it.
    /// <paramref name="scale"/> is the OCR upscale factor: gaps are measured in
    /// upscaled pixels, so the thresholds scale with it (1 = original size).
    /// </summary>
    public static List<OcrWord> GroupWords(IReadOnlyList<OcrWord> words, double scale)
    {
        if (words.Count == 0) return new();
        if (scale < 1) scale = 1;
        double gapX = 40 * scale, gapY = 12 * scale;

        var sorted = words.OrderBy(w => w.Y).ThenBy(w => w.X).ToList();
        var groups = new List<OcrWord>();
        var cur = Clone(sorted[0]);
        foreach (var w in sorted.Skip(1))
        {
            bool sameLine = Math.Abs(w.Y - cur.Y) <= gapY;
            bool near = w.X <= cur.X + cur.W + gapX;
            if (sameLine && near)
            {
                int right = Math.Max(cur.X + cur.W, w.X + w.W);
                int bottom = Math.Max(cur.Y + cur.H, w.Y + w.H);
                cur.X = Math.Min(cur.X, w.X);
                cur.Y = Math.Min(cur.Y, w.Y);
                cur.W = right - cur.X;
                cur.H = bottom - cur.Y;
                cur.Text += " " + w.Text;
                if (w.Conf < cur.Conf) cur.Conf = w.Conf;
            }
            else
            {
                groups.Add(cur);
                cur = Clone(w);
            }
        }
        groups.Add(cur);
        return groups;
    }

    private static OcrWord Clone(OcrWord w) => new()
    {
        Text = w.Text, X = w.X, Y = w.Y, W = w.W, H = w.H, Conf = w.Conf,
    };
}
