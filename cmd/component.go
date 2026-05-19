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

// componentCmd dispatches `component <action> <path> [<type>]` to the
// `component` tool with named params.
//
// Layouts:
//
//	component list   <objectpath>
//	component add    <objectpath> <type>
//	component remove <objectpath> <type>[<index>]
//
// Stdin (multi-path mode):
//
//	find ... --plain | unity-cli component add <type>
//	find ... --plain | unity-cli component remove <type>[<n>]
//	find ... --plain | unity-cli component list
//
// When stdin is piped and the path positional is omitted, each line is
// treated as a GameObject path and the action is applied to every one.
// Mutators share one Undo group.
func componentCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli component <list|add|remove> <path> [<type>]")
	}

	action := args[0]
	rest := args[1:]

	// Pull positionals out of `rest` while letting any `--flag value` pairs
	// (e.g. `--json`) flow through to buildParams unchanged.
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

	// Normalize --json → --format json so the C# tool sees the same param
	// shape it does for every other tool.
	for i, a := range passthrough {
		if a == "--json" {
			passthrough = append(append([]string{}, passthrough[:i]...), append([]string{"--format", "json"}, passthrough[i+1:]...)...)
			break
		}
	}

	stdinPaths := readStdinPaths()
	hasStdin := len(stdinPaths) > 0

	switch action {
	case "list":
		if hasStdin && len(positionals) == 0 {
			return sendComponentMulti(send, "list", stdinPaths, "", passthrough)
		}
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli component list <path>")
		}
		return sendComponent(send, "list", positionals[0], "", passthrough)

	case "add":
		// Multi-path: `component add <Type>` + stdin paths → 1 positional after action.
		if hasStdin && len(positionals) == 1 {
			return sendComponentMulti(send, "add", stdinPaths, positionals[0], passthrough)
		}
		if len(positionals) < 2 {
			return nil, fmt.Errorf("usage: unity-cli component add <path> <type>")
		}
		return sendComponent(send, "add", positionals[0], positionals[1], passthrough)

	case "remove", "rm":
		if hasStdin && len(positionals) == 1 {
			return sendComponentMulti(send, "remove", stdinPaths, positionals[0], passthrough)
		}
		if len(positionals) < 2 {
			return nil, fmt.Errorf("usage: unity-cli component remove <path> <type>[<index>]")
		}
		return sendComponent(send, "remove", positionals[0], positionals[1], passthrough)

	default:
		return nil, fmt.Errorf("unknown component action: %s\nAvailable: list, add, remove", action)
	}
}

func sendComponent(send sendFn, action, path, typeName string, passthrough []string) (*client.CommandResponse, error) {
	params, err := buildParams(passthrough, map[string]interface{}{
		"action": action,
		"path":   path,
	})
	if err != nil {
		return nil, err
	}
	if typeName != "" {
		params["type"] = typeName
	}
	// `args` from buildParams is empty here (no positionals in passthrough),
	// but strip defensively to keep the wire payload tight.
	delete(params, "args")
	return send("component", params)
}

// sendComponentMulti is the stdin/batch variant: each path resolves and
// processes independently on the connector side, all mutators inside one
// Undo group.
func sendComponentMulti(send sendFn, action string, paths []string, typeName string, passthrough []string) (*client.CommandResponse, error) {
	params, err := buildParams(passthrough, map[string]interface{}{
		"action": action,
		"paths":  paths,
	})
	if err != nil {
		return nil, err
	}
	if typeName != "" {
		params["type"] = typeName
	}
	delete(params, "args")
	delete(params, "path")
	return send("component", params)
}
