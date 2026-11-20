"""Maps MCP tools; native work is delegated to the bridge."""
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=READ)
    def pro_describe_dataset(path: str, include_count: bool=False) -> dict:
        """Describe a local shapefile or GDB feature class; optionally count all records."""
        return bridge.request('describe_dataset', dict(path=path, include_count=include_count))

    @mcp.tool(annotations=READ)
    def pro_read_table(
        path: str, fields: list[str] | None=None, order_by: str | None=None,
        direction: str='asc', offset: int=0, limit: int=50, include_count: bool=False,
    ) -> dict:
        """Read attribute rows from a local shapefile, File GDB feature class or table. Pass a statistics job's output path to read its values. Choose up to 32 fields and 100 rows. Follow pagination.next_offset. File GDB supports field sorting with ObjectID as tie-breaker. Shapefile uses ascending FID."""
        return bridge.request('read_table', clean(dict(
            path=path, fields=fields, order_by=order_by, direction=direction,
            offset=offset, limit=limit, include_count=include_count,
        )))

    @mcp.tool(annotations=WRITE)
    def pro_add_layer(project_id: str, map_id: str, path: str, display_name: str | None=None) -> dict:
        """Add local vector data to the specified 2D map. Reuse the same source if present."""
        args = dict(project_id=project_id, map_id=map_id, path=path)
        if display_name is not None:
            args['display_name'] = display_name
        return bridge.request('add_layer', args)

    @mcp.tool(annotations=WRITE)
    def pro_set_layer_visibility(project_id: str, map_id: str, layer_id: str, visible: bool) -> dict:
        """Set visibility only for the identified layer."""
        return bridge.request('set_layer_visibility', dict(project_id=project_id, map_id=map_id, layer_id=layer_id, visible=visible))

    @mcp.tool(annotations=WRITE)
    def pro_zoom_to_layer(project_id: str, map_id: str, layer_id: str) -> dict:
        """Zoom only when the active view belongs to the specified map."""
        return bridge.request('zoom_to_layer', dict(project_id=project_id, map_id=map_id, layer_id=layer_id))
