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

// deleteCmd destroys GameObjects by path, with support for single, ambiguous
// (--all), and batch (stdin) deletion.
//
// Forms:
//   delete <path>               delete a single object (error on ambiguity)
//   delete <path> --all         delete all matches when ambiguous
//   find ... --plain | delete   batch mode: delete each path from stdin
func deleteCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Check for --all flag.
	var hasAll bool
	var filteredArgs []string
	for _, a := range args {
		if a == "--all" {
			hasAll = true
		} else {
			filteredArgs = append(filteredArgs, a)
		}
	}

	// Pull positional path if given.
	var positionalPath string
	if len(filteredArgs) > 0 {
		positionalPath = filteredArgs[0]
	}

	// If no positional and stdin is piped, read all lines as batch paths.
	var batchPaths []string
	if positionalPath == "" {
		batchPaths = readStdinPaths()
	}

	params := map[string]interface{}{
		"all": hasAll,
	}
	if positionalPath != "" {
		params["path"] = positionalPath
	}
	if len(batchPaths) > 0 {
		params["args"] = batchPaths
	}

	return send("delete", params)
}

func readStdinPaths() []string {
	info, err := os.Stdin.Stat()
	if err != nil {
		return nil
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return nil
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil || len(data) == 0 {
		return nil
	}
	text := strings.TrimRight(string(data), "\r\n")
	var paths []string
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if line != "" {
			paths = append(paths, line)
		}
	}
	return paths
}
