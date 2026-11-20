using System.Text.Json;
using System.Text.Json.Serialization;
using CoreIdml.Dom;

namespace CoreIdml.Json;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(DocumentSpec))]
[JsonSerializable(typeof(FillSpec))]
[JsonSerializable(typeof(FillReport))]
[JsonSerializable(typeof(DocumentSummary))]
[JsonSerializable(typeof(ValidationReport))]
[JsonSerializable(typeof(SummaryOptions))]
[JsonSerializable(typeof(StorySummary))]
[JsonSerializable(typeof(XmlNodeSummary))]
[JsonSerializable(typeof(XmlImportOptionsSpec))]
[JsonSerializable(typeof(XmlImportReportSpec))]
[JsonSerializable(typeof(DiffReport))]
internal sealed partial class IdmlJsonContext : JsonSerializerContext;

/// <summary>
/// JSON entry points: build documents from a <see cref="DocumentSpec"/>, fill templates, and read summaries.
/// Source-generated serialization only, so this works under Native AOT; it is what the C/Dart API exposes.
/// </summary>
public static partial class IdmlJson
{
    /// <summary>
    /// Writer options for JSON the library produces: accents and quotes are written as-is instead of \u escapes,
    /// which keeps Spanish text readable and summaries smaller for agents. Still valid JSON (not meant for HTML).
    /// </summary>
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly IdmlJsonContext Output = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Creates a document from a JSON <see cref="DocumentSpec"/> (null or empty: blank A4).</summary>
    public static IdmlDocument CreateDocument(string? json) => CreateDocument(ParseSpec(json));

    public static IdmlDocument CreateDocument(DocumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var unit = spec.Unit;
        var setup = spec.Page ?? new PageSetupSpec();
        // An unknown page size is reported with the spec's other errors; A4 stands in until then.
        var sizeError = SizeError(setup);
        var size = sizeError is null ? ResolveSize(setup, unit) : PageSizes.A4;
        var pageCount = Math.Max(1, spec.Pages?.Count(page => page.Index is null) ?? 1);
        var document = IdmlDocument.Create(new DocumentOptions
        {
            PageSize = size,
            Margins = ToSpacing(setup.Margins, unit) ?? Spacing.All(36),
            FacingPages = setup.FacingPages ?? false,
            PageCount = pageCount,
            Columns = setup.Columns ?? 1,
            ColumnGutter = setup.ColumnGutter is { } gutter ? Units.FromUnit(gutter, unit) : 12,
            Bleed = setup.Bleed is { } bleed ? Units.FromUnit(bleed, unit) : 0,
        });
        try
        {
            Render(document, spec, reuseExistingPages: true, sizeError is null ? null : [new SpecError("page", sizeError, IdmlErrorKind.InvalidOperation)]);
        }
        catch
        {
            document.Dispose();
            throw;
        }
        return document;
    }

    /// <summary>Adds the colours, styles and pages of a JSON spec to an existing document (pages are appended).</summary>
    public static void Apply(IdmlDocument document, string json) => Render(document, ParseSpec(json), reuseExistingPages: false);

    public static void Apply(IdmlDocument document, DocumentSpec spec) => Render(document, spec, reuseExistingPages: false);

    /// <summary>Fills a template from a JSON <see cref="FillSpec"/>; returns a JSON <see cref="FillReport"/>.</summary>
    public static string Fill(IdmlDocument document, string json)
    {
        ArgumentNullException.ThrowIfNull(document);
        var spec = JsonSerializer.Deserialize(json, IdmlJsonContext.Default.FillSpec) ?? new FillSpec();
        var data = new TemplateData
        {
            Text = spec.Text ?? [],
            Images = spec.Images ?? [],
            ImageFit = ParseFit(spec.Fit),
            PlaceholderFormat = spec.PlaceholderFormat ?? "{{{{{0}}}}}",
        };
        var result = document.Fill(data);
        return JsonSerializer.Serialize(new FillReport { Filled = result.Filled, Missing = result.Missing }, Output.FillReport);
    }

    public static string ValidateToJson(IdmlDocument document)
    {
        var result = IdmlValidator.Validate(document);
        var report = new ValidationReport
        {
            IsValid = result.IsValid,
            Issues = result.Issues.Select(issue => new IssueReport
            {
                Severity = issue.Severity.ToString().ToLowerInvariant(),
                Code = issue.Code,
                Message = issue.Message,
                Part = issue.Part,
            }).ToList(),
        };
        return JsonSerializer.Serialize(report, Output.ValidationReport);
    }

    public static DocumentSpec ParseSpec(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new DocumentSpec()
            : JsonSerializer.Deserialize(json, IdmlJsonContext.Default.DocumentSpec) ?? new DocumentSpec();

    // ---- rendering --------------------------------------------------------------------------------

    /// <summary>
    /// Builds the spec into the document. Every error is found first (<see cref="FindSpecErrors"/>) and reported
    /// together, so a spec either applies completely or changes nothing.
    /// </summary>
    private static void Render(IdmlDocument document, DocumentSpec spec, bool reuseExistingPages, List<SpecError>? earlierErrors = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(spec);
        var errors = earlierErrors ?? [];
        errors.AddRange(FindSpecErrors(document, spec, reuseExistingPages));
        ThrowSpecErrors(errors, creating: reuseExistingPages);
        var unit = spec.Unit;

        foreach (var color in spec.Colors ?? [])
        {
            if (color.Cmyk is { Length: 4 } cmyk)
            {
                document.Swatches.AddCmyk(color.Name, cmyk[0], cmyk[1], cmyk[2], cmyk[3]);
            }
            else if (color.Rgb is { Length: 3 } rgb)
            {
                document.Swatches.AddRgb(color.Name, rgb[0], rgb[1], rgb[2]);
            }
            else if (color.Hex is { } hex)
            {
                document.Swatches.AddHex(color.Name, hex);
            }
            else
            {
                throw new IdmlException($"Colour '{color.Name}' needs cmyk, rgb or hex.");
            }
        }

        foreach (var style in spec.CharacterStyles ?? [])
        {
            var basedOn = style.BasedOn is { } baseName ? RequireCharacterStyle(document, baseName) : null;
            document.Styles.AddCharacterStyle(style.Name, ToCharacterFormat(document, style), basedOn);
        }
        foreach (var style in spec.ParagraphStyles ?? [])
        {
            var basedOn = style.BasedOn is { } baseName ? RequireParagraphStyle(document, baseName) : null;
            document.Styles.AddParagraphStyle(style.Name, ToParagraphFormat(document, style), basedOn);
        }

        var nextExisting = 0;
        foreach (var pageSpec in spec.Pages ?? [])
        {
            Page page;
            if (pageSpec.Index is { } index)
            {
                var pages = document.Pages;
                page = index >= 0 && index < pages.Count ? pages[index] : throw new IdmlException(IdmlErrorKind.NotFound, $"Page index {index} does not exist.");
            }
            else if (reuseExistingPages && nextExisting < document.Pages.Count)
            {
                page = document.Pages[nextExisting++];
            }
            else
            {
                page = document.AddPage();
            }
            if (ToSpacing(pageSpec.Margins, unit) is { } margins)
            {
                page.Margins = margins;
            }
            foreach (var item in pageSpec.Items ?? [])
            {
                RenderItem(document, page, item, unit);
            }
        }
    }

    private static void RenderItem(IdmlDocument document, Page page, ItemSpec item, string? unit)
    {
        double U(double value) => Units.FromUnit(value, unit);
        var bounds = new Rect(U(item.X), U(item.Y), U(item.Width), U(item.Height));
        var type = item.Type.ToLowerInvariant();
        var shape = new ShapeStyle
        {
            Fill = item.Fill is { } fill ? RequireSwatch(document, fill) : null,
            FillTint = item.FillTint,
            // A spec's shapes have no outline unless it asks for one (InDesign's default graphics frame style would
            // draw a 1 pt black stroke around every rectangle and ellipse).
            Stroke = item.Stroke is { } stroke ? RequireSwatch(document, stroke) : type is "rectangle" or "ellipse" ? Swatch.None : null,
            StrokeWeight = item.StrokeWeight,
            StrokeTint = item.StrokeTint,
            StrokeKind = item.StrokeType is { } strokeType ? ParseChoice<StrokeKind>(strokeType, "strokeType") : null,
            EndCap = item.EndCap is { } endCap ? ParseChoice<LineCap>(endCap, "endCap") : null,
            Opacity = item.Opacity,
            CornerRadius = item.CornerRadius is { } radius ? U(radius) : null,
            Rotation = item.Rotation,
        };

        PageItem created;
        switch (type)
        {
            case "text":
            {
                var frame = page.AddTextFrame(bounds, item.Name);
                if (item.Columns is { } columns)
                {
                    frame.Columns = columns;
                }
                if (item.ColumnGutter is { } gutter)
                {
                    frame.ColumnGutter = U(gutter);
                }
                if (ToSpacing(item.Inset, unit) is { } inset)
                {
                    frame.Inset = inset;
                }
                if (item.VerticalAlign is { } align)
                {
                    frame.VerticalJustification = ParseChoice<VerticalJustification>(align, "verticalAlign");
                }
                if (item.ContinueFrom is { } previousName)
                {
                    var previous = document.FindPageItem(previousName) as TextFrame
                        ?? throw new IdmlException(IdmlErrorKind.NotFound, $"continueFrom: no text frame named '{previousName}'.");
                    previous.ThreadTo(frame);
                }
                else
                {
                    RenderText(document, frame.Story, item);
                }
                created = frame;
                break;
            }
            case "rectangle":
                created = page.AddRectangle(bounds, null, item.Name);
                break;
            case "ellipse":
                created = page.AddEllipse(bounds, null, item.Name);
                break;
            case "line":
                created = page.AddLine(
                    new Point(U(item.X), U(item.Y)),
                    new Point(U(item.X2 ?? item.X + item.Width), U(item.Y2 ?? item.Y + item.Height)),
                    new ShapeStyle { Stroke = Swatch.Black, StrokeWeight = 1 },
                    item.Name);
                break;
            case "image":
            {
                var path = item.Path ?? throw new IdmlException("Image items need a path.");
                created = page.AddImage(bounds, path, ParseFit(item.Fit), item.Name);
                break;
            }
            case "table":
            {
                var frame = page.AddTextFrame(bounds, item.Name);
                var rows = item.Rows ?? [];
                var columnCount = Math.Max(1, rows.Count == 0 ? item.ColumnWidths?.Length ?? 1 : rows.Max(row => row.Count));
                var table = frame.AddTable(Math.Max(1, rows.Count), columnCount, new TableOptions
                {
                    ColumnWidths = item.ColumnWidths?.Select(U).ToArray(),
                    HeaderRows = item.HeaderRows ?? 0,
                    RowHeight = item.RowHeight is { } height ? U(height) : 18,
                    CellParagraphStyle = item.CellStyle is { } cellStyle ? RequireParagraphStyle(document, cellStyle) : null,
                    HeaderParagraphStyle = item.HeaderStyle is { } headerStyle ? RequireParagraphStyle(document, headerStyle) : null,
                    HeaderFill = item.HeaderFill is { } headerFill ? RequireSwatch(document, headerFill) : null,
                    AlternateFill = item.AlternateFill is { } alternateFill ? RequireSwatch(document, alternateFill) : null,
                    Stroke = shape.Stroke,
                    StrokeWeight = item.StrokeWeight,
                    CellInset = item.CellInset is { } cellInset ? U(cellInset) : null,
                });
                for (var row = 0; row < rows.Count; row++)
                {
                    table.SetRow(row, [.. rows[row]]);
                }
                created = frame;
                shape = new ShapeStyle { Fill = shape.Fill, FillTint = shape.FillTint, Opacity = shape.Opacity, Rotation = shape.Rotation };
                break;
            }
            default:
                throw new IdmlException(ItemTypeError(item.Type));
        }

        if (type != "line")
        {
            created.ApplyStyle(shape);
        }
        else
        {
            created.ApplyStyle(new ShapeStyle
            {
                Stroke = shape.Stroke,
                StrokeWeight = shape.StrokeWeight,
                StrokeTint = shape.StrokeTint,
                StrokeKind = shape.StrokeKind,
                EndCap = shape.EndCap,
                Opacity = shape.Opacity,
                Rotation = shape.Rotation,
            });
        }
        if (item.Label is { } label)
        {
            created.Label = label;
        }
        if (item.Layer is { } layerName)
        {
            created.MoveToLayer(document.Layers.Add(layerName));
        }
        if (item.Tag is { } tag)
        {
            document.Tags.TagPageItem(created, tag);
        }
    }

    private static void RenderText(IdmlDocument document, Story story, ItemSpec item)
    {
        if (item.Text is { } text)
        {
            var style = item.Style is { } styleName ? RequireParagraphStyle(document, styleName) : null;
            story.AddParagraph(text, style);
        }
        foreach (var paragraphSpec in item.Paragraphs ?? [])
        {
            var style = paragraphSpec.Style is { } styleName ? RequireParagraphStyle(document, styleName) : null;
            var paragraph = story.AddParagraph(style, ToParagraphFormatOrNull(document, paragraphSpec));
            if (paragraphSpec.Text is { } paragraphText)
            {
                paragraph.AddText(paragraphText);
            }
            foreach (var run in paragraphSpec.Runs ?? [])
            {
                var characterStyle = run.Style is { } runStyle ? RequireCharacterStyle(document, runStyle) : null;
                paragraph.AddText(run.Text, characterStyle, ToCharacterFormatOrNull(document, run));
            }
        }
    }

    // ---- conversions ------------------------------------------------------------------------------

    private static Size ResolveSize(PageSetupSpec setup, string? unit)
    {
        var size = setup.Width is { } width && setup.Height is { } height
            ? new Size(Units.FromUnit(width, unit), Units.FromUnit(height, unit))
            : (setup.Size ?? "A4").ToUpperInvariant() switch
            {
                "A3" => PageSizes.A3,
                "A4" => PageSizes.A4,
                "A5" => PageSizes.A5,
                "LETTER" => PageSizes.Letter,
                "LEGAL" => PageSizes.Legal,
                _ => throw new IdmlException(SizeError(setup)!),
            };
        return setup.Landscape == true && size.Height > size.Width ? new Size(size.Height, size.Width) : size;
    }

    /// <summary>Why the page setup's size is unknown, or null when it is fine.</summary>
    private static string? SizeError(PageSetupSpec setup) =>
        setup.Width is not null && setup.Height is not null
        || (setup.Size ?? "A4").ToUpperInvariant() is "A3" or "A4" or "A5" or "LETTER" or "LEGAL"
            ? null
            : $"Unknown page size '{setup.Size!.ToUpperInvariant()}'. Use A3, A4, A5, Letter, Legal or width/height.";

    private static Spacing? ToSpacing(SpacingSpec? spec, string? unit)
    {
        if (spec is null)
        {
            return null;
        }
        var all = spec.All ?? 0;
        return new Spacing(
            Units.FromUnit(spec.Top ?? all, unit),
            Units.FromUnit(spec.Right ?? all, unit),
            Units.FromUnit(spec.Bottom ?? all, unit),
            Units.FromUnit(spec.Left ?? all, unit));
    }

    private static ImageFit ParseFit(string? fit) =>
        string.IsNullOrEmpty(fit) ? ImageFit.Fill : ParseChoice<ImageFit>(fit, "fit");

    /// <summary>An enum value by name, ignoring case; anything else is an error that lists the names it accepts.</summary>
    private static TEnum ParseChoice<TEnum>(string value, string field)
        where TEnum : struct, Enum =>
        ChoiceError<TEnum>(value, field) is { } message ? throw new IdmlException(message) : Enum.Parse<TEnum>(value, ignoreCase: true);

    private static string? ChoiceError<TEnum>(string value, string field)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? null
            : $"Unknown {field} '{value}'. Use {string.Join(", ", Enum.GetNames<TEnum>().Select(name => char.ToLowerInvariant(name[0]) + name[1..]))}.";

    private static ParagraphFormat ToParagraphFormat(IdmlDocument document, TextFormatSpec spec) => new()
    {
        FontFamily = spec.Font,
        FontStyle = spec.FontStyle,
        PointSize = spec.Size,
        Leading = spec.Leading,
        FillColor = spec.Color is { } color ? RequireSwatch(document, color) : null,
        FillTint = spec.Tint,
        Justification = ParseJustification(spec.Align),
        SpaceBefore = spec.SpaceBefore,
        SpaceAfter = spec.SpaceAfter,
        FirstLineIndent = spec.FirstLineIndent,
        LeftIndent = spec.LeftIndent,
        RightIndent = spec.RightIndent,
        Underline = spec.Underline,
        StrikeThrough = spec.StrikeThrough,
        Tracking = spec.Tracking,
        Capitalization = ParseCaps(spec.Caps),
        Position = ParsePosition(spec.Position),
        ListType = spec.List is { } list ? TryListType(list) ?? throw new IdmlException(ParagraphValueError(spec)!) : null,
        BulletCharacter = spec.Bullet is { } bullet ? IsOneCharacter(bullet) ? bullet : throw new IdmlException(ParagraphValueError(spec)!) : null,
        BulletTextAfter = spec.BulletAfter,
        NumberingFormat = spec.NumberFormat is { } numberFormat ? TryNumberingFormat(numberFormat) ?? throw new IdmlException(ParagraphValueError(spec)!) : null,
        NumberingExpression = spec.NumberText,
        NumberingLevel = spec.ListLevel is { } level ? level is >= 1 and <= 9 ? level : throw new IdmlException(ParagraphValueError(spec)!) : null,
        NumberingStartAt = spec.StartAt,
        // On a paragraph, startAt restarts the numbering there; on a style it only sets where lists start (making the
        // style restart would number every paragraph of it the same).
        NumberingContinue = spec.StartAt is not null && spec is not StyleSpec ? false : null,
        TabStops = spec.Tabs?.Select(tab => new TabStop(
            tab.Position >= 0 ? tab.Position : throw new IdmlException(ParagraphValueError(spec)!),
            tab.Align is { } align ? TryTabAlignment(align) ?? throw new IdmlException(ParagraphValueError(spec)!) : TabAlignment.Left,
            tab.Leader is { Length: > 8 } ? throw new IdmlException(ParagraphValueError(spec)!) : tab.Leader ?? string.Empty,
            tab.AlignOn is { } alignOn ? IsOneCharacter(alignOn) ? alignOn : throw new IdmlException(ParagraphValueError(spec)!) : ".")).ToList(),
        KeepWithNext = spec.KeepWithNext is { } keepWithNext ? keepWithNext is >= 0 and <= 5 ? keepWithNext : throw new IdmlException(ParagraphValueError(spec)!) : null,
        // First or last lines only count while Keep Lines Together is on; keepTogether turns on All Lines.
        KeepLinesTogether = spec.KeepFirstLines is not null || spec.KeepLastLines is not null ? true : spec.KeepTogether,
        KeepAllLinesTogether = spec.KeepTogether,
        KeepFirstLines = spec.KeepFirstLines is { } firstLines ? firstLines is >= 1 and <= 50 ? firstLines : throw new IdmlException(ParagraphValueError(spec)!) : null,
        KeepLastLines = spec.KeepLastLines is { } lastLines ? lastLines is >= 1 and <= 50 ? lastLines : throw new IdmlException(ParagraphValueError(spec)!) : null,
        StartParagraph = spec.StartParagraph is { } start ? TryStartParagraph(start) ?? throw new IdmlException(ParagraphValueError(spec)!) : null,
    };

    private static StartParagraph? TryStartParagraph(string start) => start.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "anywhere" => Dom.StartParagraph.Anywhere,
        "nextcolumn" or "column" => Dom.StartParagraph.NextColumn,
        "nextframe" or "frame" => Dom.StartParagraph.NextFrame,
        "nextpage" or "page" => Dom.StartParagraph.NextPage,
        "nextoddpage" or "oddpage" => Dom.StartParagraph.NextOddPage,
        "nextevenpage" or "evenpage" => Dom.StartParagraph.NextEvenPage,
        _ => null,
    };

    private static TabAlignment? TryTabAlignment(string align) => align.ToLowerInvariant() switch
    {
        "left" => TabAlignment.Left,
        "center" or "centre" => TabAlignment.Center,
        "right" => TabAlignment.Right,
        "decimal" or "character" or "char" => TabAlignment.Character,
        _ => null,
    };

    private static ListType? TryListType(string list) => list.ToLowerInvariant() switch
    {
        "bullet" or "bullets" or "bulleted" => ListType.Bullet,
        "numbered" or "number" or "numbers" => ListType.Numbered,
        "none" or "nolist" => ListType.None,
        _ => null,
    };

    /// <summary>InDesign's name of a numbering format, from the short form ("i") or the name itself.</summary>
    private static string? TryNumberingFormat(string format) => format.Trim() switch
    {
        "1" => NumberingFormats.Arabic,
        "01" => NumberingFormats.ArabicTwoDigits,
        "001" => NumberingFormats.ArabicThreeDigits,
        "0001" => NumberingFormats.ArabicFourDigits,
        "I" => NumberingFormats.UpperRoman,
        "i" => NumberingFormats.LowerRoman,
        "A" => NumberingFormats.UpperLetters,
        "a" => NumberingFormats.LowerLetters,
        NumberingFormats.Arabic or NumberingFormats.ArabicTwoDigits or NumberingFormats.ArabicThreeDigits or NumberingFormats.ArabicFourDigits
            or NumberingFormats.UpperRoman or NumberingFormats.LowerRoman or NumberingFormats.UpperLetters or NumberingFormats.LowerLetters => format.Trim(),
        _ => null,
    };

    private static CharacterFormat ToCharacterFormat(IdmlDocument document, TextFormatSpec spec) => new()
    {
        FontFamily = spec.Font,
        FontStyle = spec.FontStyle,
        PointSize = spec.Size,
        FillColor = spec.Color is { } color ? RequireSwatch(document, color) : null,
        FillTint = spec.Tint,
        Underline = spec.Underline,
        StrikeThrough = spec.StrikeThrough,
        Tracking = spec.Tracking,
        Capitalization = ParseCaps(spec.Caps),
        Position = ParsePosition(spec.Position),
    };

    private static ParagraphFormat? ToParagraphFormatOrNull(IdmlDocument document, TextFormatSpec spec) =>
        HasFormatting(spec) ? ToParagraphFormat(document, spec) : null;

    private static CharacterFormat? ToCharacterFormatOrNull(IdmlDocument document, TextFormatSpec spec) =>
        HasFormatting(spec) ? ToCharacterFormat(document, spec) : null;

    private static bool HasFormatting(TextFormatSpec spec) =>
        spec.Font is not null || spec.FontStyle is not null || spec.Size is not null || spec.Leading is not null || spec.Color is not null
        || spec.Tint is not null || spec.Align is not null || spec.SpaceBefore is not null || spec.SpaceAfter is not null
        || spec.FirstLineIndent is not null || spec.LeftIndent is not null || spec.RightIndent is not null || spec.Underline is not null
        || spec.StrikeThrough is not null || spec.Tracking is not null || spec.Caps is not null || spec.Position is not null
        || spec.List is not null || spec.Bullet is not null || spec.BulletAfter is not null || spec.NumberFormat is not null
        || spec.NumberText is not null || spec.ListLevel is not null || spec.StartAt is not null || spec.Tabs is not null
        || spec.KeepWithNext is not null || spec.KeepTogether is not null || spec.KeepFirstLines is not null || spec.KeepLastLines is not null
        || spec.StartParagraph is not null;

    private static Justification? ParseJustification(string? align) =>
        align is null ? null : TryJustification(align) ?? throw new IdmlException(AlignError(align));

    private static Justification? TryJustification(string align) => align.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "left" => Justification.Left,
        "center" or "centre" => Justification.Center,
        "right" => Justification.Right,
        "justify" => Justification.LeftJustified,
        "justifyall" => Justification.FullyJustified,
        _ => null,
    };

    private static Capitalization? ParseCaps(string? caps) => caps is null ? null : ParseChoice<Capitalization>(caps, "caps");

    private static TextPosition? ParsePosition(string? position) => position is null ? null : ParseChoice<TextPosition>(position, "position");

    private static Swatch RequireSwatch(IdmlDocument document, string name) =>
        document.Swatches.Find(name) ?? throw new IdmlException(IdmlErrorKind.NotFound, SwatchMissing(name));

    private static ParagraphStyle RequireParagraphStyle(IdmlDocument document, string name) =>
        document.Styles.FindParagraphStyle(name) ?? throw new IdmlException(IdmlErrorKind.NotFound, ParagraphStyleMissing(name));

    private static CharacterStyle RequireCharacterStyle(IdmlDocument document, string name) =>
        document.Styles.FindCharacterStyle(name) ?? throw new IdmlException(IdmlErrorKind.NotFound, CharacterStyleMissing(name));
}
