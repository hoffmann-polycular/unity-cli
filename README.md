# unity-cli

> Treat the Unity Editor like a filesystem. Inspect scenes, mutate objects, manage prefabs and assets — all from the terminal, composable with standard Unix tools.

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) [![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**No server to run. No config to write. No process to manage.**

---

Based on [unity-cli](https://github.com/youngwoocho02/unity-cli) by [DevBookOfArray](https://www.youtube.com/@DevBookOfArray), which established the core architecture: a single binary talking directly to Unity via HTTP, with zero-config instance discovery and a `[UnityCliTool]` extension point. This fork substantially expands on that foundation with a full scene inspection and mutation API, prefab lifecycle management, asset operations, a structured path grammar, and pipe-native output.

---

## What this does

unity-cli connects to a running Unity Editor and lets you work with it from the shell. The scene hierarchy behaves like a filesystem tree — `ls`, `find`, `inspect`, `get`, `set` work on GameObjects the way they work on files. Standard Unix tools (`jq`, `xargs`, `grep`, `awk`) compose naturally with the output.

```bash
# What's in the scene?
unity-cli ls -R

# Find every light and read its intensity
unity-cli find --component Light --plain | unity-cli get :Light.intensity

# Disable all UI canvases while in play mode
unity-cli find /UI --component Canvas --plain | \
    unity-cli set :GameObject.activeSelf false

# Diff prefab instances that have diverged
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides --plain | \
    unity-cli prefab diff

# Batch-update texture import settings
unity-cli find Assets/Sprites/ --type Texture2D --plain | \
    unity-cli set :Importer.maxTextureSize 512
```

---

## Features

**Scene inspection**
- `ls` — list children of any object, or all scene roots
- `find` — search by name, component, tag, layer, prefab, overrides, active state; also searches the asset database
- `inspect` — dump any object, component, or property as JSON
- `get` — read a single property value; scalar output composes with shell arithmetic
- `select` — bridge between the terminal and the Editor's Hierarchy/Inspector

**Scene mutation**
- `set` — write a property value; broadcasts to a fan-out from stdin or selection
- `create` — create GameObjects (empty, primitives, or prefab instances)
- `rm` — delete objects; accepts paths on stdin for batch removal
- `cp` / `mv` — copy or move objects in the hierarchy
- `reorder` — reorder siblings or components on a GameObject
- `component` — add, remove, or list components

**Prefab management**
- `prefab status/diff` — inspect overrides on instances
- `prefab apply/revert` — push or pull overrides at property, component, or instance granularity
- `prefab create/unpack/variant` — create assets, break connections, derive variants
- `prefab open/close` — enter prefab editing mode; all path commands rebase under the stage root

**Scene management**
- `scene list/open/save/close/reload/set-active/new/dirty` — full scene lifecycle

**Asset operations**
- `guid` / `path` — translate between asset paths and GUIDs
- `reimport` — force re-run of the import pipeline on one or many assets
- `reserialize` — rewrite assets through Unity's YAML serializer (safe after text edits)
- `:Importer` path suffix — read/write any importer setting like a property

**Editor control** (from the original project)
- `editor` — play/stop/pause/refresh, with `--wait` polling
- `console` — read and filter Unity log entries
- `exec` — run arbitrary C# inside the Editor
- `test` — run EditMode/PlayMode tests
- `menu` — execute any menu item by path
- `screenshot` / `profiler` — capture views and query frame data

**Shell integration**
- `completion` — tab completion for bash, zsh, fish, and PowerShell, including live hierarchy paths
- Pipe-native output: `ls`, `find`, `get` default to one-path-per-line plain output
- `--json` on every command for `jq` composition
- Null-delimited output (`--null-delimited`) for paths with spaces

---

## Documentation

| Guide | Description |
|-------|-------------|
| [Installation](docs/installation.md) | Binary install, Unity package setup, multi-instance, update |
| [Path Grammar](docs/paths.md) | How paths work: selection-as-cwd, fan-out, anchors, `:Importer`, `:GameObject` |
| [Command Reference](docs/commands.md) | All commands with options and examples |
| [Custom Tools](docs/custom-tools.md) | Writing `[UnityCliTool]` extensions for your project |

---

## Quick start

### 1. Install the CLI

**Linux / macOS**
```bash
curl -fsSL https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.sh | sh
```

**Windows (PowerShell)**
```powershell
irm https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.ps1 | iex
```

Add `--with-skill` / `-WithSkill` to also install the [Claude Code skill](docs/installation.md#claude-code-skill) that teaches Claude how to use unity-cli.

**Go install**
```bash
go install github.com/hoffmann-polycular/unity-cli@latest
```

### 2. Add the Unity package

In **Package Manager → Add package from git URL**:
```
https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector
```

Or add to `Packages/manifest.json`:
```json
"com.polycular.unity-cli-connector": "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector"
```

Once added, the Connector starts automatically when Unity opens and writes a heartbeat file so the CLI can find it. No configuration needed.

**Recommended:** Set **Edit → Preferences → General → Interaction Mode → No Throttling** so CLI commands are responsive when Unity is unfocused.

### 3. Use it

```bash
# Check the connection
unity-cli status

# Explore the scene
unity-cli ls -R
unity-cli find --component Rigidbody --plain | unity-cli inspect :Rigidbody

# Read and write properties
unity-cli get /Player:Transform.position
unity-cli set /Player:Rigidbody.mass 15

# Control play mode
unity-cli editor play --wait
unity-cli editor stop

# Run C# code directly
unity-cli exec "return Application.dataPath;"
```

---

## How it works

```
Terminal                              Unity Editor
────────                              ────────────
$ unity-cli set /Player:Rigidbody.mass 15
    │
    ├─ scans ~/.unity-cli/instances/*.json
    │  → finds Unity on port 8090
    │
    ├─ POST http://127.0.0.1:8090/command
    │  { "command": "set",
    │    "params": { "path": "/Player:Rigidbody.mass",
    │                "value": "15" }}
    │                                      │
    │                              CommandRouter dispatches
    │                                      │
    │                              SetProperty.HandleCommand()
    │                              → SerializedObject + ApplyModifiedProperties
    │                              → Undo registration
    │                                      │
    └─ { "success": true, "message": "Set." } ◄────┘
```

The Connector opens an HTTP server on localhost and writes a heartbeat file every 500ms with the current Editor state (`ready`, `compiling`, `playing`, …). The CLI reads that file to discover Unity instances and waits for a fresh heartbeat before sending commands if Unity is busy. All tool handlers run on Unity's main thread and share a serialized dispatch queue — concurrent CLI calls never race.

See [Installation](docs/installation.md) for more detail on the Connector internals.

---

## Command overview

| Command | Category | Description |
|---------|----------|-------------|
| `ls` | Inspect | List children of a GameObject or scene roots |
| `find` | Inspect | Search scene or asset database with filters |
| `inspect` | Inspect | Dump object/component/property as JSON |
| `get` | Inspect | Read a single property value |
| `select` | Inspect | Get or set the Editor's selection |
| `set` | Mutate | Write a property value |
| `create` | Mutate | Create a GameObject or prefab instance |
| `rm` | Mutate | Delete a GameObject |
| `cp` | Mutate | Copy a GameObject in the hierarchy |
| `mv` | Mutate | Move or rename a GameObject |
| `reorder` | Mutate | Reorder siblings or components |
| `component` | Mutate | Add, remove, or list components |
| `prefab` | Prefabs | Prefab status, diff, apply, revert, create, unpack, variant, open/close |
| `scene` | Scenes | List, open, save, close, reload, set-active, new |
| `guid` | Assets | Translate asset path → GUID |
| `path` | Assets | Translate GUID → asset path |
| `reimport` | Assets | Force re-import of assets |
| `reserialize` | Assets | Rewrite assets through Unity's YAML serializer |
| `editor` | Editor | Play/stop/pause/refresh |
| `console` | Editor | Read Unity log entries |
| `exec` | Editor | Run arbitrary C# code |
| `test` | Editor | Run EditMode/PlayMode tests |
| `menu` | Editor | Execute a menu item by path |
| `screenshot` | Editor | Capture scene or game view |
| `profiler` | Editor | Query profiler hierarchy and control recording |
| `status` | Tooling | Show Unity connection state |
| `list` | Tooling | List all registered tools and their schemas |
| `completion` | Tooling | Print shell completion script |
| `update` | Tooling | Self-update the CLI binary |

Full documentation with options and examples: [docs/commands.md](docs/commands.md)

---

## Global options

| Flag | Description | Default |
|------|-------------|---------|
| `--port <N>` | Select Unity instance by port | auto |
| `--project <path>` | Select Unity instance by project path | latest |
| `--timeout <ms>` | HTTP request timeout | 120000 |
| `--ignore-version-mismatch` | Run even when CLI and connector versions differ | false |

---

## License

GPLv3 and MIT. Commercial license available for enterprises and closed-source projects. Contact [info@polycular.com](mailto:info@polycular.com).

---

## Authors

**This fork** — [Polycular GmbH](https://www.polycular.com/) — expanded scene inspection and mutation, prefab management, scene management, asset operations, path grammar, and pipe-native composition.

**Original project** — [DevBookOfArray](https://www.youtube.com/@DevBookOfArray) ([GitHub](https://github.com/youngwoocho02)) — core architecture: single-binary HTTP client, zero-config instance discovery, `[UnityCliTool]` auto-discovery, `editor`, `console`, `exec`, `test`, `menu`, `reserialize`, `screenshot`, `profiler`, `status`, `list`, `update`.
