using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>Layout edits: items (move, resize, layer, delete, duplicate), pages (add, delete, move), styles, colours, tables.</summary>
internal static class LayoutTools
{
    private const string ItemProperty = """ "itemId":{"type":"string","description":"From idml_summarize or idml_find"},"name":{"type":"string","description":"Name or script label, instead of itemId"}""";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new()
        {
            Name = "idml_edit_item",
            Title = "Edit an item",
            Description = "Change a page item: geometry (x, y, width, height in pt, page coordinates; omitted ones keep their value), look, page (same position), layer, name, visibility, lock. columns, columnGutter, inset, verticalAlign: text frames only. Only rectangles, ovals and text frames resize; other items only move. Returns the new bounds.",
            InputSchema = Schema.Edit(ItemProperty + "," +
                """ "x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number","exclusiveMinimum":0},"height":{"type":"number","exclusiveMinimum":0},""" +
                """ "pageIndex":{"type":"integer","minimum":0,"description":"Move to this page"},""" +
                """ "layer":{"type":"string","description":"Move to this layer"},"createLayer":{"type":"boolean","description":"Create the layer if missing"},""" +
                """ "newName":{"type":"string"},"visible":{"type":"boolean"},"locked":{"type":"boolean"},""" + LookProperties),
            OutputSchema = BoundsSchema,
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var (document, args) = (edit.Document, edit.Args);
                var item = RequireItem(document, args);
                if (args.OptionalInt("pageIndex") is { } pageIndex && item.Page?.Id != RequirePage(document, pageIndex).Id)
                {
                    document.MovePageItem(item, 0, 0, RequirePage(document, pageIndex));
                    item = RequireItem(document, args);
                }
                // Only sizes the caller gives must be positive: a horizontal or vertical line is 0 high or wide, and
                // keeps that size when it is only moved or restyled.
                var width = args.OptionalDouble("width");
                var height = args.OptionalDouble("height");
                if (width is <= 0 || height is <= 0)
                {
                    throw ToolException.Invalid("width and height must be positive.");
                }
                var bounds = item.Bounds;
                var target = new Rect(args.OptionalDouble("x") ?? bounds.X, args.OptionalDouble("y") ?? bounds.Y, width ?? bounds.Width, height ?? bounds.Height);
                if (Math.Abs(target.Width - bounds.Width) > 1e-6 || Math.Abs(target.Height - bounds.Height) > 1e-6)
                {
                    item.SetBounds(target);
                }
                else if (Math.Abs(target.X - bounds.X) > 1e-6 || Math.Abs(target.Y - bounds.Y) > 1e-6)
                {
                    document.MovePageItem(item, target.X - bounds.X, target.Y - bounds.Y);
                }
                if (args.OptionalString("layer") is { } layerName)
                {
                    var layer = document.Layers.Find(layerName)
                        ?? (args.Bool("createLayer") ? document.Layers.Add(layerName) : throw ToolException.NotFound($"No layer '{layerName}' (layers: {string.Join(", ", document.Layers.All.Select(l => l.Name))}); pass createLayer: true to create it."));
                    item.MoveToLayer(layer);
                }
                if (args.OptionalString("newName") is { } newName)
                {
                    item.Name = newName;
                }
                if (args.OptionalBool("visible") is { } visible)
                {
                    item.Visible = visible;
                }
                if (args.OptionalBool("locked") is { } locked)
                {
                    item.Locked = locked;
                }
                ApplyLook(document, item, args);
                return Describe(RequireItem(document, args, item.Id));
            },
        },
        new()
        {
            Name = "idml_delete_item",
            Title = "Delete an item",
            Description = "Remove a page item. A text frame's story goes too unless another frame shows it (threads rejoined); bound XML tags are unbound.",
            InputSchema = Schema.Edit(ItemProperty),
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var item = RequireItem(edit.Document, edit.Args);
                item.Remove();
                return $$"""{"deleted":{{JsonText.String(item.Id)}}}""";
            },
        },
        new()
        {
            Name = "idml_duplicate_item",
            Title = "Duplicate an item",
            Description = "Copy a top-level page item (text frames get their own unthreaded, untagged story) to the same or another page, optionally offset. Returns the new id and bounds.",
            InputSchema = Schema.Edit(ItemProperty + "," +
                """ "pageIndex":{"type":"integer","minimum":0,"description":"Default the item's page"},"offsetX":{"type":"number"},"offsetY":{"type":"number"}"""),
            OutputSchema = BoundsSchema,
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var item = RequireItem(edit.Document, edit.Args);
                var page = edit.Args.OptionalInt("pageIndex") is { } index ? RequirePage(edit.Document, index) : null;
                var copy = edit.Document.DuplicatePageItem(item, page, edit.Args.OptionalDouble("offsetX") ?? 0, edit.Args.OptionalDouble("offsetY") ?? 0);
                return Describe(copy);
            },
        },
        new()
        {
            Name = "idml_add_page",
            Title = "Add a page",
            Description = "Insert an empty page (first master) at 0-based position 'at'; default at the end. Later pages shift with their items.",
            InputSchema = Schema.Edit(""" "at":{"type":"integer","minimum":0}"""),
            OutputSchema = PageSchema,
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var at = edit.Args.OptionalInt("at") ?? edit.Document.Pages.Count;
                var page = Checked(() => edit.Document.InsertPage(at));
                return PageResult(edit.Document, page.Index);
            },
        },
        new()
        {
            Name = "idml_delete_page",
            Title = "Delete a page",
            Description = "Delete a page (0-based) and its items; later pages, page numbers and sections follow.",
            InputSchema = Schema.Edit(""" "pageIndex":{"type":"integer","minimum":0}""", "pageIndex"),
            OutputSchema = PageSchema,
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var index = edit.Args.Int("pageIndex");
                RequirePage(edit.Document, index);
                Checked(() => edit.Document.DeletePage(index));
                return PageResult(edit.Document, null);
            },
        },
        new()
        {
            Name = "idml_move_page",
            Title = "Move or reorder pages",
            Description = "Move page 'from' to position 'to' (0-based), or give the full new 'order' (every current index once). Items, masters, threads and sections follow.",
            InputSchema = Schema.Edit(""" "from":{"type":"integer","minimum":0},"to":{"type":"integer","minimum":0},"order":{"type":"array","items":{"type":"integer","minimum":0}}"""),
            OutputSchema = PageSchema,
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                if (edit.Args.Has("order"))
                {
                    Checked(() => edit.Document.ReorderPages(edit.Args.Ints("order")));
                    return PageResult(edit.Document, null);
                }
                var (from, to) = (edit.Args.Int("from"), edit.Args.Int("to"));
                RequirePage(edit.Document, from);
                RequirePage(edit.Document, to);
                Checked(() => edit.Document.MovePage(from, to));
                return PageResult(edit.Document, to);
            },
        },
        new()
        {
            Name = "idml_apply_style",
            Title = "Apply a style to text",
            Description = "Apply a paragraph style to body paragraph paragraphIndex (idml_get_story includeParagraphs), or a character style to content run runIndex. Local overrides are kept.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"paragraphIndex":{"type":"integer","minimum":0},"runIndex":{"type":"integer","minimum":0},""" +
                """ "paragraphStyle":{"type":"string","description":"With paragraphIndex"},"characterStyle":{"type":"string","description":"With runIndex; [None] removes"}""",
                "storyId"),
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var (document, args) = (edit.Document, edit.Args);
                var story = DocumentTools.RequireStory(document, args.String("storyId"));
                if (args.OptionalString("paragraphStyle") is { } paragraphStyle)
                {
                    var style = document.Styles.FindParagraphStyle(paragraphStyle)
                        ?? throw ToolException.NotFound($"No paragraph style '{paragraphStyle}' (styles: {string.Join(", ", document.Styles.ParagraphStyles.Select(s => s.DisplayName))}); create it with idml_define_style.");
                    Checked(() => story.ApplyParagraphStyle(args.Int("paragraphIndex"), style));
                }
                else if (args.OptionalString("characterStyle") is { } characterStyle)
                {
                    var style = document.Styles.FindCharacterStyle(characterStyle)
                        ?? throw ToolException.NotFound($"No character style '{characterStyle}' (styles: {string.Join(", ", document.Styles.CharacterStyles.Select(s => s.DisplayName))}); create it with idml_define_style.");
                    Checked(() => story.ApplyCharacterStyle(args.Int("runIndex"), style));
                }
                else
                {
                    throw ToolException.Invalid("Pass paragraphStyle with paragraphIndex, or characterStyle with runIndex.");
                }
                return JsonText.Ok;
            },
        },
        new()
        {
            Name = "idml_define_style",
            Title = "Create or edit a style",
            Description = "Create a paragraph or character style, or update one by name; only the fields given change. Fields as in the spec's styles (size, leading, spacing, indents in pt).",
            InputSchema = Schema.Edit(
                """ "kind":{"type":"string","enum":["paragraph","character"]},"name":{"type":"string"},"basedOn":{"type":"string"},""" +
                """ "font":{"type":"string"},"fontStyle":{"type":"string"},"size":{"type":"number"},"leading":{"type":"number"},"color":{"type":"string","description":"Swatch name"},"tint":{"type":"number"},""" +
                """ "align":{"type":"string","enum":["left","center","right","justify","justifyAll"]},"spaceBefore":{"type":"number"},"spaceAfter":{"type":"number"},""" +
                """ "firstLineIndent":{"type":"number"},"leftIndent":{"type":"number"},"rightIndent":{"type":"number"},"underline":{"type":"boolean"},"strikeThrough":{"type":"boolean"},""" +
                """ "tracking":{"type":"number"},"caps":{"type":"string","enum":["normal","allCaps","smallCaps","capToSmallCap"]},"position":{"type":"string","enum":["normal","superscript","subscript"]},""" +
                """ "list":{"type":"string","enum":["bullet","numbered","none"],"description":"Paragraph styles. InDesign draws the bullet or number. Hanging: leftIndent 18, firstLineIndent -18"},"bullet":{"type":"string","description":"Default •"},"bulletAfter":{"type":"string","description":"Default a tab; ^m em space"},""" +
                """ "numberFormat":{"type":"string","enum":["1","01","001","0001","a","A","i","I"]},"numberText":{"type":"string","description":"^# the number, ^1-^9 a level's number; default ^#.\\t"},"listLevel":{"type":"integer","minimum":1,"maximum":9},"startAt":{"type":"integer"}""",
                "kind", "name"),
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var kind = edit.Args.String("kind");
                if (kind is not ("paragraph" or "character"))
                {
                    throw ToolException.Invalid("Argument 'kind' must be \"paragraph\" or \"character\".");
                }
                var style = JsonNode.Parse(edit.Args.Json.GetRawText())!.AsObject();
                foreach (var key in (string[])["path", "outPath", "inPlace", "dryRun", "kind"])
                {
                    style.Remove(key);
                }
                var spec = JsonText.Write(new JsonObject { [kind == "paragraph" ? "paragraphStyles" : "characterStyles"] = new JsonArray(style) });
                IdmlJson.Apply(edit.Document, spec);
                return WithWarnings(spec);
            },
        },
        new()
        {
            Name = "idml_add_color",
            Title = "Add a colour",
            Description = "Add or update (by name) a process colour swatch: cmyk [c,m,y,k] percent, rgb [r,g,b] 0-255, or hex \"#RRGGBB\".",
            InputSchema = Schema.Edit(
                """ "name":{"type":"string"},"cmyk":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4},"rgb":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3},"hex":{"type":"string"}""",
                "name"),
            Access = ToolAccess.Edit,
            Idempotent = true,
            Apply = edit =>
            {
                var color = JsonNode.Parse(edit.Args.Json.GetRawText())!.AsObject();
                foreach (var key in (string[])["path", "outPath", "inPlace", "dryRun"])
                {
                    color.Remove(key);
                }
                var spec = JsonText.Write(new JsonObject { ["colors"] = new JsonArray(color) });
                IdmlJson.Apply(edit.Document, spec);
                return WithWarnings(spec);
            },
        },
        new()
        {
            Name = "idml_edit_table",
            Title = "Edit a table",
            Description = "Edit a story's table. Operations run in order: {op:\"setCell\",row,column,text}, {op:\"setRow\",row,values}, {op:\"addRow\",values}, {op:\"setColumnWidth\",column,width}, {op:\"setRowHeight\",row,height}. 0-based; sizes in pt. Returns the table's size.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"tableIndex":{"type":"integer","minimum":0,"description":"Default 0"},""" +
                """ "operations":{"type":"array","items":{"type":"object","properties":{"op":{"type":"string","enum":["setCell","setRow","addRow","setColumnWidth","setRowHeight"]},"row":{"type":"integer"},"column":{"type":"integer"},"text":{"type":"string"},"values":{"type":"array","items":{"type":"string"}},"width":{"type":"number"},"height":{"type":"number"}},"required":["op"]}}""",
                "storyId", "operations"),
            Access = ToolAccess.Edit,
            Apply = edit =>
            {
                var story = DocumentTools.RequireStory(edit.Document, edit.Args.String("storyId"));
                var tables = story.Tables;
                var index = edit.Args.OptionalInt("tableIndex") ?? 0;
                if (index < 0 || index >= tables.Count)
                {
                    throw ToolException.NotFound($"Story {story.Id} has {tables.Count} table(s); table {index} does not exist.");
                }
                var table = tables[index];
                foreach (var operation in edit.Args.Array("operations"))
                {
                    TableOperation(table, edit.Args.With(operation));
                }
                return JsonText.Write(new JsonObject
                {
                    ["rows"] = table.RowCount,
                    ["columns"] = table.ColumnCount,
                    ["columnWidths"] = new JsonArray([.. table.ColumnWidths.Select(width => (JsonNode)Math.Round(width, 3))]),
                });
            },
        },
    ];

    internal const string LookProperties =
        """ "fill":{"type":"string","description":"Swatch; None removes"},"fillTint":{"type":"number","minimum":0,"maximum":100},""" +
        """ "stroke":{"type":"string","description":"Swatch; None removes"},"strokeWeight":{"type":"number","minimum":0},""" +
        """ "strokeTint":{"type":"number","minimum":0,"maximum":100},"strokeType":{"type":"string","enum":["solid","dashed","dotted"]},""" +
        """ "endCap":{"type":"string","enum":["butt","round","projecting"]},"cornerRadius":{"type":"number","minimum":0},""" +
        """ "opacity":{"type":"number","minimum":0,"maximum":100},"rotateBy":{"type":"number","description":"Degrees counter-clockwise, around the centre"},""" +
        """ "columns":{"type":"integer","minimum":1},"columnGutter":{"type":"number","minimum":0},""" +
        """ "inset":{"type":"object","description":"Sides not given fall back to all, then keep their value","properties":{"all":{"type":"number"},"top":{"type":"number"},"right":{"type":"number"},"bottom":{"type":"number"},"left":{"type":"number"}}},""" +
        """ "verticalAlign":{"type":"string","enum":["top","center","bottom","justify"]}""";

    /// <summary>The look arguments of idml_edit_item (see <see cref="LookProperties"/>) applied to an item.</summary>
    internal static void ApplyLook(IdmlDocument document, PageItem item, ToolArguments args)
    {
        Swatch? SwatchArgument(string name) => args.OptionalString(name) is { } swatchName ? RequireSwatch(document, swatchName) : null;
        var style = new ShapeStyle
        {
            Fill = SwatchArgument("fill"),
            FillTint = args.OptionalDouble("fillTint"),
            Stroke = SwatchArgument("stroke"),
            StrokeWeight = args.OptionalDouble("strokeWeight"),
            StrokeTint = args.OptionalDouble("strokeTint"),
            StrokeKind = args.OptionalString("strokeType") is { } strokeType ? Choice<StrokeKind>(strokeType, "strokeType") : null,
            EndCap = args.OptionalString("endCap") is { } endCap ? Choice<LineCap>(endCap, "endCap") : null,
            CornerRadius = args.OptionalDouble("cornerRadius"),
            Opacity = args.OptionalDouble("opacity"),
            Rotation = args.OptionalDouble("rotateBy"),
        };
        item.ApplyStyle(style);

        var textArguments = (string[])["columns", "columnGutter", "inset", "verticalAlign"];
        if (!textArguments.Any(args.Has))
        {
            return;
        }
        if (item is not TextFrame frame)
        {
            throw ToolException.Invalid($"columns, columnGutter, inset and verticalAlign apply to text frames; {item.Id} is a {item.Kind}.");
        }
        if (args.OptionalInt("columns") is { } columns)
        {
            frame.Columns = columns >= 1 ? columns : throw ToolException.Invalid("columns must be 1 or more.");
        }
        if (args.OptionalDouble("columnGutter") is { } gutter)
        {
            frame.ColumnGutter = gutter;
        }
        if (args.OptionalObject("inset") is { } inset)
        {
            var current = frame.Inset;
            var all = inset.OptionalDouble("all");
            frame.Inset = new Spacing(
                inset.OptionalDouble("top") ?? all ?? current.Top,
                inset.OptionalDouble("right") ?? all ?? current.Right,
                inset.OptionalDouble("bottom") ?? all ?? current.Bottom,
                inset.OptionalDouble("left") ?? all ?? current.Left);
        }
        if (args.OptionalString("verticalAlign") is { } verticalAlign)
        {
            frame.VerticalJustification = Choice<VerticalJustification>(verticalAlign, "verticalAlign");
        }
    }

    /// <summary>{"ok":true}, with the warnings of a one-entry spec built from a tool's arguments when there are any.</summary>
    private static string WithWarnings(string spec)
    {
        var warnings = IdmlJson.CheckSpec(spec)
            .Select(warning => warning.Replace("paragraphStyles[0]", "arguments", StringComparison.Ordinal)
                .Replace("characterStyles[0]", "arguments", StringComparison.Ordinal)
                .Replace("colors[0]", "arguments", StringComparison.Ordinal))
            .ToList();
        return warnings.Count == 0 ? JsonText.Ok : JsonText.Write(new JsonObject { ["ok"] = true, ["warnings"] = JsonText.Array(warnings) });
    }

    internal static Swatch RequireSwatch(IdmlDocument document, string name) =>
        document.Swatches.Find(name)
            ?? throw ToolException.NotFound($"No swatch '{name}' (swatches: {string.Join(", ", document.Swatches.All.Select(swatch => swatch.Name).Where(swatchName => !swatchName.StartsWith("$ID/", StringComparison.Ordinal)))}). Add one with idml_add_color.");

    /// <summary>An enum value by name, ignoring case; anything else is an input error listing the names.</summary>
    internal static TEnum Choice<TEnum>(string value, string argument)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw ToolException.Invalid($"Unknown {argument} '{value}'. Use {string.Join(", ", Enum.GetNames<TEnum>().Select(name => char.ToLowerInvariant(name[0]) + name[1..]))}.");

    private const string BoundsSchema = """{"type":"object","properties":{"id":{"type":"string"},"type":{"type":"string"},"kind":{"type":"string"},"name":{"type":"string"},"pageIndex":{"type":"integer"},"pageName":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number"},"height":{"type":"number"},"layer":{"type":"string"}},"required":["id","kind","x","y","width","height"]}""";

    private const string PageSchema = """{"type":"object","properties":{"pageIndex":{"type":"integer"},"pageId":{"type":"string"},"pageCount":{"type":"integer"},"pages":{"type":"array","items":{"type":"object"}}},"required":["pageCount","pages"]}""";

    private static void TableOperation(Table table, ToolArguments operation)
    {
        var row = operation.OptionalInt("row");
        var column = operation.OptionalInt("column");
        void CheckRow(int value)
        {
            if (value < 0 || value >= table.RowCount)
            {
                throw ToolException.NotFound($"Row {value} does not exist (the table has {table.RowCount} rows).");
            }
        }
        void CheckColumn(int value)
        {
            if (value < 0 || value >= table.ColumnCount)
            {
                throw ToolException.NotFound($"Column {value} does not exist (the table has {table.ColumnCount} columns).");
            }
        }
        switch (operation.String("op"))
        {
            case "setCell":
                CheckRow(operation.Int("row"));
                CheckColumn(operation.Int("column"));
                table.Cell(row!.Value, column!.Value).SetText(operation.String("text"));
                break;
            case "setRow":
                CheckRow(operation.Int("row"));
                table.SetRow(row!.Value, [.. operation.Strings("values")]);
                break;
            case "addRow":
                table.AddRow([.. operation.Strings("values")]);
                break;
            case "setColumnWidth":
                CheckColumn(operation.Int("column"));
                table.SetColumnWidth(column!.Value, Positive(operation, "width"));
                break;
            case "setRowHeight":
                CheckRow(operation.Int("row"));
                table.SetRowHeight(row!.Value, Positive(operation, "height"));
                break;
            case var other:
                throw ToolException.Invalid($"Unknown table operation '{other}' (setCell, setRow, addRow, setColumnWidth, setRowHeight).");
        }
    }

    private static double Positive(ToolArguments args, string name) =>
        args.Double(name) is var value and > 0 ? value : throw ToolException.Invalid($"'{name}' must be positive.");

    /// <summary>The item named by itemId (Self) or name (name or script label).</summary>
    internal static PageItem RequireItem(IdmlDocument document, ToolArguments args, string? id = null)
    {
        id ??= args.OptionalString("itemId");
        if (id is not null)
        {
            return document.AllPageItems.FirstOrDefault(item => item.Id == id)
                ?? throw ToolException.NotFound($"No page item '{id}' on any spread (ids are listed by idml_summarize and idml_find).");
        }
        var name = args.OptionalString("name") ?? throw ToolException.Invalid("Pass itemId (or name) to choose the item.");
        var matches = document.FindPageItems(name).ToList();
        return matches.Count switch
        {
            0 => throw ToolException.NotFound($"No page item named or labelled '{name}'."),
            1 => matches[0],
            _ => throw ToolException.Invalid($"{matches.Count} items are named or labelled '{name}' ({string.Join(", ", matches.Select(item => item.Id))}); pass itemId."),
        };
    }

    internal static Page RequirePage(IdmlDocument document, int index)
    {
        var pages = document.Pages;
        return index >= 0 && index < pages.Count ? pages[index] : throw ToolException.NotFound($"Page index {index} does not exist (the document has {pages.Count} page(s), 0 to {pages.Count - 1}).");
    }

    /// <summary>Library refusals (an IdmlException from a check) are input errors here.</summary>
    private static T Checked<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (IdmlException exception)
        {
            throw ToolException.Invalid(exception.Message);
        }
    }

    private static void Checked(Action action) => Checked(() =>
    {
        action();
        return 0;
    });

    /// <summary>
    /// The item's type in the document spec's words (text, table, image, rectangle, ellipse, line), so results match
    /// what was asked for; other kinds (polygon, group, button…) as InDesign names them, lower-cased.
    /// </summary>
    internal static string SpecType(PageItem item) => item switch
    {
        TextFrame frame when HoldsOnlyTables(frame) => "table",
        TextFrame => "text",
        GraphicFrame { Graphic: not null } => "image",
        GraphicFrame when item.Kind == "Oval" => "ellipse",
        GraphicFrame when item.Kind == "Rectangle" => "rectangle",
        GraphicLine => "line",
        _ => char.ToLowerInvariant(item.Kind[0]) + item.Kind[1..],
    };

    /// <summary>A frame whose story is one or more tables and no text outside them (what a spec "table" makes).</summary>
    private static bool HoldsOnlyTables(TextFrame frame) =>
        frame.Document.FindStory(frame.StoryId)?.Element is { } story
        && story.Descendants("Table").Any()
        && story.Descendants("Content").All(content => content.Ancestors("Table").Any() || string.IsNullOrWhiteSpace(content.Value));

    /// <summary>
    /// Where a result says which page: pageIndex (0-based, as every tool's arguments count) and pageName (the page's
    /// name as InDesign shows it: "1", "iii", "A3"). Nothing for items off the pages.
    /// </summary>
    internal static void AddPage(JsonObject result, Page? page)
    {
        if (page is not null)
        {
            result["pageIndex"] = page.Index;
            result["pageName"] = page.Name;
        }
    }

    internal static string Describe(PageItem item)
    {
        var bounds = item.Bounds;
        var result = new JsonObject
        {
            ["id"] = item.Id,
            ["type"] = SpecType(item),
            ["kind"] = item.Kind,
            ["name"] = item.Name,
            ["pageIndex"] = item.Page?.Index,
            ["pageName"] = item.Page?.Name,
            ["x"] = Math.Round(bounds.X, 3),
            ["y"] = Math.Round(bounds.Y, 3),
            ["width"] = Math.Round(bounds.Width, 3),
            ["height"] = Math.Round(bounds.Height, 3),
            ["layer"] = item.Document.Layers.All.FirstOrDefault(layer => layer.Id == item.LayerId)?.Name,
        };
        foreach (var (key, value) in result.ToList())
        {
            if (value is null)
            {
                result.Remove(key);
            }
        }
        return JsonText.Write(result);
    }

    private static string PageResult(IdmlDocument document, int? pageIndex)
    {
        var pages = document.Pages;
        var result = new JsonObject();
        if (pageIndex is { } index)
        {
            result["pageIndex"] = index;
            result["pageId"] = pages[index].Id;
        }
        result["pageCount"] = pages.Count;
        result["pages"] = new JsonArray([.. pages.Select((page, i) => (JsonNode)new JsonObject { ["index"] = i, ["id"] = page.Id, ["name"] = page.Name })]);
        return JsonText.Write(result);
    }
}
