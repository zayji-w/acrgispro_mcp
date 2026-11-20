"""Geoprocessing MCP tools; native work is delegated to the bridge."""
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=READ)
    def pro_list_gp_tools() -> dict:
        """List the full geoprocessing catalog: tool names, parameter schemas, argument order, output kind and risk."""
        return bridge.request('list_gp_tools')

    @mcp.tool(annotations=READ)
    def pro_gp_tool_info(tool: str) -> dict:
        """Read the catalog entry (parameters, argument order, output kind, risk) for one GP tool."""
        listing = bridge.request('list_gp_tools')
        data = listing.get('data', listing) if isinstance(listing, dict) else {}
        tools = data.get('tools', {})
        if tool not in tools:
            raise ToolError('CAPABILITY_UNAVAILABLE: Geoprocessing tool is not registered: ' + tool)
        return {'catalog_version': data.get('catalog_version'), 'sha256': data.get('sha256'), 'tool': tool, 'schema': tools[tool]}

    @mcp.tool(annotations=WRITE)
    def pro_run_gp(project_id: str, map_id: str, tool: str, parameters: dict, idempotency_key: str) -> dict:
        """Submit any catalogued GP tool by name with named parameters and return job_id. Reuse the key after a lost response."""
        return bridge.request('run_gp', dict(project_id=project_id, map_id=map_id, tool=tool, parameters=parameters, idempotency_key=idempotency_key))

    @mcp.tool(annotations=WRITE)
    def pro_clip(project_id: str, map_id: str, input_path: str, clip_path: str, idempotency_key: str, output_name: str | None=None) -> dict:
        """Clip the full dataset by polygons into a new GDB. Sources and selections remain intact."""
        return run_gp(bridge, project_id, map_id, 'analysis.Clip', clean(dict(input_path=input_path, clip_path=clip_path, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_buffer(project_id: str, map_id: str, input_path: str, distance: float, idempotency_key: str, unit: str='Meters', line_side: str='FULL', line_end_type: str='ROUND', dissolve_option: str='NONE', dissolve_field: list[str] | None=None, method: str='PLANAR', output_name: str | None=None) -> dict:
        """Buffer full-dataset features by a distance with an explicit unit (e.g. distance=2, unit='Meters')."""
        return run_gp(bridge, project_id, map_id, 'analysis.Buffer', clean(dict(input_path=input_path, distance=distance, unit=unit, line_side=line_side, line_end_type=line_end_type, dissolve_option=dissolve_option, dissolve_field=dissolve_field, method=method, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_dissolve(project_id: str, map_id: str, input_path: str, idempotency_key: str, dissolve_field: list[str] | None=None, statistics_fields: str | None=None, multi_part: str='MULTI_PART', output_name: str | None=None) -> dict:
        """Dissolve features by optional fields; optionally compute statistics per group."""
        return run_gp(bridge, project_id, map_id, 'management.Dissolve', clean(dict(input_path=input_path, dissolve_field=dissolve_field, statistics_fields=statistics_fields, multi_part=multi_part, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_project(project_id: str, map_id: str, input_path: str, target_wkid: int, idempotency_key: str, transform_method: str | None=None, output_name: str | None=None) -> dict:
        """Project a full dataset to a target WKID into a new output; explicit datum transform when required."""
        return run_gp(bridge, project_id, map_id, 'management.Project', clean(dict(input_path=input_path, target_wkid=target_wkid, transform_method=transform_method, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_intersect(project_id: str, map_id: str, input_paths: list[str], idempotency_key: str, join_attributes: str='ALL', output_type: str='INPUT', output_name: str | None=None) -> dict:
        """Intersect two or more full datasets into a new feature class."""
        return run_gp(bridge, project_id, map_id, 'analysis.Intersect', clean(dict(input_paths=input_paths, join_attributes=join_attributes, output_type=output_type, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_union(project_id: str, map_id: str, input_paths: list[str], idempotency_key: str, join_attributes: str='ALL', output_name: str | None=None) -> dict:
        """Union two or more polygon datasets into a new feature class."""
        return run_gp(bridge, project_id, map_id, 'analysis.Union', clean(dict(input_paths=input_paths, join_attributes=join_attributes, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_erase(project_id: str, map_id: str, input_path: str, erase_path: str, idempotency_key: str, output_name: str | None=None) -> dict:
        """Erase polygon areas from a full dataset into a new feature class."""
        return run_gp(bridge, project_id, map_id, 'analysis.Erase', clean(dict(input_path=input_path, erase_path=erase_path, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_spatial_join(project_id: str, map_id: str, target_path: str, join_path: str, idempotency_key: str, join_operation: str='JOIN_ONE_TO_ONE', join_type: str='KEEP_ALL', match_option: str='INTERSECT', search_distance: str | None=None, output_name: str | None=None) -> dict:
        """Spatially join attributes from join_path onto target_path into a new feature class."""
        return run_gp(bridge, project_id, map_id, 'analysis.SpatialJoin', clean(dict(target_path=target_path, join_path=join_path, join_operation=join_operation, join_type=join_type, match_option=match_option, search_distance=search_distance, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_select_by_attribute(project_id: str, map_id: str, layer_id: str, idempotency_key: str, where_clause: str | None=None, selection_type: str='NEW_SELECTION') -> dict:
        """Select features of a live map layer by an SQL WHERE clause; returns selected_count."""
        return run_gp(bridge, project_id, map_id, 'management.SelectLayerByAttribute', clean(dict(layer_id=layer_id, where_clause=where_clause, selection_type=selection_type)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_select_by_location(project_id: str, map_id: str, layer_id: str, select_features_path: str, idempotency_key: str, overlap_type: str='INTERSECT', search_distance: str | None=None, selection_type: str='NEW_SELECTION') -> dict:
        """Select features of a live map layer by spatial relationship to another dataset; returns selected_count."""
        return run_gp(bridge, project_id, map_id, 'management.SelectLayerByLocation', clean(dict(layer_id=layer_id, select_features_path=select_features_path, overlap_type=overlap_type, search_distance=search_distance, selection_type=selection_type)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_feature_to_point(project_id: str, map_id: str, input_path: str, idempotency_key: str, point_location: str='INSIDE', output_name: str | None=None) -> dict:
        """Convert a full polygon dataset to representative points in a new feature class."""
        return run_gp(bridge, project_id, map_id, 'management.FeatureToPoint', clean(dict(input_path=input_path, point_location=point_location, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_merge(project_id: str, map_id: str, input_paths: list[str], idempotency_key: str, output_name: str | None=None) -> dict:
        """Merge multiple full datasets with the same schema into a new feature class."""
        return run_gp(bridge, project_id, map_id, 'management.Merge', clean(dict(input_paths=input_paths, output_name=output_name)), idempotency_key)

    @mcp.tool(annotations=WRITE)
    def pro_statistics(project_id: str, map_id: str, input_path: str, statistics_fields: str, idempotency_key: str, case_field: list[str] | None=None, output_name: str | None=None) -> dict:
        """Compute summary statistics (e.g. 'test_id SUM') into a new table."""
        return run_gp(bridge, project_id, map_id, 'analysis.Statistics', clean(dict(input_path=input_path, statistics_fields=statistics_fields, case_field=case_field, output_name=output_name)), idempotency_key)
