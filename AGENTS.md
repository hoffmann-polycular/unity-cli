# unity-cli — Agent Guide

CLI tool to control Unity Editor from the command line. See [README.md](README.md) for full feature overview and [docs/](docs/) for reference.

## Structure

```
cmd/                  # Go CLI commands (one file per command)
internal/client/      # Unity HTTP client, instance discovery
unity-connector/      # C# Unity Editor package (UPM)
  Editor/
    Core/             # Shared utilities (Response, ParamCoercion, ToolParams)
    Tools/            # Tool implementations ([UnityCliTool] auto-registered)
    TestRunner/       # Test runner
```

## Adding a Command

1. Add a C# class in `unity-connector/Editor/Tools/` with `[UnityCliTool(Name = "command_name")]` — see [docs/custom-tools.md](docs/custom-tools.md)
2. Go-side code only needed for polling/waiting logic (see `cmd/editor.go`, `cmd/test.go`)
3. When changing any CLI option, command, or parameter, update all of: C# tool, Go help text (`cmd/root.go` overview + per-command help), `README.md`

## Verification (run before every push)

```bash
go clean -testcache
gofmt -w .
~/go/bin/golangci-lint run ./...
~/go/bin/golangci-lint fmt --diff
go test ./...
```

Integration tests require a running Unity Editor and are excluded by default:

```bash
go test -tags integration ./...
```

## Version Management

CLI (Go) and Connector (C#) must always share the same release version. Bump
these three version strings on every release, then tag:

- `unity-connector/package.json` — `version: "X.Y.Z"`
- `unity-connector/Editor/Heartbeat.cs` — `CONNECTOR_VERSION = "X.Y.Z"`
- `flake.nix` — `version = "X.Y.Z"` (no leading `v`, Nix convention)
- Git tag — `vX.Y.Z`

The flake's injected `main.Version` derives from its `version` attr as
`v${version}`, so you do **not** edit it by hand — it always tracks the line
above with the leading `v` added. Release-binary builds stamp `main.Version`
straight from the git tag (`${GITHUB_REF_NAME}`), so both install paths report
the same `vX.Y.Z`.

The CLI validates the connector version at startup and errors if they differ.

Two guards catch drift before a release ships:
- `go test ./cmd/...` (`TestConnectorVersionsInSync`) compares `package.json`
  against `Heartbeat.cs` on every CI run.
- The `verify-tag` job in `.github/workflows/release.yml` refuses to build
  unless `package.json`, `Heartbeat.cs`, and `flake.nix` all match the pushed
  tag (with the leading `v` stripped).

### Init from dev builds

`unity-cli init` pins the connector to a git tag matching the CLI's own
release version, so the two always agree by construction. Dev builds
(`main.Version == "dev"`) have no tag to point at — they refuse the git
install and require `--local <path-to-unity-connector-checkout>`
instead, which writes a `file:` reference into `Packages/manifest.json`.
This is also the way to install the connector while iterating on it
from a local working copy.

## Release Flow

1. Run all verification steps
2. Bump version in `package.json`, `Heartbeat.cs`, and `flake.nix` (`version =`)
3. Commit + push
4. Push new tag (`vX.Y.Z`) if CLI changed
5. Wait for CI + Release: `gh run watch --exit-status` (background)
6. `go clean -cache -testcache`
7. On success: `unity-cli update`

## Rules

- **Windows / Git Bash:** Git Bash (MSYS2) rewrites arguments that start with `/` as Windows paths before the binary sees them. Run `export MSYS_NO_PATHCONV=1` once at the start of a session (or add it to `~/.bashrc`). Do **not** prepend `MSYS_NO_PATHCONV=1` to every individual command. 
- **`exec` is a last resort:** `unity-cli exec` evaluates arbitrary C# and exists for cases nothing else covers. Prefer the purpose-built subcommands (`get`, `set`, `find`, `component`, `scene`, …) and compose them with pipes and standard shell tools (`jq`, `grep`, `xargs`). Only reach for `exec` when the task genuinely cannot be expressed through the available commands.
- **Execution:** Use the installed `unity-cli` binary. `go run .` is for testing only.
- **Interactive mode:** `unity-cli interactive` opens a REPL; can not be used by agents that cant use interactive terminal sessions. users can drop the `unity-cli` prefix and pipe with `|` (segments without prefix are dispatched internally; `!cmd` shells out for grep/jq/etc).
- **Git:** Commit all unstaged changes before finishing. Unrelated changes go in separate commits.
- **Finish checklist:** Run verification, test with live Unity if available, clean up temp files.

