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

### Windows — Git Bash note

unity-cli paths start with `/` (e.g. `/World/Player`). Git Bash (MSYS2) treats leading slashes as Unix paths and rewrites them to Windows paths before the binary ever sees them. Set this variable to disable that conversion:

```bash
export MSYS_NO_PATHCONV=1
```

Add it to `~/.bashrc` or `~/.bash_profile` to make it permanent.

### Go install

```bash
go install github.com/hoffmann-polycular/unity-cli@latest
```

### Nix flake

The repository is a flake. Run it directly without installing:

```bash
nix run github:hoffmann-polycular/unity-cli -- status
```

Install it into your profile:

```bash
nix profile install github:hoffmann-polycular/unity-cli
```

Pin a released version by appending the tag:

```bash
nix profile install github:hoffmann-polycular/unity-cli/v0.4.1
```

Or add it to a flake-based system / home-manager config via the `default`
package output (`packages.<system>.default`). For hacking on unity-cli itself,
`nix develop` drops you into a shell with `go`, `gopls`, and `golangci-lint`.

> Don't use `unity-cli update` on a Nix install — the binary lives in the
> read-only Nix store. Upgrade by bumping the flake input (or re-running
> `nix profile install`/`nix profile upgrade`) instead.

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

## Claude Code Skill

The repository ships a [Claude Code](https://claude.ai/code) skill that teaches Claude the full unity-cli command set, path grammar, and composition patterns. With it installed, Claude automatically reaches for unity-cli when you describe what you want to do in Unity terms — without you needing to mention the tool by name.

Pass `--with-skill` (Linux/macOS) or `-WithSkill` (Windows) to the installer to install it alongside the binary:

**Linux / macOS**
```bash
curl -fsSL https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.sh | sh -s -- --with-skill
```

**Windows (PowerShell)**
```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.ps1))) -WithSkill
```

The skill is written to `~/.claude/skills/unity-cli/SKILL.md` (`%USERPROFILE%\.claude\skills\unity-cli\SKILL.md` on Windows). To update it after a unity-cli upgrade, re-run the installer with the flag, or copy `.claude/skills/unity-cli/SKILL.md` from the repo manually.

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

Both the CLI and Connector must be updated together for a release. See the [AGENTS.md](../AGENTS.md) checklist for the release procedure.
