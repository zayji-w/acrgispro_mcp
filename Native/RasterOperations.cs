using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;

namespace SafeBridge.Native;

internal sealed class RasterOperations(BridgeConfig config, JobManager jobs)
{
    private sealed record Plan(string Operation, string ProjectId, string MapId, string Name,
        bool Attach, RasterData Source, SpatialReference OutputCrs)
    {
        public string Method { get; init; } = "NEAREST";
        public double? CellSize { get; init; }
        public RasterData? Snap { get; init; }
        public string? Transform { get; init; }
        public RasterBounds? ClipBounds { get; init; }
        public string? MaskPath { get; init; }
        public string? PositiveBandPath { get; init; }
        public string? NegativeBandPath { get; init; }
        public string? Index { get; init; }
        public string? ZonePath { get; init; }
        public string? ZoneField { get; init; }
        public string? Statistic { get; init; }
        public bool IgnoreNoData { get; init; }
    }

    public async Task<object> Submit(string op, JsonElement args, double expires)
    {
        var key = Contracts.Text(args, "idempotency_key");
        var payload = JsonSerializer.SerializeToElement(new
        {
            operation = op,
            arguments = args.EnumerateObject().Where(p => p.Name != "idempotency_key")
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => p.Value)
        });
        var replay = jobs.Replay(key, payload);
        if (replay is not null) return replay.Value;
        var plan = await QueuedTask.Run(() =>
        {
            CheckExpiry(expires);
            return Preflight(op, args);
        });
        CheckExpiry(expires);
        return jobs.Submit(key, op, payload, (id, ct, report) => Execute(id, plan, ct, report));
    }

    private static void CheckExpiry(double expires)
    {
        if (Contracts.Now > expires) throw new BridgeException("REQUEST_EXPIRED", "Raster request expired before submission");
    }

    private static Plan Preflight(string op, JsonElement args)
    {
        var project = Contracts.Text(args, "project_id");
        var map = Contracts.Text(args, "map_id");
        MapOperations.Resolve(project, map);
        var inputKey = op switch
        {
            "raster_index" => "positive_path",
            "raster_zonal_statistics" => "value_path",
            _ => "input_path"
        };
        var source = ReadGrid(Contracts.Text(args, inputKey));
        var plan = new Plan(op, project, map, PathPolicy.OutputName(Contracts.OptionalText(args, "output_name") ?? op),
            Contracts.Bool(args, "add_to_map", true), source, source.Crs);
        return op switch
        {
            "raster_clip" => Clip(plan, args),
            "raster_project" => Project(plan, args),
            "raster_resample" => Resize(plan, args),
            "raster_index" => Index(plan, args),
            "raster_zonal_statistics" => Zones(plan, args),
            _ => throw new BridgeException("UNKNOWN_OPERATION", "Unknown raster operation")
        };
    }

    private static RasterData ReadGrid(string path)
    {
        var raster = RasterData.Read(path);
        if (raster.Crs.IsUnknown)
            throw new BridgeException("UNKNOWN_CRS", "Define the raster coordinate system before processing");
        if (!raster.NorthUp)
            throw new BridgeException("INVALID_GRID", "Use a north-up raster with an axis-aligned pixel grid");
        return raster;
    }

    private static (string Path, RasterBounds Bounds, string? Field) Polygon(string path, RasterData source, string? field = null)
    {
        var canonical = PathPolicy.Dataset(path).Path;
        return DatasetOperations.WithFeatureClass<(string Path, RasterBounds Bounds, string? Field)>(canonical, fc =>
        {
            using var definition = fc.GetDefinition();
            if (definition.GetShapeType() != GeometryType.Polygon)
                throw new BridgeException("INVALID_ARGUMENT", "Use a polygon feature class");
            var crs = definition.GetSpatialReference();
            if (crs.IsUnknown || !crs.IsEqual(source.Crs))
                throw new BridgeException("CRS_MISMATCH", "Polygon and raster must use the same coordinate system");
            var bounds = RasterData.Bounds(fc.GetExtent());
            if (!bounds.HasArea || !source.Extent.Overlaps(bounds))
                throw new BridgeException("NO_OVERLAP", "Polygon extent does not overlap the raster");
            if (field is null) return (canonical, bounds, null);
            var actual = definition.GetFields().FirstOrDefault(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (actual is null) throw new BridgeException("FIELD_NOT_FOUND", "Zone field not found");
            if (actual.FieldType.ToString() is not ("OID" or "SmallInteger" or "Integer" or "BigInteger" or "String"))
                throw new BridgeException("INVALID_ARGUMENT", "Zone field must be integer or text");
            return (canonical, bounds, actual.Name);
        });
    }

    private static Plan Clip(Plan plan, JsonElement args)
    {
        if (Contracts.OptionalText(args, "mask_path") is { } path)
        {
            var polygon = Polygon(path, plan.Source);
            return plan with { MaskPath = polygon.Path, ClipBounds = polygon.Bounds };
        }
        var bounds = RasterSpec.Bounds(args.GetProperty("bbox"));
        if (!plan.Source.Extent.Overlaps(bounds))
            throw new BridgeException("NO_OVERLAP", "Clip bounds do not overlap the raster");
        return plan with { ClipBounds = bounds };
    }

    private static Plan Resize(Plan plan, JsonElement args) => plan with
    {
        CellSize = args.GetProperty("cell_size").GetDouble(),
        Method = Contracts.OptionalText(args, "resampling")?.ToUpperInvariant() ?? "NEAREST"
    };

    private static Plan Project(Plan plan, JsonElement args)
    {
        plan = Resize(plan, args);
        var wkid = args.GetProperty("target_wkid").GetInt32();
        SpatialReference target;
        try { target = SpatialReferenceBuilder.CreateSpatialReference(wkid); }
        catch (Exception ex) when (ex is ArgumentException or ArcGIS.Core.Geometry.Exceptions.GeometryException)
        {
            throw new BridgeException("INVALID_ARGUMENT", "Unknown target WKID: " + wkid);
        }
        if (target.IsUnknown || !(target.IsProjected || target.IsGeographic))
            throw new BridgeException("INVALID_ARGUMENT", "Target must be a projected or geographic coordinate system");
        var transform = Contracts.OptionalText(args, "transform_method");
        var source = plan.Source.Crs;
        if (transform is null && (source.GcsWkid != target.GcsWkid
            || (source.GcsWkid == 0 && source.GcsWkt != target.GcsWkt)))
            throw new BridgeException("DATUM_TRANSFORM_REQUIRED", "Specify an ArcGIS datum transformation for the change in geographic CRS");
        RasterData? snap = null;
        if (Contracts.OptionalText(args, "snap_path") is { } path)
        {
            snap = ReadGrid(path);
            if (!snap.Crs.IsEqual(target) || !HasCellSize(snap, plan.CellSize!.Value, plan.CellSize.Value))
                throw new BridgeException("GRID_MISMATCH", "Snap raster must use the target CRS and requested cell size");
        }
        return plan with { OutputCrs = target, Transform = transform, Snap = snap };
    }

    private static Plan Index(Plan plan, JsonElement args)
    {
        var otherPath = PathPolicy.Local(Contracts.Text(args, "negative_path"));
        var other = otherPath.Equals(plan.Source.Path, StringComparison.OrdinalIgnoreCase) ? plan.Source : ReadGrid(otherPath);
        if (!SameGrid(plan.Source, other) || !RasterSpec.SameSize(plan.Source.XSize, plan.Source.YSize))
            throw new BridgeException("GRID_MISMATCH", "Index operands must use identical CRS, extent, dimensions and square cell size");
        var a = args.TryGetProperty("positive_band", out var positive) ? positive.GetInt32() : 1;
        var b = args.TryGetProperty("negative_band", out var negative) ? negative.GetInt32() : 1;
        if (ReferenceEquals(other, plan.Source) && a == b)
            throw new BridgeException("INVALID_ARGUMENT", "Select two distinct bands when using the same raster");
        return plan with
        {
            Index = Contracts.Text(args, "index").ToUpperInvariant(),
            PositiveBandPath = plan.Source.BandPath(a),
            NegativeBandPath = other.BandPath(b),
            Snap = plan.Source
        };
    }

    private static Plan Zones(Plan plan, JsonElement args)
    {
        var source = plan.Source;
        if (source.Bands.Length != 1 || !RasterSpec.SameSize(source.XSize, source.YSize))
            throw new BridgeException("INVALID_GRID", "Zonal statistics needs a single-band raster with square cells");
        var zone = Polygon(Contracts.Text(args, "zone_path"), source, Contracts.Text(args, "zone_field"));
        return plan with
        {
            Attach = false, ZonePath = zone.Path, ZoneField = zone.Field, Snap = source,
            CellSize = source.XSize, Statistic = Contracts.OptionalText(args, "statistics")?.ToUpperInvariant() ?? "MEAN",
            IgnoreNoData = Contracts.Bool(args, "ignore_nodata", true)
        };
    }

    private static bool HasCellSize(RasterData raster, double x, double y) =>
        RasterSpec.SameSize(raster.XSize, x) && RasterSpec.SameSize(raster.YSize, y);

    private static bool SameGrid(RasterData a, RasterData b) => a.Crs.IsEqual(b.Crs) && HasCellSize(a, b.XSize, b.YSize)
        && a.Width == b.Width && a.Height == b.Height && SameOrigin(a, b);

    private static bool SameOrigin(RasterData a, RasterData b) =>
        Math.Abs(a.Extent.XMin - b.Extent.XMin) <= b.XSize * 1e-6
        && Math.Abs(a.Extent.YMin - b.Extent.YMin) <= b.YSize * 1e-6;

    private static bool IsAligned(RasterData output, RasterData snap) =>
        RasterSpec.Aligned(output.Extent.XMin, snap.Extent.XMin, snap.XSize)
        && RasterSpec.Aligned(output.Extent.YMin, snap.Extent.YMin, snap.YSize);

    private static async Task Run(string tool, object?[] values, IEnumerable<KeyValuePair<string, string>> environment,
        CancellationToken ct, Action<string, int?> report)
    {
        ct.ThrowIfCancellationRequested();
        report(tool, null);
        // No AddOutputsToMap or AddToHistory. Attach only validated outputs to the requested map below.
        var result = await Geoprocessing.ExecuteToolAsync(tool, Geoprocessing.MakeValueArray(values!), environment,
            ct, null, GPExecuteToolFlags.GPThread);
        if (result.IsCanceled || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        var messages = result.Messages.Select(m => m.Text).Take(30).ToArray();
        foreach (var message in messages) report(message, null);
        if (!result.IsFailed) return;
        if (result.ErrorCode is 10096 or 824 || messages.Any(m => m.Contains("010096", StringComparison.Ordinal)
            || m.Contains("000824", StringComparison.Ordinal)))
            throw new BridgeException("LICENSE_UNAVAILABLE", "The required ArcGIS Analyst extension is unavailable", new { tool, messages });
        throw new BridgeException("GP_FAILED", "ArcGIS geoprocessing failed: " + tool, new { tool, messages });
    }

    private async Task<JobOutcome> Execute(string id, Plan plan, CancellationToken ct, Action<string, int?> report)
    {
        ct.ThrowIfCancellationRequested();
        var folder = PathPolicy.OutputFolder(config.output_root, id);
        var source = plan.Source;
        var output = Path.Combine(folder, plan.Name + ".tif");
        var targetCell = plan.CellSize is { } size ? RasterSpec.GpNumber(size) : null;
        var extent = plan.Operation is "raster_index" or "raster_zonal_statistics" ? source.Extent.GpExtent : "MAXOF";
        var snap = plan.Operation == "raster_clip" ? source.Path : plan.Snap?.Path;
        var environment = Geoprocessing.MakeEnvironmentArray(overwriteoutput: false, extent: extent,
            outputCoordinateSystem: plan.OutputCrs, snapRaster: snap ?? "", mask: "",
            geographicTransformations: plan.Transform ?? "", scratchWorkspace: folder);

        switch (plan.Operation)
        {
            case "raster_clip":
                await Run("management.Clip", [source.Path, plan.MaskPath is null ? plan.ClipBounds!.Value.GpExtent : "#",
                    output, plan.MaskPath, null, plan.MaskPath is null ? "NONE" : "ClippingGeometry", "NO_MAINTAIN_EXTENT"],
                    environment, ct, report);
                break;
            case "raster_project":
                await Run("management.ProjectRaster", [source.Path, output, plan.OutputCrs, plan.Method, targetCell, plan.Transform],
                    environment, ct, report);
                break;
            case "raster_resample":
                await Run("management.Resample", [source.Path, output, targetCell, plan.Method], environment, ct, report);
                break;
            case "raster_index":
                await CalculateIndex(plan, folder, output, environment, ct, report);
                break;
            case "raster_zonal_statistics":
                var gdb = Path.Combine(folder, "zones.gdb");
                output = Path.Combine(gdb, plan.Name);
                await Run("management.CreateFileGDB", [folder, "zones.gdb"], environment, ct, report);
                environment = Geoprocessing.MakeEnvironmentArray(overwriteoutput: false, extent: extent,
                    outputCoordinateSystem: plan.OutputCrs, snapRaster: source.Path, cellSize: targetCell,
                    mask: "", geographicTransformations: "", scratchWorkspace: folder);
                await Run("sa.ZonalStatisticsAsTable", [plan.ZonePath, plan.ZoneField, source.Path, output,
                    plan.IgnoreNoData ? "DATA" : "NODATA", plan.Statistic], environment, ct, report);
                break;
        }
        ct.ThrowIfCancellationRequested();
        return await QueuedTask.Run(() => Finish(plan, output, ct));
    }

    private static async Task CalculateIndex(Plan plan, string folder, string output,
        IEnumerable<KeyValuePair<string, string>> environment, CancellationToken ct, Action<string, int?> report)
    {
        var workspace = Path.Combine(folder, "index_work");
        Directory.CreateDirectory(workspace);
        var a = Path.Combine(workspace, "float_operand.tif");
        var minus = Path.Combine(workspace, "difference.tif");
        var plus = Path.Combine(workspace, "sum.tif");
        try
        {
            // Floating-point operand avoids integer divide, unsigned subtraction and integer overflow.
            await Run("sa.Float", [plan.PositiveBandPath, a], environment, ct, report);
            await Run("sa.Minus", [a, plan.NegativeBandPath, minus], environment, ct, report);
            await Run("sa.Plus", [a, plan.NegativeBandPath, plus], environment, ct, report);
            await Run("sa.Divide", [minus, plus, output], environment, ct, report);
        }
        finally
        {
            try
            {
                PathPolicy.EnsureNoLinks(workspace);
                foreach (var file in Directory.EnumerateFiles(workspace))
                {
                    PathPolicy.EnsureNoLinks(file);
                    File.Delete(file);
                }
                Directory.Delete(workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BridgeException)
            {
                report("Temporary index files could not be removed from " + workspace, null);
            }
        }
    }

    private static JobOutcome Finish(Plan plan, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (plan.Operation == "raster_zonal_statistics")
        {
            var table = DatasetOperations.DescribeTable(output, true);
            return new([table], new { status = "verified", check = "table_exists", statistic = plan.Statistic,
                zone_field = plan.ZoneField, ignore_nodata = plan.IgnoreNoData },
                new { status = "not_requested" }, []);
        }
        var raster = ReadGrid(output);
        ValidateOutput(plan, raster);
        var validation = new { status = "verified", scope = "raster_metadata", index = plan.Index,
            method = plan.Operation is "raster_resample" or "raster_project" ? plan.Method : null };
        if (!plan.Attach) return new([raster.Summary()], validation, new { status = "not_requested" }, []);
        ct.ThrowIfCancellationRequested();
        try
        {
            var map = MapOperations.Resolve(plan.ProjectId, plan.MapId);
            var (layer, _) = RasterData.AddLayer(map, raster.Path, plan.Name);
            return new([raster.Summary()], validation, new { status = "attached", layer_ids = new[] { layer.URI } }, []);
        }
        catch (BridgeException ex) when (ex.Code is "STALE_CONTEXT" or "NO_PROJECT")
        {
            return new([raster.Summary()], validation, new { status = "context_changed", error = ex.Message },
                ["Raster was saved, but the target project or map is no longer open"]);
        }
        catch (Exception ex)
        {
            return new([raster.Summary()], validation, new { status = "failed", error = ex.Message },
                ["Raster was saved, but adding the layer failed"]);
        }
    }

    private static void ValidateOutput(Plan plan, RasterData output)
    {
        var source = plan.Source;
        if (!output.Crs.IsEqual(plan.OutputCrs))
            throw new BridgeException("VALIDATION_FAILED", "Output CRS differs from the requested coordinate system");
        if (plan.CellSize is { } cell && !HasCellSize(output, cell, cell))
            throw new BridgeException("VALIDATION_FAILED", "Output cell size differs from the requested cell size");
        if (plan.Snap is { } snap && !IsAligned(output, snap))
            throw new BridgeException("VALIDATION_FAILED", "Output origin is not aligned to the reference grid");
        var expectedBands = plan.Index is null ? source.Bands.Length : 1;
        if (output.Bands.Length != expectedBands)
            throw new BridgeException("VALIDATION_FAILED", "Output band count differs from the expected band count");
        if (plan.Operation == "raster_clip" && (!HasCellSize(output, source.XSize, source.YSize)
            || !IsAligned(output, source) || !output.Extent.Overlaps(plan.ClipBounds!.Value)))
            throw new BridgeException("VALIDATION_FAILED", "Clip did not preserve the source grid or overlap the requested extent");
        if (plan.Operation == "raster_resample" && !SameOrigin(output, source))
            throw new BridgeException("VALIDATION_FAILED", "Resample changed the lower-left origin");
        if (plan.Index is not null && !SameGrid(output, source))
            throw new BridgeException("VALIDATION_FAILED", "Index changed the input grid");
    }
}
