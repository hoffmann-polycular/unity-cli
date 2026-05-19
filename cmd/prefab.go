// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// COMMERCIAL LICENSE NOTICE:
// If you wish to use this code inside a non-GPL, proprietary software product,
// you must instead acquire a commercial license from the copyright holder.
//
// Contact: info@polycular.com | Website: https://www.polycular.com/

package cmd

import (
	"fmt"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// prefabCmd dispatches `prefab <action> <args...>` to the `prefab` tool with
// named params, matching the component.go pattern.
//
// Layouts (per unity-cli-reference.md §prefab):
//
//	prefab status  <path>
//	prefab diff    <path>
//	prefab apply   <path>[:Component[.prop]]
//	prefab revert  <path>[:Component[.prop]]
//	prefab create  <scenepath> <assetpath>
//	prefab unpack  <path> [--completely]
//	prefab variant <sourceassetpath> <newassetpath>
//	prefab open    <assetpath>
//	prefab close   [--discard]
//
// Translates to named params (action / path / asset) so the C# tool's
// error messages stay unambiguous.
func prefabCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf(
			"usage: unity-cli prefab <status|diff|apply|revert|create|unpack|variant|open|close> <args...>")
	}

	action := args[0]
	rest := args[1:]

	// Split positionals from flag passthroughs (--json, --format, etc.).
	var positionals []string
	var passthrough []string
	for i := 0; i < len(rest); i++ {
		a := rest[i]
		if len(a) > 1 && a[0] == '-' {
			passthrough = append(passthrough, a)
			continue
		}
		positionals = append(positionals, a)
	}

	// Normalize --json → --format json so the C# side sees a consistent param.
	for i, a := range passthrough {
		if a == "--json" {
			passthrough = append(
				append([]string{}, passthrough[:i]...),
				append([]string{"--format", "json"}, passthrough[i+1:]...)...,
			)
			break
		}
	}

	// For the actions that take a single GameObject target, stdin paths
	// drive multi-path mode. If a positional starts with ":" it's a suffix
	// appended to every piped path (e.g. `prefab apply :Rigidbody.mass`).
	stdinPaths := readStdinPaths()
	hasStdin := len(stdinPaths) > 0

	switch action {
	case "status":
		if hasStdin && len(positionals) <= 1 {
			return sendPrefabMulti(send, "status", buildPrefabMultiPaths(stdinPaths, positionals), passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli prefab status <path>")
		}
		return sendPrefab(send, "status", positionals[0], "", passthrough)

	case "diff":
		if hasStdin && len(positionals) <= 1 {
			return sendPrefabMulti(send, "diff", buildPrefabMultiPaths(stdinPaths, positionals), passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli prefab diff <path>")
		}
		return sendPrefab(send, "diff", positionals[0], "", passthrough)

	case "apply":
		if hasStdin && len(positionals) <= 1 {
			return sendPrefabMulti(send, "apply", buildPrefabMultiPaths(stdinPaths, positionals), passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab apply <path>[:Component[.prop]]")
		}
		return sendPrefab(send, "apply", positionals[0], "", passthrough)

	case "revert":
		if hasStdin && len(positionals) <= 1 {
			return sendPrefabMulti(send, "revert", buildPrefabMultiPaths(stdinPaths, positionals), passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab revert <path>[:Component[.prop]]")
		}
		return sendPrefab(send, "revert", positionals[0], "", passthrough)

	case "create":
		if len(positionals) < 2 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab create <scenepath> <assetpath>")
		}
		return sendPrefab(send, "create", positionals[0], positionals[1], passthrough)

	case "unpack":
		if hasStdin && len(positionals) == 0 {
			return sendPrefabMulti(send, "unpack", stdinPaths, passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab unpack <path> [--completely]")
		}
		return sendPrefab(send, "unpack", positionals[0], "", passthrough)

	case "variant":
		if len(positionals) < 2 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab variant <sourceassetpath> <newassetpath>")
		}
		// Reuse path/asset as (source, new) — matches create's parameter shape.
		return sendPrefab(send, "variant", positionals[0], positionals[1], passthrough)

	case "open":
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli prefab open <assetpath>")
		}
		return sendPrefab(send, "open", positionals[0], "", passthrough)

	case "close":
		// No positionals required. --discard is a passthrough flag.
		return sendPrefab(send, "close", "", "", passthrough)

	default:
		return nil, fmt.Errorf(
			"unknown prefab action: %s\nAvailable: status, diff, apply, revert, create, unpack, variant, open, close",
			action)
	}
}

// buildPrefabMultiPaths combines stdin lines with an optional :suffix
// positional. If positionals[0] starts with ":", it's appended to every
// piped path (e.g. ":Rigidbody.mass" + "/A" → "/A:Rigidbody.mass").
// Otherwise stdin lines are used unmodified.
func buildPrefabMultiPaths(stdinPaths []string, positionals []string) []string {
	suffix := ""
	if len(positionals) >= 1 && len(positionals[0]) > 0 && positionals[0][0] == ':' {
		suffix = positionals[0]
	}
	if suffix == "" {
		return stdinPaths
	}
	out := make([]string, 0, len(stdinPaths))
	for _, p := range stdinPaths {
		out = append(out, p+suffix)
	}
	return out
}

func sendPrefabMulti(send sendFn, action string, paths []string, passthrough []string) (*client.CommandResponse, error) {
	params, err := buildParams(passthrough, map[string]interface{}{
		"action": action,
		"paths":  paths,
	})
	if err != nil {
		return nil, err
	}
	delete(params, "args")
	delete(params, "path")
	return send("prefab", params)
}

func sendPrefab(send sendFn, action, path, asset string, passthrough []string) (*client.CommandResponse, error) {
	params, err := buildParams(passthrough, map[string]interface{}{
		"action": action,
	})
	if err != nil {
		return nil, err
	}
	if path != "" {
		params["path"] = path
	}
	if asset != "" {
		params["asset"] = asset
	}
	// No positionals expected here; defensively strip what buildParams might add.
	delete(params, "args")
	return send("prefab", params)
}
