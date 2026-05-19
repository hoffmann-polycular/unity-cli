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
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// sceneCmd dispatches `scene <action> <args...>` to the `scene` tool with
// named params. Action-style subcommand routing matching prefab.go / editor.go.
//
// Layouts (per unity-cli-reference.md §scene):
//
//	scene list
//	scene open       <assetpath> [--mode single|additive|additive-without-loading]
//	scene close      <pathOrName> [--save|--discard]
//	scene save       [<pathOrName>] [--as <newassetpath>]
//	scene reload     [<pathOrName>]
//	scene set-active <pathOrName>
//	scene new        [--as <assetpath>]
//	scene dirty      [<pathOrName>]
func sceneCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf(
			"usage: unity-cli scene <list|open|close|save|reload|set-active|new|dirty> [args...]")
	}

	action := args[0]
	rest := args[1:]

	// Split positionals from flags, keeping value-flags paired with their
	// values. Boolean flags consume no next arg; everything else greedily
	// pairs with the following non-flag token. Mirrors how buildParams sees
	// flag/value pairs once we hand them off.
	var positionals []string
	var passthrough []string
	for i := 0; i < len(rest); i++ {
		a := rest[i]
		if strings.HasPrefix(a, "--") {
			passthrough = append(passthrough, a)
			if !isSceneBoolFlag(a) && i+1 < len(rest) && !strings.HasPrefix(rest[i+1], "--") {
				passthrough = append(passthrough, rest[i+1])
				i++
			}
			continue
		}
		positionals = append(positionals, a)
	}

	// Normalise common aliases. `--as` is more natural to type than `--asset`
	// but the connector parameter is named `asset`; `--json`/`--plain` map to
	// `--format <name>` so the tool sees a uniform output knob.
	out := make([]string, 0, len(passthrough))
	for i := 0; i < len(passthrough); i++ {
		a := passthrough[i]
		switch a {
		case "--as":
			out = append(out, "--asset")
		case "--json":
			out = append(out, "--format", "json")
		case "--plain":
			out = append(out, "--format", "plain")
		default:
			out = append(out, a)
		}
	}
	passthrough = out

	switch action {
	case "list":
		return sendScene(send, "list", "", passthrough)

	case "open":
		if len(positionals) < 1 {
			return nil, fmt.Errorf(
				"usage: unity-cli scene open <assetpath> [--mode single|additive|additive-without-loading]")
		}
		return sendScene(send, "open", positionals[0], passthrough)

	case "close":
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli scene close <pathOrName> [--save|--discard]")
		}
		return sendScene(send, "close", positionals[0], passthrough)

	case "save":
		path := ""
		if len(positionals) >= 1 {
			path = positionals[0]
		}
		return sendScene(send, "save", path, passthrough)

	case "reload":
		path := ""
		if len(positionals) >= 1 {
			path = positionals[0]
		}
		return sendScene(send, "reload", path, passthrough)

	case "set-active":
		if len(positionals) < 1 {
			return nil, fmt.Errorf("usage: unity-cli scene set-active <pathOrName>")
		}
		return sendScene(send, "set-active", positionals[0], passthrough)

	case "new":
		return sendScene(send, "new", "", passthrough)

	case "dirty":
		path := ""
		if len(positionals) >= 1 {
			path = positionals[0]
		}
		return sendScene(send, "dirty", path, passthrough)

	default:
		return nil, fmt.Errorf(
			"unknown scene action: %s\nAvailable: list, open, close, save, reload, set-active, new, dirty",
			action)
	}
}

// isSceneBoolFlag returns true for flags that don't take a value, so the
// parser knows not to greedily consume the next token (a positional) as a
// flag value.
func isSceneBoolFlag(flag string) bool {
	switch flag {
	case "--save", "--discard", "--json", "--plain":
		return true
	}
	return false
}

func sendScene(send sendFn, action, path string, passthrough []string) (*client.CommandResponse, error) {
	params, err := buildParams(passthrough, map[string]interface{}{
		"action": action,
	})
	if err != nil {
		return nil, err
	}
	if path != "" {
		params["path"] = path
	}
	// No bare positionals reach the C# tool — path is explicit.
	delete(params, "args")
	return send("scene", params)
}
