"""Jobs MCP tools; native work is delegated to the bridge."""
from mcp.server.mcpserver.exceptions import ToolError
from .common import READ, WRITE, clean, run_gp

def register(mcp, bridge):

    @mcp.tool(annotations=READ)
    def pro_job_status(job_id: str) -> dict:
        """Read persisted task state, messages, validation, outputs and map binding."""
        return bridge.request('job_status', {'job_id': job_id})

    @mcp.tool(annotations=WRITE)
    def pro_cancel_job(job_id: str) -> dict:
        """Request cooperative cancellation; query status for the actual terminal outcome."""
        return bridge.request('cancel_job', {'job_id': job_id})
