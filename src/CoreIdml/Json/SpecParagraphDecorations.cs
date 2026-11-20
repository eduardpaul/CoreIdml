using CoreIdml.Dom;

namespace CoreIdml.Json;

public static partial class IdmlJson
{
    private static bool HasRuleAbove(TextFormatSpec spec) =>
        spec.RuleAbove is not null || spec.RuleAboveColor is not null || spec.RuleAboveWeight is not null || spec.RuleAboveOffset is not null
        || spec.RuleAboveTint is not null || spec.RuleAboveType is not null || spec.RuleAboveWidth is not null
        || spec.RuleAboveLeftIndent is not null || spec.RuleAboveRightIndent is not null;

    private static bool HasRuleBelow(TextFormatSpec spec) =>
        spec.RuleBelow is not null || spec.RuleBelowColor is not null || spec.RuleBelowWeight is not null || spec.RuleBelowOffset is not null
        || spec.RuleBelowTint is not null || spec.RuleBelowType is not null || spec.RuleBelowWidth is not null
        || spec.RuleBelowLeftIndent is not null || spec.RuleBelowRightIndent is not null;

    private static bool HasBorder(TextFormatSpec spec) =>
        spec.Border is not null || spec.BorderWeight is not null || spec.BorderTint is not null || spec.BorderType is not null
        || spec.BorderOffset is not null || spec.BorderCornerRadius is not null;

    private static bool HasShading(TextFormatSpec spec) =>
        spec.Shading is not null || spec.ShadingTint is not null || spec.ShadingOffset is not null || spec.ShadingCornerRadius is not null;

    private static bool HasParagraphDecoration(TextFormatSpec spec) => HasRuleAbove(spec) || HasRuleBelow(spec) || HasBorder(spec) || HasShading(spec);

    /// <summary>A rule from the spec's ruleAbove... or ruleBelow... fields; given fields turn it on unless ruleAbove/ruleBelow is false.</summary>
    private static ParagraphRule? ToRule(IdmlDocument document, TextFormatSpec spec, bool above) =>
        !(above ? HasRuleAbove(spec) : HasRuleBelow(spec)) ? null : new ParagraphRule
        {
            On = (above ? spec.RuleAbove : spec.RuleBelow) ?? true,
            Color = (above ? spec.RuleAboveColor : spec.RuleBelowColor) is { } color ? RequireSwatch(document, color) : null,
            Weight = above ? spec.RuleAboveWeight : spec.RuleBelowWeight,
            Offset = above ? spec.RuleAboveOffset : spec.RuleBelowOffset,
            Tint = above ? spec.RuleAboveTint : spec.RuleBelowTint,
            Type = (above ? spec.RuleAboveType : spec.RuleBelowType) is { } type ? ParseChoice<TextLineType>(type, above ? "ruleAboveType" : "ruleBelowType") : null,
            TextWidth = (above ? spec.RuleAboveWidth : spec.RuleBelowWidth) is { } width ? IsTextWidth(width)!.Value : null,
            LeftIndent = above ? spec.RuleAboveLeftIndent : spec.RuleBelowLeftIndent,
            RightIndent = above ? spec.RuleAboveRightIndent : spec.RuleBelowRightIndent,
        };

    /// <summary>A border from the spec's border... fields: "None" turns it off, any other field turns it on.</summary>
    private static ParagraphBox? ToBorder(IdmlDocument document, TextFormatSpec spec) => !HasBorder(spec) ? null : new ParagraphBox
    {
        On = !IsNone(spec.Border),
        Color = spec.Border is { } color && !IsNone(color) ? RequireSwatch(document, color) : null,
        Weight = spec.BorderWeight,
        Tint = spec.BorderTint,
        Type = spec.BorderType is { } type ? ParseChoice<TextLineType>(type, "borderType") : null,
        Offset = ToSpacing(spec.BorderOffset, null),
        CornerRadius = spec.BorderCornerRadius,
    };

    /// <summary>Shading from the spec's shading... fields: "None" turns it off, any other field turns it on.</summary>
    private static ParagraphBox? ToShading(IdmlDocument document, TextFormatSpec spec) => !HasShading(spec) ? null : new ParagraphBox
    {
        On = !IsNone(spec.Shading),
        Color = spec.Shading is { } color && !IsNone(color) ? RequireSwatch(document, color) : null,
        Tint = spec.ShadingTint,
        Offset = ToSpacing(spec.ShadingOffset, null),
        CornerRadius = spec.ShadingCornerRadius,
    };

    private static bool IsNone(string? swatch) => string.Equals(swatch, "None", StringComparison.OrdinalIgnoreCase);

    private static bool? IsTextWidth(string width) => width.ToLowerInvariant() switch
    {
        "column" or "columnwidth" => false,
        "text" or "textwidth" => true,
        _ => null,
    };

    /// <summary>What is wrong with a format's rule, border or shading values, or null.</summary>
    private static string? DecorationError(TextFormatSpec format)
    {
        foreach (var (name, type) in (ReadOnlySpan<(string, string?)>)[("ruleAboveType", format.RuleAboveType), ("ruleBelowType", format.RuleBelowType), ("borderType", format.BorderType)])
        {
            if (type is not null && ChoiceError<TextLineType>(type, name) is { } message)
            {
                return message;
            }
        }
        foreach (var (name, width) in (ReadOnlySpan<(string, string?)>)[("ruleAboveWidth", format.RuleAboveWidth), ("ruleBelowWidth", format.RuleBelowWidth)])
        {
            if (width is not null && IsTextWidth(width) is null)
            {
                return $"Unknown {name} '{width}'. Use column or text.";
            }
        }
        foreach (var (name, weight) in (ReadOnlySpan<(string, double?)>)[("ruleAboveWeight", format.RuleAboveWeight), ("ruleBelowWeight", format.RuleBelowWeight), ("borderWeight", format.BorderWeight)])
        {
            if (weight is < 0 or > 1000)
            {
                return $"{name} {weight} is out of range. Use 0 to 1000 pt.";
            }
        }
        foreach (var (name, tint) in (ReadOnlySpan<(string, double?)>)[("ruleAboveTint", format.RuleAboveTint), ("ruleBelowTint", format.RuleBelowTint), ("borderTint", format.BorderTint), ("shadingTint", format.ShadingTint)])
        {
            if (tint is < 0 or > 100)
            {
                return $"{name} {tint} is out of range. Use 0 to 100.";
            }
        }
        if (format.BorderCornerRadius is < 0 || format.ShadingCornerRadius is < 0)
        {
            return "Corner radii must not be negative.";
        }
        return null;
    }
}
