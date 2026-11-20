using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Rendering;

namespace CoreIdml.Pdf.Tests;

/// <summary>Object styles in the PDF: text frame options and strokes that move the text, transparency from the style.</summary>
public sealed class ObjectStyleRenderingTests
{
    private const string Text = "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.";

    private static StoryLayout Layout(IdmlDocument document, TextFrame frame) =>
        new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story);

    private static XElement ObjectStyles(IdmlDocument document) =>
        document.Package.GetXml("Resources/Styles.xml").Root!.Element("RootObjectStyleGroup")!;

    // "OS Text Frame" of 02-item-object-styles: 2 pt red stroke, two columns, 8 pt inset, general options enabled.
    private static void AddTextFrameStyle(IdmlDocument document) =>
        ObjectStyles(document).Add(new XElement("ObjectStyle",
            new XAttribute("Self", "ObjectStyle/OS Text Frame"), new XAttribute("Name", "OS Text Frame"),
            new XAttribute("EnableFill", "true"), new XAttribute("EnableStroke", "true"),
            new XAttribute("EnableTextFrameGeneralOptions", "true"),
            new XAttribute("StrokeWeight", "2"), new XAttribute("StrokeColor", "Color/Black"),
            new XElement("Properties", new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[None]")),
            new XElement("TextFramePreference", new XAttribute("TextColumnCount", "2"), new XAttribute("TextColumnGutter", "12"),
                new XElement("Properties", new XElement("InsetSpacing", new XAttribute("type", "list"),
                    Enumerable.Range(0, 4).Select(_ => new XElement("ListItem", new XAttribute("type", "unit"), "8")))))));

    [Fact]
    public void Text_sits_inside_the_style_inset_and_half_the_stroke()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        AddTextFrameStyle(document);
        var frame = document.Pages[0].AddText(new Rect(36, 36, 540, 150), string.Join("\n", Enumerable.Repeat(Text, 8)));
        frame.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/OS Text Frame");
        frame.Element.Element("TextFramePreference")!.SetAttributeValue("TextColumnCount", null);

        var layout = Layout(document, frame);

        // 02-item-object-styles p1: text 9 pt in from the frame, two 255 pt columns ((540 - 2 - 16 - 12) / 2).
        var first = layout.Lines[0];
        Assert.Equal(9, first.Left - frame.InnerBounds.X, 6);
        var plain = document.Pages[0].AddText(new Rect(36, 300, 540, 150), Text);
        Assert.Equal(9, first.Baseline - frame.InnerBounds.Y - (Layout(document, plain).Lines[0].Baseline - plain.InnerBounds.Y), 6);
        var second = layout.Lines.First(line => line.Column == 1);
        Assert.Equal(9 + 255 + 12, second.Left - frame.InnerBounds.X, 6);
    }

    [Fact]
    public void A_stroke_without_colour_takes_no_room()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        var frame = document.Pages[0].AddText(new Rect(36, 36, 300, 100), Text);
        frame.Element.SetAttributeValue("StrokeWeight", "1");
        frame.Element.SetAttributeValue("StrokeColor", "Swatch/None");
        Assert.Equal(0, Layout(document, frame).Lines[0].Left - frame.InnerBounds.X, 6);

        frame.Element.SetAttributeValue("StrokeColor", "Color/Black");
        Assert.Equal(0.5, Layout(document, frame).Lines[0].Left - frame.InnerBounds.X, 6);
    }

    [Fact]
    public void Opacity_from_the_object_style_is_drawn()
    {
        using var document = IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });
        ObjectStyles(document).Add(new XElement("ObjectStyle",
            new XAttribute("Self", "ObjectStyle/Faded"), new XAttribute("Name", "Faded"),
            new XElement("Properties", new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[None]")),
            new XElement("TransparencySetting", new XElement("BlendingSetting", new XAttribute("Opacity", "40")))));
        var rectangle = document.Pages[0].AddRectangle(new Rect(100, 100, 80, 40), new ShapeStyle { Fill = Swatch.Black });
        rectangle.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Faded");

        var pdf = Encoding.Latin1.GetString(TestSupport.Export(document));

        Assert.Contains("/ca 0.4 /CA 0.4", pdf, StringComparison.Ordinal);
    }
}
