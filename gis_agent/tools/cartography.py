"""Cartography MCP tools; native work is delegated to the bridge."""
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=WRITE)
    def pro_set_simple_renderer(project_id: str, map_id: str, layer_id: str, color: str, outline_color: str='#333333', width: float=1.5, size: float=8, outline_width: float=0.5) -> dict:
        """Style one point/line/polygon layer with #RRGGBB colors; sizes in points. Does not save the project."""
        return bridge.request('set_simple_renderer', dict(project_id=project_id, map_id=map_id, layer_id=layer_id, color=color, outline_color=outline_color, width=width, size=size, outline_width=outline_width))

    @mcp.tool(annotations=WRITE)
    def pro_set_unique_value_renderer(project_id: str, map_id: str, layer_id: str, field: str,
                                      colors: list[str] | None = None, category_limit: int = 32,
                                      show_other: bool = True, other_label: str = '其他值') -> dict:
        """Style a feature layer by one text or integer field. Colors are #RRGGBB in returned class order. Over-limit classes are rejected. Returns the applied renderer including legend labels."""
        return bridge.request('set_unique_value_renderer', clean(dict(
            project_id=project_id, map_id=map_id, layer_id=layer_id, field=field, colors=colors,
            category_limit=category_limit, show_other=show_other, other_label=other_label)))

    @mcp.tool(annotations=WRITE)
    def pro_set_graduated_colors_renderer(project_id: str, map_id: str, layer_id: str, field: str,
                                          method: str | None = None, class_count: int | None = None,
                                          breaks: list[float] | None = None, colors: list[str] | None = None,
                                          show_other: bool = True, other_label: str = '无数据或超出范围') -> dict:
        """Apply color classes to a numeric field. Automatic methods: natural_breaks (default), equal_interval, quantile. For manual supply increasing edges [minimum, upper1, upper2, …]. Colors are #RRGGBB. Read the actual class boundaries in the result."""
        return bridge.request('set_graduated_colors_renderer', clean(dict(
            project_id=project_id, map_id=map_id, layer_id=layer_id, field=field, method=method,
            class_count=class_count, breaks=breaks, colors=colors, show_other=show_other,
            other_label=other_label)))

    @mcp.tool(annotations=READ)
    def pro_get_thematic_renderer(project_id: str, map_id: str, layer_id: str) -> dict:
        """Read the actual thematic renderer, with categories or numeric class boundaries, legend labels and colors."""
        return bridge.request('get_thematic_renderer', dict(project_id=project_id, map_id=map_id, layer_id=layer_id))

    @mcp.tool(annotations=WRITE)
    def pro_set_labels(project_id: str, map_id: str, layer_id: str, field: str, visible: bool=True, font_size: float=10, color: str='#222222', font_family: str='Microsoft YaHei', class_name: str | None=None) -> dict:
        """Label features by an existing field. Use class_name to edit one existing label class. Without a name, edit the first class and use visible for the whole layer as before."""
        return bridge.request('set_labels', clean(dict(project_id=project_id, map_id=map_id, layer_id=layer_id, field=field, visible=visible, font_size=font_size, color=color, font_family=font_family, class_name=class_name)))

    @mcp.tool(annotations=READ)
    def pro_list_layouts(project_id: str | None=None, offset: int | None=None, limit: int | None=None) -> dict:
        """Read layouts. Supply offset or limit to page layout summaries. Use pro_list_layout_elements to read elements in a layout."""
        args = clean(dict(project_id=project_id, offset=offset, limit=limit))
        return bridge.request('list_layouts', args) if args else bridge.request('list_layouts')

    @mcp.tool(annotations=READ)
    def pro_layer_presentation(project_id: str, map_id: str, layer_id: str) -> dict:
        """Read actual renderer and label settings for verification before/after styling."""
        return bridge.request('layer_presentation', dict(project_id=project_id, map_id=map_id, layer_id=layer_id))

    @mcp.tool(annotations=WRITE)
    def pro_create_layout(project_id: str, map_id: str, extent_layer_id: str, name: str, title: str, orientation: str='auto', page_size: str='A4', legend: bool=True, scale_bar: bool=True, north_arrow: bool=True) -> dict:
        """Create a new layout with title, map, legend, scale bar and north arrow. Auto orientation uses the study layer extent in map CRS; supply the study-area polygon layer, not the map view shape. Never replaces a layout or saves the project."""
        return bridge.request('create_layout', dict(project_id=project_id, map_id=map_id, extent_layer_id=extent_layer_id, name=name, title=title, orientation=orientation, page_size=page_size, legend=legend, scale_bar=scale_bar, north_arrow=north_arrow))

    @mcp.tool(annotations=WRITE)
    def pro_open_layout(project_id: str, layout_id: str) -> dict:
        """Open the identified layout in a visible ArcGIS Pro pane."""
        return bridge.request('open_layout', dict(project_id=project_id, layout_id=layout_id))

    @mcp.tool(annotations=WRITE)
    def pro_export_layout(project_id: str, layout_id: str, idempotency_key: str, format: str='PNG', dpi: int=300, output_name: str='map') -> dict:
        """Export an existing layout to a new PNG/PDF (72-600 dpi). Returns job_id; poll pro_job_status. Never overwrites files. Native render cannot be interrupted mid-export."""
        return bridge.request('export_layout', dict(project_id=project_id, layout_id=layout_id, idempotency_key=idempotency_key, format=format, dpi=dpi, output_name=output_name))

    @mcp.tool(annotations=WRITE)
    def pro_export_map(project_id: str, map_id: str, idempotency_key: str, dpi: int=300, width: int=2400, height: int=1800, output_name: str='map') -> dict:
        """Export the active 2D map directly to a new PNG, without a layout. Returns job_id; poll status. Pixel dimensions are explicit and capped at 40 megapixels."""
        return bridge.request('export_map', dict(project_id=project_id, map_id=map_id, idempotency_key=idempotency_key, dpi=dpi, width=width, height=height, output_name=output_name))
