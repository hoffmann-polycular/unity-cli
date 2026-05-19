---
name: unity-cli
description: >
  How to use unity-cli to inspect and control a live Unity Editor from the terminal.
  Use this skill whenever the user is working on a Unity project and wants to:
  - inspect the scene hierarchy or find GameObjects matching some criteria
  - read or change properties on GameObjects, components, or assets
  - do any kind of batch operation across multiple objects (rename, set a value, add/remove components, delete)
  - manage prefab overrides (diff, apply, revert)
  - control the Editor (play/stop, run tests, capture screenshots, read the console)
  - compose shell pipelines that feed Unity data into standard tools (jq, grep, awk)
  Trigger even when the user does not say "unity-cli" — e.g. "find all my lights and set them to warm color",
  "revert all prefab overrides in my scene", "show me what's in the hierarchy", or
  "run the EditMode tests and show me any failures". If a Unity Editor is (or could be) running, reach for this skill.
---

# unity-cli

Treats the Unity Editor like a filesystem. `ls`, `find`, `inspect`, `get`, `set` work on GameObjects the way they work on files. Output is pipe-friendly and composable with `jq`, `xargs`, `grep`, and `awk`.

---

## Step 0 — Verify the Editor is reachable

Every command requires a live Editor with the Connector package installed.

```bash
unity-cli status          # prints port, project path, version, PID
                          # errors if Editor not running or connector missing
```

If `status` fails, tell the user the Editor needs to be open before proceeding.

---

## Path Grammar

Paths are the core abstraction shared by every command.

| Form | Example | Meaning |
|------|---------|---------|
| Absolute | `/World/Player` | Object from scene root |
| Relative | `Hat` or `./Hat` | Child of current Editor selection |
| Selection | `.` | The selected object(s) — fan-out if multi-select |
| Parent | `..` or `../Sibling` | Walk up the hierarchy |
| Disambiguate | `/World/Enemy[1]` | 0-based index for same-named siblings |
| Component | `/World/Player:Rigidbody` | Component on the object |
| Property | `:Transform.position.x` | Field (or sub-field) on a component |
| Pseudo-component | `:GameObject.name` | Built-ins: `name`, `activeSelf`, `tag`, `layer`, `isStatic` |
| Asset | `Assets/Prefabs/Enemy.prefab` | Asset database path |
| Sub-object | `Assets/Prefabs/Enemy.prefab//Weapon` | Object inside a prefab asset |
| Importer | `Assets/Foo.png:Importer.maxTextureSize` | Asset importer property |
| Project setting | `ProjectSettings/Physics.gravity` | Project-level settings |
| Instance ID | `#14352` | Object pinned by instance ID |

**Fan-out**: when stdin provides multiple paths (or multiple objects are selected), all mutating commands apply to all of them inside one Undo group.

---

## Output Formats

| Flag | Best for |
|------|----------|
| `--plain` (default for read commands) | Piping to another `unity-cli` call or shell tool |
| `--json` | Passing to `jq` or a script that parses JSON |
| `--format human` | Displaying results to the user |
| `--null-delimited` | Paths that contain spaces, used with `xargs -0` |

---

## Command Reference

### Exploring the scene

```bash
# List scene roots or children of an object
unity-cli ls
unity-cli ls /World
unity-cli ls -R /World                      # recursive
unity-cli ls -R /World --components         # include component names

# Search the hierarchy (filters AND-combine)
unity-cli find --name "Enemy*"
unity-cli find --component Rigidbody
unity-cli find --component Rigidbody --missing Collider
unity-cli find --tag Player --active
unity-cli find --layer "Ignore Raycast" --inactive
unity-cli find --is-prefab-instance
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides
unity-cli find --max-depth 2 /World --component Light

# Search the asset database (path must start with Assets/ or Packages/)
unity-cli find Assets/ --type Prefab --name "Enemy*"
unity-cli find Assets/Sprites/ --type Texture2D

# Read a full object, component, or property
unity-cli inspect /World/Player
unity-cli inspect /World/Player:Rigidbody
unity-cli inspect /World/Player:Rigidbody.velocity

# Read a single scalar value
unity-cli get /World/Player:Rigidbody.mass
unity-cli get /World/Player:Transform.position

# Editor selection
unity-cli select --get                      # what's selected now
unity-cli select /World/Player              # select an object
unity-cli select --add /World/Enemy[0]      # add to selection
unity-cli select --clear
```

### Modifying the scene

```bash
# Set a property value
unity-cli set /World/Player:Rigidbody.mass 25
unity-cli set /World/Player:Transform.position "0 1 0"
unity-cli set /World/Player:Light.color "#ff8800"
unity-cli set /World/Enemy:AIScript.target /World/Player    # object reference
unity-cli set /World/Player:MeshRenderer.enabled false

# Create GameObjects
unity-cli create Empty /World/Managers/AudioManager
unity-cli create Cube /World/Terrain/Rock
unity-cli create --prefab Assets/Prefabs/Enemy.prefab /World/Enemies/Enemy_01

# Delete
unity-cli rm /World/Temp
unity-cli find --name "Temp_*" --plain | unity-cli rm

# Copy / move / rename
unity-cli cp /World/Player /World/Player --auto-suffix "_{n}"
unity-cli mv /World/OldName /World/Enemies/NewName

# Reorder siblings
unity-cli reorder /World/Player --first
unity-cli reorder /World/Player:Rigidbody --up

# Components
unity-cli component list /World/Player
unity-cli component add /World/Player NavMeshAgent
unity-cli component remove /World/Player NavMeshAgent
```

### Prefab management

```bash
unity-cli prefab status /World/Enemy[0]          # connection info + override count
unity-cli prefab diff /World/Enemy[0]            # show override delta
unity-cli prefab apply /World/Enemy[0]           # push all overrides to asset
unity-cli prefab apply /World/Enemy[0]:Rigidbody.mass    # one property only
unity-cli prefab revert /World/Enemy[0]          # pull asset values onto instance
unity-cli prefab create /World/Enemy[0] Assets/Prefabs/Enemy.prefab  # save as new asset
unity-cli prefab unpack /World/Enemy[0]          # break connection
unity-cli prefab unpack /World/Enemy[0] --completely     # unpack nested too

# Enter prefab editing mode (subsequent path commands use the prefab stage as root)
unity-cli prefab open Assets/Prefabs/Enemy.prefab
unity-cli inspect /Enemy/Weapon
unity-cli set /Enemy/Weapon:MeshRenderer.enabled false
unity-cli prefab close
```

### Scene management

```bash
unity-cli scene list
unity-cli scene open Assets/Scenes/Main.unity
unity-cli scene open Assets/Scenes/Extra.unity --mode additive
unity-cli scene save
unity-cli scene save --as Assets/Scenes/Main_backup.unity
unity-cli scene close Assets/Scenes/Extra.unity --save
unity-cli scene reload
unity-cli scene dirty                            # true/false
```

### Asset operations

```bash
unity-cli guid Assets/Prefabs/Player.prefab      # asset path → GUID
unity-cli path 1a2b3c4d5e6f708090a0b0c0d0e0f010  # GUID → asset path
unity-cli reimport Assets/Textures/Icon.png
unity-cli reimport Assets/Textures/ --recursive
unity-cli reserialize Assets/Prefabs/Player.prefab  # rewrite through Unity's YAML serializer
```

### Editor control

```bash
unity-cli editor play --wait          # enter play mode, block until fully in
unity-cli editor stop
unity-cli editor pause
unity-cli editor refresh --compile    # reimport + wait for script compilation

unity-cli console --type error,warning --stacktrace user --lines 20
unity-cli console --clear

unity-cli exec "return Camera.main.transform.position.ToString();"
unity-cli exec "Selection.activeGameObject.name = \"Renamed\";"

unity-cli menu "File/Save Project"

unity-cli test --mode EditMode
unity-cli test --mode PlayMode --filter MyTests.SmokeTest

unity-cli screenshot --view game -o Screenshots/frame.png
unity-cli profiler hierarchy --depth 3 --min 0.5
```

---

## Composition Patterns

### Find → read (inspect matching objects)
```bash
unity-cli find --component Light --plain | unity-cli get :Light.intensity
unity-cli find --name "Enemy*" --plain | unity-cli inspect :Rigidbody
```

### Find → mutate (batch set, one Undo group)
```bash
# Disable every Canvas in the scene
unity-cli find --component Canvas --plain | unity-cli set :Canvas.enabled false

# Set mass on all Rigidbodies
unity-cli find --component Rigidbody --plain | unity-cli set :Rigidbody.mass 10

# Add a component to all matching objects
unity-cli find --name "Enemy*" --plain | unity-cli component add NavMeshAgent

# Delete all matching objects
unity-cli find --name "Debug_*" --plain | unity-cli rm
```

### Copy a value between objects
```bash
unity-cli get /World/Source:Transform.position | \
    unity-cli set /World/Target:Transform.position
```

### Create then inspect
```bash
# create prints the new object path; pipe it straight into inspect
unity-cli create Cube /World/Terrain/Rock | unity-cli inspect
```

### Batch prefab operations
```bash
# Revert every override on every instance of a prefab
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides --plain | \
    unity-cli prefab revert

# Find all instances that have drifted
unity-cli find --is-prefab-instance --has-overrides --plain | unity-cli prefab diff
```

### Batch asset importer changes
```bash
unity-cli find Assets/Sprites/ --type Texture2D --plain | \
    unity-cli set :Importer.maxTextureSize 512
```

---

## Value Syntax for `set`

| Type | Accepted forms |
|------|---------------|
| Number | `42`, `3.14` |
| Bool | `true`, `false` |
| String | `"hello"` |
| Vector2/3/4 | `"1 2 3"`, `"1,2,3"`, `{"x":1,"y":2,"z":3}`, `[1,2,3]` |
| Color | `"#ff8800"`, `"1 0.53 0 1"` (RGBA float) |
| Quaternion | `"0 90 0"` (Euler degrees) or `"0 0.707 0 0.707"` (xyzw) |
| Object reference | `"Assets/Prefabs/Enemy.prefab"`, `"/World/Player"`, `"#14352"` |
| Null / clear | `null`, `none`, `""` |

---

## Common Mistakes

- **Path separator**: always `/`, never `\`.
- **`get`/`set` without a property**: `:Rigidbody` without `.mass` returns the object path, not a value. Always include `:Component.property`.
- **Duplicate sibling names**: use `[0]`, `[1]` to disambiguate — e.g. `/World/Enemy[1]`.
- **Prefab stage active**: after `prefab open`, all paths are relative to the prefab root. Remember to `prefab close` when done.
- **Asset vs. hierarchy path**: `Assets/...` addresses the asset database; `/World/...` or bare names address the scene hierarchy. Don't mix them.
- **Editor not running**: check `unity-cli status` before a long batch operation. All commands fail immediately if the Editor isn't reachable.

---

## Introspection

```bash
unity-cli list              # all registered tools + parameter schemas
unity-cli help <command>    # full flag reference for any command
unity-cli status            # confirm connection before a batch job
```
