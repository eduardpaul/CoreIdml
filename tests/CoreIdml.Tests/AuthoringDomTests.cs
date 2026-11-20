using CoreIdml.Dom;

namespace CoreIdml.Tests;

/// <summary>The DOM writes the features the renderer draws in the form InDesign stores them (checked against the doc-samples IDML).</summary>
public class AuthoringDomTests
{
    [Fact]
    public void Paragraph_formats_write_drop_caps_nested_styles_grid_and_column_spans()
    {
        using var document = IdmlDocument.Create();
        var lead = document.Styles.AddCharacterStyle("Lead", new CharacterFormat { FontStyle = "Bold" });
        var style = document.Styles.AddParagraphStyle("Split", new ParagraphFormat
        {
            DropCapLines = 2,
            DropCapCharacters = 1,
            NestedStyles = [new NestedStyle(lead, NestedStyleDelimiter.Character(":"), Inclusive: false)],
            AlignToBaselineGrid = true,
            SpanColumnType = ColumnSpan.SplitColumns,
        });

        var element = style.Element;
        Assert.Equal("2", element.Attribute("DropCapLines")!.Value);
        Assert.Equal("AlignBaseline", element.Attribute("GridAlignment")!.Value);
        Assert.Equal("SplitColumns", element.Attribute("SpanColumnType")!.Value);
        // A split paragraph takes two sub-columns unless told otherwise (06-frame-span-balance: type="short").
        Assert.Equal("2", element.Element("Properties")!.Element("SpanSplitColumnCount")!.Value);
        var delimiter = element.Element("Properties")!.Element("AllNestedStyles")!.Element("ListItem")!.Element("Delimiter")!;
        Assert.Equal(("string", ":"), (delimiter.Attribute("type")!.Value, delimiter.Value));
        Assert.Throws<ArgumentException>(() => NestedStyleDelimiter.Character("::"));
    }

    [Fact]
    public void A_text_wrap_reads_back_and_is_replaced_or_removed()
    {
        using var document = IdmlDocument.Create();
        var box = document.Pages[0].AddRectangle(new Rect(0, 0, 50, 50));
        var wrap = new TextWrap(TextWrapMode.BoundingBox, new Spacing(1, 2, 3, 4), TextWrapSide.SideAwayFromSpine, Inverse: true);

        box.TextWrap = wrap;
        Assert.Equal(wrap, box.TextWrap);
        box.TextWrap = wrap with { Mode = TextWrapMode.NextColumn };

        Assert.Single(box.Element.Elements("TextWrapPreference"));
        Assert.Equal("NextColumnTextWrap", box.Element.Element("TextWrapPreference")!.Attribute("TextWrapMode")!.Value);
        box.TextWrap = null;
        Assert.Null(box.TextWrap);
        Assert.Null(box.Element.Element("TextWrapPreference"));
    }

    [Fact]
    public void The_baseline_grid_changes_only_its_start_increment_and_origin()
    {
        using var document = IdmlDocument.Create();

        Assert.Equal(new BaselineGridOptions(36, 12), document.BaselineGrid);
        document.BaselineGrid = new BaselineGridOptions(48, 13.5, RelativeToTopMargin: true);

        Assert.Equal(new BaselineGridOptions(48, 13.5, true), document.BaselineGrid);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.BaselineGrid = new BaselineGridOptions(0, 0));
    }
}
