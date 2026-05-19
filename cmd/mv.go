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

// mvCmd reparents and/or renames a GameObject in one operation.
//
// Forms:
//
//	mv <src> <parent>/<name>     reparent + rename
//	mv <src> <parent>/           reparent, keep source name
//	mv <src> <parent>/<newname>  pure rename when parent == current parent
func mvCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	if len(args) < 2 {
		return nil, fmt.Errorf("usage: unity-cli mv <src> <dst>\n  <dst> is 'parent/name' or 'parent/' (keep source name)")
	}
	args = translateJSONFlag(args)
	params, err := buildParams(args, nil)
	if err != nil {
		return nil, err
	}
	return send("mv", params)
}
