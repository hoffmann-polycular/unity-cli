# unity-cli Path Contract — Redesign Proposal v3

Refinement of v2 driven by three corrections:

1. **Cross-scene addressing is not a thing.** The Hierarchy window already presents every loaded scene as one combined tree; `/` means exactly that — "what the user sees in the scene view". The `@<scene>/` sigil is dropped.
2. **`ProjectSettings/` stays as `ProjectSettings/`.** The directory on disk is literally named that, so the namespace prefix matches.
3. **`//` for sub-asset paths stays.** A folder name can legally contain a dot (e.g. `Assets/Stuff.v2/Thing.prefab`), so the single-slash form would be ambiguous between "folder containing asset" and "asset containing sub-object".

Everything else from v2 (selection as cwd, fan-out by default, no inline globs, shell-safe grammar) is retained.

---

## 1. The shell-safety budget

Path syntax may use only characters that survive argv parsing in `bash`, `zsh`, `fish`, and `pwsh`:

- Always safe: `A-Za-z0-9`, `_`, `-`, `.`, `/`, `,`, `+`, `=`, `:`, `@`, `%`, `#`.
- Not safe: `~`, `*`, `?`, `[`, `]`, `$`, `!`, `(`, `)`, `;`, `&`, `|`, `<`, `>`, `` ` ``, leading `-`.

Notable consequences:

- `./` and `../` are **safe** in argv. The shell only interprets them in path-lookup contexts (executable resolution, `cd`), never in argument values.
- `~` is **not safe** — POSIX shells perform tilde expansion before the program runs.
- `*` and `?` are **not safe** — globbing happens before argv is built.
- Anything starting with `-` is **not safe** — parsed as a flag.

Path grammar is built strictly inside this budget.

---

## 2. Foundational principles

### 2.1 Selection is the cwd

`Selection.objects` is the working directory. There is no other notion of "current". Every command reads it once at start.

- Empty selection: relative paths fail loudly with a recovery hint.
- Single selection: relative paths resolve against that one object.
- Multi selection: relative paths fan out (§4).

### 2.2 Fan-out is the default

Path expressions resolve to a *set* of targets. Commands process the set as their unit of work. Single-target is just N=1.

- `inspect ./Hat` with 5 selected GameObjects → 5 inspections.
- `set :Rigidbody.mass 100` with 5 selected → 5 writes, single Undo group.
- `cp . World/Backup/` with 5 selected → 5 copies into `Backup`.

This subsumes most `xargs -I{}` boilerplate.

### 2.3 No inline globs

Globs are filter operations, not address operations. They live behind named flags on `find` and per-command filter flags. Path positionals are always literal.

### 2.4 No cross-scene addressing

What the Hierarchy window shows is the addressable namespace, full stop. When multiple scenes are loaded, their roots all appear under `/` exactly as they do in the scene view. The user does not pick "which scene" — they walk the tree they already see.

Same-named roots from different scenes are disambiguated by sibling-order indexing (`/World[0]`, `/World[1]`), the same rule that already applies to duplicate sibling names anywhere else in the hierarchy. No new grammar required.

This matches what Unity actually allows operationally: GameObjects don't move *across* scenes via simple reparenting, prefab references work uniformly, and the user's mental model is a single tree.

---

## 3. The path grammar

```
path        = anchor segments? component? property?
anchor      =
              ''                            # bare → selection-relative
              | './'                        # selection-relative (explicit)
              | '../' ('../')*              # walk up from selection
              | '/'                         # Hierarchy root (all loaded scenes / open prefab stage)
              | 'Assets/'                   # asset DB
              | 'Packages/'                 # package assets
              | 'ProjectSettings/'          # project settings
              | '#' instanceId              # instance ID
segments    = segment ('/' segment)*
segment     = name ('[' index ']')?
component   = ':' typename ('[' index ']')?
property    = '.' ident ('.' ident)*
```

### Anchor table

| Anchor             | Resolves to                                                           |
|--------------------|-----------------------------------------------------------------------|
| (empty / bare)     | Children of the current selection                                     |
| `./`               | The selection itself (or its children when followed by a segment)     |
| `..`, `../`        | Parent of the selection                                               |
| `../../`, `../../../`, … | Walk further up                                                  |
| `/`                | Hierarchy root — same set of root GameObjects the scene view shows    |
| `Assets/`          | Asset database (project Assets folder)                                |
| `Packages/`        | Package assets                                                        |
| `ProjectSettings/` | Project settings (matches the on-disk folder name)                    |
| `#<id>`            | Pinned instance ID                                                    |

When a prefab stage is open via `prefab open`, `/` resolves under the stage root, mirroring how the Hierarchy itself rebases. Closing the stage restores normal Hierarchy resolution.

### Sub-assets keep `//`

A path under `Assets/` (or `Packages/`) is interpreted as a filesystem path until it hits an asset file. To address an object *inside* an asset, use the explicit `//` separator:

```
Assets/Stuff.v2/Hat.prefab           # asset file inside a folder named "Stuff.v2"
Assets/Stuff.v2/Hat.prefab//Brim     # sub-object "Brim" inside Hat.prefab
Assets/Stuff.v2/Hat.prefab//Brim:MeshRenderer.material   # property on a sub-object
```

The `//` is the only unambiguous boundary, because a folder name may legally contain dots. Single-slash sub-paths after an asset extension are still treated as filesystem paths until proven otherwise (and would error if no such folder exists).

### Examples side-by-side

| Intent                                | Form                                              |
|---------------------------------------|---------------------------------------------------|
| Selection itself                      | `.`                                               |
| Property on selection                 | `:Rigidbody.mass` or `.:Rigidbody.mass`           |
| Child of selection                    | `Hat` or `./Hat`                                  |
| Sibling                               | `../Hat`                                          |
| Grandparent                           | `../..`                                           |
| Hierarchy-root object                 | `/World/Player`                                   |
| Hierarchy roots when same-named       | `/World[0]/Player`, `/World[1]/Player`            |
| Asset                                 | `Assets/Prefabs/Enemy.prefab`                     |
| Asset in a dotted folder              | `Assets/Stuff.v2/Hat.prefab`                      |
| Sub-object inside an asset            | `Assets/Prefabs/Enemy.prefab//Weapon`             |
| Property on an asset sub-object       | `Assets/Prefabs/Enemy.prefab//Weapon:MeshRenderer.enabled` |
| Project setting                       | `ProjectSettings/Physics.gravity`                 |
| Pinned by ID                          | `#14352`                                          |

The leading-character ambiguity from v1 is gone: a leading letter is selection-relative; `/`, `.`, `Assets/`, `Packages/`, `ProjectSettings/`, `#` are unambiguous prefixes.

---

## 4. Multi-selection: fan-out as default

### 4.1 The expansion rule

When a command accepts a path positional, the path is expanded against the selection set:

- **Bare or `./`-prefixed paths** → one resolved path per selected object.
- **`../`-prefixed paths** → one resolved path per selected object's ancestor chain.
- **`/`, `Assets/`, `Packages/`, `ProjectSettings/`, `#id` paths** → no fan-out; one literal path.

### 4.2 Operand multiplicity

Most commands take a single fan-out operand:

```
inspect <paths>                # N inspects
get <paths>                    # N reads
delete <paths>                 # N deletes
component add <paths> <type>   # add type to each
prefab apply <paths>           # apply each
```

Two-operand commands have explicit rules:

| Command            | Multiplicity                                                                 |
|--------------------|------------------------------------------------------------------------------|
| `set <p> <v>`      | `<p>` fans out, `<v>` is single (broadcast value).                           |
| `set <p> <v-path>` | If `<v>` is a fan-out path with cardinality matching `<p>`, pairwise. Otherwise broadcast or error. |
| `cp <src> <dst>`   | `<src>` fans out; `<dst>` must end with `/` (a parent) or have matching cardinality. |
| `mv <src> <dst>`   | Same rules as `cp`.                                                          |
| `reorder <p> <op>` | `<p>` fans out, `<op>` is single.                                            |

### 4.3 Cardinality errors

Mismatched cardinality (`<p>` is 5, `<v>` is 3) is a hard error with code 2 (ambiguous), listing both expansions. No silent first-wins or broadcast-shorter.

### 4.4 Empty selection

Relative path with empty selection → relative to root (with empty selection World/Boss ./World/Boss and /World/Boss all refer to the same thing)

### 4.5 Output ordering

Output preserves selection order (matching Hierarchy top-to-bottom). Each line is prefixed with the canonical resolved path:

```
$ unity-cli get :Rigidbody.mass     # 3 enemies selected
/World/Enemies/Enemy[0]:Rigidbody.mass  10
/World/Enemies/Enemy[1]:Rigidbody.mass  10
/World/Enemies/Enemy[2]:Rigidbody.mass  15
```

`--plain` drops the prefix when only values matter. `--json` emits an array of `{path, value}` records.

### 4.6 Per-target failure

A failure on one target does not stop the others. Final exit code is the worst of per-target codes; stderr lists the failures with their paths. Matches GNU `cp`/`mv`/`rm` on multi-source operations.

---

## 5. Shell-safety in practice

### 5.1 What you can type unquoted

Every token in §3 is shell-safe. Real-world examples needing no quoting:

```
unity-cli inspect .
unity-cli inspect ../UI/HealthBar
unity-cli set :Rigidbody.mass 100
unity-cli set ./Hat:MeshRenderer.enabled false
unity-cli rm /World/Temp
unity-cli inspect Assets/Prefabs/Enemy.prefab
unity-cli inspect Assets/Prefabs/Enemy.prefab//Weapon
unity-cli inspect ProjectSettings/Physics.gravity
unity-cli inspect #14352
```

### 5.2 What requires quoting (and the alternative)

| Situation                          | Why                       | Recommended form                          |
|------------------------------------|---------------------------|-------------------------------------------|
| Object name starts with `-`        | Shell parses as flag      | Address via anchor: `./-Camera`, `/-Root` |
| Object name contains a space       | argv splitting            | Quote: `'./Main Camera'`                  |
| String value contains `*`          | Glob expansion            | Quote: `set :TextMesh.text 'A*'`          |
| Value with shell metacharacters    | Same                      | Quote it.                                 |

There is no path *syntax* requiring quoting in v3 — every quoting need is about an underlying name string, never the address scheme.

### 5.3 Where globs live

Glob filtering is opt-in via flags, never inline:

```
unity-cli find --name-glob 'Enemy*'                   # quoting on glob value, not path
unity-cli find . --name-glob 'Boss*'                  # restricted to selection subtree
unity-cli rm --where-name 'Temp_*' /World/Trash       # rare; usually find | rm
```

For common match-by-prefix/suffix/contains cases, ship explicit non-glob flags so users can avoid quoting altogether:

```
unity-cli find --name-prefix Enemy
unity-cli find --name-suffix _spawn
unity-cli find --name-contains Boss
```

`--regex` remains for power users with the obvious quoting requirement.

---

## 6. Reference resolution under fan-out

When a property expects an Object reference and the value is a path, the same expansion rules apply:

```
unity-cli set :AIScript.target /World/Player
# 5 enemies selected → 5 writes, all pointing at the same Player

unity-cli select /World/Boss
unity-cli set /World/Enemies/E1:AIScript.target .
# E1 now targets Boss
```

Pairwise is opt-in via stdin chaining or by selecting two equal-cardinality groups in the Editor — cardinality must match exactly.

String properties never coerce: a string value that happens to look like a path stays a literal.

---

## 7. Sub-objects, prefab stages, and component context

- **Prefab stage open**: `/` and `.` resolve under the stage root. Closing the stage restores Hierarchy resolution. No `@`-style sigil needed; the resolver inspects `PrefabStageUtility.GetCurrentPrefabStage()` and rebases automatically.
- **Sub-objects of assets**: `Assets/Foo.prefab//Hat:Mesh.color` — `//` separates asset path from sub-asset path. Required because folder names may contain dots (e.g. `Assets/Stuff.v2/Foo.prefab` is a `.prefab` asset inside a `.v2` folder).
- **Component picking on a fan-out**: `:Rigidbody` on N selected objects yields the Rigidbody on each. If a selected object lacks the component, that target errors but others proceed.

---

## 8. Worked examples

### 8.1 Replacing `xargs` with selection fan-out

```
# In the Editor: search "Enemy*" in Hierarchy → Ctrl-A
unity-cli set :Rigidbody.mass 50
```

Or fully terminal-driven:

```
unity-cli find --name-prefix Enemy --plain | unity-cli select
unity-cli set :Rigidbody.mass 50
```

### 8.2 Hierarchical surgery

```
# Children of selection that have a Renderer but no Collider, into a "bad" bin.
unity-cli find . --component MeshRenderer --missing Collider --plain | unity-cli select
unity-cli mv . /World/Bad/
```

### 8.3 Walking up

```
unity-cli inspect ..             # parents of every selected object
unity-cli inspect .. --plain | unity-cli select   # select those parents
```

### 8.4 Asset reference targeting selection

```
unity-cli set /World/Enemies/Elite:AIScript.target .
```

### 8.5 Bulk property copy

```
unity-cli get /World/Anchor:Transform.rotation | unity-cli set :Transform.rotation
```

### 8.6 Working inside a prefab

```
unity-cli prefab open Assets/Prefabs/Enemy.prefab
unity-cli ls /                                # roots under prefab stage
unity-cli set /Root:Rigidbody.mass 10         # / now means stage root
unity-cli prefab close
```

### 8.7 Sub-asset access

```
unity-cli inspect Assets/Models/Hero.fbx//Mesh
unity-cli get Assets/Materials/Pack.asset//RedVariant:Material.color
```

---

## 9. Implementation outline

### Connector

- Rewrite `PathResolver` to accept the new anchor set:
  1. Tokenize anchor.
  2. If anchor depends on selection (bare, `./`, `../+`), expand against `Selection.objects` to produce an N-element list of resolved roots.
  3. Walk the tail under each root, returning the cross product as canonical paths.
- `/` resolution walks all roots returned by `SceneManager` for every loaded scene (or the prefab stage's root when one is open). Same-named roots get sibling-indexed.
- `EditorSelectionSnapshot` — capture `Selection.objects` and active prefab stage once per request to avoid races mid-fan-out.
- `Assets/` resolver's existing `//` handling is preserved verbatim.

### CLI (Go)

- `internal/path/parse.go` — recognize anchors enough to dispatch single-vs-batch HTTP. Actual resolution is server-side.
- `internal/cmd/exec.go` — every command runs in vector mode: read N targets (positional, stdin, or fan-out from selection), batch them into one connector request, aggregate per-target results, emit ordered output.
- Cobra `--` end-of-options handling kept as a safety belt for names with leading `-`, even though anchors usually obviate the need.
- `--plain` strips path prefixes; `--json` wraps in `[{path, …}]`.

### Completions

- `unity-cli inspect <TAB>` → `./`, `../`, `/`, `Assets/`, `Packages/`, `ProjectSettings/`, plus current selection-relative children.
- `unity-cli inspect /<TAB>` → root GameObjects from every loaded scene combined, sibling-indexed where duplicates exist.
- `unity-cli inspect Assets/<TAB>` → folders and assets under `Assets/`.
- `unity-cli inspect <asset>.prefab//<TAB>` → sub-objects within the asset.

---

## 10. Pros and cons

### `/` as the unified Hierarchy root

- **Pro.** Matches Unity's own scene-view presentation: one tree, regardless of how many scenes contribute roots.
- **Pro.** No new sigil to learn; `/` does the obvious thing.
- **Pro.** Removes the awkward case of "I want to address an object across scenes" — Unity itself doesn't really support that operationally, so neither should the path grammar.
- **Con.** Same-named scene roots from different loaded scenes both appear under `/`, requiring sibling-indexing (`/World[0]`, `/World[1]`). Mitigated: this is the same disambiguation rule that applies to any duplicate sibling, so users already know it.
- **Con.** A scripted pipeline that loaded a known scene and expected to address it by scene name has to switch to indexing. Acceptable: scripts that need that level of determinism can `select` first and use `.`.

### Selection-as-cwd, no `--cwd`/`UNITY_CLI_CWD`

- **Pro.** Zero state in the CLI; the only "where am I" knob is one users already manipulate constantly.
- **Pro.** Click-driven scripting: pick something, then issue commands without retyping its path.
- **Pro.** No env-var precedence rules.
- **Con.** Selection state can be surprising — accidentally selecting the wrong object before a destructive command is a foot-gun. Mitigated by `--dry-run` and the per-line canonical-path output prefix.
- **Con.** A scripted pipeline that depends on selection has a hidden global. Mitigated by `unity-cli select <abs> && unity-cli <op>` to pin selection at script start.

### Fan-out by default

- **Pro.** Eliminates ~90% of the `xargs -I{}` boilerplate from the v1 examples.
- **Pro.** Multi-edit is the default, matching Unity's Inspector under multi-select.
- **Pro.** Composes naturally with stdin-fed paths (improvement plan item 3) and with batch-mode Undo grouping (item 11).
- **Con.** Multi-select + irreversible command = larger blast radius. Mitigated by `--dry-run`, by Undo grouping (single Ctrl-Z reverses fan-out), and by hard cardinality errors (no silent broadcast).
- **Con.** Output volume scales with selection size. Acceptable — the user explicitly selected.

### No inline globs, only filter flags

- **Pro.** Eliminates the single biggest shell-quoting pitfall.
- **Pro.** Forces a clean separation between *finding* (predicate) and *acting* (mutation).
- **Con.** `unity-cli rm 'World/Temp_*'` doesn't work; users compose `find | rm` or `find | select | rm`. Slight typing tax, but consistent with how POSIX tools structure things at scale.

### `./` and `../` instead of any new sigils

- **Pro.** Shell-safe in argv (`./` is only interpreted by the shell in path-lookup contexts, never in arguments).
- **Pro.** Mirrors POSIX intuition exactly — selection is genuinely the cwd, so reusing the cwd-relative notation is precise, not analogical.
- **Con.** Conceptual stretch for users who think "selection is not really a directory". Mitigated by the analogy holding precisely throughout.

### `ProjectSettings/` retained verbatim

- **Pro.** Matches the actual on-disk folder name. No translation between what's typed and what's there.
- **Pro.** Unambiguous when scanning a path: nothing else starts with `ProjectSettings`.
- **Con.** Slightly more typing than a shortened `Settings/`. Tab-completion mitigates.

### `//` retained for sub-asset access

- **Pro.** Disambiguates "asset file with sub-object" from "folder containing asset". Folder names with dots are legal in Unity and do happen in practice (versioned folders, framework folders, etc.).
- **Pro.** Visually distinct: `//` reads as a "step into the file" boundary.
- **Con.** Two slashes look like a typo at a glance. Mitigated by consistent use across the doc and in tab-completion output.

### Mandatory cardinality match for paired fan-out

- **Pro.** No silent surprises: 5 srcs to 3 dsts is an error, not a partial run.
- **Pro.** Loud failures match the existing "fail loudly" rule for ambiguity.
- **Con.** Slightly more verbose for users who *want* "broadcast value to N targets" — but cardinality 1 broadcasts unambiguously, which covers the common case.

---

## 11. What this design eliminates (vs. existing v1 reference doc)

- `--cwd` flag and `UNITY_CLI_CWD` env var: not introduced.
- `@here`, `@sel`, `@active`, `@parent`, `@root`, `@prefab` sigils: not introduced.
- `@<scene>/` / `<scene>::<path>` cross-scene addressing: not introduced — `/` is the unified Hierarchy.
- `~/` POSIX-style anchor: not introduced (POSIX shells expand it).
- `--for-each-selected` and `--first` flags: not needed — fan-out is default. (`--first-only` could exist as a guardrail flag for destructive ops but is not a path-grammar concern.)
- Inline globs in path positionals: not allowed — moved to filter flags on `find` and `--where-*` flags on mutators.

---

## 12. What this design preserves

- `:` for component, `.` for property, `[n]` for indexing.
- `#<id>` instance-ID escape hatch.
- `Assets/` and `Packages/` namespace prefixes.
- `ProjectSettings/` namespace prefix (unchanged).
- `//` separator for sub-objects inside assets (unchanged).
- Canonical-path emission rule: tools always emit fully-disambiguated paths.
- Reference auto-coercion in `set` (asset path → asset, scene path → GameObject, component path → Component or `.gameObject`).
- Prefab stage's transparent path rebasing while open.
- "Fail loudly on ambiguity" — extended to apply to cardinality.

---

## 13. Summary

Selection is the cwd. Fan-out is the default. The path grammar uses only shell-safe characters, and POSIX-style `./` / `../` work unquoted because shells leave them alone in argv. `/` is the Hierarchy root that Unity itself shows — a single combined tree across all loaded scenes, with same-named roots disambiguated by sibling index. There is no cross-scene addressing because Unity's operational model doesn't have one. `Assets/`, `Packages/`, and `ProjectSettings/` keep their on-disk names. The `//` sub-asset separator stays because folder names can contain dots. Globs live in filter flags, not path positionals. The result: every common operation is typeable without a single quote character, multi-edit is the path of least resistance, and the only state the CLI cares about is the one the user is already manipulating in the Hierarchy.
