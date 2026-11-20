"""Composition root: explicitly register tools against one injected bridge client."""
from . import VERSION
from .client import BridgeClient
from .registry import StrictMCPServer
from .tools import session, maps, geoprocessing, jobs, cartography, layout_edit, raster

INSTRUCTIONS = 'Use pro_launch when asked to start Pro. Use bridge_status then pro_map_state. Native SDK tools operate the live project. Vector GP is catalog-driven: call pro_list_gp_tools, then pro_run_gp with the exact catalogued tool name and named parameters (or use a typed wrapper). For imagery use pro_describe_raster, pro_add_raster_layer and the pro_raster_* tools. For thematic maps apply a unique-value or graduated-colors renderer, verify it with pro_get_thematic_renderer, then create and export a layout. Poll pro_job_status for jobs. Full-dataset inputs, new outputs only, no arbitrary code and no project save.'


def create_server(bridge=None):
    bridge = BridgeClient() if bridge is None else bridge
    mcp = StrictMCPServer('arcgispro-safe', version=VERSION, instructions=INSTRUCTIONS)
    session.register(mcp, bridge)
    maps.register(mcp, bridge)
    geoprocessing.register(mcp, bridge)
    jobs.register(mcp, bridge)
    cartography.register(mcp, bridge)
    layout_edit.register(mcp, bridge)
    raster.register(mcp, bridge)
    return mcp
