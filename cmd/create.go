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

// createCmd spawns GameObjects (empty/primitive) or prefab instances.
//
// Forms:
//
//	create Empty <parentpath>/<name>
//	create <primitive> <parentpath>/<name>
//	create --prefab <assetpath> <parentpath>/<name>
//
// Type resolution (Empty, Cube, Sphere, Capsule, Cylinder, Plane, Quad)
// is handled by the C# side to avoid duplication.
func createCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli create <type|--prefab> <parentpath>/<name>")
	}

	// Parse: [type, path] or [--prefab, assetpath, path]
	var typeArg, pathArg, prefabArg string

	if args[0] == "--prefab" {
		if len(args) < 3 {
			return nil, fmt.Errorf("usage: unity-cli create --prefab <assetpath> <parentpath>/<name>")
		}
		prefabArg = args[1]
		pathArg = args[2]
	} else {
		if len(args) == 1 {

			pathArg = args[0]
		} else {
			typeArg = args[0]
			pathArg = args[1]
		}
	}

	params := map[string]interface{}{
		"path": pathArg,
	}
	if typeArg != "" {
		params["type"] = typeArg
	}
	if prefabArg != "" {
		params["prefab"] = prefabArg
	}

	return send("create", params)
}
