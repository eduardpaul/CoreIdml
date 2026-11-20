using CoreIdml.Pdf.Layout;
using CoreIdml.Pdf.Writing;

namespace CoreIdml.Pdf.Rendering;

internal sealed partial class IdmlRenderer
{
    /// <summary>
    /// Draws an underline or strikethrough as InDesign 20 writes it (07-char-underline-strike, measured in its PDF): a
    /// butt-capped line of the rule's weight, centred on <see cref="TextRuleOp.From"/>, over a solid band in the gap
    /// colour when the type is not solid. The types:
    /// <list type="bullet">
    /// <item><c>Dashed</c>: equal dashes and gaps of about 12 pt whatever the weight, adjusted so the rule starts and
    /// ends with a dash (<c>[11.783 11.783]</c> on 247.4 pt at 2 and 3 pt: 11 dashes).</item>
    /// <item><c>Canned Dotted</c>: round dots as wide as the weight, the first and last inside the rule's ends, about
    /// three weights apart (28 dots 9.053 pt apart at 3 pt).</item>
    /// <item><c>Wavy</c>: a wave of whole periods of about four weights (20 on 247.4 pt at 3 pt), as tall as the weight,
    /// drawn with a line of 0.265 × the weight; it starts upwards.</item>
    /// <item><c>ThickThin</c>: half the weight at the top, a sixth at the bottom, the band as wide as the weight.</item>
    /// <item>The canned dashes (<c>Canned Dashed 3x2</c>, <c>4x4</c>) as on frame strokes (not in the sample).</item>
    /// </list>
    /// Other stroke styles are drawn solid. The wave and dot spacing are fitted to the one weight of the sample.
    /// </summary>
    private static void DrawTextRule(TextRuleOp rule, ContentBuilder content)
    {
        var (x, y, length, weight) = (rule.From.X, rule.From.Y, rule.Length, rule.Weight);
        if (length <= 0 || weight <= 0)
        {
            return;
        }
        var name = rule.Type.StartsWith("StrokeStyle/$ID/", StringComparison.Ordinal) ? rule.Type["StrokeStyle/$ID/".Length..] : rule.Type;
        content.Save().LineCap(0);
        if (rule.Gap is { } gap && name != "Solid")
        {
            content.StrokeColor(gap).LineWidth(weight).MoveTo(x, y).LineTo(x + length, y).Stroke();
        }
        content.StrokeColor(rule.Color);
        switch (name)
        {
            case "Dashed":
                {
                    const double Nominal = 12;
                    var dashes = Math.Max(1, Math.Round((length + Nominal) / (2 * Nominal)));
                    var unit = length / ((2 * dashes) - 1);
                    content.LineWidth(weight).Dash([unit, unit]).MoveTo(x, y).LineTo(x + length, y).Stroke();
                    break;
                }
            case "Canned Dashed 3x2" or "Canned Dashed 4x4":
                content.LineWidth(weight).Dash(name.EndsWith("3x2", StringComparison.Ordinal) ? [weight * 3, weight * 2] : [weight * 4, weight * 4])
                    .MoveTo(x, y).LineTo(x + length, y).Stroke();
                break;
            case "Canned Dotted":
                {
                    var span = Math.Max(0, length - weight);
                    var step = span > 0 ? span / Math.Max(1, Math.Round(span / (3 * weight))) : weight;
                    content.LineWidth(weight).LineCap(1).Dash([0, step])
                        .MoveTo(x + (weight / 2), y).LineTo(x + (weight / 2) + span + (step / 2), y).Stroke();
                    break;
                }
            case "Wavy":
                {
                    var half = length / (2 * Math.Max(1, Math.Floor(length / (4 * weight))));
                    var lift = 0.4887 * weight;
                    content.LineWidth(0.265 * weight).MoveTo(x, y);
                    var up = true;
                    for (var at = x; at < x + length - (half / 2); at += half, up = !up)
                    {
                        var control = up ? y - lift : y + lift;
                        content.CurveTo(at + (half / 4), control, at + (half * 3 / 4), control, at + half, y);
                    }
                    content.Stroke();
                    break;
                }
            case "ThickThin":
                content.LineWidth(weight / 2).MoveTo(x, y - (weight / 4)).LineTo(x + length, y - (weight / 4)).Stroke();
                content.LineWidth(weight / 6).MoveTo(x, y + (weight * 5 / 12)).LineTo(x + length, y + (weight * 5 / 12)).Stroke();
                break;
            default:
                content.LineWidth(weight).MoveTo(x, y).LineTo(x + length, y).Stroke();
                break;
        }
        content.Restore();
    }
}
