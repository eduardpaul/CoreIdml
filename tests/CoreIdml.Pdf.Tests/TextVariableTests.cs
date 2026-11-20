using System.Xml.Linq;
using CoreIdml.Dom;
using CoreIdml.Pdf.Rendering;
using UglyToad.PdfPig;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Page markers and text variables, with the rules measured in InDesign 20's export of the doc-samples
/// 11-long-markers-variables package: jump lines, section markers, running headers and the last page number.
/// </summary>
public sealed class TextVariableTests
{
    private static IdmlDocument NewDocument(int pages) =>
        IdmlDocument.Create(new DocumentOptions { PageSize = PageSizes.A4, PageCount = pages });

    private static ParagraphStyle Body(IdmlDocument document) =>
        document.Styles.AddParagraphStyle("Body", new ParagraphFormat { FontFamily = "Liberation Sans", PointSize = 10 });

    /// <summary>Appends a run to the frame's (single) paragraph.</summary>
    private static void Append(TextFrame frame, XElement run) =>
        frame.Story.Element.Descendants("ParagraphStyleRange").Last().Add(run);

    private static XElement Marker(string code, string? pageNumberType = null) =>
        new("CharacterStyleRange",
            new XAttribute("AppliedCharacterStyle", "CharacterStyle/$ID/[No character style]"),
            pageNumberType is null ? null : new XAttribute("PageNumberType", pageNumberType),
            new XElement("Content", new XProcessingInstruction("ACE", code)));

    private static XElement Variable(string name, string resultText) =>
        new("CharacterStyleRange",
            new XAttribute("AppliedCharacterStyle", "CharacterStyle/$ID/[No character style]"),
            new XAttribute("PageNumberType", "TextVariable"),
            new XElement("TextVariableInstance",
                new XAttribute("Self", "tv" + name.GetHashCode(StringComparison.Ordinal).ToString("x", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("Name", name),
                new XAttribute("ResultText", resultText),
                new XAttribute("AssociatedTextVariable", "dTextVariablen" + name)));

    private static void Define(IdmlDocument document, string name, string type, XElement preference) =>
        document.DesignMap.Add(new XElement("TextVariable",
            new XAttribute("Self", "dTextVariablen" + name), new XAttribute("Name", name), new XAttribute("VariableType", type), preference));

    private static List<string> PageTexts(IdmlDocument document)
    {
        using var reader = PdfDocument.Open(TestSupport.Export(document));
        return reader.GetPages().Select(page => string.Join(" ", page.GetWords().Select(word => word.Text))).ToList();
    }

    [Fact]
    public void Jump_lines_print_the_pages_of_the_next_and_previous_frames_of_the_thread_they_touch()
    {
        // As in the sample: a story threaded over pages 1 and 3, jump-line frames overlapping its frames by 1 pt.
        using var document = NewDocument(3);
        var style = Body(document);
        var first = document.Pages[0].AddTextFrame(new Rect(50, 50, 400, 200));
        var last = document.Pages[2].AddTextFrame(new Rect(50, 50, 400, 200));
        first.ThreadTo(last);
        first.Story.AddParagraph("Story text.", style);
        var onward = document.Pages[0].AddText(new Rect(50, 249, 400, 20), "Continued on page ", style);
        Append(onward, Marker("18", "NextPageNumber"));
        var from = document.Pages[2].AddText(new Rect(50, 30, 400, 21), "Continued from page ", style);
        Append(from, Marker("18", "PreviousPageNumber"));
        var alone = document.Pages[1].AddText(new Rect(50, 400, 400, 20), "Not touching: ", style);
        Append(alone, Marker("18", "NextPageNumber"));

        var pages = PageTexts(document);

        Assert.Contains("Continued on page 3", pages[0], StringComparison.Ordinal);
        Assert.Contains("Continued from page 1", pages[2], StringComparison.Ordinal);
        // No thread to follow: the current page, as before.
        Assert.Contains("Not touching: 2", pages[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_marker_prints_the_pages_section_marker()
    {
        using var document = NewDocument(1);
        document.DesignMap.Elements("Section").Single().SetAttributeValue("Marker", "Part A");
        var frame = document.Pages[0].AddText(new Rect(50, 50, 400, 40), "Section: ", Body(document));
        Append(frame, Marker("19"));

        Assert.Contains("Section: Part A", PageTexts(document)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Running_headers_on_a_master_follow_the_pages_and_repeat_the_last_one()
    {
        using var document = NewDocument(4);
        var body = Body(document);
        var heading = document.Styles.AddParagraphStyle("Heading", new ParagraphFormat { FontFamily = "Liberation Sans", PointSize = 14 });
        Define(document, "First", "MatchParagraphStyleType", new XElement("MatchParagraphStylePreference",
            new XAttribute("TextBefore", "first: "), new XAttribute("TextAfter", ""), new XAttribute("AppliedParagraphStyle", heading.Reference),
            new XAttribute("SearchStrategy", "FirstOnPage"), new XAttribute("ChangeCase", "None"), new XAttribute("DeleteEndPunctuation", "false")));
        Define(document, "Last", "MatchParagraphStyleType", new XElement("MatchParagraphStylePreference",
            new XAttribute("TextBefore", "last: "), new XAttribute("TextAfter", "."), new XAttribute("AppliedParagraphStyle", heading.Reference),
            new XAttribute("SearchStrategy", "LastOnPage"), new XAttribute("ChangeCase", "UpperCase"), new XAttribute("DeleteEndPunctuation", "true")));
        var master = document.MasterSpreads[0].Pages[0];
        var header = master.AddText(new Rect(50, 20, 450, 20), "Head ", body);
        Append(header, Variable("First", "first: <First>"));
        Append(header, new XElement("CharacterStyleRange", new XElement("Content", " ")));
        Append(header, Variable("Last", "last: <Last>"));
        // Page 1: nothing yet; page 2: two headings; page 3: none; page 4: one.
        document.Pages[0].AddText(new Rect(50, 100, 400, 100), "Plain text.", body);
        var second = document.Pages[1].AddTextFrame(new Rect(50, 100, 400, 200));
        second.Story.AddParagraph("Alpha", heading);
        second.Story.AddParagraph("Text.", body);
        second.Story.AddParagraph("Beta!", heading);
        document.Pages[3].AddTextFrame(new Rect(50, 100, 400, 200)).Story.AddParagraph("Delta", heading);

        var pages = PageTexts(document);

        Assert.DoesNotContain("first:", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("<First>", pages[0], StringComparison.Ordinal);
        Assert.Contains("Head first: Alpha last: BETA.", pages[1], StringComparison.Ordinal);
        Assert.Contains("Head first: Alpha last: BETA.", pages[2], StringComparison.Ordinal);
        Assert.Contains("Head first: Delta last: DELTA.", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_page_number_counts_the_document_or_the_section()
    {
        using var document = NewDocument(3);
        var section = document.DesignMap.Elements("Section").Single();
        // A second section from page 3, numbered in lower roman.
        section.AddAfterSelf(new XElement(section.Name, section.Attributes().Where(a => a.Name != "Self" && a.Name != "PageStart"),
            new XAttribute("Self", "usec2"), new XAttribute("PageStart", document.Pages[2].Id)));
        document.Pages[2].Element.SetAttributeValue("Name", "i");
        Define(document, "Doc", "LastPageNumberType", new XElement("PageNumberVariablePreference",
            new XAttribute("TextBefore", "doc "), new XAttribute("Format", "Current"), new XAttribute("TextAfter", ""), new XAttribute("Scope", "DocumentScope")));
        Define(document, "Sec", "LastPageNumberType", new XElement("PageNumberVariablePreference",
            new XAttribute("TextBefore", "section "), new XAttribute("Format", "Current"), new XAttribute("TextAfter", ""), new XAttribute("Scope", "SectionScope")));
        var footer = document.MasterSpreads[0].Pages[0].AddText(new Rect(50, 780, 450, 20), "Page ", Body(document));
        Append(footer, Marker("18"));
        Append(footer, new XElement("CharacterStyleRange", new XElement("Content", " of ")));
        Append(footer, Variable("Doc", "1"));
        Append(footer, new XElement("CharacterStyleRange", new XElement("Content", ", ")));
        Append(footer, Variable("Sec", "1"));

        var pages = PageTexts(document);

        Assert.Contains("Page 1 of doc i, section 2", pages[0], StringComparison.Ordinal);
        Assert.Contains("Page 2 of doc i, section 2", pages[1], StringComparison.Ordinal);
        Assert.Contains("Page i of doc i, section i", pages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Other_variables_print_their_stored_text_and_never_break_across_lines()
    {
        using var document = NewDocument(1);
        var frame = document.Pages[0].AddText(new Rect(50, 50, 150, 100), "Made on ", Body(document));
        Append(frame, Variable("Created", "Created 20 November 2026"));

        var lines = new IdmlRenderer(document, TestSupport.ExportOptions()).LayoutStory(frame.Story).Lines;

        Assert.Equal(["Made on", "Created 20 November 2026"], lines.Select(line => line.Text));
    }
}
