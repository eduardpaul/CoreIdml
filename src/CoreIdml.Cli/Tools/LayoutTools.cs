using System.Text.Json.Nodes;
using CoreIdml.Dom;
using CoreIdml.Json;

namespace CoreIdml.Cli.Tools;

/// <summary>Layout edits: items (move, resize, layer, delete, duplicate), pages (add, delete, move), styles, colours, tables.</summary>
internal static class LayoutTools
{
    private const string ItemProperty = """ "itemId":{"type":"string","description":"Page item id (Self) from idml_summarize or idml_find; or pass name"},"name":{"type":"string","description":"Item name or script label, instead of itemId"}""";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new()
        {
            Name = "idml_edit_item",
            Title = "Move, resize or relayer an item",
            Description = "Change a page item's geometry (page coordinates in points: x, y, width, height; any you leave out keep their value), move it to another page (pageIndex, same position), move it to another layer, rename it or lock/hide it. Rectangles, ovals and text frames can be resized; other items (groups, lines, polygons) can only be moved. Returns the new bounds.",
            InputSchema = Schema.Edit(ItemProperty + "," +
                """ "x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number","exclusiveMinimum":0},"height":{"type":"number","exclusiveMinimum":0},""" +
                """ "pageIndex":{"type":"integer","minimum":0,"description":"Move to this page (0-based), keeping the position on the page"},""" +
                """ "layer":{"type":"string","description":"Name of the layer to move the item to"},"createLayer":{"type":"boolean","description":"Create the layer when it does not exist"},""" +
                """ "newName":{"type":"string","description":"Rename the item"},"visible":{"type":"boolean"},"locked":{"type":"boolean"}"""),
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
                var bounds = item.Bounds;
                var target = new Rect(args.OptionalDouble("x") ?? bounds.X, args.OptionalDouble("y") ?? bounds.Y, args.OptionalDouble("width") ?? bounds.Width, args.OptionalDouble("height") ?? bounds.Height);
                if (target.Width <= 0 || target.Height <= 0)
                {
                    throw ToolException.Invalid("width and height must be positive.");
                }
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
                return Describe(RequireItem(document, args, item.Id));
            },
        },
        new()
        {
            Name = "idml_delete_item",
            Title = "Delete an item",
            Description = "Remove a page item. A text frame's story goes too unless another frame still shows it (threads are joined); XML tags bound to it are unbound.",
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
            Description = "Copy a top-level page item (with its own copy of the story for text frames, unthreaded and untagged), on the same or another page, optionally offset. Returns the new item's id and bounds.",
            InputSchema = Schema.Edit(ItemProperty + "," +
                """ "pageIndex":{"type":"integer","minimum":0,"description":"Page for the copy (0-based); default the item's page"},"offsetX":{"type":"number"},"offsetY":{"type":"number"}"""),
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
            Description = "Insert a new empty page (with the first master) at a 0-based position; default at the end. Later pages move after it, with their items.",
            InputSchema = Schema.Edit(""" "at":{"type":"integer","minimum":0,"description":"Position of the new page; default after the last page"}"""),
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
            Description = "Delete a page (0-based) and the items on it; later pages move up with their items, page numbers and sections follow.",
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
            Description = "Move page 'from' to position 'to' (0-based), or give the whole new 'order' (every current page index once). Items move with their pages; masters, threads and sections stay consistent.",
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
            Description = "Apply a paragraph style to body paragraph paragraphIndex (see idml_get_story with includeParagraphs), or a character style to content run runIndex, of a story. Local overrides are kept, like InDesign's default.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"paragraphIndex":{"type":"integer","minimum":0},"runIndex":{"type":"integer","minimum":0},""" +
                """ "paragraphStyle":{"type":"string","description":"Paragraph style name (with paragraphIndex)"},"characterStyle":{"type":"string","description":"Character style name (with runIndex); [None] removes it"}""",
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
            Description = "Create a paragraph or character style, or change an existing one's format (same fields as the JSON spec's styles: font, fontStyle, size, leading, color, tint, align, spaceBefore, spaceAfter, firstLineIndent, leftIndent, rightIndent, underline, strikeThrough, tracking, caps, position, basedOn). Only the fields you give change.",
            InputSchema = Schema.Edit(
                """ "kind":{"type":"string","enum":["paragraph","character"]},"name":{"type":"string"},"basedOn":{"type":"string"},""" +
                """ "font":{"type":"string"},"fontStyle":{"type":"string"},"size":{"type":"number"},"leading":{"type":"number"},"color":{"type":"string","description":"Swatch name"},"tint":{"type":"number"},""" +
                """ "align":{"type":"string","enum":["left","center","right","justify","justify-all"]},"spaceBefore":{"type":"number"},"spaceAfter":{"type":"number"},""" +
                """ "firstLineIndent":{"type":"number"},"leftIndent":{"type":"number"},"rightIndent":{"type":"number"},"underline":{"type":"boolean"},"strikeThrough":{"type":"boolean"},""" +
                """ "tracking":{"type":"number"},"caps":{"type":"string"},"position":{"type":"string"}""",
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
                var spec = new JsonObject { [kind == "paragraph" ? "paragraphStyles" : "characterStyles"] = new JsonArray(style) };
                IdmlJson.Apply(edit.Document, JsonText.Write(spec));
                return JsonText.Ok;
            },
        },
        new()
        {
            Name = "idml_add_color",
            Title = "Add a colour",
            Description = "Add a process colour swatch (or update the one with that name): cmyk [c,m,y,k] in percent, rgb [r,g,b] 0-255, or hex \"#RRGGBB\".",
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
                IdmlJson.Apply(edit.Document, JsonText.Write(new JsonObject { ["colors"] = new JsonArray(color) }));
                return JsonText.Ok;
            },
        },
        new()
        {
            Name = "idml_edit_table",
            Title = "Edit a table",
            Description = "Edit a table in an existing story: operations run in order, each one of {op:\"setCell\",row,column,text}, {op:\"setRow\",row,values:[...]}, {op:\"addRow\",values:[...]}, {op:\"setColumnWidth\",column,width}, {op:\"setRowHeight\",row,height}. Rows and columns are 0-based; widths in points. Returns the table's size.",
            InputSchema = Schema.Edit(
                """ "storyId":{"type":"string"},"tableIndex":{"type":"integer","minimum":0,"description":"Which table of the story; default 0"},""" +
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

    private const string BoundsSchema = """{"type":"object","properties":{"id":{"type":"string"},"kind":{"type":"string"},"name":{"type":"string"},"pageIndex":{"type":"integer"},"x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number"},"height":{"type":"number"},"layer":{"type":"string"}},"required":["id","kind","x","y","width","height"]}""";

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

    internal static string Describe(PageItem item)
    {
        var bounds = item.Bounds;
        var result = new JsonObject
        {
            ["id"] = item.Id,
            ["kind"] = item.Kind,
            ["name"] = item.Name,
            ["pageIndex"] = item.Page?.Index,
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
