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

// findCmd is a thin pass-through for the unified `find` C# tool.
//
// The C# side decides scene vs asset mode by inspecting the first positional
// argument (an "Assets/" or "Packages/" prefix triggers asset search).
// Go-side responsibilities are limited to:
//   - collecting repeated --component / --missing flags into arrays
//   - translating the output flags (--json / --plain / --null-delimited)
//     into the C# tool's `format` parameter
//
// Examples:
//
//	find --name "Enemy*"               (scene)
//	find --component Rigidbody         (scene)
//	find Assets/                       (asset)
//	find Assets/Prefabs/ --type Prefab (asset)
func findCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	var components []string
	var missing []string
	rest := make([]string, 0, len(args))

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--component":
			if i+1 < len(args) {
				components = append(components, args[i+1])
				i++
			}
		case "--missing":
			if i+1 < len(args) {
				missing = append(missing, args[i+1])
				i++
			}
		case "--json":
			rest = append(rest, "--format", "json")
		case "--plain":
			rest = append(rest, "--format", "plain")
		case "--null-delimited", "--null":
			rest = append(rest, "--format", "null")
		default:
			rest = append(rest, args[i])
		}
	}

	params, err := buildParams(rest, nil)
	if err != nil {
		return nil, err
	}

	if len(components) > 0 {
		params["component"] = components
	}
	if len(missing) > 0 {
		params["missing"] = missing
	}

	return send("find", params)
}
