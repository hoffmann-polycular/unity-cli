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

**This document teaches the model: path grammar, composition, and the traps. It is not the flag reference.** The binary is — and the binary is always the one actually installed. Ask it rather than guessing, and prefer it over this file wherever the two disagree.

---

## Step 0 — Check the Editor, then ask the binary

```bash
unity-cli status                # port, project path, version, PID — errors if unreachable
```

If `status` fails, the Editor needs to be open (with the Connector package installed) before anything else works.

```bash
unity-cli help <command>        # authoritative flags for any command
unity-cli list                  # every registered tool, including this project's own
unity-cli list <tool>           # one tool's parameter schema (--group <g> for a group)
unity-cli help <tool>           # same, rendered as help, for a project-registered tool
```

`list` and `help` resolve **project-registered tools** too — a Unity project can add its own `[UnityCliTool]` subcommands, which no general document can know about. When a task sounds project-specific ("export the localization sheet", "rebuild the atlas"), check `unity-cli list` before concluding it cannot be done.

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

## Command Map

What exists, and which command owns the job. For flags, run `unity-cli help <command>`.

| Job | Command |
|-----|---------|
| List children / scene roots | `ls [-R] [path]` |
| Search scenes or the asset database | `find [path] [filters]` |
| Dump an object, component, or property | `inspect <path>` |
| Read one value | `get <path>:Comp.prop` |
| Write one value (registers Undo) | `set <path>:Comp.prop <value>` |
| Call a method / Odin `[Button]` | `invoke <path>:Comp.Method [args]` |
| Read / set / clear the Editor selection | `select [<path>...]` |
| Create empty, primitive, or prefab instance | `create <type> <path>` |
| Delete | `rm <path>` |
| Copy / reparent / rename | `cp <src> <dst>`, `mv <src> <dst>` |
| Reorder siblings or components | `reorder <path> --up\|--down\|--first\|--last\|…` |
| Add / remove / list components | `component add\|remove\|list <path> [<type>]` |
| Prefab overrides and lifecycle | `prefab status\|diff\|apply\|revert\|create\|unpack\|variant\|open\|close` |
| Load / save / activate scenes | `scene list\|open\|close\|save\|reload\|set-active\|new\|dirty` |
| Asset path ↔ GUID | `guid <path>`, `path <guid>` |
| Re-run importers / rewrite YAML | `reimport <path>`, `reserialize <path>` |
| Play mode, pause, recompile | `editor play\|stop\|pause\|refresh` |
| Read or clear the console | `console [--type ...] [--lines N] [--clear]` |
| Run a Unity menu item | `menu "File/Save Project"` |
| Capture the Game/Scene view or a camera | `screenshot [--view ...] [-o file]` |
| Profiler samples | `profiler hierarchy\|enable\|disable\|status\|clear` |
| Run tests | `test [--mode EditMode\|PlayMode] [--filter ...]` |
| Arbitrary C# — **last resort** | `exec "<code>"` |
| This skill's own install / state | `skill install\|status` |

`exec` evaluates arbitrary C# and bypasses everything the purpose-built commands do for you (path resolution, Undo grouping, fan-out, error classification). Reach for it only when the task genuinely cannot be expressed any other way — and check `unity-cli list` first, in case the project already registered a tool for it.

---

## Output Formats

| Flag | Best for |
|------|----------|
| `--plain` (default for read commands) | Piping to another `unity-cli` call or shell tool |
| `--json` | Passing to `jq` or a script that parses JSON |
| `--format human` | Displaying results to the user |
| `--null-delimited` | Paths that contain spaces, used with `xargs -0` |

---

## Writing Files (output paths)

Some commands take a caller-supplied output path — `screenshot -o`, and any project-registered tool with a `--file` / `--out` / `--output` style flag. **The Editor performs the write, not this shell.** It is a separate process and does not necessarily share your filesystem view: a sandboxed Unity Hub (Flatpak/Snap), a container or WSL shell driving a host Editor, and `PrivateTmp=true` all give the two sides different directories behind the same absolute path. When that happens the command still exits **0** and reports the path it wrote — and nothing exists there for you. The failure is silent and success-shaped; only a listing from this side reveals it.

**Rule: write outputs into the project directory.** It is the one location both sides provably agree on — the Editor has it open. Relative output paths are resolved against the project root, so a project-relative path is always safe. Do **not** write to a session scratchpad, `/tmp`, or any other path outside the project and expect to read it back.

```bash
unity-cli status                              # prints the project path
unity-cli screenshot -o Screenshots/shot.png  # project-relative — safe
ls -l <project-path>/Screenshots/shot.png     # verify before using the file
```

unity-cli warns on stderr when a command reports writing a file that is not visible from here, but verify anyway before reading a file back. If a file is reported written yet missing, confirm the split rather than blaming the command — list the same directory from both sides:

```bash
ls -l /some/dir                                     # caller's view
unity-cli exec 'return string.Join("\n", System.IO.Directory.GetFiles(@"/some/dir"));'
```

Disjoint listings mean the two processes are looking at different directories; re-run with a path under the project root.

---

## Composition Patterns

The point of the tool. `find` emits one path per line; every mutating command accepts paths on stdin and applies them in a single Undo group.

### Find → read
```bash
unity-cli find --component Light --plain | unity-cli get :Light.intensity
unity-cli find --name "Enemy*" --plain | unity-cli inspect :Rigidbody
```

### Find → mutate (one Undo group)
```bash
unity-cli find --component Canvas --plain | unity-cli set :Canvas.enabled false
unity-cli find --name "Enemy*" --plain | unity-cli component add NavMeshAgent
unity-cli find --name "Debug_*" --plain | unity-cli rm
```

### Copy a value between objects
```bash
unity-cli get /World/Source:Transform.position | \
    unity-cli set /World/Target:Transform.position
```

### Chain through a command's own output
```bash
# create prints the new object path
unity-cli create Cube /World/Terrain/Rock | unity-cli inspect
```

### Batch prefab and importer work
```bash
unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides --plain | \
    unity-cli prefab revert

unity-cli find Assets/Sprites/ --type Texture2D --plain | \
    unity-cli set :Importer.maxTextureSize 512
```

### Filter through standard tools
```bash
unity-cli find --component Light --json | jq -r '.[] | select(.name | test("^Sun")) | .path' | \
    unity-cli set :Light.intensity 2
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

- **Guessing flags instead of asking**: run `unity-cli help <command>`. This document is deliberately not a flag reference, and a flag that existed in some other version is the easiest way to waste a turn.
- **Assuming a capability is missing**: run `unity-cli list`. Projects register their own subcommands, and they are invisible from here.
- **Output paths outside the project**: a command can report a file written and exit 0 while nothing is there for you — the Editor may not share this shell's filesystem view. Write outputs project-relative and verify (see [Writing Files](#writing-files-output-paths)).
- **Windows / Git Bash path mangling**: Git Bash (MSYS2) rewrites arguments starting with `/` into Windows paths before the binary sees them, so `/World/Player` becomes something like `C:/Program Files/Git/World/Player`. Use `export MSYS_NO_PATHCONV=1` — run it once at the start of the session or add it to `~/.bashrc`. Do **not** prepend `MSYS_NO_PATHCONV=1` before each individual command.
- **Path separator**: always `/`, never `\`.
- **`get`/`set` without a property**: `:Rigidbody` without `.mass` returns the object path, not a value. Always include `:Component.property`.
- **Duplicate sibling names**: use `[0]`, `[1]` to disambiguate — e.g. `/World/Enemy[1]`.
- **Prefab stage active**: after `prefab open`, all paths are relative to the prefab root. Remember to `prefab close` when done.
- **Asset vs. hierarchy path**: `Assets/...` addresses the asset database; `/World/...` or bare names address the scene hierarchy. Don't mix them.
- **Editor not running**: check `unity-cli status` before a long batch operation. All commands fail immediately if the Editor isn't reachable.
- **`exec` as a first resort**: it bypasses gating, Undo grouping, and fan-out. Exhaust the purpose-built commands and `unity-cli list` first.

---

## Interactive mode

`unity-cli interactive` opens a REPL where commands drop the `unity-cli` prefix and pipe internally (`!cmd` shells out for `grep`/`jq`). It cannot be used by agents without an interactive terminal — mention it to users, don't reach for it yourself.
