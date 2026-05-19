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

## Rules and constraints

- Class must be `static`.
- Handler must be `public static object HandleCommand(JObject)` or `public static async Task<object> HandleCommand(JObject)`.
- Must be in an `Editor` assembly (an `Editor/` folder, or a dedicated Editor-only assembly definition).
- Runs on Unity's main thread — all Unity APIs are safe to call.
- Do not use `Thread.Sleep` or long synchronous waits in the handler. For async waits, use the `async Task<object>` variant and `await Task.Yield()`.
- Return `SuccessResponse` for successful results, `ErrorResponse` for failures.
- Duplicate tool names: first-discovered wins; all duplicates are logged as errors.
- No explicit registration — drop the class anywhere in an Editor assembly and it's live after the next compile.
