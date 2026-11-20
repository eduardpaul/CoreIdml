using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Tests;

/// <summary>Object styles: page item attributes resolved through the style chain, its categories and its transparency.</summary>
public sealed class ObjectStyleTests
{
    // As InDesign writes them in 02-item-object-styles: every category flag on the style element.
    private static XElement ObjectStyle(string name, string? basedOn, params object[] content) =>
        new("ObjectStyle",
            new XAttribute("Self", "ObjectStyle/" + name), new XAttribute("Name", name),
            new XAttribute("EnableFill", "true"), new XAttribute("EnableStroke", "true"),
            new XAttribute("EnableStrokeAndCornerOptions", "true"), new XAttribute("EnableTextFrameGeneralOptions", "true"),
            new XAttribute("EnableTextFrameBaselineOptions", "true"),
            new XElement("Properties", basedOn is null
                ? new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[None]")
                : new XElement("BasedOn", new XAttribute("type", "object"), "ObjectStyle/" + basedOn)),
            content);

    private static void AddStyles(IdmlDocument document, params XElement[] styles) =>
        document.StylesRoot.Element("RootObjectStyleGroup")!.Add(styles);

    private static XElement Inset(double value) =>
        new("TextFramePreference", new XAttribute("TextColumnCount", "2"),
            new XElement("Properties", new XElement("InsetSpacing", new XAttribute("type", "list"),
                Enumerable.Range(0, 4).Select(_ => new XElement("ListItem", new XAttribute("type", "unit"), value)))));

    [Fact]
    public void Items_take_the_style_chain_and_their_own_values_win()
    {
        using var document = IdmlDocument.Create();
        AddStyles(document,
            ObjectStyle("Card", null, new XAttribute("FillColor", "Color/Yellow"), new XAttribute("StrokeWeight", "3")),
            ObjectStyle("Card Rounded", "Card", new XAttribute("CornerRadius", "16")));
        var rectangle = document.Pages[0].AddRectangle(new Rect(0, 0, 100, 50));
        rectangle.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Card Rounded");
        rectangle.Element.SetAttributeValue("StrokeWeight", null);
        rectangle.Element.SetAttributeValue("FillColor", null);
        var resolver = new AttributeResolver(document);

        Assert.Equal("Color/Yellow", resolver.Item(rectangle.Element, "FillColor")); // from the style it is based on
        Assert.Equal("16", resolver.Item(rectangle.Element, "CornerRadius"));
        // 02-item-object-styles p2: a local fill and stroke weight on top of the style.
        rectangle.Element.SetAttributeValue("FillColor", "Color/Magenta");
        rectangle.Element.SetAttributeValue("StrokeWeight", "8");
        resolver = new AttributeResolver(document);
        Assert.Equal("Color/Magenta", resolver.Item(rectangle.Element, "FillColor"));
        Assert.Equal("8", resolver.Item(rectangle.Element, "StrokeWeight"));
    }

    [Fact]
    public void A_category_the_style_leaves_alone_keeps_the_items_own_values()
    {
        using var document = IdmlDocument.Create();
        var strokeOnly = ObjectStyle("Stroke Only", null,
            new XAttribute("FillColor", "Color/Orange"), new XAttribute("StrokeColor", "Color/Blue"), new XAttribute("StrokeWeight", "4"));
        strokeOnly.SetAttributeValue("EnableFill", "false");
        var child = ObjectStyle("Child", "Stroke Only");
        child.SetAttributeValue("EnableFill", null);
        AddStyles(document, strokeOnly, child);
        var oval = document.Pages[0].AddEllipse(new Rect(0, 0, 100, 50));
        foreach (var name in (string[])["FillColor", "StrokeColor", "StrokeWeight"])
        {
            oval.Element.SetAttributeValue(name, null);
        }
        oval.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Stroke Only");
        var resolver = new AttributeResolver(document);

        Assert.Null(resolver.Item(oval.Element, "FillColor"));
        Assert.Equal("Color/Blue", resolver.Item(oval.Element, "StrokeColor"));
        oval.Element.SetAttributeValue("FillColor", "Swatch/None");
        Assert.Equal("Swatch/None", resolver.Item(oval.Element, "FillColor"));

        // A style based on it without its own flag inherits the cleared category; clearing Stroke drops the parent's stroke.
        oval.Element.SetAttributeValue("FillColor", null);
        oval.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Child");
        resolver = new AttributeResolver(document);
        Assert.Null(resolver.Item(oval.Element, "FillColor"));
        child.SetAttributeValue("EnableStroke", "false");
        resolver = new AttributeResolver(document);
        Assert.Null(resolver.Item(oval.Element, "StrokeWeight"));
    }

    [Fact]
    public void Text_frame_options_come_from_the_style_only_when_it_enables_them()
    {
        using var document = IdmlDocument.Create();
        AddStyles(document, ObjectStyle("Text Frame", null, Inset(8)));
        var frame = document.Pages[0].AddText(new Rect(0, 0, 200, 100), "Text");
        frame.Element.Element("TextFramePreference")!.SetAttributeValue("TextColumnCount", null);
        frame.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Text Frame");
        var resolver = new AttributeResolver(document);
        Assert.Equal("2", resolver.FramePreference(frame.Element, "TextColumnCount"));
        Assert.NotNull(resolver.FramePreferenceSource(frame.Element, "InsetSpacing"));

        document.StylesRoot.Descendants("ObjectStyle").Single(style => style.Attribute("Name")?.Value == "Text Frame")
            .SetAttributeValue("EnableTextFrameGeneralOptions", "false");
        resolver = new AttributeResolver(document);
        Assert.Null(resolver.FramePreference(frame.Element, "TextColumnCount"));
        Assert.Null(resolver.FramePreferenceSource(frame.Element, "InsetSpacing"));
        Assert.Equal("TopAlign", resolver.FramePreference(frame.Element, "VerticalJustification")); // the frame's own
    }

    [Fact]
    public void Opacity_comes_from_the_item_then_from_its_style_when_the_style_applies_transparency()
    {
        using var document = IdmlDocument.Create();
        var effects = new XElement("ObjectStyleObjectEffectsCategorySettings", new XAttribute("EnableTransparency", "true"));
        AddStyles(document, ObjectStyle("Faded", null,
            new XElement("TransparencySetting", new XElement("BlendingSetting", new XAttribute("Opacity", "40"))), effects));
        var rectangle = document.Pages[0].AddRectangle(new Rect(0, 0, 100, 50));
        rectangle.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Faded");
        var resolver = new AttributeResolver(document);
        Assert.Equal("40", resolver.Blending(rectangle.Element, "Opacity"));

        rectangle.Element.Add(new XElement("TransparencySetting", new XElement("BlendingSetting", new XAttribute("Opacity", "75"))));
        Assert.Equal("75", resolver.Blending(rectangle.Element, "Opacity"));

        rectangle.Element.Element("TransparencySetting")!.Remove();
        effects.SetAttributeValue("EnableTransparency", "false");
        Assert.Null(resolver.Blending(rectangle.Element, "Opacity"));
    }

    [Fact]
    public void The_summary_lists_object_styles_and_the_style_of_each_item()
    {
        using var document = IdmlDocument.Create();
        AddStyles(document, ObjectStyle("Card", null, new XAttribute("FillColor", "Color/Yellow")));
        var card = document.Pages[0].AddRectangle(new Rect(0, 0, 100, 50), name: "card");
        card.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Card");
        document.Pages[0].AddText(new Rect(0, 100, 100, 50), "Text", name: "text");

        var summary = IdmlJson.Summarize(document);

        Assert.Equal(["[None]", "[Normal Graphics Frame]", "[Normal Text Frame]", "[Normal Grid]", "Card"], summary.ObjectStyles);
        var items = summary.Pages![0].Items;
        Assert.Equal("Card", items.Single(item => item.Name == "card").ObjectStyle);
        Assert.Null(items.Single(item => item.Name == "text").ObjectStyle);
    }
}
