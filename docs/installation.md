# Installation

[← Back to README](../README.md)

## CLI Binary

### Linux / macOS

```bash
curl -fsSL https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.sh | sh
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.ps1 | iex
```

### Go install

```bash
go install github.com/hoffmann-polycular/unity-cli@latest
```

### Manual download

Pre-built binaries for Linux (amd64, arm64), macOS (Intel, Apple Silicon), and Windows (amd64) are attached to each [GitHub release](https://github.com/hoffmann-polycular/unity-cli/releases/latest).

```bash
# Example: Linux amd64
curl -fsSL https://github.com/hoffmann-polycular/unity-cli/releases/latest/download/unity-cli-linux-amd64 -o unity-cli
chmod +x unity-cli && sudo mv unity-cli /usr/local/bin/
```

### Self-update

```bash
# Update to the latest release
unity-cli update

# Check for a newer version without installing
unity-cli update --check
```

---

## Unity Connector Package

The Connector is a UPM package that runs inside the Unity Editor. It opens an HTTP server and handles incoming CLI commands.

### Add via Package Manager

1. Open **Package Manager** (Window → Package Manager)
2. Click **+** → **Add package from git URL**
3. Enter:

```
https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector
```

### Add via manifest.json

Edit `Packages/manifest.json` directly:

```json
{
  "dependencies": {
    "com.polycular.unity-cli-connector": "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector"
  }
}
```

To pin a specific version, append a tag:

```
https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18
```

### What the Connector does

Once installed, the Connector:

1. Opens an HTTP server on `localhost:8090` when Unity starts (probes fallback ports 8091–8099 if 8090 is taken)
2. Writes a heartbeat file every 500ms to `~/.unity-cli/instances/<hash>.json` — that's how the CLI finds it with no configuration
3. Survives domain reloads (script recompilation restarts the server transparently)
4. Discovers all `[UnityCliTool]` classes via reflection after each reload
5. Routes commands to the matching handler on Unity's main thread

### Recommended: disable editor throttling

By default, Unity throttles editor updates when the window is unfocused. This delays CLI commands because tool handlers run on the main thread.

Go to **Edit → Preferences → General → Interaction Mode** and set it to **No Throttling**.

The Connector also nudges the Editor tick on each incoming request, but No Throttling is still recommended for the most responsive background behavior.

---

## Multiple Unity instances

When more than one Unity Editor is open, each registers on its own port (8090, 8091, …). The CLI defaults to the most recently active instance.

```bash
# See all running instances
ls ~/.unity-cli/instances/

# Select by project path (substring match)
unity-cli --project MyGame editor play

# Select by port
unity-cli --port 8091 status
```

---

## Version matching

The CLI checks the Connector's reported version on every invocation. If they differ, the CLI exits with an error. Use `--ignore-version-mismatch` to override when testing a development build.

Both the CLI and Connector must be updated together for a release. See the [CLAUDE.md](../CLAUDE.md) checklist for the release procedure.
