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
// Accepts three input forms for the value:
//
//	unity-cli set <path> <value>
//	unity-cli set <path> --value <value>
//	echo <value> | unity-cli set <path>           (piped stdin)
//
// The piped form is the round-trip target for `get | set`. We only pull
// stdin when no value was provided on the command line, so writing a
// literal flag value still works fine.
func setCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if needsStdinValue(args) {
		if v := readStdinValue(); v != "" {
			args = append(args, v)
		}
	}

	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
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
