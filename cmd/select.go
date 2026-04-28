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
	"io"
	"os"
	"strings"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// selectCmd bridges the Editor's Selection with the terminal.
//
// Forms:
//   select <path>           set selection
//   select --get            list selected paths
//   select --add <path>     add to selection
//   select --clear          deselect all
//   echo <path> | select    pipe path as selection (or with --add)
func selectCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Check for non-positional flags.
	hasGet := contains(args, "--get")
	hasClear := contains(args, "--clear")
	hasAdd := contains(args, "--add")

	// Pull positional path (only one expected).
	var positionalPath string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--get" || a == "--clear" {
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
	// try to read from stdin (for piping from `find` etc).
	if positionalPath == "" && !hasGet && !hasClear {
		if stdinPath := readStdinPath(); stdinPath != "" {
			positionalPath = stdinPath
		}
	}

	params := map[string]interface{}{
		"get":   hasGet,
		"clear": hasClear,
		"add":   hasAdd,
	}
	if positionalPath != "" {
		params["path"] = positionalPath
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

func readStdinPath() string {
	info, err := os.Stdin.Stat()
	if err != nil {
		return ""
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return ""
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil || len(data) == 0 {
		return ""
	}
	return strings.TrimRight(string(data), "\r\n")
}
