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

// lsCmd lists scene hierarchy.
//
// Translates short flags and output-format flags into the named params the
// C# `ls` tool understands. Everything else passes through buildParams.
func lsCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	normalized := make([]string, 0, len(args))
	for _, a := range args {
		switch a {
		case "-R", "--recursive":
			normalized = append(normalized, "--recursive")
		case "-c", "--components":
			normalized = append(normalized, "--components")
		case "--json":
			normalized = append(normalized, "--format", "json")
		case "--plain":
			normalized = append(normalized, "--format", "plain")
		case "--null-delimited", "--null":
			normalized = append(normalized, "--format", "null")
		default:
			normalized = append(normalized, a)
		}
	}

	params, err := buildParams(normalized, nil)
	if err != nil {
		return nil, err
	}
	return send("ls", params)
}
