namespace FastBlur.Core.Detect;

/// <summary>One word from <c>tesseract stdout tsv</c> output.</summary>
public sealed class OcrWord
{
    public string Text { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Conf { get; set; }
}
