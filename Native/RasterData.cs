using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;
using ArcGIS.Core.Data.Raster;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;

namespace SafeBridge.Native;

internal sealed record RasterBandInfo(string name, string pixel_type, object? nodata);

// An MCT read returns metadata only and releases the native dataset and all band handles.
internal sealed record RasterData(
    string Path, string Format, SpatialReference Crs, RasterBounds Extent,
    int Width, int Height, double XSize, double YSize, bool NorthUp,
    RasterBandInfo[] Bands)
{
    private static readonly HashSet<string> FileFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".tif", ".tiff", ".img", ".jp2", ".dat" };

    public static RasterData Read(string path)
    {
        var full = PathPolicy.Local(path);
        try
        {
            var parent = System.IO.Path.GetDirectoryName(full)!;
            var name = System.IO.Path.GetFileName(full);
            if (parent.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) && Directory.Exists(parent))
            {
                using var store = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(parent)));
                return ReadDataset(store, name, full);
            }
            if (!FileFormats.Contains(System.IO.Path.GetExtension(full)))
                throw new BridgeException("UNSUPPORTED_DATASET", "Use local TIFF, IMG, JP2, ENVI DAT or a File GDB raster dataset");
            if (!File.Exists(full))
                throw new BridgeException("DATASET_NOT_FOUND", "Raster file does not exist");
            if (full.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                string header = System.IO.Path.ChangeExtension(full, ".hdr");
                string alternate = full + ".hdr";
                if (!File.Exists(header) && !File.Exists(alternate))
                    throw new BridgeException("DATASET_NOT_FOUND", "ENVI raster header (.hdr) is missing");
                if (File.Exists(header)) PathPolicy.EnsureNoLinks(header);
                if (File.Exists(alternate)) PathPolicy.EnsureNoLinks(alternate);
            }
            using var files = new FileSystemDatastore(new FileSystemConnectionPath(new Uri(parent), FileSystemDatastoreType.Raster));
            return ReadDataset(files, name, full);
        }
        catch (GeodatabaseException ex)
        {
            throw new BridgeException("DATASET_NOT_FOUND", "Unable to open raster: " + ex.Message);
        }
    }

    private static RasterData ReadDataset(Datastore store, string name, string path)
    {
        using var dataset = store switch
        {
            Geodatabase gdb => gdb.OpenDataset<RasterDataset>(name),
            FileSystemDatastore files => files.OpenDataset<RasterDataset>(name),
            _ => throw new InvalidOperationException("Unsupported raster datastore")
        };
        using var definition = dataset.GetDefinition();
        using var raster = dataset.CreateFullRaster();
        var crs = raster.GetSpatialReference();
        var extent = Bounds(raster.GetExtent());
        var size = raster.GetMeanCellSize();
        var width = raster.GetWidth();
        var height = raster.GetHeight();
        if (!extent.HasArea || width < 1 || height < 1 || !double.IsFinite(size.Item1)
            || !double.IsFinite(size.Item2) || size.Item1 <= 0 || size.Item2 <= 0)
            throw new BridgeException("INVALID_GRID", "Raster extent or cell size is invalid");
        var northUp = IsNorthUp(raster, extent, width, height, size.Item1, size.Item2);
        var bands = new RasterBandInfo[raster.GetBandCount()];
        for (int i = 0; i < bands.Length; i++)
        {
            using var band = raster.GetBand(i);
            using var bandDefinition = band.GetDefinition();
            bands[i] = new(band.GetName(), bandDefinition.GetPixelType().ToString(), NoData(bandDefinition.GetNoDataValue()));
        }
        return new(path, definition.GetFormat(), crs, extent, width, height, size.Item1, size.Item2, northUp, bands);
    }

    public static RasterBounds Bounds(Envelope extent) =>
        new(extent.XMin, extent.YMin, extent.XMax, extent.YMax);

    private static bool IsNorthUp(Raster raster, RasterBounds extent, int width, int height, double x, double y)
    {
        if (!RasterSpec.SameSize(extent.XMax - extent.XMin, width * x)
            || !RasterSpec.SameSize(extent.YMax - extent.YMin, height * y))
            return false;
        var origin = raster.PixelToMap(0, 0);
        var east = raster.PixelToMap(width - 1, 0);
        var south = raster.PixelToMap(0, height - 1);
        return Math.Abs((east.Item1 - origin.Item1) / x - (width - 1)) <= 1e-6
            && Math.Abs((east.Item2 - origin.Item2) / y) <= 1e-6
            && Math.Abs((south.Item1 - origin.Item1) / x) <= 1e-6
            && Math.Abs((origin.Item2 - south.Item2) / y - (height - 1)) <= 1e-6;
    }

    private static object? NoData(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is Array values) return values.Cast<object?>().Select(NoData).ToArray();
        if (value is float f && !float.IsFinite(f)) return f.ToString(CultureInfo.InvariantCulture);
        if (value is double d && !double.IsFinite(d)) return d.ToString(CultureInfo.InvariantCulture);
        return value is string or float or double or int or uint or short or ushort or sbyte or byte or long or ulong
            ? value : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public object Summary() => new
    {
        path = Path, kind = "raster", format = Format,
        crs = new { wkid = Crs.Wkid, gcs_wkid = Crs.GcsWkid, wkt = Crs.Wkt, unknown = Crs.IsUnknown },
        extent = Extent.Summary(), width = Width, height = Height,
        cell_size = new { x = XSize, y = YSize }, north_up = NorthUp, band_count = Bands.Length, bands = Bands
    };

    public string BandPath(int band)
    {
        if (band < 1 || band > Bands.Length)
            throw new BridgeException("INVALID_ARGUMENT", "Band index is outside the raster's 1-based band range");
        return Bands.Length == 1 ? Path : System.IO.Path.Combine(Path, Bands[band - 1].name);
    }

    public static object Add(JsonElement args)
    {
        var map = MapOperations.Resolve(Contracts.Text(args, "project_id"), Contracts.Text(args, "map_id"));
        var raster = Read(Contracts.Text(args, "path"));
        var (layer, existing) = AddLayer(map, raster.Path, Contracts.OptionalText(args, "display_name"));
        return new { layer_id = layer.URI, map_id = map.URI, canonical_source = raster.Path, already_present = existing, name = layer.Name };
    }

    public static (RasterLayer Layer, bool AlreadyPresent) AddLayer(Map map, string path, string? name)
    {
        var existing = map.GetLayersAsFlattenedList().OfType<RasterLayer>().FirstOrDefault(
            layer => string.Equals(MapOperations.Source(layer), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return (existing, true);
        var parameters = new RasterLayerCreationParams(new Uri(path));
        if (name is not null) parameters.Name = name;
        return (LayerFactory.Instance.CreateLayer<RasterLayer>(parameters, map), false);
    }

    public static string? LayerSource(RasterLayer layer)
    {
        if (layer.GetDefinition() is not CIMRasterLayer { DataConnection: CIMStandardDataConnection connection }
            || connection.WorkspaceFactory is not (WorkspaceFactory.Raster or WorkspaceFactory.FileGDB))
            return null;
        // Layer.GetPath() can substitute the display caption for the raster dataset name.
        var workspace = new DbConnectionStringBuilder { ConnectionString = connection.WorkspaceConnectionString };
        if (workspace.TryGetValue("DATABASE", out var database) && database is string directory
            && System.IO.Path.IsPathFullyQualified(directory) && !string.IsNullOrWhiteSpace(connection.Dataset))
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, connection.Dataset));
        return null;
    }
}
