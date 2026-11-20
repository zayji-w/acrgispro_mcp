"""Typed raster tools backed by the live ArcGIS Pro bridge."""

from .common import READ, WRITE, clean


def register(mcp, bridge):
    @mcp.tool(annotations=READ)
    def pro_describe_raster(path: str) -> dict:
        """Read a local raster's CRS, grid, band count, pixel type and NoData metadata."""
        return bridge.request('describe_raster', dict(path=path))

    @mcp.tool(annotations=WRITE)
    def pro_add_raster_layer(project_id: str, map_id: str, path: str, display_name: str | None = None) -> dict:
        """Add a local raster to the specified 2D map, reusing an existing layer for the same source."""
        return bridge.request('add_raster_layer', clean(dict(
            project_id=project_id, map_id=map_id, path=path, display_name=display_name)))

    @mcp.tool(annotations=WRITE)
    def pro_raster_clip(project_id: str, map_id: str, input_path: str, idempotency_key: str,
                        mask_path: str | None = None, bbox: list[float] | None = None,
                        add_to_map: bool = True, output_name: str = 'raster_clip') -> dict:
        """Clip to a polygon mask or an explicit [xmin,ymin,xmax,ymax] in the source CRS. Use exactly one. Preserve the source grid."""
        return bridge.request('raster_clip', clean(dict(
            project_id=project_id, map_id=map_id, input_path=input_path,
            idempotency_key=idempotency_key, mask_path=mask_path, bbox=bbox,
            add_to_map=add_to_map, output_name=output_name)))

    @mcp.tool(annotations=WRITE)
    def pro_raster_project(project_id: str, map_id: str, input_path: str, target_wkid: int,
                           cell_size: float, idempotency_key: str, resampling: str = 'NEAREST',
                           transform_method: str | None = None, snap_path: str | None = None,
                           add_to_map: bool = True, output_name: str = 'raster_project') -> dict:
        """Reproject to a WKID and explicit square cell size in target units. Optional snap raster must match both. Specify a datum transform when the geographic CRS changes."""
        return bridge.request('raster_project', clean(dict(
            project_id=project_id, map_id=map_id, input_path=input_path, target_wkid=target_wkid,
            cell_size=cell_size, idempotency_key=idempotency_key, resampling=resampling,
            transform_method=transform_method, snap_path=snap_path, add_to_map=add_to_map,
            output_name=output_name)))

    @mcp.tool(annotations=WRITE)
    def pro_raster_resample(project_id: str, map_id: str, input_path: str, cell_size: float,
                            idempotency_key: str, resampling: str = 'NEAREST',
                            add_to_map: bool = True, output_name: str = 'raster_resample') -> dict:
        """Change raster square cell size in source units with the same CRS and lower-left origin. Methods: NEAREST, BILINEAR, CUBIC, MAJORITY."""
        return bridge.request('raster_resample', dict(
            project_id=project_id, map_id=map_id, input_path=input_path, cell_size=cell_size,
            idempotency_key=idempotency_key, resampling=resampling, add_to_map=add_to_map,
            output_name=output_name))

    @mcp.tool(annotations=WRITE)
    def pro_raster_index(project_id: str, map_id: str, index: str, positive_path: str,
                         negative_path: str, idempotency_key: str, positive_band: int = 1,
                         negative_band: int = 1, add_to_map: bool = True,
                         output_name: str = 'raster_index') -> dict:
        """Compute (positive-negative)/(positive+negative) on matching grids. Bands are 1-based. Ordered bands: NDVI NIR/Red, NDWI Green/NIR, MNDWI Green/SWIR1, NDMI NIR/SWIR1, NDBI SWIR1/NIR. NoData and zero denominators produce NoData."""
        return bridge.request('raster_index', dict(
            project_id=project_id, map_id=map_id, index=index, positive_path=positive_path,
            negative_path=negative_path, idempotency_key=idempotency_key, positive_band=positive_band,
            negative_band=negative_band, add_to_map=add_to_map, output_name=output_name))

    @mcp.tool(annotations=WRITE)
    def pro_raster_zonal_statistics(project_id: str, map_id: str, zone_path: str, zone_field: str,
                                    value_path: str, idempotency_key: str,
                                    statistics: str = 'MEAN', ignore_nodata: bool = True,
                                    output_name: str = 'zonal_statistics') -> dict:
        """Summarize a single-band raster by polygon zones in the same CRS into a new File GDB table. Use pro_read_table on the output. Poll pro_job_status for completion."""
        return bridge.request('raster_zonal_statistics', dict(
            project_id=project_id, map_id=map_id, zone_path=zone_path, zone_field=zone_field,
            value_path=value_path, idempotency_key=idempotency_key, statistics=statistics,
            ignore_nodata=ignore_nodata, output_name=output_name))
