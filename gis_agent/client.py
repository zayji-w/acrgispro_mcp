"""Translate transport errors at the MCP boundary; GIS stays in the native bridge."""
import bridge_common
from contracts import BridgeError
from mcp.server.mcpserver.exceptions import ToolError


class BridgeClient:
    def __init__(self, send=None, read_status=None):
        self._send = bridge_common.request if send is None else send
        self._read_status = bridge_common.status if read_status is None else read_status

    def request(self, *args, **kwargs):
        try:
            return self._send(*args, **kwargs)
        except BridgeError as exc:
            raise ToolError(str(exc)) from exc

    def status(self):
        return self._read_status()
