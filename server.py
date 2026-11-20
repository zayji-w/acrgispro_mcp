"""Stable stdio entry point. Tool implementation lives in gis_agent/tools/."""
from gis_agent.app import create_server

mcp = create_server()

if __name__ == "__main__":
    mcp.run(transport="stdio")
