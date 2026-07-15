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

// invokeCmd calls a method on a component by reflection.
//
//	unity-cli invoke /World/Safe:KnobCombination.Solve
//	unity-cli invoke :InvokeTest.Add 2 3
//
// Stdin support (fan-out):
//
//	find --component Enemy --plain | unity-cli invoke :Enemy.Reset
//
// The first positional is the `path:Component.Method`; any remaining
// positionals are the method arguments (coerced to the parameter types on the
// connector side). When stdin is piped, each line is a target GameObject and
// the positional supplies only the `:Component.Method` — targets go in a
// separate `paths` param so they don't collide with the method arguments.
func invokeCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	rest := translateJSONFlag(args)

	params, err := buildParams(rest, nil)
	if err != nil {
		return nil, err
	}

	if stdinPaths := readStdinPaths(); len(stdinPaths) > 0 {
		params["paths"] = stdinPaths
	}

	return send("invoke", params)
}
