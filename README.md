# Unity MCP Addressables

A Unity Editor package that exposes [Addressable Asset System](https://docs.unity3d.com/Packages/com.unity.addressables@latest) configuration as an [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) server, enabling AI coding assistants like Claude Code to read and modify Addressables groups, entries, labels, and settings.

## Architecture

```
AI Client (Claude Code / OpenCode / ...)
    | MCP Protocol (stdio)
    v
Python MCP Server (server.py)
    | HTTP (localhost:8091)
    v
Unity Editor HTTP Server (McpHttpServer.cs)
    |
    v
Unity Addressables Editor API
```

The Python process acts as a thin MCP-to-HTTP bridge. The real work happens inside the Unity Editor on the main thread, using the Addressables Editor API and `AssetDatabase`.

## Requirements

- **Unity** 2021.3 or later
- **Addressables** 1.21.0 or later
- **Python** 3.10 or later

## Installation

### Option A: Unity Package Manager (Git URL)

1. Open Unity, go to **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter:
   ```
   https://github.com/StromKuo/Unity-MCP-Addressables.git
   ```

### Option B: Git Submodule

```bash
git submodule add https://github.com/StromKuo/Unity-MCP-Addressables.git Packages/com.strodio.unity-mcp-addressables
```

### Option C: Local Clone

```bash
cd YourProject/Packages
git clone https://github.com/StromKuo/Unity-MCP-Addressables.git com.strodio.unity-mcp-addressables
```

## Setup

### 1. Setup Python Environment

Go to **Tools > MCP Addressables > Setup Python Environment**.

This will:
- Find a suitable Python 3.10+ interpreter on your system
- Create a virtual environment inside the package (`MCP~/venv/`)
- Install the Python dependencies (`mcp`, `httpx`)

### 2. Configure Your AI Client

Go to **Tools > MCP Addressables > Copy MCP Config** to copy the MCP server configuration JSON to your clipboard.

The config looks like this:

```json
{
  "mcpServers": {
    "unity-addressables": {
      "command": "/path/to/Packages/com.strodio.unity-mcp-addressables/MCP~/venv/bin/python",
      "args": ["/path/to/Packages/com.strodio.unity-mcp-addressables/MCP~/server.py"]
    }
  }
}
```

Paste it into your AI client's MCP settings:
- **Claude Code**: `~/.claude/settings.json`
- **OpenCode**: `~/.config/opencode/config.json` (under the `mcp_servers` section)

### 3. Verify

Go to **Tools > MCP Addressables > Check Environment** to verify everything is set up correctly. You should see:

```
System Python 3.10+:  OK
Virtual Env:          OK
Dependencies:         OK
HTTP Server:          Running (port 8091)
```

## Available MCP Tools

### Read Tools

#### `list_groups`

List all Addressable groups with their name, entry count, schema types, and read-only status.

```
list_groups()
```

#### `get_group_entries`

Get all entries in a group with address, GUID, asset path, type, and labels.

```
get_group_entries(group_name="AUTO_Atlas")
```

#### `get_group_settings`

Get schema settings for a group (bundle mode, compression, build/load paths, etc.).

```
get_group_settings(group_name="AUTO_Atlas")
```

#### `get_entry_dependencies`

Get all asset dependencies for each entry in a group. Shows what assets will actually be included in the bundle.

```
get_entry_dependencies(group_name="AUTO_Atlas")
```

#### `find_entry_by_address`

Find which group contains an entry with the given Addressable address.

```
find_entry_by_address(address="Assets/Prefabs/Enemy.prefab")
```

#### `get_addressables_settings`

Get global Addressables settings including profiles, active profile, build configuration, and label count.

```
get_addressables_settings()
```

#### `analyze_group_dependencies`

Analyze cross-group dependencies to find assets shared between multiple groups but not addressable themselves. These assets will be **duplicated** in each bundle.

```
analyze_group_dependencies()
```

#### `list_labels`

List all Addressable labels defined in the project.

```
list_labels()
```

### Write Tools

#### `create_group`

Create a new Addressable group. Adds `BundledAssetGroupSchema` and `ContentUpdateGroupSchema` by default.

```
create_group(name="MyNewGroup")
create_group(name="MyGroup", schemas=["BundledAssetGroupSchema"])
```

#### `move_entries`

Move entries to a different group by their GUIDs.

```
move_entries(guids=["abc123", "def456"], target_group="TargetGroup")
```

#### `set_entry_address`

Change the Addressable address of an entry.

```
set_entry_address(guid="abc123", address="NewAddress")
```

#### `add_entry`

Add an asset to an Addressable group.

```
add_entry(asset_path="Assets/Prefabs/Enemy.prefab", group_name="MyGroup")
add_entry(asset_path="Assets/Prefabs/Enemy.prefab", group_name="MyGroup", address="Enemy")
```

#### `remove_entry`

Remove an entry from its Addressable group (the asset itself is not deleted).

```
remove_entry(guid="abc123")
```

#### `set_entry_labels`

Set labels on an entry. Labels that don't exist will be created automatically.

```
set_entry_labels(guid="abc123", labels=["combat", "preload"])
set_entry_labels(guid="abc123", labels=["combat"], exclusive=True)
```

#### `rename_group`

Rename an Addressable group.

```
rename_group(old_name="OldName", new_name="NewName")
```

## Unity Editor Menu

All menu items are under **Tools > MCP Addressables**:

| Menu Item | Description |
|---|---|
| Start Server | Start the HTTP server (auto-starts on editor launch) |
| Stop Server | Stop the HTTP server |
| Setup Python Environment | Create venv and install dependencies |
| Check Environment | Verify all components are working |
| Copy MCP Config | Copy MCP server config JSON to clipboard |
| Server Status | Show current server status |

## How It Works

- The **Unity HTTP server** (`McpHttpServer.cs`) starts automatically when the editor opens via `[InitializeOnLoad]`. It listens on `localhost:8091`.
- GET requests handle read operations; POST requests with JSON bodies handle write operations.
- HTTP requests from the Python bridge are queued and processed on Unity's main thread (required by Addressables Editor APIs).
- The **Python MCP server** (`MCP~/server.py`) translates MCP tool calls into HTTP requests. The `MCP~` directory is ignored by Unity's asset importer (directories ending with `~` are excluded).
- The Python venv lives inside the package at `MCP~/venv/` and is excluded from version control via `.gitignore`.
- Write operations automatically call `SetDirty()` and `AssetDatabase.SaveAssets()` to persist changes.

## Troubleshooting

**"Cannot connect to Unity Editor"**
- Make sure Unity Editor is open and focused (the HTTP server runs in the editor process)
- Check **Tools > MCP Addressables > Server Status**
- Try **Tools > MCP Addressables > Stop Server**, then **Start Server**

**"Addressables settings not found"**
- Ensure the Addressables package is installed and initialized in your project
- Open **Window > Asset Management > Addressables > Groups** to create default settings

**Timeouts on dependency analysis**
- `analyze_group_dependencies` and `get_entry_dependencies` scan all assets and may take time on large projects. The timeout is set to 180 seconds for these operations.

**Python setup fails**
- Ensure Python 3.10+ is installed: `python3 --version`
- On macOS with Homebrew: `brew install python@3.12`
- On Windows: download from https://www.python.org/downloads/

## License

MIT
