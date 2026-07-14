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
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// getCmd reads one serialized-property value from a path.
//
// Stdin support:
//
//	find --component Light --plain | unity-cli get :Light.intensity
//	find --component Rigidbody --plain | unity-cli get :Rigidbody.mass
//
// When stdin is piped, each line is treated as a path. If the positional
// starts with ":" (a component/property suffix), it's appended to every
// piped path. Otherwise piped lines are used as-is.
func getCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Translate --json to --format json so the wire param matches what the
	// C# tool expects. Also translate the -P short flag to its --with-path
	// long form.
	rest := translateJSONFlag(args)
	for i, a := range rest {
		if a == "-P" {
			rest[i] = "--with-path"
		}
	}

	// Pull positional + flags. Use stdin paths to drive fan-out when piped.
	stdinPaths := readStdinPaths()
	if len(stdinPaths) > 0 {
		positional, flagArgs := splitFlagsAndPositionals(rest)
		suffix := ""
		if len(positional) > 0 && strings.HasPrefix(positional[0], ":") {
			suffix = positional[0]
		}
		fullPaths := make([]string, 0, len(stdinPaths))
		for _, p := range stdinPaths {
			fullPaths = append(fullPaths, p+suffix)
		}
		params, err := buildParams(flagArgs, nil)
		if err != nil {
			return nil, err
		}
		// Single path → use scalar shape; multi-path → use args[] array.
		if len(fullPaths) == 1 {
			params["path"] = fullPaths[0]
		} else {
			params["args"] = fullPaths
		}
		return send("get", params)
	}

	params, err := buildParams(rest, nil)
	if err != nil {
		return nil, err
	}
	return send("get", params)
}
