using System.Text.Json;
using CoreIdml.Dom;

namespace CoreIdml.Json;

public static partial class IdmlJson
{
    private const string TextFrameOptionNames = "autoSize, autoSizeFrom, autoSizeMinHeight, balanceColumns, firstBaseline, firstBaselineMin and ignoreWrap";

    /// <summary>
    /// Applies the text wrap (any item) and the auto-size, balance, first baseline and ignore-wrap options (text frames)
    /// of a JSON <see cref="ItemSpec"/> to an existing item, as idml_edit_item does; other fields are ignored and lengths
    /// are in points. Options not given keep their value: wrapOffset sides fall back to <c>all</c>, then to the item's
    /// current offsets. Throws <see cref="IdmlException"/> for unknown values or text frame options on another item.
    /// </summary>
    public static void ApplyItemOptions(IdmlDocument document, PageItem item, string json)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);
        var spec = JsonSerializer.Deserialize(json, IdmlJsonContext.Default.ItemSpec) ?? new ItemSpec();
        if (ItemOptionsError(spec) is { } error)
        {
            throw new IdmlException(error);
        }
        ApplyItemOptions(item, spec, unit: null);
    }

    private static bool HasTextFrameOptions(ItemSpec spec) =>
        spec.AutoSize is not null || spec.AutoSizeFrom is not null || spec.AutoSizeMinHeight is not null || spec.BalanceColumns is not null
        || spec.FirstBaseline is not null || spec.FirstBaselineMin is not null || spec.IgnoreWrap is not null;

    private static void ApplyItemOptions(PageItem item, ItemSpec spec, string? unit)
    {
        double U(double value) => Units.FromUnit(value, unit);
        if (HasTextFrameOptions(spec))
        {
            var frame = item as TextFrame ?? throw new IdmlException($"{TextFrameOptionNames} apply to text frames; {item.Id} is a {item.Kind}.");
            if (spec.AutoSize is { } autoSize)
            {
                var type = TryAutoSizing(autoSize)!.Value;
                frame.AutoSizing = type;
                if (type != AutoSizing.Off || spec.AutoSizeFrom is not null)
                {
                    // A frame that grows down keeps its top; one that grows sideways its left edge.
                    frame.AutoSizingReferencePoint = spec.AutoSizeFrom is { } from ? TryReferencePoint(from)!.Value : type switch
                    {
                        AutoSizing.HeightOnly => AutoSizingReferencePoint.TopCenterPoint,
                        AutoSizing.WidthOnly => AutoSizingReferencePoint.LeftCenterPoint,
                        _ => AutoSizingReferencePoint.TopLeftPoint,
                    };
                }
            }
            else if (spec.AutoSizeFrom is { } from)
            {
                frame.AutoSizingReferencePoint = TryReferencePoint(from)!.Value;
            }
            if (spec.AutoSizeMinHeight is { } minimum)
            {
                frame.MinimumHeightForAutoSizing = U(minimum);
            }
            if (spec.BalanceColumns is { } balance)
            {
                frame.BalanceColumns = balance;
            }
            if (spec.FirstBaseline is { } firstBaseline)
            {
                frame.FirstBaselineOffset = TryFirstBaseline(firstBaseline)!.Value;
            }
            if (spec.FirstBaselineMin is { } firstBaselineMin)
            {
                frame.MinimumFirstBaselineOffset = U(firstBaselineMin);
            }
            if (spec.IgnoreWrap is { } ignoreWrap)
            {
                frame.IgnoreWrap = ignoreWrap;
            }
        }
        if (spec.Wrap is not null || spec.WrapOffset is not null || spec.WrapSide is not null || spec.WrapInverse is not null)
        {
            var current = item.TextWrap;
            var mode = spec.Wrap is { } wrap ? TryWrapMode(wrap)!.Value
                : current?.Mode ?? throw new IdmlException($"wrapOffset, wrapSide and wrapInverse need wrap: {item.Id} has no text wrap of its own.");
            var offset = current?.Offset ?? default;
            if (spec.WrapOffset is { } given)
            {
                double Side(double? value, double fallback) => (value ?? given.All) is { } points ? U(points) : fallback;
                offset = new Spacing(Side(given.Top, offset.Top), Side(given.Right, offset.Right), Side(given.Bottom, offset.Bottom), Side(given.Left, offset.Left));
            }
            item.TextWrap = new TextWrap(
                mode,
                offset,
                spec.WrapSide is { } side ? TryWrapSide(side)!.Value : current?.Side ?? TextWrapSide.BothSides,
                spec.WrapInverse ?? current?.Inverse ?? false);
        }
    }

    /// <summary>What is wrong with an item's text wrap or text frame option values, or null.</summary>
    private static string? ItemOptionsError(ItemSpec spec)
    {
        if (spec.Wrap is { } wrap && TryWrapMode(wrap) is null)
        {
            return $"Unknown wrap '{wrap}'. Use none, boundingBox, objectShape, jumpObject or nextColumn.";
        }
        if (spec.WrapSide is { } side && TryWrapSide(side) is null)
        {
            return $"Unknown wrapSide '{side}'. Use both, left, right, towardsSpine, awayFromSpine or largest.";
        }
        if (spec.WrapOffset is { } offset && ((double?[])[offset.All, offset.Top, offset.Right, offset.Bottom, offset.Left]).Any(value => value < 0))
        {
            return "wrapOffset sides must not be negative.";
        }
        if (spec.AutoSize is { } autoSize && TryAutoSizing(autoSize) is null)
        {
            return $"Unknown autoSize '{autoSize}'. Use off, height, width, heightAndWidth or proportional.";
        }
        if (spec.AutoSizeFrom is { } from && TryReferencePoint(from) is null)
        {
            return $"Unknown autoSizeFrom '{from}'. Use topLeft, top, topRight, left, center, right, bottomLeft, bottom or bottomRight.";
        }
        if (spec.AutoSizeMinHeight is < 0)
        {
            return $"autoSizeMinHeight {spec.AutoSizeMinHeight} is negative.";
        }
        if (spec.FirstBaseline is { } firstBaseline && TryFirstBaseline(firstBaseline) is null)
        {
            return $"Unknown firstBaseline '{firstBaseline}'. Use ascent, capHeight, leading, xHeight, emBox or fixed.";
        }
        if (spec.FirstBaselineMin is < 0)
        {
            return $"firstBaselineMin {spec.FirstBaselineMin} is negative.";
        }
        return null;
    }

    private static string Simplified(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static TextWrapMode? TryWrapMode(string wrap) => Simplified(wrap) switch
    {
        "none" or "off" => TextWrapMode.None,
        "boundingbox" or "box" or "boundingboxtextwrap" => TextWrapMode.BoundingBox,
        "objectshape" or "shape" or "contour" => TextWrapMode.ObjectShape,
        "jumpobject" or "jump" or "jumpobjecttextwrap" => TextWrapMode.JumpObject,
        "nextcolumn" or "jumptonextcolumn" or "nextcolumntextwrap" => TextWrapMode.NextColumn,
        _ => null,
    };

    private static TextWrapSide? TryWrapSide(string side) => Simplified(side) switch
    {
        "both" or "bothsides" => TextWrapSide.BothSides,
        "left" or "leftside" => TextWrapSide.LeftSide,
        "right" or "rightside" => TextWrapSide.RightSide,
        "towardsspine" or "sidetowardsspine" => TextWrapSide.SideTowardsSpine,
        "awayfromspine" or "sideawayfromspine" => TextWrapSide.SideAwayFromSpine,
        "largest" or "largestarea" => TextWrapSide.LargestArea,
        _ => null,
    };

    private static AutoSizing? TryAutoSizing(string autoSize) => Simplified(autoSize) switch
    {
        "off" or "none" => AutoSizing.Off,
        "height" or "heightonly" => AutoSizing.HeightOnly,
        "width" or "widthonly" => AutoSizing.WidthOnly,
        "heightandwidth" => AutoSizing.HeightAndWidth,
        "proportional" or "proportionally" or "heightandwidthproportionally" => AutoSizing.HeightAndWidthProportionally,
        _ => null,
    };

    private static AutoSizingReferencePoint? TryReferencePoint(string point) => Simplified(point).Replace("point", string.Empty, StringComparison.Ordinal) switch
    {
        "topleft" => AutoSizingReferencePoint.TopLeftPoint,
        "top" or "topcenter" => AutoSizingReferencePoint.TopCenterPoint,
        "topright" => AutoSizingReferencePoint.TopRightPoint,
        "left" or "leftcenter" => AutoSizingReferencePoint.LeftCenterPoint,
        "center" or "centre" => AutoSizingReferencePoint.CenterPoint,
        "right" or "rightcenter" => AutoSizingReferencePoint.RightCenterPoint,
        "bottomleft" => AutoSizingReferencePoint.BottomLeftPoint,
        "bottom" or "bottomcenter" => AutoSizingReferencePoint.BottomCenterPoint,
        "bottomright" => AutoSizingReferencePoint.BottomRightPoint,
        _ => null,
    };

    private static FirstBaselineOffset? TryFirstBaseline(string firstBaseline) => Simplified(firstBaseline) switch
    {
        "ascent" or "ascentoffset" => FirstBaselineOffset.AscentOffset,
        "capheight" => FirstBaselineOffset.CapHeight,
        "leading" or "leadingoffset" => FirstBaselineOffset.LeadingOffset,
        "xheight" => FirstBaselineOffset.XHeight,
        "embox" or "emboxheight" => FirstBaselineOffset.EmboxHeight,
        "fixed" or "fixedheight" => FirstBaselineOffset.FixedHeight,
        _ => null,
    };
}
