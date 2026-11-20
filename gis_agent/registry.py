"""Explicit tool registry with strict validation before SDK coercion."""
import inspect
import types
from types import MappingProxyType
from typing import Union, get_args, get_origin, get_type_hints
from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError


def matches_type(value, expected):
    if expected is None or expected is type(None):
        return value is None
    origin = get_origin(expected)
    if origin in (types.UnionType, Union):
        return any(matches_type(value, arg) for arg in get_args(expected))
    if origin is list:
        args = get_args(expected)
        return isinstance(value, list) and (not args or all(matches_type(item, args[0]) for item in value))
    if origin is dict:
        return isinstance(value, dict)
    if expected is float:
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if expected is int:
        return isinstance(value, int) and not isinstance(value, bool)
    try:
        return isinstance(value, expected)
    except TypeError:
        return True


class StrictMCPServer(MCPServer):
    def __init__(self, *args, **kwargs):
        self._registered = {}
        super().__init__(*args, **kwargs)

    @property
    def functions(self):
        return MappingProxyType({name: entry[0] for name, entry in self._registered.items()})

    def add_tool(self, fn, name=None, **metadata):
        key = name or fn.__name__
        if key in self._registered:
            raise ValueError('Duplicate MCP tool: ' + key)
        signature, hints = inspect.signature(fn), get_type_hints(fn)
        super().add_tool(fn, name=name, **metadata)
        self._registered[key] = (fn, signature, hints)

    def remove_tool(self, name):
        super().remove_tool(name)
        self._registered.pop(name, None)

    async def call_tool(self, name, arguments, context=None):
        if not isinstance(arguments, dict):
            raise ToolError('INVALID_ARGUMENT: Arguments must be an object')
        entry = self._registered.get(name)
        if entry:
            _, signature, hints = entry
            try:
                signature.bind(**arguments)
            except TypeError as exc:
                raise ToolError('INVALID_ARGUMENT: Missing or unexpected tool argument') from exc
            for key, value in arguments.items():
                if key in hints and not matches_type(value, hints[key]):
                    raise ToolError('INVALID_ARGUMENT: Invalid type for ' + key)
        return await super().call_tool(name, arguments, context)
