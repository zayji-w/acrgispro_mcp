using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeBridge.Native;

internal static class ThematicSpec
{
    public static bool ValidColors(JsonElement value) => value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() is >= 1 and <= 128
        && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
            && Regex.IsMatch(item.GetString()!, @"\A#[0-9a-fA-F]{6}\z"));

    public static bool ValidBreaks(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 3 or > 13) return false;
        double previous = double.NegativeInfinity;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number)
                || !double.IsFinite(number) || number <= previous) return false;
            previous = number;
        }
        return true;
    }

    public static string Method(JsonElement args) => Contracts.OptionalText(args, "method")
        ?? (args.TryGetProperty("breaks", out _) ? "manual" : "natural_breaks");

    public static void Validate(string op, JsonElement args)
    {
        if (op is not ("set_unique_value_renderer" or "set_graduated_colors_renderer")) return;
        if (Contracts.Text(args, "field").Length > 128 || Contracts.OptionalText(args, "other_label") is { Length: > 120 })
            throw new BridgeException("INVALID_ARGUMENT", "Field name or fallback label is too long");
        if (op != "set_graduated_colors_renderer") return;
        var method = Method(args);
        if (method is not ("natural_breaks" or "equal_interval" or "quantile" or "manual"))
            throw new BridgeException("INVALID_ARGUMENT", "Unknown classification method");
        bool manual = args.TryGetProperty("breaks", out _);
        if (manual != (method == "manual") || (manual && args.TryGetProperty("class_count", out _)))
            throw new BridgeException("INVALID_ARGUMENT", "Manual classification requires breaks without class_count");
    }

    public static string RangeLabel(double lower, double upper, bool first)
    {
        return (first ? "[" : "(") + lower.ToString(CultureInfo.InvariantCulture) + ", "
            + upper.ToString(CultureInfo.InvariantCulture) + "]";
    }
}
