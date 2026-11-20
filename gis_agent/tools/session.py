"""Session MCP tools; native work is delegated to the bridge."""
import asyncio

from ..launcher import launch
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=READ)
    def bridge_status() -> dict:
        """Read connection, protocol and capabilities without launching Pro."""
        return bridge.status()

    @mcp.tool(annotations=WRITE)
    async def pro_launch(project_path: str | None=None, wait_seconds: int=25) -> dict:
        """Launch ArcGIS Pro, optionally with a local .aprx, and verify native bridge/project readiness. Reuses a running instance and reports project_mismatch when it has another project. Wait 1–30 seconds; retry a pending launch to check its existing process."""
        return await asyncio.to_thread(launch, bridge, project_path, wait_seconds)

    @mcp.tool(annotations=READ)
    def pro_version() -> dict:
        """Read native Pro and bridge runtime versions."""
        return bridge.request('version')

    @mcp.tool(annotations=READ)
    def pro_map_state(project_id: str | None=None, map_id: str | None=None, offset: int | None=None, limit: int | None=None) -> dict:
        """Read project/map/layer identifiers. Without paging, returns the complete view. Supply offset or limit to page map summaries. Supply project_id and map_id to page a map's layers. Use next_offset for the next page."""
        args = clean(dict(project_id=project_id, map_id=map_id, offset=offset, limit=limit))
        return bridge.request('map_state', args) if args else bridge.request('map_state')

    @mcp.tool(annotations=WRITE)
    def pro_add_demo_layer() -> dict:
        """Add three owned graphics for legacy diagnostics."""
        return bridge.request('add_demo')

    @mcp.tool(annotations=WRITE)
    def pro_remove_demo_layer() -> dict:
        """Remove only owned diagnostic graphics."""
        return bridge.request('remove_demo')

    @mcp.tool(annotations=WRITE)
    def pro_stop_bridge() -> dict:
        """Stop after cancellation of any running job; never close Pro or save the project."""
        return bridge.request('stop')
