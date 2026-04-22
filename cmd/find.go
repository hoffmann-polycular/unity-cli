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
	"github.com/youngwoocho02/unity-cli/internal/client"
)

// findCmd searches for GameObjects across loaded scenes.
//
// Handles repeated `--component` / `--missing` flags (collected into arrays)
// and translates the output-format flags into the C# tool's `format` param.
// Every other flag passes through the generic flag parser.
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
		case "--has-overrides":
			rest = append(rest, "--has_overrides")
		default:
			rest = append(rest, args[i])
		}
	}

	params, err := buildParams(rest, nil)
	if err != nil {
		return nil, err
	}
	// Strip spurious positional args (find takes none).
	delete(params, "args")

	if len(components) > 0 {
		params["component"] = components
	}
	if len(missing) > 0 {
		params["missing"] = missing
	}

	return send("find", params)
}
