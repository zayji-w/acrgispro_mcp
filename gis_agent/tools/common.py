"""Shared annotations and named-parameter GP forwarding."""
from mcp.types import ToolAnnotations

READ = ToolAnnotations(readOnlyHint=True, destructiveHint=False, openWorldHint=False)
WRITE = ToolAnnotations(readOnlyHint=False, destructiveHint=False, openWorldHint=False)


def clean(values):
    return {key: value for key, value in values.items() if value is not None}


def run_gp(bridge, project_id, map_id, tool, parameters, idempotency_key):
    return bridge.request('run_gp', dict(project_id=project_id, map_id=map_id, tool=tool,
                                       parameters=parameters, idempotency_key=idempotency_key))
