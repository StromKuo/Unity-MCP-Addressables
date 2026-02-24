"""Unity MCP Addressables Bridge - forwards MCP tool calls to Unity Editor HTTP API."""

import os

import httpx
from mcp.server.fastmcp import FastMCP

UNITY_BASE_URL = "http://localhost:8091"
TIMEOUT = 60.0
LONG_TIMEOUT = 180.0  # For expensive operations like dependency analysis

mcp = FastMCP("Unity Addressables")

# Bypass system proxy for localhost connections to Unity Editor.
os.environ.setdefault("NO_PROXY", "localhost,127.0.0.1")
_http = httpx.Client(timeout=TIMEOUT, trust_env=True)


def _get(endpoint: str, params: dict | None = None, timeout: float | None = None) -> dict:
    """Send GET request to Unity HTTP server."""
    try:
        resp = _http.get(
            f"{UNITY_BASE_URL}{endpoint}",
            params=params,
            timeout=timeout,
        )
        resp.raise_for_status()
        return resp.json()
    except httpx.ConnectError:
        return {"error": "Cannot connect to Unity Editor. Make sure Unity is open and MCP Addressables server is running (port 8091)."}
    except httpx.HTTPStatusError as e:
        try:
            return e.response.json()
        except Exception:
            return {"error": f"HTTP {e.response.status_code}: {e.response.text}"}
    except Exception as e:
        return {"error": str(e)}


def _post(endpoint: str, json_body: dict, timeout: float | None = None) -> dict:
    """Send POST request to Unity HTTP server."""
    try:
        resp = _http.post(
            f"{UNITY_BASE_URL}{endpoint}",
            json=json_body,
            timeout=timeout,
        )
        resp.raise_for_status()
        return resp.json()
    except httpx.ConnectError:
        return {"error": "Cannot connect to Unity Editor. Make sure Unity is open and MCP Addressables server is running (port 8091)."}
    except httpx.HTTPStatusError as e:
        try:
            return e.response.json()
        except Exception:
            return {"error": f"HTTP {e.response.status_code}: {e.response.text}"}
    except Exception as e:
        return {"error": str(e)}


# ─── READ tools ───


@mcp.tool()
def list_groups() -> dict:
    """List all Addressable groups with their name, entry count, schema types, and read-only status.

    Returns:
        List of all Addressable groups.
    """
    return _get("/api/list-groups")


@mcp.tool()
def get_group_entries(group_name: str) -> dict:
    """Get all entries in an Addressable group.

    Args:
        group_name: Name of the Addressable group.

    Returns:
        List of entries with address, GUID, asset path, type, and labels.
    """
    return _get("/api/get-group-entries", {"group_name": group_name})


@mcp.tool()
def get_group_settings(group_name: str) -> dict:
    """Get the schema settings for an Addressable group (BundledAssetGroupSchema, ContentUpdateGroupSchema).

    Args:
        group_name: Name of the Addressable group.

    Returns:
        Group settings including bundle mode, compression, build/load paths, etc.
    """
    return _get("/api/get-group-settings", {"group_name": group_name})


@mcp.tool()
def get_entry_dependencies(group_name: str) -> dict:
    """Get all asset dependencies for each entry in a group. Shows what assets will actually be included in the bundle.

    Args:
        group_name: Name of the Addressable group.

    Returns:
        List of entries with their dependency lists.
    """
    return _get("/api/get-entry-dependencies", {"group_name": group_name}, timeout=LONG_TIMEOUT)


@mcp.tool()
def find_entry_by_address(address: str) -> dict:
    """Find which group contains an entry with the given Addressable address.

    Args:
        address: The Addressable address string to search for.

    Returns:
        Entry details including group name, GUID, asset path, and labels.
    """
    return _get("/api/find-entry-by-address", {"address": address})


@mcp.tool()
def get_addressables_settings() -> dict:
    """Get global Addressables settings including profiles, build configuration, and label count.

    Returns:
        Global settings overview.
    """
    return _get("/api/get-addressables-settings")


@mcp.tool()
def analyze_group_dependencies() -> dict:
    """Analyze cross-group dependencies to find assets that are shared between multiple groups but not addressable themselves. These assets will be duplicated in each bundle.

    Returns:
        List of shared assets sorted by reference count, with the groups that reference them.
    """
    return _get("/api/analyze-group-dependencies", timeout=LONG_TIMEOUT)


@mcp.tool()
def list_labels() -> dict:
    """List all Addressable labels defined in the project.

    Returns:
        List of label strings.
    """
    return _get("/api/list-labels")


# ─── WRITE tools ───


@mcp.tool()
def create_group(name: str, schemas: list[str] | None = None) -> dict:
    """Create a new Addressable group with default schemas.

    Args:
        name: Name for the new group.
        schemas: Optional list of schema type names to add (e.g. ["BundledAssetGroupSchema"]). Defaults to both BundledAssetGroupSchema and ContentUpdateGroupSchema.

    Returns:
        Result with success status and group name.
    """
    body = {"name": name}
    if schemas:
        body["schemas"] = schemas
    return _post("/api/create-group", body)


@mcp.tool()
def move_entries(guids: list[str], target_group: str) -> dict:
    """Move Addressable entries to a different group.

    Args:
        guids: List of asset GUIDs to move.
        target_group: Name of the target group.

    Returns:
        Result with moved count and any errors.
    """
    return _post("/api/move-entries", {"guids": guids, "target_group": target_group})


@mcp.tool()
def set_entry_address(guid: str, address: str) -> dict:
    """Change the Addressable address of an entry.

    Args:
        guid: GUID of the asset entry.
        address: New address string.

    Returns:
        Result with old and new address.
    """
    return _post("/api/set-entry-address", {"guid": guid, "address": address})


@mcp.tool()
def add_entry(asset_path: str, group_name: str, address: str | None = None) -> dict:
    """Add an asset to an Addressable group.

    Args:
        asset_path: Unity asset path (e.g. "Assets/Prefabs/Enemy.prefab").
        group_name: Name of the target group.
        address: Optional custom address. Defaults to the asset path.

    Returns:
        Result with GUID, address, and group info.
    """
    body = {"asset_path": asset_path, "group_name": group_name}
    if address:
        body["address"] = address
    return _post("/api/add-entry", body)


@mcp.tool()
def remove_entry(guid: str) -> dict:
    """Remove an entry from its Addressable group (the asset itself is not deleted).

    Args:
        guid: GUID of the asset entry to remove.

    Returns:
        Result with removed address and group name.
    """
    return _post("/api/remove-entry", {"guid": guid})


@mcp.tool()
def set_entry_labels(guid: str, labels: list[str], exclusive: bool = False) -> dict:
    """Set labels on an Addressable entry. Labels that don't exist will be created.

    Args:
        guid: GUID of the asset entry.
        labels: List of label strings to apply.
        exclusive: If true, remove all existing labels before applying new ones. Default false (additive).

    Returns:
        Result with current labels after modification.
    """
    return _post("/api/set-entry-labels", {"guid": guid, "labels": labels, "exclusive": exclusive})


@mcp.tool()
def rename_group(old_name: str, new_name: str) -> dict:
    """Rename an Addressable group.

    Args:
        old_name: Current group name.
        new_name: New group name.

    Returns:
        Result with old and new names.
    """
    return _post("/api/rename-group", {"old_name": old_name, "new_name": new_name})


if __name__ == "__main__":
    mcp.run()
