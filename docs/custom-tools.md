# Custom Tools

[← Back to README](../README.md) | [Command Reference](commands.md)

Any static C# class decorated with `[UnityCliTool]` in an Editor assembly is auto-discovered and callable directly from the terminal. No registration step — drop a class anywhere under an `Editor/` folder and it's live after the next script recompilation.

---

## Contents

- [Minimal example](#minimal-example)
- [Full example with parameters](#full-example-with-parameters)
- [Tool contract](#tool-contract)
- [Attribute reference](#attribute-reference)
- [ToolParams API](#toolparams-api)
- [Response types](#response-types)
- [Calling custom tools](#calling-custom-tools)
- [Discovery and listing](#discovery-and-listing)
- [Async tools](#async-tools)
- [Writing files](#writing-files)
- [Teaching an agent about your tools](#teaching-an-agent-about-your-tools)
- [Rules and constraints](#rules-and-constraints)

---

## Minimal example

```csharp
using UnityCliConnector;
using Newtonsoft.Json.Linq;

[UnityCliTool(Name = "ping", Description = "Check that the connector is alive")]
public static class PingTool
{
    public static object HandleCommand(JObject parameters)
    {
        return new SuccessResponse("pong");
    }
}
```

```bash
unity-cli ping
# pong
```

---

## Full example with parameters

```csharp
using UnityCliConnector;
using Newtonsoft.Json.Linq;
using UnityEngine;

[UnityCliTool(Name = "spawn", Description = "Spawn an enemy at a world position", Group = "gameplay")]
public static class SpawnEnemy
{
    public class Parameters
    {
        [ToolParameter("X world position", Required = true)]
        public float X { get; set; }

        [ToolParameter("Y world position", Required = true)]
        public float Y { get; set; }

        [ToolParameter("Z world position", Required = true)]
        public float Z { get; set; }

        [ToolParameter("Prefab name in Resources folder", DefaultValue = "Enemy")]
        public string Prefab { get; set; }
    }

    public static object HandleCommand(JObject parameters)
    {
        var p = new ToolParams(parameters);
        float x = p.GetFloat("x", 0);
        float y = p.GetFloat("y", 0);
        float z = p.GetFloat("z", 0);
        string prefabName = p.Get("prefab", "Enemy");

        var prefab = Resources.Load<GameObject>(prefabName);
        if (prefab == null)
            return new ErrorResponse($"Prefab '{prefabName}' not found in Resources.");

        var instance = Object.Instantiate(prefab, new Vector3(x, y, z), Quaternion.identity);

        return new SuccessResponse("Enemy spawned", new
        {
            name = instance.name,
            position = new { x, y, z }
        });
    }
}
```

```bash
unity-cli spawn --x 1 --y 0 --z 5 --prefab Goblin
unity-cli spawn --params '{"x":1,"y":0,"z":5,"prefab":"Goblin"}'
```

---

## Tool contract

### Handler method

```csharp
public static object HandleCommand(JObject parameters)
```

Or the async variant:

```csharp
public static async Task<object> HandleCommand(JObject parameters)
```

The handler runs on Unity's main thread — all `UnityEngine` and `UnityEditor` APIs are safe to call.

### Parameters class

The nested `Parameters` class is optional but strongly recommended:

- `unity-cli list` uses it to display parameter names, types, descriptions, required flags, and defaults.
- AI agents can discover your tool's interface without reading the source.
- Each property corresponds to a flag (`--x`, `--y`, `--prefab`) the CLI accepts.

---

## Attribute reference

### `[UnityCliTool]`

Applied to the class.

| Property | Type | Description |
|----------|------|-------------|
| `Name` | string | Command name (default: class name converted to snake_case) |
| `Description` | string | Tool description shown in `unity-cli list` |
| `Group` | string | Category for grouping in `list` output |

**Name derivation:** `SpawnEnemy` → `spawn_enemy`, `UITree` → `ui_tree`. Override with `Name = "my_name"` for a shorter or custom name.

### `[ToolParameter]`

Applied to properties in the nested `Parameters` class.

| Property | Type | Description |
|----------|------|-------------|
| *(constructor arg)* | string | Parameter description |
| `Required` | bool | Whether the parameter is required (default: `false`) |
| `Name` | string | Parameter name override |
| `DefaultValue` | object | Default value hint shown in `list` |

---

## ToolParams API

`ToolParams` provides consistent parameter reading with type coercion:

```csharp
var p = new ToolParams(parameters);

// Strings
string name = p.Get("name");                     // null if missing
string name = p.Get("name", "default");          // with fallback

// Numbers
int count    = p.GetInt("count", 1);
float mass   = p.GetFloat("mass", 0f);

// Boolean
bool flag    = p.GetBool("enabled", false);

// Raw JToken (for arrays, nested objects, etc.)
JToken raw   = p.GetRaw("data");
```

Parameter names are matched case-insensitively. The CLI normalizes flag names from `--my-flag` to `my_flag` before passing them to the handler.

---

## Response types

### `SuccessResponse`

```csharp
return new SuccessResponse("message");
return new SuccessResponse("message", dataObject);
```

The `message` is printed to the terminal. The `dataObject` is serialized as JSON under the `data` key in the response.

### `ErrorResponse`

```csharp
return new ErrorResponse("Something went wrong.");
```

Causes the CLI to print the message and exit with a non-zero exit code.

### Raw return

Any serializable object can be returned directly — it will be JSON-serialized as the `data` field. Using `SuccessResponse` / `ErrorResponse` is recommended for clarity.

---

## Calling custom tools

```bash
# Positional args become the "args" array
unity-cli my_tool arg1 arg2

# Named flags become named params
unity-cli my_tool --count 5 --name "Player"

# Raw JSON params (useful for complex structures)
unity-cli my_tool --params '{"count":5,"name":"Player"}'

# Both flag-style and --params can be combined; --params fills in the rest
unity-cli my_tool --count 5 --params '{"name":"Player"}'
```

---

## Discovery and listing

```bash
# Show all available tools — built-in + project custom
unity-cli list

# Narrow it: one tool, or one group
unity-cli list my_tool
unity-cli list --group my_group

# Rendered like built-in command help (description + parameter table)
unity-cli help my_tool
unity-cli help my_group
```

Output groups tools by their `Group` attribute. Built-in tools appear under `built-in`; your tools appear under their declared group (or `custom` if unset).

Discovery runs on every script recompilation (domain reload). Duplicate tool names are detected at startup and logged as errors — only the first discovered handler is used.

---

## Async tools

For operations that involve waiting (e.g. loading an asset, querying an external service):

```csharp
[UnityCliTool(Name = "load_bundle", Description = "Load an asset bundle and return its contents")]
public static class LoadBundle
{
    public static async Task<object> HandleCommand(JObject parameters)
    {
        var p = new ToolParams(parameters);
        string path = p.Get("path");

        var request = AssetBundle.LoadFromFileAsync(path);
        while (!request.isDone)
            await Task.Yield();

        var bundle = request.assetBundle;
        if (bundle == null)
            return new ErrorResponse($"Failed to load bundle at '{path}'.");

        var names = bundle.GetAllAssetNames();
        return new SuccessResponse($"Loaded {names.Length} assets", new { assets = names });
    }
}
```

---

## Writing files

A tool that writes a file to a path the caller supplied must resolve it **against the project
root**, and report the resolved absolute path back in its `SuccessResponse` data:

```csharp
private static string ResolveOutputPath(string userPath)
{
    if (Path.IsPathRooted(userPath))
        return Path.GetFullPath(userPath);
    var projectRoot = Path.GetDirectoryName(Application.dataPath);
    return Path.GetFullPath(Path.Combine(projectRoot, userPath));
}
```

The Editor is a separate process from the shell that invoked the command, and the two do not
necessarily share a filesystem view — a sandboxed Unity Hub (Flatpak/Snap), a container or WSL
shell driving a host Editor, and `PrivateTmp=true` all give the two sides different directories
behind the same absolute path. A write outside the project can therefore succeed here and be
invisible to the caller, with nothing in the exit code or the response to show it. The project
directory is the one location both sides agree on.

The CLI checks this from its own side: when a command is handed an output path (`--file`,
`--out`, `--output`, `--output-path`, `-o`, …) and reports success, the client stats the file
and warns on stderr when it is not there. Naming the output flag conventionally, and returning
the resolved path under a `path` key, is what lets that check work for a custom tool.

---

## Teaching an agent about your tools

A project's `[UnityCliTool]` subcommands are invisible to the general
unity-cli skill — it cannot know what you registered. Two things make them
discoverable:

- **Write a `Description` and `[ToolParameter]` descriptions.** `unity-cli list`
  and `unity-cli help <tool>` render them, and the shipped skill tells agents to
  run those before concluding a capability is missing. A described tool is found;
  an undescribed one is not.
- **If you ship a project skill, do not call it `unity-cli`.** A user-level skill
  shadows a project-level skill of the same name, so `<project>/.claude/skills/unity-cli/`
  is invisible on any machine where someone installed the unity-cli skill itself
  (`--with-skill`, or `unity-cli skill install`). Name it after the project —
  `myproject-unity` — and it loads alongside, instead of being silently ignored.

---

## Rules and constraints

- Class must be `static`.
- Handler must be `public static object HandleCommand(JObject)` or `public static async Task<object> HandleCommand(JObject)`.
- Must be in an `Editor` assembly (an `Editor/` folder, or a dedicated Editor-only assembly definition).
- Runs on Unity's main thread — all Unity APIs are safe to call.
- Do not use `Thread.Sleep` or long synchronous waits in the handler. For async waits, use the `async Task<object>` variant and `await Task.Yield()`.
- Return `SuccessResponse` for successful results, `ErrorResponse` for failures.
- Duplicate tool names: first-discovered wins; all duplicates are logged as errors.
- No explicit registration — drop the class anywhere in an Editor assembly and it's live after the next compile.
- Resolve caller-supplied output paths against the project root and return the absolute path — see [Writing files](#writing-files).
