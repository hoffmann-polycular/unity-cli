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

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// prefabCmd dispatches `prefab <action> <args...>` to the `prefab` tool with
// named params, matching the component.go pattern.
//
// Layouts (per unity-cli-reference.md §prefab):
//
//	prefab status <path>
//	prefab diff   <path>
//	prefab apply  <path>[:Component[.prop]]
//	prefab revert <path>[:Component[.prop]]
//	prefab create <scenepath> <assetpath>
//
// Translates to named params (action / path / asset) so the C# tool's
// error messages stay unambiguous.
func prefabCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf(
			"usage: unity-cli prefab <status|diff|apply|revert|create> <args...>")
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

	switch action {
	case "status":
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli prefab status <path>")
		}
		return sendPrefab(send, "status", positionals[0], "", passthrough)

	case "diff":
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli prefab diff <path>")
		}
		return sendPrefab(send, "diff", positionals[0], "", passthrough)

	case "apply":
		if len(positionals) < 1 {
			return nil, fmt.Errorf(
				"usage: unity-cli prefab apply <path>[:Component[.prop]]")
		}
		return sendPrefab(send, "apply", positionals[0], "", passthrough)

	case "revert":
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

	default:
		return nil, fmt.Errorf(
			"unknown prefab action: %s\nAvailable: status, diff, apply, revert, create",
			action)
	}
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
