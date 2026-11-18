using System.Globalization;
using System.Text;
using CoreIdml.Dom;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Graphics.Colors;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace CoreIdml.Pdf.Import;

/// <summary>
/// Rebuilds an editable IDML document from a PDF with PdfPig: each page becomes a page; text is grouped into
/// lines, blocks (one text frame each) and paragraphs with paragraph and character styles inferred from font,
/// size and colour; images are extracted to files and placed in frames; rectangles and straight lines become shapes.
/// </summary>
internal sealed class PdfImporter
{
    private readonly PdfImportOptions options;
    private readonly List<string> warnings = [];
    private readonly List<string> images = [];
    private readonly Dictionary<string, Swatch> swatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParagraphStyle> paragraphStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterStyle> characterStyles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportedFont> fonts = new(StringComparer.Ordinal);
    private int textFrames;
    private int imageFrames;
    private int shapes;

    public PdfImporter(PdfImportOptions options)
    {
        this.options = options;
    }

    public PdfImportResult Import(byte[] data)
    {
        using var pdf = PdfSource.Open(data, "The PDF");
        var numbers = options.Pages is { Count: > 0 } wanted
            ? wanted.Where(n => n >= 1 && n <= pdf.NumberOfPages).ToList()
            : Enumerable.Range(1, pdf.NumberOfPages).ToList();
        foreach (var missing in options.Pages?.Where(n => n < 1 || n > pdf.NumberOfPages) ?? [])
        {
            warnings.Add($"Page {missing} does not exist: the PDF has {pdf.NumberOfPages} page(s), numbered from 1.");
        }
        if (numbers.Count == 0)
        {
            throw new IdmlException(IdmlErrorKind.InvalidFormat, "The PDF has no pages to import.");
        }
        var first = pdf.GetPage(numbers[0]);
        var document = IdmlDocument.Create(new DocumentOptions
        {
            PageSize = new Size(first.Width, first.Height),
            PageCount = numbers.Count,
            Margins = Spacing.All(0),
        });
        for (var i = 0; i < numbers.Count; i++)
        {
            var page = i == 0 ? first : pdf.GetPage(numbers[i]);
            if (Math.Abs(page.Width - first.Width) > 0.5 || Math.Abs(page.Height - first.Height) > 0.5)
            {
                warnings.Add($"Page {numbers[i]} is {page.Width:0.#}×{page.Height:0.#} pt; the IDML uses the first page's size for every page.");
            }
            options.CancellationToken.ThrowIfCancellationRequested();
            ImportPage(page, document, document.Pages[i]);
            if (options.Progress is { } progress && !progress(i + 1, numbers.Count))
            {
                throw new OperationCanceledException("The PDF import was cancelled.");
            }
        }
        if (options.Fonts is { } fontOptions)
        {
            var library = new Fonts.FontLibrary(fontOptions);
            foreach (var font in fonts.Values)
            {
                font.Available = library.IsAvailable(font.Family, font.Style);
            }
        }
        return new PdfImportResult
        {
            Fonts = fonts.Values.OrderBy(f => f.Family, StringComparer.Ordinal).ThenBy(f => f.Style, StringComparer.Ordinal).ToList(),
            Document = document,
            TextFrames = textFrames,
            ImageFrames = imageFrames,
            Shapes = shapes,
            Images = images,
            Warnings = warnings,
        };
    }

    private void ImportPage(UglyToad.PdfPig.Content.Page source, IdmlDocument document, Dom.Page target)
    {
        var height = source.Height;
        var (rules, clips) = CollectGeometry(source, height);
        if (options.ImportShapes)
        {
            ImportShapes(source, document, target, height);
        }
        ImportImages(source, target, height, clips);

        // Text is grouped per orientation, in the text's own axes (u along the baseline, v downwards); rotated
        // blocks become rotated frames, like InDesign's vertical side labels.
        var byOrientation = new Dictionary<TextOrientation, List<TextGlyph>>();
        var skipped = 0;
        foreach (var letter in source.Letters)
        {
            if (letter.TextOrientation == TextOrientation.Other)
            {
                skipped++;
                continue;
            }
            var (family, style) = FontNames(letter);
            var pdfName = letter.FontDetails?.Name ?? letter.FontName ?? string.Empty;
            var fontKey = family + "|" + style;
            if (!fonts.TryGetValue(fontKey, out var imported))
            {
                imported = new ImportedFont(family, style, pdfName);
                fonts[fontKey] = imported;
            }
            if (!imported.Pages.Contains(source.Number))
            {
                imported.Pages.Add(source.Number);
            }
            var (u, v) = ToTextAxes(letter.TextOrientation, letter.StartBaseLine.X, height - letter.StartBaseLine.Y);
            var dx = letter.EndBaseLine.X - letter.StartBaseLine.X;
            var dy = letter.EndBaseLine.Y - letter.StartBaseLine.Y;
            if (!byOrientation.TryGetValue(letter.TextOrientation, out var list))
            {
                list = [];
                byOrientation[letter.TextOrientation] = list;
            }
            list.Add(new TextGlyph(
                letter.Value,
                u,
                v,
                Math.Sqrt((dx * dx) + (dy * dy)),
                Math.Round(letter.PointSize, 2),
                family,
                style,
                ToColor(letter.Color)));
        }
        if (skipped > 0)
        {
            warnings.Add($"Page {source.Number}: {skipped} characters at arbitrary angles were skipped.");
        }
        foreach (var (orientation, glyphs) in byOrientation.OrderBy(pair => pair.Key))
        {
            // Rules only apply to horizontal text.
            var orientationRules = orientation == TextOrientation.Horizontal ? rules : [];
            var lines = TextGrouping.BuildLines(glyphs, orientationRules);
            foreach (var block in TextGrouping.BuildBlocks(lines, options.BlockGapFactor, orientationRules))
            {
                AddTextFrame(block, document, target, TextAxesToPage(orientation));
            }
        }
    }

    /// <summary>Page coordinates (y down) to the text's axes: u along the baseline, v perpendicular, downwards.</summary>
    private static (double U, double V) ToTextAxes(TextOrientation orientation, double x, double y) => orientation switch
    {
        TextOrientation.Rotate270 => (-y, x),
        TextOrientation.Rotate90 => (y, -x),
        TextOrientation.Rotate180 => (-x, -y),
        _ => (x, y),
    };

    /// <summary>The inverse of <see cref="ToTextAxes"/> as an IDML transform (text axes to page).</summary>
    private static CoreIdml.Matrix TextAxesToPage(TextOrientation orientation) => orientation switch
    {
        TextOrientation.Rotate270 => new CoreIdml.Matrix(0, -1, 1, 0, 0, 0),
        TextOrientation.Rotate90 => new CoreIdml.Matrix(0, 1, -1, 0, 0, 0),
        TextOrientation.Rotate180 => new CoreIdml.Matrix(-1, 0, 0, -1, 0, 0),
        _ => CoreIdml.Matrix.Identity,
    };

    // ---- text ----------------------------------------------------------------------------------------

    private void AddTextFrame(TextBlock block, IdmlDocument document, Dom.Page page, CoreIdml.Matrix toPage)
    {
        var all = block.Lines.SelectMany(l => l.Glyphs).ToList();
        var size = TextGrouping.Dominant(all, g => g.Size);
        var family = TextGrouping.Dominant(all, g => g.Family);
        var style = TextGrouping.Dominant(all, g => g.Style);
        var color = TextGrouping.Dominant(all, g => ColorKey(g.Color));
        var colorValue = all.First(g => ColorKey(g.Color) == color).Color;
        var leading = Math.Round(block.Leading, 2);
        var alignment = block.Alignment;
        var paragraphStyle = ParagraphStyleFor(document, family, style, size, leading, colorValue, alignment);

        var firstBaseline = block.Lines[0].Baseline;
        var top = firstBaseline - block.Lines[0].Size;
        var bottom = block.Lines[^1].Baseline + (block.Lines[^1].Size * 0.35) + leading + options.FramePadding;
        // Justified text must keep its measure; ragged text gets some room in case the reader's fonts are wider.
        var padding = alignment == "LeftJustified" ? 0 : 2 * options.FramePadding;
        if (block.Lines.Count == 1)
        {
            // A single line must not wrap if the reader's font runs a little wider.
            padding = Math.Max(padding, (block.Right - block.Left) * 0.1);
        }
        var bounds = new Rect(block.Left, top, (block.Right - block.Left) + padding, bottom - top);
        var frame = page.AddTextFrame(bounds);
        if (toPage != CoreIdml.Matrix.Identity)
        {
            // The frame was laid out in the text's axes; turn it onto the page: inner → text axes → page → spread.
            var origin = page.SpreadBounds;
            frame.Transform = CoreIdml.Matrix.Translation(bounds.Center.X, bounds.Center.Y).Then(toPage).Then(CoreIdml.Matrix.Translation(origin.X, origin.Y));
        }
        // The first baseline sits exactly where the PDF had it, whatever font metrics the reader has.
        var preference = frame.Element.Element("TextFramePreference")!;
        preference.SetAttributeValue("FirstBaselineOffset", "FixedHeight");
        preference.SetAttributeValue("MinimumFirstBaselineOffset", (firstBaseline - top).ToString("R", CultureInfo.InvariantCulture));
        textFrames++;

        for (var p = 0; p < block.ParagraphStarts.Count; p++)
        {
            var start = block.ParagraphStarts[p];
            var end = p + 1 < block.ParagraphStarts.Count ? block.ParagraphStarts[p + 1] : block.Lines.Count;
            var paragraph = frame.Story.AddParagraph(paragraphStyle);
            var lineSize = block.Lines[start].Size;
            if (Math.Abs(lineSize - size) > 0.5)
            {
                paragraph.Format(new ParagraphFormat { PointSize = lineSize, Leading = Math.Round(lineSize * leading / size, 2) });
            }
            for (var l = start; l < end; l++)
            {
                if (l > start)
                {
                    paragraph.AddText(options.PreserveLineBreaks ? "\u2028" : JoinSeparator(block.Lines[l - 1]));
                }
                foreach (var (text, sample) in Runs(block.Lines[l]))
                {
                    var characterStyle = sample.Family == family && sample.Style == style && ColorKey(sample.Color) == color
                        ? null
                        : CharacterStyleFor(document, sample, family, color);
                    var lineParagraphSize = Math.Abs(lineSize - size) > 0.5 ? lineSize : size;
                    paragraph.AddText(text, characterStyle, Math.Abs(sample.Size - lineParagraphSize) > 0.1 ? new CharacterFormat { PointSize = sample.Size } : null);
                }
            }
        }
    }

    /// <summary>Joining two lines of a flowing paragraph: a space, unless the line ends with a hyphen.</summary>
    private static string JoinSeparator(TextLine previous) => previous.Text.EndsWith('-') ? string.Empty : " ";

    /// <summary>Splits a line into runs of identical font, style and colour.</summary>
    private static IEnumerable<(string Text, TextGlyph Sample)> Runs(TextLine line)
    {
        var glyphs = line.Glyphs;
        var start = 0;
        for (var i = 1; i <= glyphs.Count; i++)
        {
            if (i == glyphs.Count || !SameFormat(glyphs[i], glyphs[start]))
            {
                var segment = glyphs.Skip(start).Take(i - start).ToList();
                var text = TextGrouping.JoinGlyphs(segment);
                if (start > 0)
                {
                    var previous = glyphs[start - 1];
                    var gap = glyphs[start].X - (previous.X + previous.Width);
                    if (gap > Math.Min(previous.Size, glyphs[start].Size) * 0.15 && !previous.Text.EndsWith(' ') && !text.StartsWith(' '))
                    {
                        text = " " + text;
                    }
                }
                yield return (text, glyphs[start]);
                start = i;
            }
        }
    }

    private static bool SameFormat(TextGlyph a, TextGlyph b) =>
        a.Family == b.Family && a.Style == b.Style && ColorKey(a.Color) == ColorKey(b.Color) && Math.Abs(a.Size - b.Size) < 0.1;

    private ParagraphStyle ParagraphStyleFor(IdmlDocument document, string family, string style, double size, double leading, ResolvedColor? color, string alignment)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{family}|{style}|{size}|{leading}|{ColorKey(color)}|{alignment}");
        if (paragraphStyles.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var baseName = string.Create(CultureInfo.InvariantCulture, $"{family} {(style == "Regular" ? string.Empty : style + " ")}{size:0.##} pt");
        var name = baseName;
        for (var n = 2; paragraphStyles.Values.Any(s => s.Name == name); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName} ({n})");
        }
        var created = document.Styles.AddParagraphStyle(name, new ParagraphFormat
        {
            FontFamily = family,
            FontStyle = style,
            PointSize = size,
            Leading = leading,
            FillColor = SwatchFor(document, color),
            Justification = alignment switch
            {
                "CenterAlign" => Justification.Center,
                "RightAlign" => Justification.Right,
                "LeftJustified" => Justification.LeftJustified,
                _ => Justification.Left,
            },
            Hyphenation = false,
        });
        paragraphStyles[key] = created;
        return created;
    }

    private CharacterStyle CharacterStyleFor(IdmlDocument document, TextGlyph sample, string baseFamily, string baseColor)
    {
        var key = sample.Family + "|" + sample.Style + "|" + ColorKey(sample.Color);
        if (characterStyles.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var parts = new List<string>();
        if (sample.Family != baseFamily)
        {
            parts.Add(sample.Family);
        }
        parts.Add(sample.Style);
        if (ColorKey(sample.Color) != baseColor)
        {
            parts.Add(SwatchFor(document, sample.Color).Name);
        }
        var name = string.Join(' ', parts);
        for (var n = 2; characterStyles.Values.Any(s => s.Name == name); n++)
        {
            name = string.Join(' ', parts) + string.Create(CultureInfo.InvariantCulture, $" ({n})");
        }
        var created = document.Styles.AddCharacterStyle(name, new CharacterFormat
        {
            FontFamily = sample.Family,
            FontStyle = sample.Style,
            FillColor = SwatchFor(document, sample.Color),
        });
        characterStyles[key] = created;
        return created;
    }

    /// <summary>Family and style from the PDF font: subset prefix removed, PostScript name split ("TimesNewRomanPS-BoldMT").</summary>
    internal static (string Family, string Style) FontNames(Letter letter)
    {
        var details = letter.FontDetails;
        var name = details?.Name ?? letter.FontName ?? "Minion Pro";
        return SplitFontName(name, details?.IsBold ?? false, details?.IsItalic ?? false);
    }

    internal static (string Family, string Style) SplitFontName(string name, bool bold, bool italic)
    {
        var plus = name.IndexOf('+', StringComparison.Ordinal);
        if (plus == 6)
        {
            name = name[(plus + 1)..];
        }
        var separator = name.IndexOfAny(['-', ',']);
        var familyPart = separator > 0 ? name[..separator] : name;
        var stylePart = separator > 0 ? name[(separator + 1)..] : string.Empty;
        foreach (var suffix in (string[])["PSMT", "PS", "MT"])
        {
            if (familyPart.EndsWith(suffix, StringComparison.Ordinal) && familyPart.Length > suffix.Length + 2)
            {
                familyPart = familyPart[..^suffix.Length];
            }
            if (stylePart.EndsWith(suffix, StringComparison.Ordinal) && stylePart.Length > suffix.Length)
            {
                stylePart = stylePart[..^suffix.Length];
            }
        }
        var family = SplitCamel(familyPart);
        // PostScript styles keep their compound weights ("SemiBold", "ExtraLight"); only the slope is a separate word.
        var style = stylePart.Replace("Italic", " Italic", StringComparison.Ordinal).Replace("Oblique", " Italic", StringComparison.Ordinal).Trim();
        if (style.Length == 0 || style is "Roman" or "Book" or "Normal" or "Regular")
        {
            style = bold && italic ? "Bold Italic" : bold ? "Bold" : italic ? "Italic" : "Regular";
        }
        style = style.Replace("Oblique", "Italic", StringComparison.Ordinal);
        return (family.Length == 0 ? "Minion Pro" : family, style);
    }

    private static string SplitCamel(string value)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]) && char.IsUpper(value[i - 1]))))
            {
                builder.Append(' ');
            }
            builder.Append(c == '_' ? ' ' : c);
        }
        return builder.ToString().Trim();
    }

    // ---- colours -------------------------------------------------------------------------------------

    internal static ResolvedColor? ToColor(IColor? color) => color switch
    {
        null => null,
        CMYKColor cmyk => new ResolvedColor(ColorSpaceKind.Cmyk, [cmyk.C, cmyk.M, cmyk.Y, cmyk.K]),
        GrayColor gray => new ResolvedColor(ColorSpaceKind.Gray, [gray.Gray]),
        RGBColor rgb => new ResolvedColor(ColorSpaceKind.Rgb, [rgb.R, rgb.G, rgb.B]),
        PatternColor => null, // patterns and shadings have no single colour
        _ => FromRgb(color.ToRGBValues()),
    };

    private static ResolvedColor FromRgb((double R, double G, double B) rgb) => new(ColorSpaceKind.Rgb, [rgb.R, rgb.G, rgb.B]);

    private static string ColorKey(ResolvedColor? color) =>
        color is null ? "none" : color.Space + ":" + string.Join(',', color.Components.Select(c => Math.Round(c, 3).ToString(CultureInfo.InvariantCulture)));

    /// <summary>A swatch for a colour: Black, Paper, or a process colour named like InDesign names them.</summary>
    private Swatch SwatchFor(IdmlDocument document, ResolvedColor? color)
    {
        if (color is null)
        {
            return Swatch.Black;
        }
        var key = ColorKey(color);
        if (swatches.TryGetValue(key, out var existing))
        {
            return existing;
        }
        Swatch swatch;
        var c = color.Components;
        switch (color.Space)
        {
            case ColorSpaceKind.Cmyk when c[0] < 0.005 && c[1] < 0.005 && c[2] < 0.005 && c[3] > 0.995:
                swatch = Swatch.Black;
                break;
            case ColorSpaceKind.Cmyk when c.All(v => v < 0.005):
                swatch = Swatch.Paper;
                break;
            case ColorSpaceKind.Cmyk:
                var (cy, ma, ye, bk) = (Percent(c[0]), Percent(c[1]), Percent(c[2]), Percent(c[3]));
                swatch = document.Swatches.AddCmyk(string.Create(CultureInfo.InvariantCulture, $"C={cy} M={ma} Y={ye} K={bk}"), cy, ma, ye, bk);
                break;
            case ColorSpaceKind.Gray when c[0] < 0.005:
                swatch = Swatch.Black;
                break;
            case ColorSpaceKind.Gray when c[0] > 0.995:
                swatch = Swatch.Paper;
                break;
            case ColorSpaceKind.Gray:
                var k = Percent(1 - c[0]);
                swatch = document.Swatches.AddCmyk(string.Create(CultureInfo.InvariantCulture, $"C=0 M=0 Y=0 K={k}"), 0, 0, 0, k);
                break;
            default:
                if (c.All(v => v < 0.005))
                {
                    swatch = Swatch.Black;
                    break;
                }
                var (r, g, b) = (Math.Round(c[0] * 255), Math.Round(c[1] * 255), Math.Round(c[2] * 255));
                swatch = document.Swatches.AddRgb(string.Create(CultureInfo.InvariantCulture, $"R={r} G={g} B={b}"), r, g, b);
                break;
        }
        swatches[key] = swatch;
        return swatch;
    }

    private static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 1) * 100, 1);

    // ---- images --------------------------------------------------------------------------------------

    /// <summary>Rules (thin lines and bars, which text never crosses) and rectangular clipping paths, in page coordinates.</summary>
    private static (List<Rule> Rules, List<Rect> Clips) CollectGeometry(UglyToad.PdfPig.Content.Page source, double pageHeight)
    {
        var rules = new List<Rule>();
        var clips = new List<Rect>();
        foreach (var path in source.Paths)
        {
            foreach (var subpath in path)
            {
                if (TryRectangle(subpath, out var rect))
                {
                    var box = new Rect(rect.Left, pageHeight - rect.Top, rect.Width, rect.Height);
                    if (path.IsClipping)
                    {
                        clips.Add(box);
                    }
                    else if (path.IsFilled && (box.Width <= 3 || box.Height <= 3))
                    {
                        rules.Add(new Rule(box.Left, box.Top, box.Right, box.Bottom));
                    }
                    else if (path.IsStroked)
                    {
                        rules.Add(new Rule(box.Left, box.Top, box.Right, box.Top));
                        rules.Add(new Rule(box.Left, box.Bottom, box.Right, box.Bottom));
                        rules.Add(new Rule(box.Left, box.Top, box.Left, box.Bottom));
                        rules.Add(new Rule(box.Right, box.Top, box.Right, box.Bottom));
                    }
                }
                else if (!path.IsClipping && path.IsStroked && TryLine(subpath, out var from, out var to)
                    && (Math.Abs(from.X - to.X) < 0.5 || Math.Abs(from.Y - to.Y) < 0.5))
                {
                    rules.Add(new Rule(Math.Min(from.X, to.X), pageHeight - Math.Max(from.Y, to.Y), Math.Max(from.X, to.X), pageHeight - Math.Min(from.Y, to.Y)));
                }
            }
        }
        return (rules, clips);
    }

    private void ImportImages(UglyToad.PdfPig.Content.Page source, Dom.Page target, double pageHeight, List<Rect> clips)
    {
        var index = 0;
        foreach (var image in source.GetImages())
        {
            index++;
            var box = image.BoundingBox;
            var bounds = new Rect(box.Left, pageHeight - box.Top, box.Width, box.Height);
            if (bounds.Width < 0.5 || bounds.Height < 0.5)
            {
                continue;
            }
            if (options.ImageDirectory is null)
            {
                warnings.Add($"Page {source.Number}: image {index} skipped (no ImageDirectory set).");
                continue;
            }
            byte[]? bytes = null;
            string extension;
            var raw = image.RawMemory.Span;
            if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8)
            {
                bytes = raw.ToArray();
                extension = ".jpg";
            }
            else if (image.TryGetPng(out var png))
            {
                bytes = png;
                extension = ".png";
            }
            else
            {
                warnings.Add($"Page {source.Number}: image {index} has an encoding that cannot be extracted.");
                continue;
            }
            Directory.CreateDirectory(options.ImageDirectory);
            var path = Path.Combine(options.ImageDirectory, string.Create(CultureInfo.InvariantCulture, $"page{source.Number}-image{index}{extension}"));
            File.WriteAllBytes(path, bytes);
            images.Add(path);
            // A clipping rectangle smaller than the image is the frame (InDesign crops by framing).
            var frameBounds = bounds;
            foreach (var clip in clips.OrderBy(c => c.Width * c.Height))
            {
                var left = Math.Max(clip.Left, bounds.Left);
                var top = Math.Max(clip.Top, bounds.Top);
                var right = Math.Min(clip.Right, bounds.Right);
                var bottom = Math.Min(clip.Bottom, bounds.Bottom);
                if (right - left > 1 && bottom - top > 1 && (right - left) * (bottom - top) < bounds.Width * bounds.Height * 0.999)
                {
                    frameBounds = Rect.FromEdges(left, top, right, bottom);
                    break;
                }
            }
            var frame = target.AddRectangle(frameBounds, new ShapeStyle { Fill = Swatch.None, Stroke = Swatch.None, StrokeWeight = 0 });
            frame.PlaceImage(ImageSource.FromFile(path), ImageFit.Stretch);
            PositionImage(frame, frameBounds, bounds);
            imageFrames++;
        }
    }

    /// <summary>Places the frame's image so it covers <paramref name="imageBounds"/> (page coordinates), cropped by the frame.</summary>
    private static void PositionImage(GraphicFrame frame, Rect frameBounds, Rect imageBounds)
    {
        var graphic = frame.Graphic;
        var graphicBounds = graphic?.Element("Properties")?.Element("GraphicBounds");
        if (graphic is null || graphicBounds is null)
        {
            return;
        }
        var width = double.Parse(graphicBounds.Attribute("Right")!.Value, CultureInfo.InvariantCulture) - double.Parse(graphicBounds.Attribute("Left")!.Value, CultureInfo.InvariantCulture);
        var height = double.Parse(graphicBounds.Attribute("Bottom")!.Value, CultureInfo.InvariantCulture) - double.Parse(graphicBounds.Attribute("Top")!.Value, CultureInfo.InvariantCulture);
        if (width <= 0 || height <= 0)
        {
            return;
        }
        // Frame inner coordinates are centred on the frame.
        var center = frameBounds.Center;
        var placement = new CoreIdml.Matrix(imageBounds.Width / width, 0, 0, imageBounds.Height / height, imageBounds.X - center.X, imageBounds.Y - center.Y);
        graphic.SetAttributeValue("ItemTransform", placement.ToString());
    }

    // ---- shapes --------------------------------------------------------------------------------------

    private void ImportShapes(UglyToad.PdfPig.Content.Page source, IdmlDocument document, Dom.Page target, double pageHeight)
    {
        // InDesign-style output paints a frame's fill and its stroke as two paths; rejoin them into one frame.
        var filledOnly = new Dictionary<string, GraphicFrame>(StringComparer.Ordinal);
        static string Key(Rect r) => string.Create(CultureInfo.InvariantCulture, $"{r.X:0.0}|{r.Y:0.0}|{r.Width:0.0}|{r.Height:0.0}");
        foreach (var path in source.Paths)
        {
            if (path.IsClipping || (!path.IsFilled && !path.IsStroked))
            {
                continue;
            }
            var fill = path.IsFilled ? ToColor(path.FillColor) : null;
            var stroke = path.IsStroked ? ToColor(path.StrokeColor) : null;
            foreach (var subpath in path)
            {
                if (TryRectangle(subpath, out var rect))
                {
                    var bounds = new Rect(rect.Left, pageHeight - rect.Top, rect.Width, rect.Height);
                    // A white rectangle under the whole page is the paper itself.
                    if (stroke is null && bounds.Width >= source.Width - 1 && bounds.Height >= source.Height - 1 && fill is not null && IsWhite(fill))
                    {
                        continue;
                    }
                    if (fill is null && stroke is not null && filledOnly.Remove(Key(bounds), out var existing))
                    {
                        existing.ApplyStyle(new ShapeStyle { Stroke = SwatchFor(document, stroke), StrokeWeight = Math.Round(path.LineWidth, 3) });
                        continue;
                    }
                    var frame = target.AddRectangle(bounds, new ShapeStyle
                    {
                        Fill = fill is null ? Swatch.None : SwatchFor(document, fill),
                        Stroke = stroke is null ? Swatch.None : SwatchFor(document, stroke),
                        StrokeWeight = stroke is null ? 0 : Math.Round(path.LineWidth, 3),
                    });
                    if (fill is not null && stroke is null)
                    {
                        filledOnly[Key(bounds)] = frame;
                    }
                    shapes++;
                }
                else if (TryLine(subpath, out var from, out var to) && stroke is not null)
                {
                    target.AddLine(new Point(from.X, pageHeight - from.Y), new Point(to.X, pageHeight - to.Y), new ShapeStyle
                    {
                        Stroke = SwatchFor(document, stroke),
                        StrokeWeight = Math.Round(path.LineWidth, 3),
                    });
                    shapes++;
                }
            }
        }
    }

    private static bool IsWhite(ResolvedColor color)
    {
        var (r, g, b) = color.ToRgb();
        return r > 0.99 && g > 0.99 && b > 0.99;
    }

    private static bool TryRectangle(PdfSubpath subpath, out PdfRectangle rect)
    {
        rect = default;
        if (subpath.IsDrawnAsRectangle)
        {
            rect = subpath.GetBoundingRectangle() ?? default;
            return rect.Width > 0 && rect.Height > 0;
        }
        var points = new List<PdfPoint>();
        foreach (var command in subpath.Commands)
        {
            switch (command)
            {
                case PdfSubpath.Move move:
                    points.Add(move.Location);
                    break;
                case PdfSubpath.Line line:
                    points.Add(line.To);
                    break;
                case PdfSubpath.Close:
                    break;
                default:
                    return false;
            }
        }
        if (points.Count > 1 && points[^1].Equals(points[0]))
        {
            points.RemoveAt(points.Count - 1);
        }
        if (points.Count != 4)
        {
            return false;
        }
        for (var i = 0; i < 4; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % 4];
            if (Math.Abs(a.X - b.X) > 0.01 && Math.Abs(a.Y - b.Y) > 0.01)
            {
                return false;
            }
        }
        var left = points.Min(p => p.X);
        var bottom = points.Min(p => p.Y);
        rect = new PdfRectangle(left, bottom, points.Max(p => p.X), points.Max(p => p.Y));
        return rect.Width > 0 && rect.Height > 0;
    }

    private static bool TryLine(PdfSubpath subpath, out PdfPoint from, out PdfPoint to)
    {
        from = default;
        to = default;
        var commands = subpath.Commands;
        if (commands.Count == 2 && commands[0] is PdfSubpath.Move move && commands[1] is PdfSubpath.Line line)
        {
            from = move.Location;
            to = line.To;
            return true;
        }
        return false;
    }
}
