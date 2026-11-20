using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Rendering;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Transparency and effects written the way InDesign writes them (04-fx-opacity-blend, 04-fx-shadows-glows and
/// 04-fx-feathers-other reference PDFs): transparency groups with opacity and blend modes, luminosity soft masks of
/// rasterized shadows, glows and feathers, a shading for a gradient feather; and the effect rasters themselves.
/// </summary>
public sealed class EffectTests
{
    private static XElement Setting(string container, string setting, params (string Name, string Value)[] attributes) =>
        new(container, new XElement(setting, attributes.Select(a => new XAttribute(a.Name, a.Value))));

    private static GraphicFrame Rectangle(IdmlDocument document, Rect bounds, Swatch fill, params XElement[] settings)
    {
        var item = document.Pages[0].AddRectangle(bounds, new ShapeStyle { Fill = fill, Stroke = Swatch.None });
        foreach (var setting in settings)
        {
            item.Element.Add(setting);
        }
        return item;
    }

    private static IdmlDocument NewDocument() => IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4 });

    private static PdfDocument Open(IdmlDocument document, out PdfExportResult result)
    {
        using var output = new MemoryStream();
        result = IdmlPdf.Export(document, output, new PdfExportOptions { Fonts = TestSupport.Fonts(), CompressContent = false });
        return PdfDocument.Open(output.ToArray());
    }

    /// <summary>Every dictionary reachable from the page (resources, forms, masks), streams' dictionaries included.</summary>
    private static List<DictionaryToken> Reachable(PdfDocument pdf)
    {
        var found = new List<DictionaryToken>();
        var seen = new HashSet<IndirectReference>();
        void Visit(IToken? token)
        {
            switch (token)
            {
                case IndirectReferenceToken reference when seen.Add(reference.Data):
                    Visit(pdf.Structure.GetObject(reference.Data).Data);
                    break;
                case StreamToken stream:
                    Visit(stream.StreamDictionary);
                    break;
                case DictionaryToken dictionary:
                    found.Add(dictionary);
                    foreach (var (key, value) in dictionary.Data)
                    {
                        if (key is not ("Parent" or "Font"))
                        {
                            Visit(value);
                        }
                    }
                    break;
                case ArrayToken array:
                    foreach (var item in array.Data)
                    {
                        Visit(item);
                    }
                    break;
            }
        }
        Visit(pdf.GetPage(1).Dictionary);
        return found;
    }

    private static string? NameOf(DictionaryToken dictionary, string key) => dictionary.Data.TryGetValue(key, out var value) && value is NameToken name ? name.Data : null;

    private static double? NumberOf(DictionaryToken dictionary, string key) => dictionary.Data.TryGetValue(key, out var value) && value is NumericToken number ? number.Double : null;

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void Opacity_and_blend_mode_paint_the_item_as_a_transparency_group()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black,
            Setting("TransparencySetting", "BlendingSetting", ("Opacity", "50"), ("BlendMode", "Multiply")));

        using var pdf = Open(document, out _);
        var dictionaries = Reachable(pdf);

        // 04-fx-opacity-blend p1/p2: a form with /Group <</S /Transparency /CS /DeviceCMYK /I false /K false>>, painted
        // under /ca 0.5 /CA 0.5 /BM /Multiply; the page is a CMYK transparency group too.
        var state = Assert.Single(dictionaries, d => NameOf(d, "Type") == "ExtGState");
        Assert.Equal(0.5, NumberOf(state, "ca"));
        Assert.Equal(0.5, NumberOf(state, "CA"));
        Assert.Equal("Multiply", NameOf(state, "BM"));
        var group = Assert.Single(dictionaries, d => NameOf(d, "S") == "Transparency" && d.Data.ContainsKey("I"));
        Assert.Equal("DeviceCMYK", NameOf(group, "CS"));
        Assert.False(((BooleanToken)group.Data["I"]).Data);
        var pageGroup = (DictionaryToken)pdf.GetPage(1).Dictionary.Data["Group"];
        Assert.Equal("Transparency", NameOf(pageGroup, "S"));
        Assert.Equal("DeviceCMYK", NameOf(pageGroup, "CS"));
    }

    [Fact]
    public void A_page_without_transparency_is_not_a_group()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black);

        using var pdf = Open(document, out _);

        Assert.False(pdf.GetPage(1).Dictionary.Data.ContainsKey("Group"));
        Assert.DoesNotContain(Reachable(pdf), d => NameOf(d, "Type") == "ExtGState");
    }

    [Fact]
    public void A_translucent_group_is_flattened_so_its_children_do_not_show_through_each_other()
    {
        using var document = NewDocument();
        var red = Rectangle(document, new Rect(100, 100, 100, 100), document.Swatches.AddCmyk("Red", 0, 100, 100, 0));
        var blue = Rectangle(document, new Rect(150, 150, 100, 100), document.Swatches.AddCmyk("Blue", 100, 100, 0, 0));
        var group = new XElement("Group", new XAttribute("Self", "ug1"), new XAttribute("ItemTransform", "1 0 0 1 0 0"),
            Setting("TransparencySetting", "BlendingSetting", ("Opacity", "50")));
        red.Element.AddBeforeSelf(group);
        red.Element.Remove();
        blue.Element.Remove();
        group.Add(red.Element, blue.Element);

        using var pdf = Open(document, out _);

        // 04-fx-opacity-blend p5: one transparency group holds both children and is painted at 50%; the children
        // themselves are opaque, so where they overlap only the top one shows.
        var page = PageContent(pdf);
        Assert.Equal(1, Regex.Count(page, @"/Tg\d+ Do"));
        Assert.Contains("/GS1 gs", page, StringComparison.Ordinal);
        var form = FormContent(pdf, Regex.Match(page, @"/(Tg\d+) Do").Groups[1].Value);
        Assert.Equal(2, Regex.Count(form, @"\bf\b"));
        Assert.DoesNotContain(" gs", form, StringComparison.Ordinal);
        var state = Assert.Single(Reachable(pdf), d => NameOf(d, "Type") == "ExtGState");
        Assert.Equal(0.5, NumberOf(state, "ca"));
    }

    [Fact]
    public void Fill_opacity_applies_to_the_fill_alone()
    {
        using var document = NewDocument();
        var item = Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black,
            Setting("FillTransparencySetting", "BlendingSetting", ("Opacity", "40")));
        item.Element.SetAttributeValue("StrokeColor", "Color/Black");
        item.Element.SetAttributeValue("StrokeWeight", "10");

        using var pdf = Open(document, out _);

        // 04-fx-opacity-blend p4: the fill in its own group at 40%, then the stroke as it is.
        var page = PageContent(pdf);
        Assert.Matches(@"/Tg\d+ Do\s+Q[\s\S]*\bS\b", page);
        var form = FormContent(pdf, Regex.Match(page, @"/(Tg\d+) Do").Groups[1].Value);
        Assert.Contains("f", form, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bS\b", form);
        Assert.Equal(0.4, NumberOf(Assert.Single(Reachable(pdf), d => NameOf(d, "Type") == "ExtGState"), "ca"));
    }

    private static string PageContent(PdfDocument pdf) => Decode(pdf, pdf.GetPage(1).Dictionary.Data["Contents"]);

    private static string FormContent(PdfDocument pdf, string name)
    {
        var resources = (DictionaryToken)pdf.Structure.GetObject(((IndirectReferenceToken)pdf.GetPage(1).Dictionary.Data["Resources"]).Data).Data;
        var xobjects = (DictionaryToken)resources.Data["XObject"];
        return Decode(pdf, xobjects.Data[name]);
    }

    private static string Decode(PdfDocument pdf, IToken token)
    {
        var stream = (StreamToken)pdf.Structure.GetObject(((IndirectReferenceToken)token).Data).Data;
        return Encoding.Latin1.GetString(stream.Decode(UglyToad.PdfPig.Filters.DefaultFilterProvider.Instance).ToArray());
    }

    [Fact]
    public void A_drop_shadow_is_a_colour_seen_through_a_luminosity_mask_of_a_gray_image()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 60), Swatch.Black,
            Setting("TransparencySetting", "DropShadowSetting", ("Mode", "Drop")));

        using var pdf = Open(document, out _);
        var dictionaries = Reachable(pdf);

        // 04-fx-shadows-glows p1: the default shadow is Multiply at 75% through /SMask <</S /Luminosity /G form>>, the
        // form drawing a DeviceGray image.
        var state = Assert.Single(dictionaries, d => d.Data.ContainsKey("SMask"));
        Assert.Equal("Multiply", NameOf(state, "BM"));
        Assert.Equal(0.75, NumberOf(state, "ca"));
        var mask = Assert.Single(dictionaries, d => NameOf(d, "Type") == "Mask");
        Assert.Equal("Luminosity", NameOf(mask, "S"));
        var image = Assert.Single(dictionaries, d => NameOf(d, "Subtype") == "Image");
        Assert.Equal("DeviceGray", NameOf(image, "ColorSpace"));
    }

    [Fact]
    public void A_drop_shadow_falls_below_and_right_and_fades()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 60), document.Swatches.AddCmyk("Yellow", 0, 0, 100, 0),
            Setting("TransparencySetting", "DropShadowSetting", ("Mode", "Drop"), ("Opacity", "100"), ("XOffset", "10"), ("YOffset", "10"), ("Size", "4")));

        var image = Rasterizer.Render(TestSupport.Export(document), 0, 72);

        Assert.True(Pixel(image, 205, 165).R < 60, "The shadow's inside is dark.");
        Assert.True(Pixel(image, 95, 95).R > 250, "Nothing above and left of the item.");
        var edge = Pixel(image, 210, 140).R;
        Assert.InRange(edge, 60, 200);
    }

    [Fact]
    public void Shadows_come_from_the_object_style_unless_it_leaves_them_alone()
    {
        foreach (var enabled in new[] { true, false })
        {
            using var document = NewDocument();
            var style = new XElement("ObjectStyle",
                new XAttribute("Self", "ObjectStyle/Shadowed"), new XAttribute("Name", "Shadowed"),
                new XElement("Properties", new XElement("BasedOn", new XAttribute("type", "string"), "$ID/[None]")),
                Setting("TransparencySetting", "DropShadowSetting", ("Mode", "Drop"), ("Opacity", "60")),
                new XElement("ObjectStyleObjectEffectsCategorySettings", new XAttribute("EnableDropShadow", enabled ? "true" : "false")));
            document.Package.GetXml("Resources/Styles.xml").Root!.Element("RootObjectStyleGroup")!.Add(style);
            var item = Rectangle(document, new Rect(100, 100, 100, 60), Swatch.Black);
            item.Element.SetAttributeValue("AppliedObjectStyle", "ObjectStyle/Shadowed");

            var pdf = Text(TestSupport.Export(document));

            Assert.Equal(enabled, pdf.Contains("/ca 0.6 /CA 0.6 /BM /Multiply /SMask", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_basic_feather_masks_the_item_and_fades_its_edge()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black,
            Setting("TransparencySetting", "FeatherSetting", ("Mode", "Standard"), ("Width", "20"), ("CornerType", "Sharp")));

        var image = Rasterizer.Render(TestSupport.Export(document), 0, 72);

        // 1 − (1 − d / w)²: opaque past 20 pt in, about 0.44 (gray 143) 5 pt in, almost clear at the edge.
        Assert.True(Pixel(image, 150, 150).R < 50);
        Assert.InRange(Pixel(image, 105, 150).R, 120, 170);
        Assert.True(Pixel(image, 100, 150).R > 220);
    }

    [Fact]
    public void A_gradient_feather_is_a_gray_shading_soft_mask()
    {
        using var document = NewDocument();
        Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black,
            Setting("TransparencySetting", "GradientFeatherSetting", ("Applied", "true")));

        var bytes = TestSupport.Export(document);
        using var pdf = PdfDocument.Open(bytes);
        var shading = Assert.Single(Reachable(pdf), d => d.Data.ContainsKey("ShadingType"));
        Assert.Equal("DeviceGray", NameOf(shading, "ColorSpace"));
        Assert.Equal(2, NumberOf(shading, "ShadingType"));

        // 04-fx-feathers-other p4: opaque on the left, clear on the right.
        var image = Rasterizer.Render(bytes, 0, 72);
        Assert.True(Pixel(image, 105, 150).R < 70);
        Assert.InRange(Pixel(image, 150, 150).R, 100, 160);
        Assert.True(Pixel(image, 195, 150).R > 215);
    }

    [Fact]
    public void Bevel_and_satin_are_reported_as_not_drawn()
    {
        using var document = NewDocument();
        var item = Rectangle(document, new Rect(100, 100, 100, 100), Swatch.Black,
            new XElement("TransparencySetting",
                new XElement("BevelAndEmbossSetting", new XAttribute("Applied", "true")),
                new XElement("SatinSetting", new XAttribute("Applied", "true"))));

        using var output = new MemoryStream();
        var result = IdmlPdf.Export(document, output, TestSupport.ExportOptions());

        Assert.Contains(result.Warnings, w => w.Contains("bevel and emboss", StringComparison.Ordinal) && w.Contains(item.Id, StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("satin", StringComparison.Ordinal));
    }

    [Fact]
    public void Effect_masks_are_deterministic()
    {
        byte[] Export()
        {
            using var document = NewDocument();
            Rectangle(document, new Rect(100, 100, 100, 60), Swatch.Black,
                new XElement("TransparencySetting",
                    new XElement("DropShadowSetting", new XAttribute("Mode", "Drop"), new XAttribute("Spread", "30")),
                    new XElement("InnerGlowSetting", new XAttribute("Applied", "true")),
                    new XElement("FeatherSetting", new XAttribute("Mode", "Standard"))));
            return TestSupport.Export(document);
        }

        Assert.Equal(Export(), Export());
    }

    [Fact]
    public void A_blur_has_a_gaussian_edge_of_half_the_size()
    {
        // 04-fx-shadows-glows p1: the shadow mask is Φ(d / (size / 2)) across the shape's edge.
        var mask = new AlphaMask(0, 0, 200, 4, 1);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                mask.Alpha[(y * 200) + x] = 1;
            }
        }
        mask.Blur(10);

        Assert.Equal(0.5, mask.Alpha[100] + ((mask.Alpha[99] - mask.Alpha[100]) / 2), 2);
        Assert.Equal(0.159, mask.Alpha[110], 1);
        Assert.Equal(0.841, mask.Alpha[89], 1);
        Assert.True(mask.Alpha[140] < 0.01);
    }

    [Fact]
    public void Distances_are_euclidean()
    {
        var mask = new AlphaMask(0, 0, 20, 20, 1);
        var distances = mask.Distances(i => i == (5 * 20) + 5);

        Assert.Equal(0, distances[(5 * 20) + 5]);
        Assert.Equal(5, distances[(9 * 20) + 8], 4);
        Assert.Equal(Math.Sqrt(14 * 14 + 14 * 14), distances[(19 * 20) + 19], 3);
    }

    [Fact]
    public void A_polygon_covers_its_area_with_partial_edges()
    {
        var mask = new AlphaMask(0, 0, 10, 10, 1);
        mask.Draw(new EffectShape { Fills = { new FilledArea([[new Point(2.5, 2), new Point(7.5, 2), new Point(7.5, 6), new Point(2.5, 6)]], false) } });

        Assert.Equal(20, mask.Alpha.Sum(), 3);
        Assert.Equal(0.5, mask.Alpha[(3 * 10) + 2], 3);
        Assert.Equal(1, mask.Alpha[(3 * 10) + 4], 3);
        Assert.Equal(0, mask.Alpha[(7 * 10) + 4], 3);
    }

    private static (int R, int G, int B) Pixel(Imaging.RasterImage image, int x, int y)
    {
        var i = ((y * image.Width) + x) * image.Channels;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }
}
