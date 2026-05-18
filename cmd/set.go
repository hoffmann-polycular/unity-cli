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

// setCmd writes a single serialized-property value.
//
// Input forms:
//
//	unity-cli set <path> <value>                          # explicit
//	unity-cli set <path> --value <value>                  # via flag
//	echo <value> | unity-cli set <path>                   # value from stdin
//	find ... --plain | unity-cli set <:suffix> <value>    # multi-path broadcast
//
// In multi-path broadcast mode (positional 0 starts with ":" and a value
// positional is present), each stdin line is treated as a path; the suffix
// is appended and the value is broadcast to every resulting target. All
// writes share one Undo group.
func setCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Peel known boolean flags out of args before any other processing.
	// buildParams has no boolean-flag awareness — if it sees `--json`
	// followed by a positional that doesn't start with `--`, it consumes
	// the positional as the flag's value. Strip the boolean here and
	// re-inject as named params after buildParams runs.
	jsonFormat := false
	filtered := make([]string, 0, len(args))
	for _, a := range args {
		switch a {
		case "--json":
			jsonFormat = true
		default:
			filtered = append(filtered, a)
		}
	}
	args = filtered

	positional, flagArgs := splitPositionalFromFlags(args)

	// Multi-path broadcast: ":<suffix> <value>" + stdin paths.
	if len(positional) >= 2 && strings.HasPrefix(positional[0], ":") {
		stdinPaths := readStdinPaths()
		if len(stdinPaths) > 0 {
			suffix := positional[0]
			value := positional[1]

			// Build args = [path1+suffix, path2+suffix, ..., value].
			combined := make([]string, 0, len(stdinPaths)+1)
			for _, p := range stdinPaths {
				combined = append(combined, p+suffix)
			}
			combined = append(combined, value)

			params, err := buildParams(append(flagArgs, combined...), nil)
			if err != nil {
				return nil, err
			}
			if jsonFormat {
				params["format"] = "json"
			}
			return send("set", params)
		}
	}

	// Single-target: value may come from stdin when only the path is given.
	if needsStdinValue(args) {
		if v := readStdinValue(); v != "" {
			args = append(args, v)
		}
	}

	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	if jsonFormat {
		params["format"] = "json"
	}
	return send("set", params)
}

// needsStdinValue reports whether the user supplied a value already.
// True when there's exactly one positional (the path) and no --value flag.
func needsStdinValue(args []string) bool {
	positionals := 0
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--value" {
			return false
		}
		if strings.HasPrefix(a, "--") {
			// Skip its argument unless the next token is also a flag (i.e. it's a bool flag).
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				i++
			}
			continue
		}
		positionals++
	}
	return positionals == 1
}

// readStdinValue returns trimmed stdin content if input is piped.
// Empty string for an interactive terminal (no blocking).
func readStdinValue() string {
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
