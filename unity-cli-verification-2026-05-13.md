# unity-cli Verification — 2026-05-13

Verified against project `escapefake2` (Unity 6000.3.8f1, connector 0.3.18).
Ran ~44 realistic workflows in the actual scene + asset database. Below
are the friction points worth fixing, ordered roughly by impact.

---

## Critical

### 1. `create` can't create scene-root objects ✅ FIXED

`unity-cli create Empty /TestRoot` fails with **"Both parent path and name must
be non-empty"**. The command requires `<parent>/<name>` format, so there's no
way to create a top-level GameObject. Working around it requires creating
under an existing parent and then `mv … /<name>` to promote.

`cp` and `mv` already accept `/<name>` for the scene-root case (and `/` to
keep the source name). `create` should match:

```
unity-cli create Empty /TopLevel       # currently errors
unity-cli create Cube /Floor            # currently errors
unity-cli create Empty /                # → auto-name "GameObject"?
```

**Fix:** in `cmd/create.go` (or wherever path is parsed), allow a single
leading `/` followed by a name. Translate to the scene-root parent.

---

### 2. `create` / `cp` / `mv` / `component add` etc. default to JSON, breaking the documented pipe pattern ✅ FIXED

Help for `create` says:

```
unity-cli create Quad World/UI/Canvas/Background | unity-cli set --value "1 1 1"
unity-cli create Empty World/Parent | xargs -I{} unity-cli component add {} Rigidbody
```

Both broken today — `create` emits multi-line pretty JSON, not the canonical
path. Same for `cp`, `mv`, `component add`. Currently:

```
$ unity-cli create Empty /SceneSetup/Foo
{
  "instanceId": -95214,
  "name": "Foo",
  ...
}
```

Piping that to `select` errors: `"{": No object matching '{' under the anchor`.

**Fix:** make these commands match `ls`/`find`/`get` and default to plain
output (one canonical path per line). `--json` opts back in to the full
record. Reduces friction enormously for chained workflows.

---

### 3. `select Assets/Foo.prefab` works but `select --get` lies ✅ FIXED

```
$ unity-cli select 'Assets/00_App/Prefabs/FAQ Dummy.prefab'
$ unity-cli select --get
/FAQ Dummy
```

The asset is selected in the Project window, but `--get` returns a scene-path
shape (`/FAQ Dummy`) that doesn't correspond to anything in the scene and
can't be fed back into commands that expect either an asset path or a real
scene path. The round-trip is broken.

**Fix:** when the selected object is an asset, emit its asset path
(`Assets/00_App/Prefabs/FAQ Dummy.prefab`), not the loaded-prefab in-memory
path. Same as the `Project-window Ping` plan item is meant to address —
this is the read side.

---

## Significant

### 4. Multi-target `get` (plain) emits values only — no way to tell which is which ✅ FIXED

```
$ unity-cli find --component Camera --plain | unity-cli get :Camera.fieldOfView
60
60
60
```

This is spec-correct for `--plain`, but in practice the most common ad-hoc
audit use ("dump fovs of all my cameras") becomes useless. The pipe-friendly
intent (`xargs`-able paths only) and the diagnostic intent (`path → value`
table) are in tension.

Workaround `--format human` works but isn't discoverable. Worth adding a
`--with-path` / `--prefix` flag on `get`, or making the default add a path
prefix for multi-target while keeping single-target scalar. The current
all-or-nothing flip is the worst middle ground.

---

### 5. C# accessors don't map to SerializedObject names — discoverability gap ✅ FIXED

`MeshRenderer.sharedMaterial` (singular) errors with **"No property
'sharedMaterial' on MeshRenderer"**. Every Unity dev expects this name to
work because that's the C# API. The actual SO field is `m_Materials` (plural,
array). `materials` works; `materials[0]` works; `m_Materials` works.

A handful of these are universally known: `sharedMaterial`, `sharedMesh`,
`bounds`, `localPosition` (works), `localScale` (works), etc. Most work
because they happen to match SO names. The discrepancy on `sharedMaterial`
is jarring.

**Fix:** maintain a small alias map in `PathResolver.FindPropertyByUserName`
for the well-known C# accessors that resolve to a different SO name.

---

### 6. Enum values shown as raw ints in `inspect` ✅ FIXED

```
$ unity-cli inspect Assets/.../SM_Prop_Sack_01.fbx:Importer
  materialImportMode: 0
  materialName: 0
  materialSearch: 1
  materialLocation: 0
```

These are `ModelImporterMaterialImportMode`, `ModelImporterMaterialName`,
etc. Showing `0` instead of `InPrefab` is useless to anyone who doesn't
already know the enum mapping. `set` accepts enum names (`set … 0` or
`set … InPrefab` both work), so the read side should resolve them too.

**Fix:** in `SerializedPropertyReader`, when `prop.propertyType == Enum`,
read `prop.enumDisplayNames[prop.enumValueIndex]` instead of raw `intValue`.

---

### 7. Inspecting an asset shows the wrong path

```
$ unity-cli inspect Assets/00_App/Prefabs/FAQ\ Dummy.prefab
/FAQ Dummy
  active: False  ...
```

The first line says `/FAQ Dummy` (the loaded prefab's GameObject name) when
the user inspected `Assets/00_App/Prefabs/FAQ Dummy.prefab`. The path you
typed should be the path you see at the top. Loses the original asset
context entirely — especially confusing if the prefab's root has a
different name than the asset file.

**Fix:** when inspecting a `PathKind.Asset` with no `:Component`, header
the output with the asset path, not the in-memory GameObject path.

---

### 8. `set` response is verbose JSON dict by default for multi-target

A simple bulk `set :GameObject.activeSelf false | xargs` on 50 objects
dumps 250+ lines of JSON to stdout. The only useful summary is "applied N,
failed M." `set` should default to a one-line-per-target summary in plain
mode, matching `get`. JSON via `--json` for tooling.

---

## Minor

### 9. `unity-cli help` is 207 lines

Single long help dump. Useful in `less` but overwhelming on first contact.
Consider splitting into a short "common commands" overview + `help <topic>`
for deep dives (which exists, but isn't surfaced).

### 10. `set` on a same-value still creates a prefab override

```
$ unity-cli set /Items/Flour:GameObject.tag "Untagged"
{
  "newValue": "Untagged",
  "oldValue": "Untagged",
  "override": true,
  ...
}
```

When `newValue == oldValue` and we're a prefab instance, this still seems
to mark the property as overridden. Possibly Unity's own behavior (any
write through `SerializedObject` overrides), but worth a no-op short-circuit.

### 11. Asset paths with spaces don't auto-quote when piped through select-as-asset

The `select 'Assets/Foo Bar.prefab'` worked, but the round-trip via
`--get` doesn't preserve the asset path (see #3). With spaces in the
name, even xargs-piping would need `--null-delimited`. Default null-delim
output when paths contain spaces would be nice.

### 12. `find` returns subclass instances when a base name is given

```
$ unity-cli find --component Renderer --plain | wc -l
640
```

`Renderer` matched everything that inherits from `UnityEngine.Renderer`
(MeshRenderer, SpriteRenderer, ParticleSystemRenderer, …). That's the
useful behavior, but it's undocumented. Add a one-line note to `find`
docs: "Component filter is `is-a`, not `==` — base types match subclasses."

### 13. `create` JSON `parent` field is empty for scene-root creations ✅ FIXED

When (post-fix) creating at scene root, the JSON response would have
`parent: ""`. Should be `"/"` for symmetry with `get`/`inspect` output
where the hierarchy root is `/`.

### 14. Asset-importer `inspect` shows nested lists as `[N]\n  [0] …\n  [1] …`

```
materials: [1]
    [0] 
      type: UnityEngine:Material
```

The `[1]` after the property name is the count — but it looks like an
index. `materials: (1)` or `materials (1 items):` would be clearer.

---

## Discovery gaps (not bugs, but the docs/help don't reveal these)

- The `:GameObject` pseudo-component is documented in the reference but not
  hinted at in `help` overview. New users won't find it.
- Same for `:Importer` and `ProjectSettings/<group>` — first-class features
  that don't show up in `unity-cli help` overview.
- `unity-cli __complete` exists and works but isn't documented anywhere
  user-facing.
- `unity-cli exec` is the escape hatch — discoverable, but not used in
  any example workflow in `help`. Worth one usage example in the overview.

---

## Things that work really well

- **Stdin path piping** for `get`, `inspect`, `set`, `component`, `prefab`,
  `select`, `rm`, `reimport` — the most useful change in the recent rounds.
  Made every script-style workflow trivial.
- **Multi-path fan-out on selection** for `set`/`get`/`inspect`. The "select
  some objects in Hierarchy, run set" pattern is genuinely fluid.
- **`:GameObject` pseudo-component** for activeSelf/tag/layer/name. Frequent
  enough that having a typed alias saves real time.
- **`:Importer` pseudo-component** with the auto-reimport-on-meta-change
  approach. Clean separation, no popups, just works.
- **`find` filters** (`--name-prefix`, `--component`, `--missing`, `--tag`,
  `--layer`, `--has-overrides`, `--prefab`). The combination is powerful.
- **Per-target failure** (§4.6): stdout vs stderr split, non-zero exit on
  any failure. Round-tripped cleanly in the partial-failure test
  (Workflow 42).
- **`scene` command**, especially the guards (refuses to `open(single)` /
  `close` / `reload` over dirty state without an explicit flag).
- **`ProjectSettings/<group>.<prop>`** — works, indistinguishable from a
  normal property access.
- **Undo integration**: `unity-cli exec "Undo.PerformUndo();"` cleanly
  reversed our `set` (Workflow 31).

---

## Suggested follow-up priorities

1. Fix #1 (scene-root create) — small change, removes a daily-friction
   workaround.
2. Fix #2 (plain default for create/cp/mv/component add) — makes the
   documented pipe patterns actually work and removes the most common
   "why doesn't this pipe?" frustration.
3. Fix #6 (enum-name display) — high value, low risk, one-line change in
   `SerializedPropertyReader`.
4. Address #5 (`sharedMaterial` and friends) — small alias table covers
   90% of the cases.
5. Address #3 + #11 (`select --get` for assets) once the Ping/Project-window
   plan item lands.
