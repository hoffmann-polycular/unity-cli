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

CLI (Go) and Connector (C#) must always share the same release version. Three files to keep in sync on every release:

- `unity-connector/package.json` — `version: "X.Y.Z"`
- `unity-connector/Editor/Heartbeat.cs` — `CONNECTOR_VERSION = "X.Y.Z"`
- Git tag — `vX.Y.Z`

The CLI validates the connector version at startup and errors if they differ.

## Release Flow

1. Run all verification steps
2. Bump version in `package.json` and `Heartbeat.cs`
3. Commit + push
4. Push new tag (`vX.Y.Z`) if CLI changed
5. Wait for CI + Release: `gh run watch --exit-status` (background)
6. `go clean -cache -testcache`
7. On success: `unity-cli update`

## Rules

- **Execution:** Use the installed `unity-cli` binary. `go run .` is for testing only.
- **Git:** Commit all unstaged changes before finishing. Unrelated changes go in separate commits.
- **Finish checklist:** Run verification, test with live Unity if available, clean up temp files.

