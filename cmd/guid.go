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
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// guidCmd converts asset paths to GUIDs.
//
//	unity-cli guid <assetpath>...
//	find Assets/Prefabs/ --plain | unity-cli guid
//
// One GUID per input line in input order. Unresolvable inputs emit an
// empty line on stdout and a reason on stderr; exit code is non-zero
// when any input failed.
func guidCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	return runGuidTool(args, send, "to-guid")
}

// pathCmd converts GUIDs back to asset paths. Inverse of `guid`.
//
//	unity-cli path <guid>...
//	unity-cli guid Assets/Foo.png | unity-cli path
//
// Useful when reading scene files / meta files as text and needing to
// turn the GUID references back into something a human (or another
// unity-cli command) can use.
func pathCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	return runGuidTool(args, send, "to-path")
}

func runGuidTool(args []string, send sendFn, direction string) (*client.CommandResponse, error) {
	// Peel --json out (buildParams has no boolean-flag awareness — see
	// cmd/set.go for the longer version of this story).
	jsonFormat := false
	filtered := make([]string, 0, len(args))
	for _, a := range args {
		if a == "--json" {
			jsonFormat = true
			continue
		}
		filtered = append(filtered, a)
	}
	args = filtered

	// Collect inputs: positionals first, then stdin if no positionals.
	positionals := make([]string, 0, len(args))
	for _, a := range args {
		if len(a) > 1 && a[0] == '-' {
			continue // skip unknown flags rather than send them
		}
		positionals = append(positionals, a)
	}
	if len(positionals) == 0 {
		stdinPaths := readStdinPaths()
		positionals = append(positionals, stdinPaths...)
	}

	params := map[string]interface{}{
		"direction": direction,
	}
	if len(positionals) > 0 {
		params["args"] = positionals
	}
	if jsonFormat {
		params["format"] = "json"
	}
	return send("guid", params)
}
