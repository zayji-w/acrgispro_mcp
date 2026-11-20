using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;

namespace SafeBridge.Native;

internal static class ThematicOperations
{
    private const string OtherColor = "#B5BAC3";
    private static readonly string[] CategoricalColors =
    [
        "#4477AA", "#228833", "#EE7733", "#AA3377", "#66CCEE", "#CCBB44",
        "#EE6677", "#669977", "#7755AA", "#339988", "#BB7755", "#888833"
    ];

    private static FeatureLayer Layer(JsonElement args) => MapOperations.FeatureLayer(
        Contracts.Text(args, "project_id"), Contracts.Text(args, "map_id"), Contracts.Text(args, "layer_id"));

    private static Field Field(FeatureLayer layer, JsonElement args, bool numeric)
    {
        using var table = layer.GetTable();
        using var definition = table.GetDefinition();
        var name = Contracts.Text(args, "field");
        var field = definition.GetFields().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new BridgeException("FIELD_NOT_FOUND", "Field is not present in the layer");
        bool integer = field.FieldType is FieldType.OID or FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger;
        bool supported = numeric ? integer || field.FieldType is FieldType.Single or FieldType.Double
            : integer || field.FieldType == FieldType.String;
        if (!supported)
            throw new BridgeException("INVALID_ARGUMENT", numeric
                ? "Graduated colors require a numeric field" : "Unique values require a text or integer field");
        return field;
    }

    private static CIMColor Color(string hex) => ColorFactory.Instance.CreateRGBColor(
        Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    private static string? Hex(CIMSymbolReference? reference)
    {
        if (reference?.Symbol?.GetColor() is not CIMRGBColor color) return null;
        return $"#{(int)Math.Round(color.R):X2}{(int)Math.Round(color.G):X2}{(int)Math.Round(color.B):X2}";
    }

    private static CIMSymbolReference Symbol(FeatureLayer layer, string hex)
    {
        var colors = Color(hex);
        var factory = SymbolFactory.Instance;
        CIMSymbol symbol = layer.ShapeType switch
        {
            esriGeometryType.esriGeometryPolygon => factory.ConstructPolygonSymbol(colors, SimpleFillStyle.Solid,
                factory.ConstructStroke(Color("#E7EBEF"), .45, SimpleLineStyle.Solid)),
            esriGeometryType.esriGeometryPolyline => factory.ConstructLineSymbol(colors, 2, SimpleLineStyle.Solid),
            esriGeometryType.esriGeometryPoint or esriGeometryType.esriGeometryMultipoint =>
                factory.ConstructPointSymbol(colors, 8, SimpleMarkerStyle.Circle),
            _ => throw new BridgeException("INVALID_ARGUMENT", "Thematic rendering supports point, line and polygon feature layers")
        };
        return symbol.MakeSymbolReference();
    }

    private static string[]? Colors(JsonElement args)
        => args.TryGetProperty("colors", out var colors) ? colors.EnumerateArray().Select(c => c.GetString()!).ToArray() : null;

    private static void CheckPalette(string[]? colors, int count)
    {
        if (colors is not null && colors.Length != count)
            throw new BridgeException("PALETTE_SIZE_MISMATCH", $"Provide {count} colors for the actual classes",
                new { actual_class_count = count });
    }

    public static object Unique(JsonElement args)
    {
        var layer = Layer(args);
        var field = Field(layer, args, false);
        var limit = args.TryGetProperty("category_limit", out var value) ? value.GetInt32() : 32;
        var other = Contracts.Bool(args, "show_other", true);
        var template = Symbol(layer, CategoricalColors[0]);
        var definition = new UniqueValueRendererDefinition
        {
            ValueFields = [field.Name],
            ValuesLimit = limit + 1,
            SymbolTemplate = template,
            UseDefaultSymbol = true,
            DefaultSymbol = Symbol(layer, OtherColor)
        };
        var renderer = layer.CreateRenderer(definition) as CIMUniqueValueRenderer
            ?? throw new BridgeException("RENDERER_FAILED", "ArcGIS did not create a unique-value renderer");
        var classes = (renderer.Groups ?? []).SelectMany(group => group.Classes ?? []).ToArray();
        if (classes.Length > limit)
            throw new BridgeException("CATEGORY_LIMIT_EXCEEDED", "The selected field exceeds the requested category limit",
                new { category_limit = limit });
        if (classes.Length == 0)
            throw new BridgeException("NO_VALUES", "No unique values were found in the layer");
        var colors = Colors(args);
        CheckPalette(colors, classes.Length);
        if (colors is null && classes.Length <= CategoricalColors.Length) colors = CategoricalColors[..classes.Length];
        if (colors is not null)
        {
            for (int i = 0; i < classes.Length; i++)
                classes[i].Symbol = Symbol(layer, colors[i]);
            renderer.ColorRamp = new CIMFixedColorRamp { Colors = colors.Select(Color).ToArray() };
        }
        renderer.DefaultLabel = Contracts.OptionalText(args, "other_label") ?? "其他值";
        renderer.DefaultSymbol = definition.DefaultSymbol;
        renderer.UseDefaultSymbol = other;
        renderer.IsDefaultSymbolVisible = other;
        layer.SetRenderer(renderer);
        return Summary(layer);
    }

    private static string[] BlueRamp(int count)
    {
        string[] ramp = ["#EFF3FF", "#E2ECF9", "#D0E2F3", "#BCD8EF", "#A0C9E7", "#7FB7DB",
            "#5CA4D0", "#3C89C1", "#266FAF", "#135896", "#0B447E", "#08306B"];
        return Enumerable.Range(0, count)
            .Select(i => ramp[(int)Math.Round((double)i * (ramp.Length - 1) / (count - 1))]).ToArray();
    }

    public static object Graduated(JsonElement args)
    {
        var layer = Layer(args);
        var field = Field(layer, args, true);
        var classification = ThematicSpec.Method(args) switch
        {
            "manual" => ClassificationMethod.Manual,
            "equal_interval" => ClassificationMethod.EqualInterval,
            "quantile" => ClassificationMethod.Quantile,
            _ => ClassificationMethod.NaturalBreaks
        };
        var template = Symbol(layer, "#3684BF");
        CIMClassBreaksRenderer renderer;
        if (classification == ClassificationMethod.Manual)
        {
            var edges = args.GetProperty("breaks").EnumerateArray().Select(edge => edge.GetDouble()).ToArray();
            renderer = new CIMClassBreaksRenderer
            {
                Field = field.Name,
                Heading = field.AliasName,
                MinimumBreak = edges[0],
                Breaks = edges.Skip(1).Select(upper => new CIMClassBreak { UpperBound = upper }).ToArray(),
                ClassBreakType = ClassBreakType.GraduatedColor,
                ClassificationMethod = classification
            };
        }
        else
        {
            var count = args.TryGetProperty("class_count", out var size) ? size.GetInt32() : 5;
            var definition = new GraduatedColorsRendererDefinition(field.Name, classification, count,
                new CIMFixedColorRamp { Colors = BlueRamp(count).Select(Color).ToArray() }, template);
            renderer = layer.CreateRenderer(definition) as CIMClassBreaksRenderer
                ?? throw new BridgeException("RENDERER_FAILED", "ArcGIS did not create a graduated-colors renderer");
        }

        var classes = renderer.Breaks ?? [];
        if (classes.Length < 2 || !double.IsFinite(renderer.MinimumBreak))
            throw new BridgeException("NO_VALUES", "At least two classes of finite values are required");
        var colors = Colors(args);
        CheckPalette(colors, classes.Length);
        colors ??= BlueRamp(classes.Length);
        var lower = renderer.MinimumBreak;
        for (int i = 0; i < classes.Length; i++)
        {
            var item = classes[i];
            if (!double.IsFinite(item.UpperBound) || item.UpperBound < lower || (i > 0 && item.UpperBound == lower))
                throw new BridgeException("RENDERER_FAILED", "ArcGIS returned invalid or duplicate break boundaries");
            item.Label = ThematicSpec.RangeLabel(lower, item.UpperBound, i == 0);
            item.Symbol = Symbol(layer, colors[i]);
            lower = item.UpperBound;
        }
        renderer.AlwaysUpdateClassLabels = false;
        renderer.ShowInAscendingOrder = true;
        renderer.ColorRamp = new CIMFixedColorRamp { Colors = colors.Select(Color).ToArray() };
        renderer.DefaultLabel = Contracts.OptionalText(args, "other_label") ?? "无数据或超出范围";
        renderer.DefaultSymbol = Symbol(layer, OtherColor);
        renderer.UseDefaultSymbol = Contracts.Bool(args, "show_other", true);
        layer.SetRenderer(renderer);
        return Summary(layer);
    }

    public static object Read(JsonElement args) => Summary(Layer(args));

    private static object Summary(FeatureLayer layer)
    {
        var renderer = layer.GetRenderer();
        return renderer switch
        {
            CIMUniqueValueRenderer unique => new
            {
                layer_id = layer.URI, mode = "unique_value", fields = unique.Fields,
                classes = (unique.Groups ?? []).SelectMany(group => (group.Classes ?? []).Select(item => new
                {
                    heading = group.Heading, label = item.Label, visible = item.Visible,
                    values = (item.Values ?? []).Select(v => v.FieldValues).ToArray(), color = Hex(item.Symbol)
                })).ToArray(),
                other = new { visible = unique.UseDefaultSymbol && unique.IsDefaultSymbolVisible,
                    label = unique.DefaultLabel, color = Hex(unique.DefaultSymbol) },
                project_saved = false
            },
            CIMClassBreaksRenderer breaks => new
            {
                layer_id = layer.URI, mode = breaks.ClassBreakType == ClassBreakType.GraduatedColor
                    ? "graduated_colors" : "class_breaks",
                field = breaks.Field, method = breaks.ClassificationMethod.ToString(), minimum = breaks.MinimumBreak,
                classes = (breaks.Breaks ?? []).Select((item, i) => new
                {
                    lower = i == 0 ? breaks.MinimumBreak : breaks.Breaks![i - 1].UpperBound,
                    lower_inclusive = i == 0, upper = item.UpperBound, label = item.Label, color = Hex(item.Symbol)
                }).ToArray(),
                other = new { visible = breaks.UseDefaultSymbol, label = breaks.DefaultLabel, color = Hex(breaks.DefaultSymbol) },
                project_saved = false
            },
            _ => new { layer_id = layer.URI, mode = renderer is CIMSimpleRenderer ? "simple" : "other",
                renderer_type = renderer?.GetType().Name, project_saved = false }
        };
    }
}
