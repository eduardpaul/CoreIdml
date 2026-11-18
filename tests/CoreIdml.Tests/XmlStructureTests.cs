using System.Text;
using System.Xml.Linq;
using CoreIdml.Dom;

namespace CoreIdml.Tests;

/// <summary>SimpleIDML parity: stitched structure, paths, export and its import_xml regression tests (ported).</summary>
public class XmlStructureTests
{
    private const string FullArticle = "While oceanographer and documentarian <bold>Steve Zissou (Bill Murray) is working on his latest documentary at sea, his best friend Esteban du Plantier (Seymour Cassel)</bold> is eaten by a creature Zissou describes as a \"Jaguar shark.\" For his next project, Zissou is determined to document the shark's destruction.\n            The crew aboard Zissou's research vessel <italique>Belafonte</italique> includes <italique>Pelé dos Santos (Seu Jorge)</italique>, a safety expert and Brazilian musician who sings David Bowie songs in Portuguese, and Klaus Daimler (Willem Dafoe), the German second-in-command who viewed Zissou and Esteban as father figures";
    private const string Informations = "The Life Aquatic with Steve Zissou is an American comedy-drama film directed, written, and co-produced by Wes Anderson.";

    [Fact]
    public void The_structure_is_stitched_across_story_parts()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);

        var root = document.Tags.Root!;
        var module = Assert.Single(root.Children);

        Assert.Equal("module", module.Tag);
        Assert.Equal(["main_picture", "headline", "Story"], module.Children.Select(child => child.Tag));
        Assert.Equal(["article", "informations"], module.Children[2].Children.Select(child => child.Tag));
        Assert.Equal("/Root/module/Story/article", module.Children[2].Children[0].Path);
        Assert.Equal("THE HEADLINE HERE", module.Children[1].Text);
    }

    [Fact]
    public void Paths_select_like_xpath()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);

        Assert.Single(document.Tags.Select("/Root/module[1]/headline"));
        Assert.Single(document.Tags.Select("//informations"));
        Assert.Equal(3, document.Tags.Select("/Root/module/*").Count);
        Assert.Empty(document.Tags.Select("/Root/module[2]"));
        var error = Assert.Throws<IdmlException>(() => document.Tags.SelectSingle("/Root/nothing"));
        Assert.Contains("/Root/nothing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_xml_includes_text_attributes_and_image_links()
    {
        using var document = IdmlDocument.Open(TestFiles.Article);

        var xml = document.Tags.ExportXml();

        Assert.Equal("Root", xml.Name.LocalName);
        var module = xml.Element("module")!;
        Assert.EndsWith("default.jpg", module.Element("main_picture")!.Attribute("href")!.Value, StringComparison.Ordinal);
        Assert.Equal("THE HEADLINE HERE", module.Element("headline")!.Value);
        Assert.StartsWith("Lorem ipsum", module.Element("Story")!.Element("article")!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_xml_writes_text_attributes_links_and_mapped_inline_tags()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        var report = document.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml.xml"), at: "/Root/module[1]");

        Assert.Empty(report.Unmatched);
        Assert.Equal(3, report.Added); // bold, italique, italique
        AssertModule(document, picture: "file:../../IDML/media/bouboune.jpg", headline: "The Life Aquatic with Steve Zissou", article: FullArticle, informations: Informations);
        var bold = document.Tags.FindElements("bold").Single();
        Assert.Equal("CharacterStyle/bold", bold.ContentElement.Element("CharacterStyleRange")!.Attribute("AppliedCharacterStyle")!.Value);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Xml_with_windows_line_endings_imports_like_lf()
    {
        using var lf = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        using var crlf = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        var xml = TestFiles.Xml("article-1photo_import-xml.xml").Replace("\r\n", "\n", StringComparison.Ordinal);

        lf.Tags.ImportXml(xml, at: "/Root/module[1]");
        crlf.Tags.ImportXml(xml.Replace("\n", "\r\n", StringComparison.Ordinal), at: "/Root/module[1]");

        Assert.Equal(lf.Stories.Select(story => story.Text), crlf.Stories.Select(story => story.Text));
        Assert.DoesNotContain(crlf.Stories, story => story.Text.Contains('\r', StringComparison.Ordinal));
    }

    [Fact]
    public void Nested_mapped_tags_combine_their_styles()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        document.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-nested-tags.xml"), at: "/Root/module[1]");

        var article = Canon(document.Tags.ExportXml("/Root/module[1]/Story/article"));
        Assert.Contains("<bold>Steve Zissou (Bill Murray) is <sup>working</sup> on his latest documentary at sea, his best friend <italique>Esteban du Plantier</italique> (Seymour Cassel)</bold>", article, StringComparison.Ordinal);
        var sup = document.Tags.FindElements("sup").Single().ContentElement.Element("CharacterStyleRange")!;
        Assert.Equal("CharacterStyle/bold", sup.Attribute("AppliedCharacterStyle")!.Value);
        Assert.Equal("Superscript", sup.Attribute("Position")!.Value);
        var italicInBold = document.Tags.FindElements("italique")[0].ContentElement.Element("CharacterStyleRange")!;
        Assert.Equal("Bold Italic", italicInBold.Attribute("FontStyle")!.Value);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Setcontent_false_keeps_the_text()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        document.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-with-setcontent-false.xml"), at: "/Root/module[1]");

        AssertModule(document, picture: "file:../../IDML/media/bouboune.jpg", headline: "THE HEADLINE HERE", article: FullArticle, informations: Informations);
        // Control attributes are not stored in the document.
        Assert.Null(document.Tags.SelectSingle("/Root/module/headline").GetXmlAttribute("simpleidml-setcontent"));
    }

    [Fact]
    public void Setcontent_delete_removes_the_frame_and_its_tag()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        var frames = document.AllPageItems.Count();

        var report = document.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-with-setcontent-delete.xml"), at: "/Root/module[1]");

        Assert.Equal(1, report.Deleted);
        Assert.Equal(["headline", "Story"], document.Tags.SelectSingle("/Root/module").Children.Select(child => child.Tag));
        Assert.Equal(frames - 1, document.AllPageItems.Count());
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Setcontent_delete_on_inline_text_removes_it()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        document.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-with-setcontent-delete-informations.xml"), at: "/Root/module[1]");

        Assert.Equal(["article"], document.Tags.SelectSingle("/Root/module/Story").Children.Select(child => child.Tag));
        Assert.DoesNotContain("Lorem ipsum dolor sit amet, consectetur adipisicing elit, sed do eiusmod tempor incididunt.", document.Tags.SelectSingle("/Root/module/Story").Text, StringComparison.Ordinal);
        CreateDocumentTests.AssertValid(document);
    }

    [Fact]
    public void Ignorecontent_skips_a_subtree_unless_forced()
    {
        using var ignored = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        using var forced = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        ignored.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-ignorecontent.xml"), at: "/Root/module[1]");
        forced.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-forcecontent.xml"), at: "/Root/module[1]");

        Assert.Equal("The Life Aquatic with Steve Zissou", ignored.Tags.SelectSingle("/Root/module/headline").Text);
        Assert.StartsWith("Lorem ipsum", ignored.Tags.SelectSingle("/Root/module/Story/article").Text, StringComparison.Ordinal);
        Assert.StartsWith("Lorem ipsum", ignored.Tags.SelectSingle("/Root/module/Story/informations").Text, StringComparison.Ordinal);
        Assert.StartsWith("Lorem ipsum", forced.Tags.SelectSingle("/Root/module/Story/article").Text, StringComparison.Ordinal);
        Assert.Equal(Informations, forced.Tags.SelectSingle("/Root/module/Story/informations").Text);
    }

    [Fact]
    public void Remove_previous_br_joins_the_element_to_the_previous_paragraph()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        var story = document.Tags.SelectSingle("/Root/module/Story");
        var article = document.Tags.SelectSingle("/Root/module/Story/article").Text;
        Assert.StartsWith(article + "\n", story.Text, StringComparison.Ordinal);

        // The source leaves out main_picture, headline and article: matching looks ahead.
        document.Tags.ImportXml("""<module><Story><informations simpleidml-setcontent="remove-previous-br">Nota.</informations></Story></module>""", at: "/Root/module[1]");

        Assert.Equal(article + "Nota.", story.Text);
        CreateDocumentTests.AssertValid(document);

        // SimpleIDML's own fixture (delete + remove-previous-br) imports cleanly too.
        using var fixture = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        fixture.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-with-setcontent-remove-br.xml"), at: "/Root/module[1]");
        Assert.Equal(["article"], fixture.Tags.SelectSingle("/Root/module/Story").Children.Select(child => child.Tag));
        CreateDocumentTests.AssertValid(fixture);
    }

    [Fact]
    public void Href_places_readable_images_and_an_empty_href_removes_the_frame()
    {
        var photo = TestFiles.TempPath(".png");
        File.WriteAllBytes(photo, TestFiles.Png(400, 300));
        using var placed = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        using var removed = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        placed.Tags.ImportXml($"<module><main_picture href=\"{photo}\"/></module>", at: "/Root/module[1]");
        removed.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-without-picture.xml"), at: "/Root/module[1]");

        var frame = (GraphicFrame)placed.Tags.SelectSingle("/Root/module/main_picture").BoundPageItem!;
        Assert.EndsWith(".png", frame.LinkUri, StringComparison.Ordinal);
        Assert.Equal("$ID/Portable Network Graphics (PNG)", frame.Graphic!.Attribute("ImageTypeName")!.Value);
        Assert.Null(removed.Tags.SelectSingle("/Root/module/main_picture").BoundPageItem);
        CreateDocumentTests.AssertValid(placed);
        CreateDocumentTests.AssertValid(removed);
    }

    [Fact]
    public void Unmatched_unmapped_elements_are_reported()
    {
        using var document = IdmlDocument.Open(TestFiles.ImportXmlTemplate);

        var report = document.Tags.ImportXml("<module><headline>x</headline><unknown>y</unknown></module>", at: "/Root/module[1]");

        Assert.Equal(["/module/unknown"], report.Unmatched);
        Assert.Equal("x", document.Tags.SelectSingle("/Root/module/headline").Text);
    }

    [Fact]
    public void Export_then_import_round_trips()
    {
        using var source = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        source.Tags.ImportXml(TestFiles.Xml("article-1photo_import-xml-nested-tags.xml"), at: "/Root/module[1]");
        var exported = source.Tags.ExportXml("/Root/module[1]");

        using var target = IdmlDocument.Open(TestFiles.ImportXmlTemplate);
        target.Tags.ImportXml(exported, at: "/Root/module[1]");

        Assert.Equal(Canon(exported), Canon(target.Tags.ExportXml("/Root/module[1]")));
    }

    [Fact]
    public void Tags_map_to_character_styles_and_notes_attach_to_text()
    {
        using var document = IdmlDocument.Create();
        var bold = document.Styles.AddCharacterStyle("Negrita", new CharacterFormat { FontStyle = "Bold" });
        var frame = document.Pages[0].AddText(new Rect(0, 0, 300, 100), "x");
        document.Tags.TagPageItem(frame, "texto");
        document.Tags.MapToCharacterStyle("b", bold);

        using var reopened = IdmlDocument.Open(document.ToArray());
        reopened.Tags.ImportXml("<texto>Hola <b>mundo</b>!</texto>", at: "/Root/texto");
        reopened.Tags.SelectSingle("/Root/texto").AddNote("Revisar", "Ana");

        Assert.Equal("Hola mundo!", reopened.Tags.SelectSingle("/Root/texto").Text);
        Assert.Equal("CharacterStyle/Negrita", reopened.Tags.FindElements("b").Single().ContentElement.Element("CharacterStyleRange")!.Attribute("AppliedCharacterStyle")!.Value);
        Assert.Single(reopened.Stories.SelectMany(story => story.Element.Descendants("Note")));
        Assert.Equal("b", Assert.Single(reopened.Tags.CharacterStyleMappings).Key);
        CreateDocumentTests.AssertValid(reopened);
    }

    private static void AssertModule(IdmlDocument document, string picture, string headline, string article, string informations)
    {
        var module = document.Tags.ExportXml("/Root/module[1]");
        Assert.Equal(picture, module.Element("main_picture")!.Attribute("href")!.Value);
        Assert.Equal(headline, module.Element("headline")!.Value);
        Assert.Equal(article, InnerXml(module.Element("Story")!.Element("article")!));
        Assert.Equal(informations, module.Element("Story")!.Element("informations")!.Value);
    }

    /// <remarks>XNode.ToString writes "\n" in text as Environment.NewLine ("\r\n" on Windows): undone here.</remarks>
    private static string InnerXml(XElement element) => string.Concat(element.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)))
        .Replace(Environment.NewLine, "\n", StringComparison.Ordinal)
        .Replace("&#xD;", "\r", StringComparison.Ordinal);

    /// <summary>Elements, attributes (sorted) and text, ignoring whitespace-only text between elements.</summary>
    private static string Canon(XElement element)
    {
        var builder = new StringBuilder();
        void Write(XElement current)
        {
            builder.Append('<').Append(current.Name.LocalName);
            foreach (var attribute in current.Attributes().OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
            {
                builder.Append(' ').Append(attribute.Name.LocalName).Append("=\"").Append(attribute.Value).Append('"');
            }
            builder.Append('>');
            foreach (var node in current.Nodes())
            {
                if (node is XElement child)
                {
                    Write(child);
                }
                else if (node is XText text && !(current.HasElements && string.IsNullOrWhiteSpace(text.Value) && current.Nodes().All(n => n is XElement || string.IsNullOrWhiteSpace((n as XText)?.Value))))
                {
                    builder.Append(text.Value);
                }
            }
            builder.Append("</").Append(current.Name.LocalName).Append('>');
        }
        Write(element);
        return builder.ToString();
    }
}
