# unity-cli Reference

A command-line interface for the Unity Editor. Inspect and mutate scenes, objects,
components, assets, and prefabs from the terminal — composable via standard GNU
tools (pipes, `jq`, `xargs`, `grep`, `awk`).

---

## Table of Contents

- [Philosophy](#philosophy)
- [Architecture](#architecture)
- [Path Grammar](#path-grammar)
- [Reference Resolution](#reference-resolution)
- [Output Formats](#output-formats)
- [Command Reference](#command-reference)
  - Already implemented (existing tools)
    - [exec](#exec)
    - [editor](#editor)
    - [console](#console)
    - [menu](#menu)
    - [screenshot](#screenshot)
    - [reserialize](#reserialize)
    - [profiler](#profiler)
    - [test](#test)
    - [status](#status)
    - [list](#list)
    - [update](#update)
    - [Custom tools](#custom-tools)
  - Planned additions (this doc's todo list)
    - [ls](#ls)
    - [find](#find)
    - [inspect](#inspect)
    - [get](#get)
    - [set](#set)
    - [component](#component)
    - [select](#select)
    - [create](#create)
    - [delete](#delete)
    - [find-asset](#find-asset)
    - [prefab](#prefab)
- [Common Usage Examples](#common-usage-examples)
- [Tips and Tricks](#tips-and-tricks)

---

## Philosophy

unity-cli treats the Unity Editor as a **live, queryable filesystem-like
structure**. The scene hierarchy is a tree of objects. Components hang off
objects. Properties hang off components. Assets live under `Assets/`. All of it
is addressable by path, inspectable as JSON, and mutable with structured
commands.

**Three core principles:**

1. **Stateless RPC, not a shell session.** Each invocation is independent. The
   state lives in Unity — in the scene graph, project assets, and editor
   settings — not in a persistent CLI process. Domain reloads don't matter.
2. **Do one thing, emit structured output.** Every tool follows the Unix
   convention: small, focused, pipeable. Default output is human-readable;
   `--json` and `--plain` flags unlock composition.
3. **Paths are the universal address.** A GameObject, a component, a property,
   an asset, even a property *reference* to another object — all of them are
   addressed by path. The same path that appears in `ls` output can be piped
   into `set`, `inspect`, `component`, etc. without translation.

**What this is not:**

- Not a REPL. There is no variable persistence between calls because there is
  no need — the scene persists the only state that matters.
- Not a replacement for the Editor UI. It complements it. Select in the
  Hierarchy, pipe to terminal. Find in terminal, highlight in Editor.

---

## Architecture

unity-cli is a thin Go terminal client that talks to a persistent HTTP server
running **inside** the Unity Editor. The server is registered via
`[InitializeOnLoad]` (`HttpServer.cs`) and binds to `127.0.0.1`, probing ports
starting at **8090** with up to 10 fallbacks — so multiple Unity instances can
run concurrently, each on its own port. Domain reloads (frequent during
development) transparently restart the server on the newest compiled
assemblies via `AssemblyReloadEvents.beforeAssemblyReload` /
`afterAssemblyReload`. No manual restart, ever.

```
Terminal                              Unity Editor
   │                                       │
   │   unity-cli <tool> <args>             │
   │ ────────────────────────────────────► │  POST /command
   │                                       │  { "command": "...", "params": {...} }
   │                                       │
   │   { "success": bool, "message",       │
   │     "data": ... }                     │
   │ ◄──────────────────────────────────── │
```

### One endpoint, many tools

There is **exactly one** HTTP endpoint: `POST /command`. `CommandRouter`
dispatches by the `command` field to the matching `[UnityCliTool]` class,
discovered via reflection in `ToolDiscovery` (no registration step — drop a
class into any Editor assembly and it's live after the next compile).
Browser requests are rejected by `Origin` header; the CLI's Go HTTP client
sends none, so it passes.

### Instance discovery (no config)

Each running Unity writes a heartbeat file every 500ms to
`~/.unity-cli/instances/<hash>.json` (hash = MD5 of the project path), with
fields: `{port, pid, projectPath, unityVersion, state, timestamp,
compileErrors}`. The CLI scans that directory on every invocation to find
available instances — that's the entire mechanism behind "no config, no
server to run." Stale files (dead PIDs) are pruned on scan. Multiple
instances are disambiguated with `--port` or `--project`.

The `state` field (`ready`, `compiling`, `refreshing`, `playing`, `paused`,
`reloading`, `entering_playmode`, `stopped`) is what `unity-cli status` and
the `editor play --wait` / `editor refresh --compile` polling loops read.

### Main-thread dispatch + serialized execution

Incoming requests arrive on a listener thread, get parsed into a
`ConcurrentQueue`, and are drained on `EditorApplication.update` so every
tool handler runs on Unity's main thread (where `UnityEngine` /
`UnityEditor` APIs are safe). A `SemaphoreSlim(1, 1)` in `CommandRouter`
serializes dispatch, so concurrent CLI calls against the same Editor never
race. The queue nudges the Editor tick via
`InternalEditorUtility.RepaintAllViews` so commands still execute when the
window is unfocused — pair with **Preferences → General → Interaction Mode
→ No Throttling** for best responsiveness.

### Tool contract

The `exec` tool is the raw escape hatch: it compiles and runs arbitrary C#
via `csc` + the loaded Unity assemblies — full access to `UnityEngine`,
`UnityEditor`, and every game/editor assembly in the AppDomain.

Existing structured tools (`editor`, `console`, `menu`, `screenshot`,
`profiler`, `test`, `reserialize`) wrap editor-level APIs
(`EditorApplication`, `Debug.unityLogger` stream, `AssetDatabase.Refresh`,
profiler sampler, Test Framework, etc.).

Planned structured tools (`ls`, `find`, `inspect`, `get`, `set`,
`component`, `create`, `delete`, `prefab`, …) will mutate scene and project
state through Unity's own APIs (`SerializedObject` + `ApplyModifiedProperties`,
`AssetDatabase`, `PrefabUtility`, `Undo`, `PrefabStageUtility`) — matching
exactly what clicking in the Inspector / Hierarchy / Project windows would
do, including proper undo registration and prefab override semantics.

---

## Path Grammar

```
path       = segment ('/' segment)* (':' component)? ('.' property)*
segment    = name ('[' index ']')?
component  = typename ('[' index ']')?
name       = identifier (any valid GameObject name)
typename   = identifier (Unity component type name, e.g. Rigidbody)
index      = 0-based integer (sibling order / component order)
```

### Examples

```
World/Player                              GameObject
World/Player:Transform                    Component on a GameObject
World/Player:Transform.position           Property on a component
World/Player:Transform.position.x         Sub-property (vector field)
World/Enemy[1]                            Second GameObject named "Enemy"
World/Player:AudioSource[0]               First of multiple AudioSources
Assets/Prefabs/Enemy.prefab               Prefab asset
Assets/Prefabs/Enemy.prefab//Weapon       Object inside the prefab asset
#14352                                    Instance ID (unambiguous)
```

### Disambiguation Rules

- **Duplicate sibling names**: Use `[n]` where `n` is sibling order (matches
  the top-to-bottom order in the Hierarchy window).
- **Duplicate component types**: Use `[n]` where `n` is component order
  (matches the top-to-bottom order in the Inspector).
- **Index omitted when unique**: `World/Player:Rigidbody` is fine if there's
  only one Player at that level and only one Rigidbody on it.
- **Ambiguous input fails loudly** with a list of candidate canonical paths.
- **Tools always emit canonical (fully-indexed) paths** so piped output
  never introduces ambiguity.
- **Instance ID fallback**: `#<id>` (e.g. `#14352`) always resolves to one
  exact object, regardless of hierarchy reorders. Useful for scripts that
  pin an object across multiple operations.

---

## Reference Resolution

When a property expects a reference to a Unity object (GameObject, Component,
or asset), `set` accepts a path as the value and resolves it automatically.

| Value looks like         | Resolved as             | Mechanism                        |
|--------------------------|-------------------------|----------------------------------|
| `World/...` (scene path) | `GameObject`            | scene traversal                  |
| `World/...:Type`         | `Component`             | `GetComponent` after traversal   |
| `Assets/...`             | Asset                   | `AssetDatabase.LoadAssetAtPath`  |
| `Assets/Foo.prefab//...` | Object inside prefab    | prefab asset traversal           |
| `#<id>`                  | Any object by instance ID | `EditorUtility.InstanceIDToObject` |
| `null` / `none`          | null reference          | clears the field                 |

The property's expected type drives coercion. Assigning a GameObject path to
a property that expects a Transform will auto-`GetComponent<Transform>()`.
Assigning a component path to a property that expects a GameObject will
auto-`.gameObject`. A missing component on coercion is an error.

String properties never coerce — a string that happens to look like a path
is kept as a literal.

---

## Output Formats

Every tool supports the same output contract:

| Flag               | Output                                                       |
|--------------------|--------------------------------------------------------------|
| *(default)*        | human-readable, aligned, colored when TTY                    |
| `--json`           | pretty JSON, `jq`-compatible                                 |
| `--plain`          | one value / path per line, `xargs`/`grep`-compatible         |
| `--null-delimited` | `\0`-separated, for `xargs -0` when paths contain spaces     |

Scalar `get` on a single primitive property always emits the raw value with
no wrapper, so it composes cleanly with `bc`, `awk`, and shell arithmetic.

---

## Command Reference

Status legend:
- ✅ **Implemented** — works today
- 🚧 **Not implemented** — planned, this doc is the todo list

---

### `exec`

✅ **Implemented**

Execute arbitrary C# code in the Editor. Full access to UnityEngine,
UnityEditor, and all loaded assemblies. Each invocation is independent — no
state persists between calls (this is by design; use structured commands for
stateful workflows).

```
unity-cli exec "<code>" [--usings <list>] [--csc <path>] [--dotnet <path>]
```

**Options:**
- `<code>` — C# code. Use `return` to emit a value.
- `--usings` — additional using directives (comma-separated).
- `--csc` — override csc compiler path (auto-detected).
- `--dotnet` — override dotnet runtime path (auto-detected).

**Examples:**
```bash
unity-cli exec "return GameObject.FindObjectsOfType<Light>().Length;"
unity-cli exec "Selection.activeGameObject.name = \"Renamed\";"
```

---

### `editor`

✅ **Implemented**

Control play mode and the asset database. CLI wrapper around the Unity-side
`manage_editor` and `refresh_unity` endpoints, with optional polling for
completion.

```
unity-cli editor <play|stop|pause|refresh> [options]
```

**Subcommands:**
- `play [--wait]` — enter play mode. `--wait` blocks until fully entered.
- `stop` — exit play mode.
- `pause` — toggle pause/resume (play mode only).
- `refresh` — refresh AssetDatabase.
  - `--compile` — recompile scripts and wait until compilation finishes
    (fails if errors are present).

**Underlying HTTP endpoint (`manage_editor`) also accepts these actions**
when called directly (e.g. `unity-cli manage_editor --action <name>`):
`play`, `stop`, `pause`, `refresh`, `set_active_tool`, `add_tag`,
`remove_tag`, `add_layer`, `remove_layer`.

**Examples:**
```bash
unity-cli editor play --wait
unity-cli editor stop
unity-cli editor refresh --compile
unity-cli manage_editor --action add_tag --tag_name Enemy
```

---

### `console`

✅ **Implemented**

Read or clear Unity console log entries.

```
unity-cli console [--lines <N>] [--type <types>] [--stacktrace <mode>] [--clear]
```

**Options:**
- `--lines <N>` — limit to N entries.
- `--type <types>` — comma-separated log types: `error`, `warning`, `log`
  (default: `error,warning,log`).
- `--stacktrace <mode>` — `none` (first line only), `user` (default, internal
  frames filtered), `full` (raw including all frames).
- `--clear` — clear the console.

**Examples:**
```bash
unity-cli console
unity-cli console --lines 20 --type error
unity-cli console --clear
```

---

### `menu`

✅ **Implemented**

Execute a Unity menu item by path. `File/Quit` is blocked for safety.

```
unity-cli menu "<path>"
```

**Examples:**
```bash
unity-cli menu "File/Save Project"
unity-cli menu "Assets/Refresh"
unity-cli menu "Window/General/Console"
```

---

### `screenshot`

✅ **Implemented**

Capture a screenshot of the scene or game view.

```
unity-cli screenshot [--view <scene|game>] [--width <N>] [--height <N>] [--output_path <path>]
```

**Options:**
- `--view <mode>` — `scene` (default) or `game`.
- `--width <N>` / `--height <N>` — pixel dimensions (default 1920×1080).
- `--output_path <path>` — absolute or relative to project root
  (default: `Screenshots/screenshot.png`).

**Examples:**
```bash
unity-cli screenshot
unity-cli screenshot --view game --width 3840 --height 2160
```

---

### `reserialize`

✅ **Implemented**

Force Unity to reserialize assets through its YAML serializer. Use after
editing `.prefab`, `.unity`, `.asset`, or `.mat` files as text. No arguments
= whole project.

```
unity-cli reserialize [path...]
```

**Examples:**
```bash
unity-cli reserialize
unity-cli reserialize Assets/Prefabs/Player.prefab
unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity
```

---

### `profiler`

✅ **Implemented**

Control the Unity Profiler and query frame samples.

```
unity-cli profiler <hierarchy|enable|disable|status|clear> [options]
```

**Subcommands:**
- `hierarchy` — top-level profiler samples (last frame).
  - `--depth <N>` — recursive depth (0 = unlimited, default 1).
  - `--root <name>` — set root by name (substring match, searches full tree).
  - `--frames <N>` — average over last N frames (flat, sorted by time).
  - `--from <N>` / `--to <N>` — range average.
  - `--parent <ID>` — drill into item by ID.
  - `--min <ms>` — filter items below threshold.
  - `--sort <total|self|calls>` — sort column (default `total`).
  - `--max <N>` — max children per level (default 30).
  - `--frame <N>` — specific frame index.
  - `--thread <N>` — thread index (0 = main).
- `enable` — start recording.
- `disable` — stop recording.
- `status` — show profiler state.
- `clear` — clear captured frames.

**Examples:**
```bash
unity-cli profiler hierarchy --depth 3
unity-cli profiler hierarchy --frames 30 --min 0.5 --sort self
unity-cli profiler enable
```

---

### `test`

✅ **Implemented**

Run Unity tests via the Test Runner API. Requires the
`com.unity.test-framework` package.

```
unity-cli test [--mode <EditMode|PlayMode>] [--filter <name>]
```

**Behavior:**
- EditMode tests hold the connection open and return results directly.
- PlayMode tests return immediately and poll a results file (domain-reload
  safe).

**Examples:**
```bash
unity-cli test
unity-cli test --mode PlayMode
unity-cli test --filter MyNamespace.MyTests.SpecificTest
```

---

### `status`

✅ **Implemented**

Show the current Unity Editor state: port, project path, version, PID.
Reports "not responding" if the heartbeat is older than 3 seconds.

```
unity-cli status
```

---

### `list`

✅ **Implemented**

List all registered tools (built-in + custom) with their parameter schemas.
Auto-discovery is reflection-based over every `[UnityCliTool]` class in the
loaded assemblies.

```
unity-cli list
```

---

### `update`

✅ **Implemented**

Update the CLI binary from the latest GitHub release.

```
unity-cli update [--check]
```

**Options:**
- `--check` — check for updates without installing.

---

### Custom tools

✅ **Implemented** (mechanism — tools themselves are user-authored)

Any static C# class decorated with `[UnityCliTool]` in an Editor assembly is
auto-discovered and callable directly by name:

```bash
unity-cli <tool_name> [--flag value] [--params '{"k":"v"}']
```

Class name auto-converts to snake_case (`SpawnEnemy` → `spawn_enemy`); override
with `[UnityCliTool(Name = "my_name")]`. Parameters are declared via a nested
`Parameters` class with `[ToolParameter]` attributes. The handler signature is
`public static object HandleCommand(JObject parameters)`, returning
`SuccessResponse` or `ErrorResponse`. See `unity-cli help custom-tools` for the
full template.

All planned commands below (`ls`, `find`, `inspect`, `get`, `set`,
`component`, `select`, `create`, `delete`, `find-asset`, `prefab`) will be
implemented on top of this same mechanism — one `[UnityCliTool]` class per
tool, plus Go-side wrappers where piping/polling ergonomics demand it.

---

### `ls`

✅ **Implemented**

List children of a GameObject, or root-level objects of the active scene.

```
unity-cli ls [<path>] [-r|--recursive] [--components] [--json|--plain|--null-delimited]
```

**Options:**
- `<path>` — GameObject path. Omit for scene roots.
- `-r, --recursive` — descend into children (like `ls -R`).
- `--components` — include component list alongside each object.
- Output flags as described in [Output Formats](#output-formats).

**Behavior:**
- `[n]` indices shown only on names that are actually duplicated at that level.
- Inactive GameObjects included by default, marked `(inactive)` in human output.

**Examples:**
```bash
unity-cli ls
unity-cli ls World/Player
unity-cli ls -r World --components
unity-cli ls -r --plain | grep Enemy
```

---

### `find`

🚧 **Not implemented**

Search objects by name, component, tag, layer, or prefab source.

```
unity-cli find [--name <glob>] [--component <type>] [--missing <type>]
               [--tag <tag>] [--layer <layer>] [--prefab <assetpath>]
               [--has-overrides] [--active|--inactive]
               [--json|--plain|--null-delimited]
```

**Options:**
- `--name <glob>` — name glob match (e.g. `"Enemy*"`).
- `--component <type>` — only objects that have a component of this type.
- `--missing <type>` — only objects that *lack* a component of this type.
- `--tag <tag>` — only objects with this tag.
- `--layer <layer>` — only objects on this layer.
- `--prefab <assetpath>` — only instances of this prefab asset.
- `--has-overrides` — only prefab instances with property or structural overrides.
- `--active` / `--inactive` — filter by active state.
- Flags are AND-combined. Multiple `--component` flags also AND-combine.

**Examples:**
```bash
unity-cli find --name "Enemy*"
unity-cli find --component MeshRenderer --missing Collider
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides
```

---

### `inspect`

🚧 **Not implemented**

Dump a GameObject, component, or property as JSON (the `cat` of objects).

```
unity-cli inspect <path> [--overrides-only] [--json|--plain]
```

**Options:**
- `<path>` — object, component, or property path.
- `--overrides-only` — show only properties that override the prefab (if any).
- In `--plain` output, overridden properties are marked with `*override*` and
  the prefab source value is shown in parentheses.

**Examples:**
```bash
unity-cli inspect World/Player
unity-cli inspect World/Player:Transform
unity-cli inspect World/Enemy[0] --overrides-only
unity-cli inspect World/Player --json | jq '.Transform.localPosition'
```

---

### `get`

🚧 **Not implemented**

Read a single property value. Scalar output by default, so composes with
shell arithmetic.

```
unity-cli get <path> [--source] [--json]
```

**Options:**
- `<path>` — must resolve to a single property (e.g. `:Rigidbody.mass`).
- `--source` — for prefab instances, emit the prefab source value instead of
  the (possibly overridden) instance value.
- Vector / compound properties emit space-separated components by default,
  JSON object with `--json`.
- Reference properties emit a canonical path (so `get | inspect` chains).

**Examples:**
```bash
unity-cli get World/Player:Rigidbody.mass
unity-cli get World/Player:Transform.position
unity-cli get World/Player:Transform.position.x
unity-cli get World/Enemy:AIScript.target | unity-cli inspect
```

---

### `set`

🚧 **Not implemented**

Write a single property value. Reads from stdin if value is omitted (enables
piping).

```
unity-cli set <path> [<value>] [--all]
```

**Options:**
- `<path>` — must resolve to a single property.
- `<value>` — scalar literal, space-separated vector, path (for references),
  or `null`/`none` to clear. Read from stdin if omitted.
- `--all` — broadcast to all matches when `<path>` is ambiguous.
- For prefab instances, creates an override implicitly (same as typing in
  the Inspector).

**Examples:**
```bash
unity-cli set World/Player:Transform.position "0 1 5"
unity-cli set World/Player:Rigidbody.mass 10
unity-cli set World/Player:MeshRenderer.material Assets/Materials/Metal.mat
unity-cli set World/Enemy:AIScript.target World/Player
unity-cli set World/Enemy:AIScript.target null
unity-cli get World/A:Transform.position | unity-cli set World/B:Transform.position
```

---

### `component`

🚧 **Not implemented**

Add, remove, or list components on a GameObject.

```
unity-cli component list <objectpath>
unity-cli component add <objectpath> <type>
unity-cli component remove <objectpath> <type>[<index>]
```

**Behavior:**
- `add` returns the canonical path of the newly added component (including
  its index if it's a duplicate type).
- `remove` on an ambiguous type without an index fails loudly.

**Examples:**
```bash
unity-cli component list World/Player
unity-cli component add World/Player Rigidbody
unity-cli component remove World/Player AudioSource[1]
```

---

### `select`

🚧 **Not implemented**

Get or set the Editor's current Selection. The bridge between the terminal
and the Hierarchy/Inspector windows.

```
unity-cli select <path>            # set selection
unity-cli select --get             # print currently selected path(s)
unity-cli select --clear           # deselect
unity-cli select --add <path>      # add to current selection
```

**Behavior:**
- Reads from stdin if `<path>` is omitted (enables piping).
- `--get` emits canonical paths, one per selected object.

**Examples:**
```bash
unity-cli select World/Player
unity-cli select --get
unity-cli select --get | unity-cli inspect
unity-cli find --component Light --plain | head -1 | unity-cli select
```

---

### `create`

🚧 **Not implemented**

Create a new GameObject, primitive, or prefab instance.

```
unity-cli create Empty <parentpath>/<name>
unity-cli create <primitive> <parentpath>/<name>
unity-cli create --prefab <assetpath> <parentpath>/<name>
```

**Primitives:**
`Empty`, `Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad`.

**Behavior:**
- Emits the canonical path of the new object (useful for piping to `set`,
  `component`, etc.).
- Parent path must exist.

**Examples:**
```bash
unity-cli create Empty World/Enemies/SpawnPoint
unity-cli create Cube World/Level/Platform
unity-cli create --prefab Assets/Prefabs/Enemy.prefab World/Enemies/Enemy_01
```

---

### `delete`

🚧 **Not implemented**

Destroy a GameObject (and its children).

```
unity-cli delete <path> [--all]
```

**Options:**
- `--all` — broadcast when path is ambiguous.
- Accepts paths on stdin (one per line) for batch deletion.

**Examples:**
```bash
unity-cli delete World/Enemies/OldSpawn
unity-cli find --name "Temp_*" --plain | xargs -I{} unity-cli delete {}
```

---

### `find-asset`

🚧 **Not implemented**

Search the project asset database.

```
unity-cli find-asset [<name>] [--type <type>] [--path <glob>]
                     [--json|--plain|--null-delimited]
```

**Options:**
- `<name>` — name glob (optional).
- `--type <type>` — asset type filter (e.g. `Material`, `Mesh`, `Prefab`,
  `ScriptableObject`).
- `--path <glob>` — restrict to matching asset paths.

**Examples:**
```bash
unity-cli find-asset "Metal"
unity-cli find-asset "Metal" --type Material
unity-cli find-asset --type Prefab --path "Assets/Enemies/*"
```

`set` also accepts `--find <name> --type <type>` inline as a shortcut when
the target type is known from the destination property.

---

### `prefab`

🚧 **Not implemented**

Prefab lifecycle, overrides, and context operations.

#### `prefab status`

Show prefab connection and override summary for an instance.

```
unity-cli prefab status <path>
```

Reports: source prefab asset, override counts, nesting chain, and (for nested
prefabs) the exact owner asset that an edit at this path would land on.

#### `prefab diff`

Show the override delta between an instance and its prefab asset.

```
unity-cli prefab diff <path> [--json]
```

Default output mirrors `git diff` conventions:
- `~ :Rigidbody.mass   5 → 20`    (property override)
- `+ :AudioSource`                  (added component)
- `- :BoxCollider`                  (removed component)

#### `prefab apply`

Push overrides from an instance back to the prefab asset.

```
unity-cli prefab apply <path>                    # all overrides
unity-cli prefab apply <path>:<component>        # all on one component
unity-cli prefab apply <path>:<component>.<prop> # single property
```

#### `prefab revert`

Discard overrides and pull prefab asset values onto the instance.

```
unity-cli prefab revert <path>                    # all overrides
unity-cli prefab revert <path>:<component>
unity-cli prefab revert <path>:<component>.<prop>
```

#### `prefab create`

Save a scene object as a new prefab asset.

```
unity-cli prefab create <scenepath> <assetpath>
```

#### `prefab unpack`

Break the prefab connection on an instance.

```
unity-cli prefab unpack <path> [--completely]
```

`--completely` unpacks all nested prefab layers.

#### `prefab variant`

Create a prefab variant of an existing prefab asset.

```
unity-cli prefab variant <sourceassetpath> <newassetpath>
```

#### `prefab open` / `prefab close`

Enter/exit prefab editing mode. Inside the mode, all paths resolve relative
to the prefab root — no `Assets/Foo.prefab//` prefix needed.

```
unity-cli prefab open <assetpath>
unity-cli prefab close [--discard]
```

`--discard` exits without saving.

**Examples:**
```bash
unity-cli prefab status World/Enemy[0]
unity-cli prefab diff World/Enemy[0]
unity-cli prefab apply World/Enemy[0]:Rigidbody.mass
unity-cli prefab revert World/Enemy[0]
unity-cli prefab create World/Player Assets/Prefabs/Player.prefab
unity-cli prefab open Assets/Prefabs/Enemy.prefab
# ... edits inside prefab ...
unity-cli prefab close
```

---

## Common Usage Examples

### Basic inspection

```bash
# What's in the scene?
unity-cli ls -r

# What's on this object?
unity-cli inspect World/Player

# Look at just one component
unity-cli inspect World/Player:Rigidbody

# Drill into one value
unity-cli get World/Player:Transform.position.y
```

### Editor bridge (terminal ↔ UI)

```bash
# Click in Editor, inspect from terminal
unity-cli select --get | unity-cli inspect

# Find in terminal, highlight in Editor
unity-cli find --component AudioSource --plain | head -1 | unity-cli select

# Set reference to whatever is currently clicked
unity-cli select --get | unity-cli set World/Enemy:AIScript.target
```

### Batch mutations

```bash
# Flatten all enemies to y=0
unity-cli find --name "Enemy*" --plain | \
    xargs -I{} unity-cli set {}:Transform.position.y 0

# Halve rigidbody masses (shell arithmetic via bc)
unity-cli find --component Rigidbody --plain | while read p; do
    m=$(unity-cli get "$p:Rigidbody.mass")
    unity-cli set "$p:Rigidbody.mass" $(echo "$m / 2" | bc)
done

# Add BoxCollider to anything with a MeshRenderer but no collider
unity-cli find --component MeshRenderer --missing Collider --plain | \
    xargs -I{} unity-cli component add {} BoxCollider
```

### References

```bash
# Copy transform position from one object to another
unity-cli get World/Source:Transform.position | \
    unity-cli set World/Target:Transform.position

# Assign a material by exact path
unity-cli set World/Player:MeshRenderer.material Assets/Materials/Metal.mat

# Assign by name search (--find resolves asset paths)
unity-cli set World/Player:MeshRenderer.material --find "Metal" --type Material

# Clear a reference
unity-cli set World/Enemy:AIScript.target null

# Follow a reference chain
unity-cli get World/Enemy:AIScript.target | unity-cli inspect
```

### Auditing

```bash
# List all lights and their intensity, as CSV
unity-cli find --component Light --plain | \
    xargs -I{} unity-cli inspect {}:Light --json | \
    jq -r '[.path, .intensity, .color] | @csv'

# Find broken references across the scene
unity-cli find --plain | while read p; do
    unity-cli inspect "$p" --json | \
        jq --arg path "$p" '
            .components[] |
            to_entries[] |
            select(.value == null and .key != "hideFlags") |
            {object: $path, property: .key}
        '
done
```

### Prefab workflows

```bash
# Diff every instance of a prefab against the asset
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --plain | \
    xargs -I{} unity-cli prefab diff {}

# Apply the same property value as an override to every instance
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --plain | \
    xargs -I{} unity-cli set {}:Rigidbody.mass 20

# Find instances that have diverged from the asset
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides

# Heavy prefab edit session
unity-cli prefab open Assets/Prefabs/Enemy.prefab
unity-cli ls -r                              # now relative to prefab root
unity-cli set Root:Rigidbody.mass 10
unity-cli component add Root/Weapon AudioSource
unity-cli prefab close
```

### The exec escape hatch

When no structured command covers what you need:

```bash
# Turn off shadow casting on every Renderer
unity-cli find --component Renderer --plain | \
    xargs -I{} unity-cli exec "
        var r = GameObject.Find(\"{}\").GetComponent<Renderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
    "

# One-off project query
unity-cli exec "return GameObject.FindObjectsOfType<Light>().Length;"
```

---

## Tips and Tricks

### Canonical vs. human paths

You can *type* short paths (`World/Player:Rigidbody.mass`). Tools always *emit*
canonical paths with full indices where needed
(`World/Player:Rigidbody[0].mass`). Piped chains are always unambiguous for
this reason — no need to worry about ambiguity mid-pipeline.

### Pin an object with instance ID

If a script performs many operations on one object across a session, capture
its instance ID once. Immune to hierarchy reorders:

```bash
ID=$(unity-cli find --name "Boss" --json | jq -r '.[0].instanceId')
unity-cli set "#$ID:Transform.position" "0 0 0"
unity-cli set "#$ID:Rigidbody.mass" 100
unity-cli component add "#$ID" BossAI
```

### Prefer `--plain` for pipes, `--json` for structure

`--plain` emits one path or value per line — ideal for `xargs`, `grep`, and
`while read`. `--json` is for when you need to reach into nested structure
with `jq`.

### Null-delimited for paths with spaces

If object names contain spaces (e.g. `"Main Camera"`):

```bash
unity-cli find --component Camera --null-delimited | \
    xargs -0 -I{} unity-cli inspect {}
```

### `set` reads stdin when value omitted

Enables copy-by-pipe:

```bash
unity-cli get World/A:Transform.rotation | unity-cli set World/B:Transform.rotation
```

### Ambiguity errors list the canonical forms

When a path matches multiple objects, the error lists each match as a
ready-to-copy canonical path. Don't guess — copy from the error.

### Use `prefab status` before big edits

Before overriding a property on a nested prefab instance, run `prefab status`
to confirm *which* prefab asset would actually receive the change if you
later `prefab apply`. Nesting can be non-obvious.

### `select --get` turns mouse-clicks into input

The fastest way to target a specific object for a CLI operation is often to
click it in the Hierarchy first:

```bash
unity-cli select --get | xargs -I{} unity-cli set {}:Rigidbody.mass 50
```

### Escape hatch is always available

Anything this document doesn't cover, `exec` does. Treat structured commands
as the fast path for the ~80% case, and `exec` as the fully-general backstop.
