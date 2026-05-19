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

// screenshotCmd captures a Unity scene/game view to disk.
//
// CLI surface uses kebab-case (`--output-path`, `-o`) consistent with the
// rest of the tools; the connector JSON key stays `output_path` (internal
// protocol, unchanged).
func screenshotCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	normalized := make([]string, 0, len(args))
	for i := 0; i < len(args); i++ {
		a := args[i]
		switch a {
		case "--output-path", "-o":
			// User-facing kebab → wire snake.
			normalized = append(normalized, "--output_path")
		default:
			normalized = append(normalized, a)
		}
	}

	params, err := buildParams(normalized, nil)
	if err != nil {
		return nil, err
	}
	return send("screenshot", params)
}
