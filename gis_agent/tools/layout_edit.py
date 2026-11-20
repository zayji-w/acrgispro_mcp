"""Layout Edit MCP tools; native work is delegated to the bridge."""
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=READ)
    def pro_list_layout_elements(project_id: str, layout_id: str, offset: int | None=None, limit: int | None=None) -> dict:
        """Read element dimensions and bounds in cm, text, font sizes and locks. Coordinates start at page bottom-left. Supply offset or limit to page results with next_offset."""
        return bridge.request('list_layout_elements', clean(dict(project_id=project_id, layout_id=layout_id, offset=offset, limit=limit)))

    @mcp.tool(annotations=WRITE)
    def pro_update_layout_elements(project_id: str, layout_id: str, edits: list[dict], allow_locked: bool=False) -> dict:
        """Edit 1-100 uniquely named existing elements. Each edit has element_name and optional expected_type, x_cm/y_cm (bounds bottom-left), width_cm/height_cm, scale, rotation, text, font_size, visible. All targets validate before changes; restore on SDK failure. Legend explicit dimensions use AdjustSize; scale changes fonts/patches/gaps with AdjustFrame. No project save."""
        return bridge.request('update_layout_elements', dict(project_id=project_id, layout_id=layout_id, edits=edits, allow_locked=allow_locked))

    @mcp.tool(annotations=WRITE)
    def pro_update_layout_element(project_id: str, layout_id: str, element_name: str, x_cm: float | None=None, y_cm: float | None=None, width_cm: float | None=None, height_cm: float | None=None, scale: float | None=None, rotation: float | None=None, text: str | None=None, font_size: float | None=None, visible: bool | None=None, expected_type: str | None=None, allow_locked: bool=False) -> dict:
        """Move/resize one existing legend, description box or text. Centimeter coordinates use page bottom-left. scale changes text size and bounds; legends scale content and auto-fit their frame. Exact unique name required."""
        edits = clean(dict(element_name=element_name, x_cm=x_cm, y_cm=y_cm, width_cm=width_cm, height_cm=height_cm, scale=scale, rotation=rotation, text=text, font_size=font_size, visible=visible, expected_type=expected_type))
        return pro_update_layout_elements(project_id, layout_id, [edits], allow_locked)

    @mcp.tool(annotations=WRITE)
    def pro_add_layout_text(project_id: str, layout_id: str, name: str, text: str, x_cm: float, y_cm: float, width_cm: float | None=None, height_cm: float | None=None, font_size: float=12, font_family: str='Microsoft YaHei', color: str='#222222', rotation: float=0) -> dict:
        """Add uniquely named literal text to a layout. Supply both width/height for a paragraph box; otherwise point text. Positions/bounds are in cm; fontsize in points. Does not accept markup or code."""
        return bridge.request('add_layout_text', clean(dict(project_id=project_id, layout_id=layout_id, name=name, text=text, x_cm=x_cm, y_cm=y_cm, width_cm=width_cm, height_cm=height_cm, font_size=font_size, font_family=font_family, color=color, rotation=rotation)))

    @mcp.tool(annotations=WRITE)
    def pro_place_city_name(project_id: str, layout_id: str, name: str, text: str, positions: list[dict], font_size: float=12, font_family: str='Microsoft YaHei', color: str='#222222') -> dict:
        """Place each character of a city name as an independently editable layout text element. positions contains one {x_cm,y_cm,rotation?,font_size?} per Unicode character, max64. Output names are name_01, name_02...; existing names are never replaced."""
        return bridge.request('place_city_name', dict(project_id=project_id, layout_id=layout_id, name=name, text=text, positions=positions, font_size=font_size, font_family=font_family, color=color))
