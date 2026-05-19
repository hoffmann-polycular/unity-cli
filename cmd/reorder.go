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

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// reorderCmd reorders a GameObject among its siblings, or a Component on
// its GameObject. Mode is chosen by whether the path has a ":Component"
// suffix.
//
// Forms:
//
//	reorder <path> --index N         absolute 0-based position
//	reorder <path> --first|--last    move to extremes
//	reorder <path> --up [N]          move up N (default 1)
//	reorder <path> --down [N]        move down N (default 1)
//	reorder <path> --before <name>   insert before named sibling/component
//	reorder <path> --after <name>    insert after named sibling/component
func reorderCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) < 1 {
		return nil, fmt.Errorf("usage: unity-cli reorder <path> <--index N | --first | --last | --up [N] | --down [N] | --before <name> | --after <name>>")
	}
	args = translateJSONFlag(args)
	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	return send("reorder", params)
}
