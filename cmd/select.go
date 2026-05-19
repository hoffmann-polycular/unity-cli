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

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// selectCmd bridges the Editor's Selection with the terminal.
//
// Forms:
//
//	select <path>           set selection
//	select --get            list selected paths
//	select --add <path>     add to selection
//	select --clear          deselect all
//	echo <path> | select    pipe path as selection (or with --add)
func selectCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Check for non-positional flags.
	hasGet := contains(args, "--get")
	hasClear := contains(args, "--clear")
	hasAdd := contains(args, "--add")
	// Output flag for --get: --null-delimited / --null wraps the output
	// in NUL separators (asset paths with spaces survive xargs -0 etc).
	hasNull := contains(args, "--null-delimited") || contains(args, "--null")

	// Pull positional path (only one expected).
	var positionalPath string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--get" || a == "--clear" || a == "--null-delimited" || a == "--null" {
			continue
		}
		if a == "--add" {
			// --add takes the next arg as the path
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				positionalPath = args[i+1]
				i++
			}
			continue
		}
		if !strings.HasPrefix(a, "--") {
			positionalPath = a
		}
	}

	// If no positional path and we're not in --get or --clear mode,
	// try to read from stdin (for piping from `ls --plain` etc).
	// Stdin may contain multiple newline-separated paths (one per line),
	// which we send as an args array so the connector resolves each one.
	var stdinPaths []string
	if positionalPath == "" && !hasGet && !hasClear {
		stdinPaths = readStdinPaths()
	}

	params := map[string]interface{}{
		"get":   hasGet,
		"clear": hasClear,
		"add":   hasAdd,
	}
	if hasNull {
		params["format"] = "null"
	}
	switch {
	case positionalPath != "":
		params["path"] = positionalPath
	case len(stdinPaths) == 1:
		params["path"] = stdinPaths[0]
	case len(stdinPaths) > 1:
		params["args"] = stdinPaths
	}

	return send("select", params)
}

func contains(args []string, flag string) bool {
	for _, a := range args {
		if a == flag {
			return true
		}
	}
	return false
}

