# Command Reference

[← Back to README](../README.md) | [Path Grammar](paths.md) | [Custom Tools](custom-tools.md)

All commands with their options and examples. Use `unity-cli <command> --help` for the most up-to-date flags.

---

## Contents

**Scene inspection**
[ls](#ls) · [find](#find) · [inspect](#inspect) · [get](#get) · [select](#select)

**Scene mutation**
[set](#set) · [create](#create) · [rm](#rm) · [cp](#cp) · [mv](#mv) · [reorder](#reorder) · [component](#component)

**Prefab management**
[prefab](#prefab)

**Scene management**
[scene](#scene)

**Asset operations**
[guid](#guid) · [path](#path) · [reimport](#reimport) · [reserialize](#reserialize)

**Editor control**
[editor](#editor) · [console](#console) · [exec](#exec) · [test](#test) · [menu](#menu) · [screenshot](#screenshot) · [profiler](#profiler)

**Tooling**
[status](#status) · [list](#list) · [completion](#completion) · [update](#update) · [init](#init) · [interactive](#interactive) · [Custom tools](#custom-tools)

---

## ls

List children of a GameObject, or the root-level objects of the active scene.

```
unity-cli ls [<path>] [-R|--recursive] [--components] [--json|--plain|--null-delimited]
```

**Options:**
- `<path>` — GameObject path. Omit for scene roots.
- `-R, --recursive` — descend into all descendants.
- `--components` — include component list alongside each object.
- Output flags: `--plain`, `--json`, `--null-delimited` (see [Output Formats](paths.md#output-formats)).

**Behavior:**
- `[n]` indices appear only when sibling names are actually duplicated.
- Inactive GameObjects are included by default, marked `(inactive)` in human output.
- With `--plain --components`, the component list is TAB-separated on each line; `cut -f1` strips it back to paths.
- Defaults to `--plain` output (one path per line), ready to pipe.

**Examples:**
```bash
unity-cli ls                           # scene roots
unity-cli ls /World/Player             # children of Player
unity-cli ls -R /World --components    # full subtree with component lists
unity-cli ls -R --plain | grep Enemy   # find enemies by piping through grep
unity-cli ls . | unity-cli select     # select all children of selection
```

---

## find

Unified search across the scene hierarchy or the asset database.

```
unity-cli find [<path>] [filters] [--json|--plain|--null-delimited]
```

The first positional determines the search domain: a path starting with `Assets/` or `Packages/` searches the asset database; any other path narrows scene search to that subtree; no positional searches all loaded scenes.

### Scene mode

```
unity-cli find [<scene-path>] [--name <glob>] [--name-prefix <s>] [--name-suffix <s>]
               [--name-contains <s>] [--regex <regex>]
               [--component <type>] [--missing <type>]
               [--tag <tag>] [--layer <layer>]
               [--prefab <assetpath>] [--has-overrides] [--is-prefab-instance]
               [--exact-component] [--max-depth N]
               [--active|--inactive]
```

- `--name <glob>` — name glob (`"Enemy*"`). Quoting required.
- `--name-prefix <s>` / `--name-suffix <s>` / `--name-contains <s>` — case-insensitive string matches; no quoting needed.
- `--regex <regex>` — name regex match.
- `--component <type>` — only objects that have this component type. Repeatable; multiple flags AND-combine.
- `--missing <type>` — only objects that *lack* this component type. Repeatable.
- `--tag <tag>` — only objects with this tag.
- `--layer <layer>` — only objects on this layer.
- `--prefab <assetpath>` — only instances of this prefab asset.
- `--has-overrides` — only prefab instances with overrides.
- `--is-prefab-instance` — only prefab-instance roots, regardless of source
  asset. Use `--prefab <path>` to narrow to one specific source.
- `--exact-component` — `--component` / `--missing` match the exact type only.
  Default behavior accepts subclasses (e.g. `--component Renderer` matches
  `MeshRenderer`, `SkinnedMeshRenderer`, …).
- `--max-depth N` — limit recursion depth. `1` = scope's immediate children
  only (no scope means scene roots only). Default: unlimited.
- `--active` / `--inactive` — filter by active state.

A positional scope path restricts the search to that subtree's descendants (the scope object itself is excluded). Bare `/` and `.` with empty selection mean "the whole hierarchy."

### Asset mode

```
unity-cli find Assets/[<subfolder>] [--name <pattern>] [--type <type>]
               [--label <label>] [--area <all|assets|packages>]
```

- `Assets/` — search all assets; subfolder or glob after `/` narrows the scope.
- `--name <pattern>` — partial filename match or glob.
- `--type <type>` — asset type (`Material`, `Mesh`, `Prefab`, `Texture2D`, `Scene`, `Shader`, …).
- `--label <label>` — asset label filter.
- `--area <all|assets|packages>` — search area (default: `all`).

**Examples:**
```bash
# Scene searches
unity-cli find --name "Enemy*"
unity-cli find --component MeshRenderer --missing Collider
unity-cli find --component Rigidbody --component AudioSource
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides
unity-cli find /World/Enemies --name "Boss*"
unity-cli find --component Light --plain | unity-cli inspect :Light

# Asset searches
unity-cli find Assets/
unity-cli find Assets/Prefabs/ --type Prefab
unity-cli find Assets/ --name "Metal*" --type Material --plain
unity-cli find Assets/ --label Hero --json
unity-cli find Packages/ --type Shader
unity-cli find Assets/ --type Prefab --plain | unity-cli inspect
```

---

## inspect

Dump a GameObject, component, or property. The `cat` of the scene tree.

```
unity-cli inspect [<path>] [--overrides-only] [--json|--plain]
```

**Options:**
- `<path>` — object, component, or property path. Omit to inspect the current selection (`inspect .`).
- `--overrides-only` — show only properties that override the prefab source.

**Stdin (multi-path mode):**

When stdin is piped, each line is treated as a path. If the positional starts with `:`, it's appended to every piped path as a component/property suffix:

```bash
unity-cli find --component Light --plain | unity-cli inspect :Light
unity-cli find --component Rigidbody --plain | unity-cli inspect
unity-cli find Assets/ --type Texture2D --plain | unity-cli inspect :Importer
```

**Examples:**
```bash
unity-cli inspect /World/Player
unity-cli inspect /World/Player:Transform
unity-cli inspect /World/Player:Rigidbody --json | jq '.mass'
unity-cli inspect /World/Enemy[0] --overrides-only

# :GameObject pseudo-component — name, active, tag, layer, isStatic
unity-cli inspect /World/Player:GameObject

# :Importer — asset importer settings
unity-cli inspect Assets/Foo.png:Importer
unity-cli inspect Assets/Foo.png:TextureImporter.maxTextureSize

# Inspect current selection
unity-cli inspect
unity-cli select --get | unity-cli inspect
```

---

## get

Read a single property value. Scalar output by default, so it composes with shell arithmetic.

```
unity-cli get <path> [--source] [--with-path|-P] [--json]
```

**Options:**
- `<path>` — must include a property (e.g. `:Rigidbody.mass`, `:Transform.position.x`).
- `--source` — for prefab instances, emit the prefab source value instead of the overridden instance value.
- `--with-path`, `-P` — prefix each line with `path:Component.prop`. Useful for multi-target reads where values alone are ambiguous.
- Vector/compound properties emit space-separated components by default, JSON object with `--json`.
- Reference properties emit a canonical path (so `get | inspect` chains).

**Stdin (multi-path mode):**

When stdin is piped, each line is treated as a path; the positional acts as a `:Component.property` suffix:

```bash
unity-cli find --component Light --plain | unity-cli get :Light.intensity
unity-cli find --component Rigidbody --plain | unity-cli get :Rigidbody.mass
```

**Examples:**
```bash
unity-cli get /World/Player:Rigidbody.mass
unity-cli get /World/Player:Transform.position
unity-cli get /World/Player:Transform.position.x

# :GameObject pseudo-component
unity-cli get /World/Player:GameObject.name
unity-cli get /World/Player:GameObject.activeSelf
unity-cli get /World/Player:GameObject.tag

# Asset importer access
unity-cli get Assets/Foo.png:TextureImporter.maxTextureSize
unity-cli get Assets/Hero.fbx:Importer.animationType

# Follow a reference
unity-cli get /World/Enemy:AIScript.target | unity-cli inspect

# Read mass of every Rigidbody (one value per line)
unity-cli find --component Rigidbody --plain | unity-cli get :Rigidbody.mass
```

---

## select

Get or set the Editor's current Selection. The bridge between the terminal and the Hierarchy/Inspector windows.

```
unity-cli select [<path>]     # set selection
unity-cli select --get        # print currently selected path(s)
unity-cli select --clear      # deselect everything
unity-cli select --add <path> # add to current selection
```

**Behavior:**
- Reads paths from stdin if `<path>` is omitted (enables piping).
- `--get` emits canonical paths, one per selected object.

**Examples:**
```bash
unity-cli select /World/Player
unity-cli select --get
unity-cli select --get | unity-cli inspect
unity-cli find --component Light --plain | head -1 | unity-cli select
unity-cli ls . | unity-cli select       # select all children of selection
```

---

## set

Write a property value. Reads from stdin if value is omitted.

```
unity-cli set <path> [<value>]
```

**Options:**
- `<path>` — a property path (e.g. `/World/Player:Rigidbody.mass`).
- `<value>` — scalar literal, space-separated vector, path (for references), or `null`/`none` to clear. Read from stdin if omitted.
- Creates a prefab override implicitly when writing to a prefab instance — same as typing in the Inspector.
- For importer properties (`Assets/Foo.png:Importer.x`), writes the `.meta` file and Unity re-imports automatically.

**Stdin (multi-path broadcast):**

When the first positional starts with `:` (a component/property suffix) and the second positional is a value, paths are read from stdin and the value is broadcast to each. All writes share one Undo group:

```bash
unity-cli find --component Rigidbody --plain | \
    unity-cli set :Rigidbody.mass 100
```

**Examples:**
```bash
unity-cli set /World/Player:Transform.position "0 1 5"
unity-cli set /World/Player:Rigidbody.mass 10
unity-cli set /World/Player:MeshRenderer.material Assets/Materials/Metal.mat
unity-cli set /World/Enemy:AIScript.target /World/Player
unity-cli set /World/Enemy:AIScript.target null

# Array elements: `[N]` indexes into an array property. The same form
# works inside `get` and `inspect`. Mixes freely with nested fields.
unity-cli set /World/Cube:MeshRenderer.sharedMaterials[1] Assets/Materials/Red.mat
unity-cli set /World/Enemy:Loot.drops[0].chance 0.5
unity-cli get  /World/Cube:MeshRenderer.sharedMaterials[0]

# Copy position from one object to another
unity-cli get /World/A:Transform.position | unity-cli set /World/B:Transform.position

# :GameObject pseudo-component
unity-cli set /World/Player:GameObject.activeSelf false
unity-cli set /World/Player:GameObject.name "Hero"
unity-cli set /World/Player:GameObject.tag "Player"
unity-cli set /World/Player:GameObject.layer "UI"
unity-cli set :GameObject.isStatic true    # fan-out: all selected objects

# Asset importer
unity-cli set Assets/Foo.png:TextureImporter.maxTextureSize 1024
unity-cli set Assets/Hero.fbx:Importer.animationType Generic

# Batch: halve all Rigidbody masses
unity-cli find --component Rigidbody --plain | while read p; do
    m=$(unity-cli get "$p:Rigidbody.mass")
    unity-cli set "$p:Rigidbody.mass" $(echo "$m / 2" | bc)
done
```

---

## create

Create a new GameObject, primitive, or prefab instance.

```
unity-cli create Empty <parentpath>/<name>
unity-cli create <primitive> <parentpath>/<name>
unity-cli create --prefab <assetpath> <parentpath>/<name>
```

**Primitives:** `Empty`, `Cube`, `Sphere`, `Capsule`, `Cylinder`, `Plane`, `Quad`.

**Behavior:**
- Emits the canonical path of the new object — useful for piping to `set`, `component`, etc.
- Parent path must already exist.
- To create at the scene root, use `/Name` as the path.

**Examples:**
```bash
unity-cli create Empty /World/Enemies/SpawnPoint
unity-cli create Cube /World/Level/Platform
unity-cli create --prefab Assets/Prefabs/Enemy.prefab /World/Enemies/Enemy_01

# Create and immediately configure
unity-cli create Empty /World/Anchor | xargs -I{} unity-cli set {}:Transform.position "0 0 0"
```

---

## rm

Destroy a GameObject and its children.

```
unity-cli rm <path>
```

Accepts paths on stdin (one per line) for batch deletion.

**Examples:**
```bash
unity-cli rm /World/Enemies/OldSpawn
unity-cli rm .                                        # delete the selection
unity-cli find --name-prefix "Temp_" --plain | unity-cli rm
unity-cli ls /World/Enemies --plain | unity-cli rm
```

---

## cp

Copy a GameObject to a new location. Copies the whole subtree by default.

```
unity-cli cp <src> <dst> [--depth <N>] [--auto-suffix [<format>]]
```

**Destination path forms:**

| Form | Meaning |
|------|---------|
| `<parent>/<name>` | Copy under `<parent>`, named `<name>` |
| `<parent>/` | Copy under `<parent>`, keeping the source name |
| `/<name>` | Copy at scene root, named `<name>` |
| `/` | Copy at scene root, keeping the source name |

**Options:**
- `--depth <N>` — descendant layers to include. `0` = object only (no children). Omitted = full deep copy.
- `--auto-suffix` — append a numeric suffix (` (1)`, ` (2)`, …) on sibling-name collision.
- `--auto-suffix <format>` — custom format with `{n}` placeholder (e.g. `_{n}` → `Player_1`).

**Behavior:**
- Emits the canonical path of the new object.
- Registers a single Undo entry.
- Prefab connections are **not** preserved; the result is a standalone GameObject.

**Examples:**
```bash
unity-cli cp /World/Player /World/Player2
unity-cli cp /World/Player /World/Backup/         # keep source name
unity-cli cp /World/Player /World/PlayerStub --depth 0   # object only
unity-cli cp /World/Enemy /World/Wave/Enemy --auto-suffix
unity-cli cp /World/Enemy /World/Wave/Enemy --auto-suffix "_{n}"
unity-cli cp /World/Player/Hat /Hat               # promote to scene root
unity-cli cp /World/Player /                      # copy to root, keep name

# Pipe the new path into a follow-up edit
unity-cli cp /World/Player /World/Player2 | \
    xargs -I{} unity-cli set {}:Transform.position "0 0 5"
```

---

## mv

Move and/or rename a GameObject. Reparenting and renaming are unified into one operation.

```
unity-cli mv <src> <dst>
```

**Destination path forms** match `cp`:

| Form | Meaning |
|------|---------|
| `<parent>/<name>` | Move under `<parent>` and rename |
| `<parent>/` | Move under `<parent>`, keep name |
| `/<name>` | Move to scene root, rename |
| `/` | Move to scene root, keep name |

**Behavior:**
- Emits the canonical path after the move.
- Registers a single Undo entry.
- Prefab connection is preserved (matching drag behavior in the Hierarchy).
- Moving an object into one of its own descendants is rejected.

**Examples:**
```bash
unity-cli mv /World/Player /World/Hero             # rename in place
unity-cli mv /World/Enemies/Boss /World/Bosses/    # reparent, keep name
unity-cli mv /World/Enemies/Boss /World/Bosses/FinalBoss  # reparent + rename
unity-cli mv /World/Player/Hat /Hat               # promote to scene root
unity-cli mv /World/Player /                      # move to root, keep name

# Batch reparent every "Temp_*" under World/Trash/
unity-cli find --name-prefix "Temp_" --plain | \
    xargs -I{} unity-cli mv {} /World/Trash/
```

---

## reorder

Reorder a GameObject among its siblings, or a Component on its GameObject.

```
unity-cli reorder <path> <operation>
```

**Mode is chosen by the path:**

| Path form | Mode |
|-----------|------|
| `/World/Player` | Sibling reorder |
| `/World/Player:Rigidbody` | Component reorder |

**Operations (pick exactly one):**

| Flag | Meaning |
|------|---------|
| `--index <N>` | Absolute 0-based position. Clamped to valid range. |
| `--first` | Move to first. |
| `--last` | Move to last. |
| `--up [N]` | Shift up by N (default 1). |
| `--down [N]` | Shift down by N (default 1). |
| `--before <name>` | Insert immediately before the named sibling/component. |
| `--after <name>` | Insert immediately after. |

**Behavior:**
- Sibling mode uses `Transform.SetSiblingIndex`. One Undo entry.
- Component mode uses `ComponentUtility.MoveComponentUp/Down`. `Transform`/`RectTransform` at index 0 cannot be reordered.
- Out-of-range targets are clamped, not errors.
- Already-at-target invocations succeed with `status="noop"`.

**Examples:**
```bash
unity-cli reorder /UI/Canvas/Button --first
unity-cli reorder /World/Enemies/Boss --last
unity-cli reorder /World/Tracks/Drum --index 0
unity-cli reorder /World/UI/HealthBar --up 2
unity-cli reorder /World/Player/Hand --before Body
unity-cli reorder /World/Player:Rigidbody --up
unity-cli reorder /World/Player:AudioSource --after Animator
unity-cli reorder /World/Player:PlayerController --first
```

---

## component

Add, remove, or list components on a GameObject.

```
unity-cli component list <objectpath>
unity-cli component add <objectpath> <type>
unity-cli component remove <objectpath> <type>[<index>]
```

**Behavior:**
- `add` returns the canonical path of the newly added component.
- `remove` on an ambiguous type without an index fails loudly.

**Stdin (multi-path mode):**

When stdin is piped and no `<path>` positional is given, the action is applied to every line from stdin. All mutators share a single Undo group:

```bash
# Add BoxCollider to every MeshRenderer that lacks a collider
unity-cli find --component MeshRenderer --missing Collider --plain | \
    unity-cli component add BoxCollider

# Remove a script from every selected enemy
unity-cli select --get | unity-cli component remove DebugHelper

# Audit components on a subtree
unity-cli ls /UI --plain | unity-cli component list --json
```

**Examples:**
```bash
unity-cli component list /World/Player
unity-cli component add /World/Player Rigidbody
unity-cli component remove /World/Player AudioSource[1]
```

---

## prefab

Prefab lifecycle, overrides, and context operations.

```
unity-cli prefab <subcommand> [options]
```

All mutating subcommands run in `InteractionMode.AutomatedAction` (no modal dialogs). While a prefab stage is open via `prefab open`, all path-based commands (`ls`, `find`, `inspect`, etc.) resolve under the prefab root.

### `prefab status`

Show prefab connection and override summary for an instance.

```
unity-cli prefab status <path>
```

Reports: source prefab asset, override counts, nesting chain, and (for nested prefabs) which asset an edit would land on.

### `prefab diff`

Show the override delta between an instance and its prefab asset.

```
unity-cli prefab diff <path> [--json]
```

Output mirrors `git diff` conventions:
- `~ :Rigidbody.mass   5 → 20` (property override)
- `+ :AudioSource` (added component)
- `- :BoxCollider` (removed component)

### `prefab apply`

Push overrides from an instance back to the prefab asset.

```
unity-cli prefab apply <path>                      # all overrides
unity-cli prefab apply <path>:<component>          # all on one component
unity-cli prefab apply <path>:<component>.<prop>   # single property
```

### `prefab revert`

Discard overrides and pull prefab asset values onto the instance.

```
unity-cli prefab revert <path>
unity-cli prefab revert <path>:<component>
unity-cli prefab revert <path>:<component>.<prop>
```

### `prefab create`

Save a scene object as a new prefab asset.

```
unity-cli prefab create <scenepath> <assetpath>
```

### `prefab unpack`

Break the prefab connection on an instance.

```
unity-cli prefab unpack <path> [--completely]
```

`--completely` unpacks all nested prefab layers.

### `prefab variant`

Create a prefab variant of an existing prefab asset.

```
unity-cli prefab variant <sourceassetpath> <newassetpath>
```

### `prefab open` / `prefab close`

Enter or exit prefab editing mode. While inside, all paths resolve relative to the prefab root.

```
unity-cli prefab open <assetpath>
unity-cli prefab close [--discard]
```

`--discard` exits without saving changes.

### Stdin multi-path

`status`, `diff`, `apply`, `revert`, and `unpack` accept stdin paths. If the positional starts with `:`, it's appended to every piped path as a component/property suffix:

```bash
# Diff every instance of a prefab
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --plain | unity-cli prefab diff

# Apply the mass override on every instance
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --plain | \
    unity-cli prefab apply :Rigidbody.mass

# Revert every dirty instance
unity-cli find --has-overrides --plain | unity-cli prefab revert
```

**Examples:**
```bash
unity-cli prefab status /World/Enemy[0]
unity-cli prefab diff /World/Enemy[0]
unity-cli prefab apply /World/Enemy[0]:Rigidbody.mass
unity-cli prefab revert /World/Enemy[0]
unity-cli prefab create /World/Player Assets/Prefabs/Player.prefab
unity-cli prefab unpack /World/Enemy[0]
unity-cli prefab unpack /World/Boss --completely
unity-cli prefab variant Assets/Prefabs/Enemy.prefab Assets/Prefabs/EnemyElite.prefab
unity-cli prefab open Assets/Prefabs/Enemy.prefab
# ... ls / find / inspect now resolve under the prefab root ...
unity-cli prefab close
unity-cli prefab close --discard
```

---

## scene

Manage loaded scenes.

```
unity-cli scene <subcommand> [options]
```

Identifier resolution is asset-path-first with scene-name fallback. Ambiguous name matches fail with exit code 2 listing all candidates.

### `scene list`

List every loaded scene. The active scene is prefixed with `*`; modified scenes are tagged `(modified)`.

```
unity-cli scene list [--json|--plain]
```

`--json` emits an array of `{path, name, isLoaded, isDirty, isActive, buildIndex}`.

### `scene open`

Load a scene from disk.

```
unity-cli scene open <assetpath> [--mode single|additive|additive-without-loading]
```

Default is `single` (replaces all currently loaded scenes). `single` mode refuses when any scene is dirty — save first or use `additive`.

### `scene close`

Close a loaded scene. Refuses on unsaved changes without `--save` or `--discard`.

```
unity-cli scene close <pathOrName> [--save|--discard]
```

### `scene save`

Save a loaded scene. Defaults to the active scene.

```
unity-cli scene save [<pathOrName>] [--as <newassetpath>]
```

`--as` performs "Save As…" to a new path. Required when the scene has never been saved.

### `scene reload`

Discard in-memory state and reopen the scene from disk.

```
unity-cli scene reload [<pathOrName>] [--save|--discard]
```

### `scene set-active`

Promote a loaded scene to the active scene (the target for `Instantiate`).

```
unity-cli scene set-active <pathOrName>
```

### `scene new`

Create a new untitled scene with `DefaultGameObjects` (Main Camera + Directional Light).

```
unity-cli scene new [--as <assetpath>]
```

Replaces all currently-loaded scenes; refuses on dirty state.

### `scene dirty`

Print `true`/`false` for a scene's modified state.

```
unity-cli scene dirty [<pathOrName>]
```

**Examples:**
```bash
unity-cli scene list
unity-cli scene list --plain | head -1 | xargs unity-cli scene set-active

# Multi-scene editing
unity-cli scene open Assets/Scenes/UI.unity --mode additive
unity-cli scene set-active UI
unity-cli scene save UI --as Assets/Scenes/UI_v2.unity
unity-cli scene close UI --discard

# CI: save dirty scenes, then play
unity-cli scene list --json | jq -r '.[] | select(.isDirty) | .path' | \
    xargs -I{} unity-cli scene save {}
unity-cli editor play --wait

# Bulk reformat: open, reserialize, save
unity-cli find Assets/Scenes/ --type Scene --plain | while read s; do
    unity-cli scene open "$s"
    unity-cli scene save
done
```

---

## guid

Translate an asset path to its GUID.

```
unity-cli guid <assetpath>...
find Assets/... --plain | unity-cli guid
```

**Options:**
- Plain (default): one GUID per input line.
- `--json`: array of `{input, output}` records.

Unresolvable inputs emit an empty line on stdout and a reason on stderr; exit code is non-zero when any input failed.

**Examples:**
```bash
unity-cli guid Assets/Prefabs/Player.prefab
unity-cli guid Assets/Foo.png Assets/Bar.png
unity-cli find Assets/Scenes/ --type Scene --plain | unity-cli guid
unity-cli guid Assets/Foo.png --json | jq -r '.[0].output'
```

---

## path

Translate a GUID back to its asset path. Inverse of `guid`.

```
unity-cli path <guid>...
unity-cli guid <assetpath> | unity-cli path     # round-trip
```

GUIDs are validated as 32-char hex strings before lookup. Bad shapes produce a clear error rather than a silent miss.

**Examples:**
```bash
unity-cli path 1a2b3c4d5e6f7081020304050607080a

# Audit GUIDs referenced in a serialized scene file
grep -oE 'guid: [0-9a-f]{32}' Main.unity | awk '{print $2}' | unity-cli path

# Round-trip
unity-cli guid Assets/Foo.png | unity-cli path
```

---

## reimport

Force Unity to re-run the import pipeline on one or more assets.

```
unity-cli reimport <path>...
unity-cli reimport <folder> --recursive
find ... --plain | unity-cli reimport
```

**Options:**
- `--recursive` — when a path is a folder, walk into it and reimport every asset. Without this flag, folders are an error.
- Accepts paths on stdin (one per line) for batch reimport.

**Behavior:**
- Wraps the batch in `AssetDatabase.StartAssetEditing` / `StopAssetEditing` for a single import pass.
- Not needed alongside `set <asset>:Importer.*` — Unity re-imports automatically when the `.meta` file changes. Use `reimport` when the import wasn't triggered by a meta change.

**Examples:**
```bash
unity-cli reimport Assets/Foo.png
unity-cli reimport Assets/Textures/ --recursive
unity-cli reimport Assets/A.png Assets/B.png Assets/C.png

# External tool rewrote some PNGs; force Unity to catch up
unity-cli reimport Assets/Sprites/ --recursive

# Reimport all textures found by find
unity-cli find Assets/Sprites/ --type Texture2D --plain | unity-cli reimport
```

---

## reserialize

Force Unity to reserialize assets through its YAML serializer. Use after editing `.prefab`, `.unity`, `.asset`, or `.mat` files as text.

```
unity-cli reserialize [path...]
```

No arguments = the entire project.

After a text edit, this tells Unity to load the asset into memory and write it back out through its own serializer. The result is a clean, valid YAML file — as if you had edited it through the Inspector.

**Examples:**
```bash
unity-cli reserialize
unity-cli reserialize Assets/Prefabs/Player.prefab
unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity
unity-cli reserialize Assets/Materials/Character.mat
```

---

## editor

Control play mode and the asset database.

```
unity-cli editor <play|stop|pause|refresh> [options]
```

**Subcommands:**
- `play [--wait]` — enter play mode. `--wait` blocks until fully entered.
- `stop` — exit play mode.
- `pause` — toggle pause/resume (play mode only).
- `refresh` — refresh AssetDatabase.
  - `--compile` — recompile scripts and wait until compilation finishes (fails if compile errors are present).
  - `--force` — refresh even while in play mode.

**Examples:**
```bash
unity-cli editor play --wait
unity-cli editor stop
unity-cli editor refresh --compile
unity-cli editor refresh --force
```

The underlying `manage_editor` tool also accepts these direct actions when called by name: `play`, `stop`, `pause`, `refresh`, `add_tag`, `remove_tag`, `add_layer`, `remove_layer`.

---

## console

Read or clear Unity console log entries.

```
unity-cli console [--lines <N>] [--type <types>] [--stacktrace <mode>] [--clear]
```

**Options:**
- `--lines <N>` — limit to N entries.
- `--type <types>` — comma-separated: `error`, `warning`, `log` (default: all).
- `--stacktrace <mode>` — `none` (first line only), `user` (default, internal frames filtered), `full` (raw).
- `--clear` — clear the console.

**Examples:**
```bash
unity-cli console
unity-cli console --lines 20 --type error
unity-cli console --stacktrace user
unity-cli console --clear
```

---

## exec

Run arbitrary C# code inside the Unity Editor. Full access to `UnityEngine`, `UnityEditor`, and all loaded assemblies.

```
unity-cli exec "<code>" [--usings <list>] [--csc <path>] [--dotnet <path>]
```

Use `return` to get output. Common namespaces are pre-imported. Each invocation is independent — no state persists between calls.

**Options:**
- `<code>` — C# code snippet.
- `--usings` — additional using directives (comma-separated), e.g. `Unity.Entities`.
- `--csc` / `--dotnet` — override auto-detected compiler/runtime paths.

Pipe via stdin to avoid shell escaping on complex code:

```bash
echo 'return Application.dataPath;' | unity-cli exec
```

**Examples:**
```bash
unity-cli exec "return Application.dataPath;"
unity-cli exec "return EditorSceneManager.GetActiveScene().name;"
unity-cli exec "return World.All.Count;" --usings Unity.Entities
unity-cli exec "return GameObject.FindObjectsOfType<Light>().Length;"
echo 'var go = new GameObject("Marker"); go.tag = "EditorOnly"; return go.name;' | unity-cli exec
```

`exec` is the escape hatch: anything a structured command can't do, `exec` can. For AI agents, this means zero-friction access to Unity's entire runtime without writing a single line of tool code.

---

## test

Run Unity tests via the Test Runner API. Requires `com.unity.test-framework`.

```
unity-cli test [--mode <EditMode|PlayMode>] [--filter <name>] [--auto-save-scenes] [--allow-dirty-scenes]
```

**Options:**
- `--mode` — `EditMode` (default) or `PlayMode`.
- `--filter <name>` — substring match on test name.
- `--auto-save-scenes` — save dirty open scenes before running.
- `--allow-dirty-scenes` — run even when scenes have unsaved changes.

**Behavior:**
- EditMode tests hold the connection open and return results directly.
- PlayMode tests trigger a domain reload; the CLI polls for results.

**Examples:**
```bash
unity-cli test
unity-cli test --mode PlayMode
unity-cli test --filter MyTestClass
unity-cli test --auto-save-scenes
```

---

## menu

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

## screenshot

Capture a screenshot of the scene or game view.

```
unity-cli screenshot [--view <scene|game>] [--width <N>] [--height <N>] [--output-path <path>]
```

**Options:**
- `--view` — `scene` (default) or `game`.
- `--width` / `--height` — pixel dimensions (default 1920×1080).
- `--output-path`, `-o` — absolute path or relative to project root (default: `Screenshots/screenshot.png`).

**Examples:**
```bash
unity-cli screenshot
unity-cli screenshot --view game --width 3840 --height 2160
unity-cli screenshot -o /tmp/capture.png
```

---

## profiler

Control the Unity Profiler and query frame samples.

```
unity-cli profiler <hierarchy|enable|disable|status|clear> [options]
```

**`hierarchy` options:**
- `--depth <N>` — recursive depth (0 = unlimited, default 1).
- `--root <name>` — set root by name (substring match across the full tree).
- `--frames <N>` — average over last N frames.
- `--from <N>` / `--to <N>` — average over a frame range.
- `--parent <ID>` — drill into item by ID.
- `--min <ms>` — filter items below threshold.
- `--sort <total|self|calls>` — sort column (default `total`).
- `--max <N>` — max children per level (default 30).
- `--frame <N>` — specific frame index.
- `--thread <N>` — thread index (0 = main).

**Examples:**
```bash
unity-cli profiler hierarchy
unity-cli profiler hierarchy --depth 3
unity-cli profiler hierarchy --root SimulationSystem --depth 3
unity-cli profiler hierarchy --frames 30 --min 0.5 --sort self
unity-cli profiler enable
unity-cli profiler disable
unity-cli profiler status
unity-cli profiler clear
```

---

## status

Show the current Unity Editor connection state.

```
unity-cli status
```

Output includes: port, project path, Unity version, PID, and current state (`ready`, `playing`, `compiling`, etc.). Reports "not responding" if the heartbeat is older than 3 seconds.

The CLI also checks Unity's state automatically before sending any command and waits for it to become responsive if it's busy.

---

## list

List all registered tools (built-in + custom) with their parameter schemas.

```
unity-cli list
```

Discovery is reflection-based over every `[UnityCliTool]` class in the loaded assemblies.

---

## completion

Print a shell completion script. Tab-completes commands, flags, known values, and live Unity hierarchy paths.

```
unity-cli completion <bash|zsh|fish|powershell>
```

**Static completions** (work without Unity running):
- Top-level commands and subcommands
- Flags per command, known flag values

**Dynamic completions** (require a running Unity instance):
- GameObject hierarchy paths: `World/Pl<TAB>` → `World/Player`, `World/Platform`
- Component suffixes: `World/Player:<TAB>` → `World/Player:Transform`, …
- Asset paths: `Assets/Pre<TAB>` → folders and files
- Tags and layers

When Unity isn't running, dynamic completions return nothing silently; static completions still work. Dynamic queries use a 1.5s timeout so completion never hangs.

**Installation:**

```bash
# Bash (persistent)
unity-cli completion bash >> ~/.bashrc

# Zsh
unity-cli completion zsh > "${fpath[1]}/_unity-cli"

# Fish
unity-cli completion fish > ~/.config/fish/completions/unity-cli.fish

# PowerShell
unity-cli completion powershell >> $PROFILE
```

---

## update

Update the CLI binary from the latest GitHub release.

```
unity-cli update [--check]
```

- `--check` — check for a newer version without installing.

---

## init

Install (or remove) the unity-cli connector UPM package in a Unity project by editing `Packages/manifest.json` directly. Works against the filesystem only — the Editor does not need to be running.

```
unity-cli init [<project-path>] [--local <path>] [--upgrade] [--uninstall] [--wait]
```

**Project discovery:**
- Positional `<project-path>` if given.
- Otherwise the global `--project <path>` flag.
- Otherwise walks up from the current directory looking for a Unity project signature (a folder containing both `ProjectSettings/` and `Packages/`).

**Source:**
- Release CLI builds pin the connector to a git tag matching the CLI's own version, so the connector version always matches `unity-cli` by construction. The dependency value written is:
  ```
  https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#vX.Y.Z
  ```
- Dev CLI builds (`Version == "dev"`) refuse the git install — there is no tag to pin to. Use `--local` instead.

**Options:**
- `--local <path>` — install from a local checkout. Writes `file:<abs-path>` into the manifest. Use this when iterating on the connector itself, or from a dev build of `unity-cli`.
- `--upgrade` — required to rewrite an existing dependency entry. Without it, `init` refuses to overwrite and prints the existing source.
- `--uninstall` — remove the connector dependency. Mutually exclusive with `--upgrade` and `--local`.
- `--wait` — after editing the manifest, poll until a heartbeat from this project appears (Unity has to be opened/focused so it imports the package), then run the standard connector-version check. Bounded by the global `--timeout`. Useful in scripts and for agents.

**Examples:**
```bash
# Install into the project at the current working directory
unity-cli init

# Install into a project elsewhere
unity-cli init ~/projects/MyGame

# Install and block until the connector is online and version-matched
unity-cli init --wait

# Bump a previously installed connector to whatever version this CLI is
unity-cli init --upgrade

# Dev-loop: install from a sibling checkout, no git involved
unity-cli init --local ../unity-cli/unity-connector

# Remove the connector cleanly
unity-cli init --uninstall
```

Idempotency: re-running `init` with the same target source is a no-op (prints the existing value and exits 0). Re-running with a different desired source requires `--upgrade`.

---

## interactive

Enter a REPL where unity-cli subcommands can be invoked without the `unity-cli` prefix. The Unity instance is discovered once at startup and reused across every command, so subsequent calls are faster than running the CLI fresh each time. Tab completion (the same machinery the shell completion scripts use) is wired in, and a `!` prefix drops into the host shell for any segment.

```
unity-cli interactive [<project>]
```

**Prompt.** The project's base name, truncated to 20 chars: `MyGame>`. When no instance is bound: `unity-cli (no project)>`.

**Built-in REPL commands** (not regular subcommands; they only exist inside the REPL):

- `exit`, `quit` — leave the REPL.
- `clear` — clear the screen (Ctrl-L also works).
- `use [<project>]` — print the current binding or rebind to a different project (substring match against `ProjectPath`).
- `use --port <N>` — bind by port.
- `use --clear` — unbind (subsequent commands will error until a new `use`).

**Pipelines.** Segments separated by an unquoted `|`:

- **No prefix → unity-cli command**, dispatched internally. Stdin/stdout are wired between adjacent unity-cli segments via in-memory buffers — so `find --plain | inspect` works without spawning subprocesses.
- **`!` prefix → shell command**, run through `sh -c` (Unix) or `cmd /c` (Windows). Redirection (`>`, `>>`, `<`, `2>`) lives inside `!` segments and is handled by the host shell — there is no native redirection for unity-cli segments by design.

Mix freely:

```
find --component Light --plain | inspect :Light
find --plain | !grep -v Disabled | inspect :Light --json | !jq '.intensity'
!ls Assets/Prefabs/*.prefab | inspect
inspect :Component --json | !tee /tmp/snapshot.json | !jq '.position'
```

**Forgiveness.** A leading `unity-cli` on any segment is stripped, so pasting documentation examples works:

```
MyGame> unity-cli find --component Light
```

is equivalent to:

```
MyGame> find --component Light
```

**Cancellation.** Ctrl-C clears the current input line. Ctrl-D at an empty prompt exits the REPL cleanly. Ctrl-C while a command is in flight does **not** cancel it — bound long-running commands with `--timeout` at session start.

**History.** Persistent across sessions at `~/.unity-cli/history` (or `%APPDATA%\unity-cli\history` on Windows), capped at 1000 entries. Up/Down navigate, Ctrl-R reverse-searches.

**Examples:**

```bash
# Start from inside the project directory; auto-discover the running Editor
$ cd ~/projects/MyGame
$ unity-cli interactive
Connected to Unity (port 56789, project /Users/.../MyGame, connector 0.4.0)
Type `help`, `exit`, or any unity-cli subcommand without the `unity-cli` prefix.
Prefix shell commands with `!` to drop to the host shell.
MyGame> find --component Light --plain
/World/Lights/Sun
/World/Lights/Ambient
MyGame> find --component Light --plain | inspect :Light --json | !jq -c '{path, intensity}'
{"path":"/World/Lights/Sun","intensity":1.5}
{"path":"/World/Lights/Ambient","intensity":0.3}
MyGame> use --port 56790
bound: /Users/.../OtherProject (port 56790)
OtherProject> exit
```

**Limitations (v1).**

- No variable bindings, `$_` last-output reference, or multi-line input.
- No native redirection on unity-cli segments — pipe to `!cat > file`.
- Ctrl-C does not interrupt an in-flight command (planned for v2).

---

## Custom tools

Any static C# class with `[UnityCliTool]` in an Editor assembly is auto-discovered and callable directly by name:

```bash
unity-cli <tool_name> [--flag value] [--params '{"k":"v"}']
```

See [Custom Tools](custom-tools.md) for the full guide.

---

## Common patterns

### Terminal ↔ Editor bridge

```bash
# Click in Editor, inspect from terminal
unity-cli select --get | unity-cli inspect

# Find in terminal, highlight in Editor
unity-cli find --component AudioSource --plain | head -1 | unity-cli select

# Set reference to whatever is currently clicked
unity-cli select --get | unity-cli set /World/Enemy:AIScript.target
```

### Batch mutations

```bash
# Flatten all enemies to y=0
unity-cli find --name-prefix Enemy --plain | \
    unity-cli set :Transform.position.y 0

# Add BoxCollider to anything with a MeshRenderer but no collider
unity-cli find --component MeshRenderer --missing Collider --plain | \
    unity-cli component add BoxCollider

# Disable all UI canvases
unity-cli find /UI --component Canvas --plain | \
    unity-cli set :GameObject.activeSelf false
```

### Audit and query

```bash
# All lights with intensity as CSV
unity-cli find --component Light --plain | \
    xargs -I{} unity-cli inspect {}:Light --json | \
    jq -r '[.path, .intensity] | @csv'

# Find objects with null references
unity-cli find --plain | while read p; do
    unity-cli inspect "$p" --json | \
        jq --arg path "$p" '
            .components[] | to_entries[] |
            select(.value == null and .key != "hideFlags") |
            {object: $path, property: .key}
        '
done
```

### Prefab workflows

```bash
# Find diverged instances
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides

# Apply one property back across all instances
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --plain | \
    unity-cli prefab apply :Rigidbody.mass

# Open prefab, edit, close
unity-cli prefab open Assets/Prefabs/Enemy.prefab
unity-cli ls -R                              # resolves under prefab root
unity-cli set :Rigidbody.mass 10
unity-cli prefab close
```

### The exec escape hatch

```bash
# Turn off shadow casting on every Renderer
unity-cli find --component Renderer --plain | \
    xargs -I{} unity-cli exec "
        var r = GameObject.Find(\"{}\").GetComponent<Renderer>();
        r.shadowCastingMode = ShadowCastingMode.Off;
    "

# One-off query
unity-cli exec "return Resources.FindObjectsOfTypeAll<ScriptableObject>().Length;"
```
