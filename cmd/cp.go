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

// cpCmd copies a GameObject to a new location in the hierarchy. Supports
// --depth N (descendant layers, 0 = no children) and --auto-suffix [format]
// for collision handling.
//
// Forms:
//
//	cp <src> <parent>/<name>           copy under <parent>, named <name>
//	cp <src> <parent>/                 copy under <parent>, keep source name
//	cp <src> <dst> --depth 0           shallow copy: object only, no children
//	cp <src> <dst> --auto-suffix       on collision, append " (1)", " (2)", …
//	cp <src> <dst> --auto-suffix _{n}  custom suffix format
func cpCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) < 2 {
		return nil, fmt.Errorf("usage: unity-cli cp <src> <dst> [--depth N] [--auto-suffix [format]]\n  <dst> is 'parent/name' or 'parent/' (keep source name)")
	}
	args = translateJSONFlag(args)
	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	return send("cp", params)
}
