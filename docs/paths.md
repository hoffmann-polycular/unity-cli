# Path Grammar

[← Back to README](../README.md) | [Command Reference](commands.md)

Paths are the universal address for everything unity-cli touches: GameObjects, components, properties, assets, and project settings. Understanding the grammar unlocks the full composability of the tool.

---

## Contents

- [Core idea: selection as cwd](#core-idea-selection-as-cwd)
- [Path grammar](#path-grammar)
- [Anchors](#anchors)
- [Fan-out: multi-select as default](#fan-out-multi-select-as-default)
- [Output formats](#output-formats)
- [Reference resolution](#reference-resolution)
- [`:GameObject` pseudo-component](#gameobject-pseudo-component)
- [`:Importer` pseudo-component](#importer-pseudo-component)
- [Sub-asset access (`//`)](#sub-asset-access-)
- [Shell safety](#shell-safety)

---

## Core idea: selection as cwd

The Editor's `Selection.objects` is the CLI's working directory. Every relative path resolves against it:

- **Empty selection** — relative paths fall back to the hierarchy root. `Items`, `./Items`, and `/Items` all refer to the same object.
- **One selected object** — relative paths resolve under it.
- **Multiple selected objects** — relative paths *fan out*, one resolution per selected object.

This means the fastest way to target an object for a CLI operation is often to click it in the Hierarchy first:

```bash
# Click an object in Unity, then:
unity-cli select --get | unity-cli inspect
unity-cli set :Rigidbody.mass 50
```

---

## Path grammar

```
path        = anchor segments? component? property?
anchor      = ''                          # bare → selection-relative
              | './'                      # selection itself (or children with a segment)
              | '../' ('../')*            # walk up from selection
              | '/'                       # hierarchy root (all loaded scenes, or prefab stage)
              | 'Assets/'                 # asset database
              | 'Packages/'              # package assets
              | 'ProjectSettings/'       # project settings
              | '#' instanceId           # pinned instance ID
segments    = segment ('/' segment)*
segment     = name ('[' index ']')?
component   = ':' typename ('[' index ']')?
property    = '.' ident ('.' ident)*
```

---

## Anchors

| Anchor | Resolves to |
|--------|-------------|
| *(empty / bare)* | Children of the current selection |
| `./` | The selection itself |
| `../`, `../../`, … | Walk up the selection's ancestor chain |
| `/` | Hierarchy root — every loaded scene's roots (or prefab stage root) |
| `Assets/` | Project asset database |
| `Packages/` | Package assets |
| `ProjectSettings/` | Project settings (matches the on-disk folder name) |
| `#<id>` | Pinned instance ID (`EditorUtility.InstanceIDToObject`) |

### Examples

| Intent | Path |
|--------|------|
| Selection itself | `.` or `./` |
| Property on selection | `:Rigidbody.mass` |
| Child of selection | `Hat` or `./Hat` |
| Sibling | `../Hat` |
| Grandparent | `../..` |
| Hierarchy root object | `/World/Player` |
| Duplicate-named roots | `/World[0]/Player`, `/World[1]/Player` |
| Asset | `Assets/Prefabs/Enemy.prefab` |
| Asset in a dotted folder | `Assets/Stuff.v2/Hat.prefab` |
| Sub-object inside an asset | `Assets/Prefabs/Enemy.prefab//Weapon` |
| Property on an asset sub-object | `Assets/Prefabs/Enemy.prefab//Weapon:MeshRenderer.enabled` |
| Project setting | `ProjectSettings/Physics.gravity` |
| Pinned by ID | `#14352` |

### Disambiguation

When sibling names or component types are duplicated, append a 0-based index in brackets:

```bash
unity-cli inspect /World/Enemy[1]          # second Enemy at that level
unity-cli inspect /Player:AudioSource[0]   # first AudioSource on Player
```

Tools always emit canonical (fully-indexed) paths, so piped output is always unambiguous.

### Pinning with instance ID

Instance IDs survive hierarchy reorders. Useful for scripts that reference the same object across multiple operations:

```bash
ID=$(unity-cli find --name "Boss" --json | jq -r '.[0].instanceId')
unity-cli set "#$ID:Transform.position" "0 0 0"
unity-cli set "#$ID:Rigidbody.mass" 100
```

---

## Fan-out: multi-select as default

A path may resolve to a set. Commands process the entire set as a unit.

```bash
# 5 enemies selected — set fans out to all 5 in one call, one Undo group
unity-cli set :Rigidbody.mass 50

# 3 scenes loaded with a "Boss" root — find walks all of them
unity-cli find / --name-prefix Boss
```

**Cardinality rules for two-operand commands:**

| Command | Rule |
|---------|------|
| `set <path> <value>` | Path fans out; value is broadcast to all targets |
| `cp <src> <dst>` | Source fans out; destination must end with `/` when cardinality > 1 |
| `mv <src> <dst>` | Same rule as `cp` |

Cardinality mismatch is a hard error (exit code 2) that lists both expansions.

### Fan-out via stdin

When a command reads paths from stdin, fan-out happens across those paths:

```bash
# Set all selected enemies' mass — pipe from find
unity-cli find --name-prefix Enemy --plain | unity-cli set :Rigidbody.mass 100

# Inspect all Lights
unity-cli find --component Light --plain | unity-cli inspect :Light
```

---

## Output formats

Commands support a consistent `--format` flag:

| Flag | Output |
|------|--------|
| `--plain` | One canonical path / value per line — `xargs`/`grep`-compatible |
| `--json` | Pretty-printed JSON, `jq`-compatible |
| `--format human` | Human-readable tree / aligned columns |
| `--null-delimited` | `\0`-separated, for `xargs -0` when paths contain spaces |

**Defaults by command:**

- `ls`, `find`, `get` — `--plain` (pipe-source commands; one item per line)
- `inspect` — `--format human` (interactive reading is the primary use case)
- Mutating commands (`set`, `cp`, `mv`, `create`, …) — emit the canonical path of the result

**Multi-target output:**

With `--format human`, each result is prefixed with its canonical resolved path. With `--plain`, only values are emitted:

```bash
$ unity-cli get :Rigidbody.mass --format human    # 3 enemies selected
/World/Enemies/Enemy[0]:Rigidbody.mass  10
/World/Enemies/Enemy[1]:Rigidbody.mass  10
/World/Enemies/Enemy[2]:Rigidbody.mass  15

$ unity-cli get :Rigidbody.mass                   # plain (default)
10
10
15
```

**Per-target failure:**

A failure on one target does not stop the others. Successful results go to stdout, per-target failures go to stderr with the canonical path prefix. The final exit code is non-zero when any target failed — matching GNU `cp`/`mv`/`rm` semantics.

---

## Reference resolution

When a property expects a Unity object reference, `set` accepts a path as the value and resolves it automatically:

| Value form | Resolved as | Mechanism |
|------------|-------------|-----------|
| `World/...` | GameObject | Scene traversal |
| `World/...:Type` | Component | `GetComponent` after traversal |
| `Assets/...` | Asset | `AssetDatabase.LoadAssetAtPath` |
| `Assets/Foo.prefab//...` | Object inside prefab | Prefab asset traversal |
| `#<id>` | Any object | `EditorUtility.InstanceIDToObject` |
| `null` / `none` | Null reference | Clears the field |

The property's expected type drives coercion:
- Assigning a GameObject path to a Transform property auto-calls `GetComponent<Transform>()`.
- Assigning a component path to a GameObject property auto-accesses `.gameObject`.
- String properties never coerce — a string that looks like a path stays a literal.

---

## `:GameObject` pseudo-component

The reserved component name `GameObject` exposes the core fields from Unity's Inspector top strip as readable/writable properties through the normal path grammar:

| Property | Type | R/W | Description |
|----------|------|-----|-------------|
| `name` | string | R/W | Object name shown in the Hierarchy |
| `activeSelf` | bool | R/W | Local active flag (calls `SetActive`). Alias: `active` |
| `activeInHierarchy` | bool | R | True when the object and all ancestors are active |
| `tag` | string | R/W | Unity tag — must be registered in Tag Manager |
| `layer` | int | R/W | Layer index; accepts a layer name string or `0–31` |
| `layerName` | string | R | Human-readable name of the current layer |
| `isStatic` | bool | R/W | Static flag |
| `instanceId` | int | R | Stable instance ID |

Property names are matched case-insensitively with `_` and `-` stripped (`active_self`, `activeSelf`, and `active-self` are all accepted).

```bash
unity-cli get    /Player:GameObject.name
unity-cli get    /Player:GameObject.activeSelf
unity-cli set    /Player:GameObject.activeSelf false
unity-cli set    /Player:GameObject.layer "UI"        # layer name string
unity-cli set    /Player:GameObject.layer 5           # or int index
unity-cli set    /Player:GameObject.name "Hero"
unity-cli inspect /Player:GameObject                  # show all fields

# Fan-out: set all selected objects active in one call
unity-cli set :GameObject.activeSelf true
```

---

## `:Importer` pseudo-component

Every asset has an `AssetImporter` behind it — what the Inspector shows when you click an asset in the Project window. unity-cli exposes the importer through the path grammar as a pseudo-component:

```bash
unity-cli inspect Assets/Foo.png:TextureImporter
unity-cli get     Assets/Foo.png:TextureImporter.maxTextureSize
unity-cli set     Assets/Foo.png:TextureImporter.maxTextureSize 1024
```

The alias `:Importer` always resolves to whichever importer subclass the asset uses:

```bash
unity-cli inspect Assets/Foo.png:Importer            # → TextureImporter
unity-cli set     Assets/Hero.fbx:Importer.animationType Generic
unity-cli get     Assets/Sound.wav:Importer.loadType
```

When you pass a concrete name (`:TextureImporter`, `:ModelImporter`, …), unity-cli verifies the asset uses that importer and errors with exit code 64 otherwise.

**Reimport semantics:** `set` on an importer property writes the `.meta` file and Unity re-imports the asset automatically — no separate `reimport` call is needed for routine edits.

**Batch usage:**

```bash
# Shrink all sprites to max 512px
unity-cli find Assets/Sprites/ --type Texture2D --plain | \
    unity-cli set :Importer.maxTextureSize 512

# Inspect all texture importers
unity-cli find Assets/ --type Texture2D --plain | unity-cli inspect :Importer
```

---

## Sub-asset access (`//`)

Asset folder names can contain dots (`Assets/Stuff.v2/Hat.prefab`), so `//` is the unambiguous boundary between the on-disk asset path and the object path inside it:

```bash
Assets/Prefabs/Enemy.prefab//Weapon                          # GameObject inside the prefab
Assets/Prefabs/Enemy.prefab//Weapon:MeshRenderer.material    # property on that sub-object
```

Sub-asset paths do not have importers — `Assets/Foo.prefab//Child:Importer` is rejected. Use `Assets/Foo.prefab:Importer` (without `//`) to inspect the prefab's importer.

### Prefab stages

When `prefab open <asset>` is active, `/` and `.` rebase under the stage root automatically, mirroring how the Hierarchy itself behaves. No special syntax is required; the path resolver detects the open stage automatically. Closing the stage restores normal hierarchy resolution.

---

## Shell safety

The path grammar uses only characters that survive `argv` parsing in bash, zsh, fish, and PowerShell: `A-Za-z0-9`, `_`, `-`, `.`, `/`, `,`, `+`, `=`, `:`, `@`, `%`, `#`.

The common cases need no quoting:

```bash
unity-cli inspect .
unity-cli inspect ../UI/HealthBar
unity-cli set :Rigidbody.mass 100
unity-cli inspect Assets/Prefabs/Enemy.prefab
unity-cli inspect Assets/Prefabs/Enemy.prefab//Weapon
unity-cli inspect ProjectSettings/Physics.gravity
unity-cli inspect '#14352'
```

Quoting is only needed for the underlying string, never for the path syntax itself:

| Situation | Why | Recommended form |
|-----------|-----|-----------------|
| Object name starts with `-` | Shell parses as flag | Address via anchor: `./-Camera` |
| Object name contains a space | argv splitting | Quote: `'./Main Camera'` |
| String value contains `*` | Glob expansion | Quote: `set :TextMesh.text 'A*'` |

### No inline globs

Globs are filter operations, not address operations. They live on `find` flags (`--name`, `--name-prefix`, `--name-suffix`, `--name-contains`, `--regex`). Path positionals are always literal.

```bash
# Does NOT work:
unity-cli rm '/Temp_*'

# Correct:
unity-cli find --name-prefix Temp_ --plain | unity-cli rm
```
