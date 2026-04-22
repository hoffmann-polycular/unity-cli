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

// getCmd reads one serialized-property value from a path.
//
// A thin pass-through: translates `--json` to `--format json` so the wire
// param matches what the C# tool expects, then forwards everything else.
func getCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	rest := make([]string, 0, len(args))
	for _, a := range args {
		switch a {
		case "--json":
			rest = append(rest, "--format", "json")
		default:
			rest = append(rest, a)
		}
	}

	params, err := buildParams(rest, nil)
	if err != nil {
		return nil, err
	}
	return send("get", params)
}
