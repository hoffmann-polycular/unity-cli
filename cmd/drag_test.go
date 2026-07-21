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

import "testing"

func TestDragCmd_TwoLocations(t *testing.T) {
	send, params := mockSend("drag", t)
	if _, err := dragCmd([]string{"/Inv/Item", "/Inv/Slot3"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	args, ok := (*params)["args"].([]string)
	if !ok || len(args) != 2 {
		t.Fatalf("expected two positional locations, got %v", (*params)["args"])
	}
	if args[0] != "/Inv/Item" || args[1] != "/Inv/Slot3" {
		t.Errorf("expected from/to preserved in order, got %v", args)
	}
}

func TestDragCmd_MixedPathAndCoord(t *testing.T) {
	send, params := mockSend("drag", t)
	if _, err := dragCmd([]string{"/Inv/Item", "700,400"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	args := (*params)["args"].([]string)
	if len(args) != 2 || args[0] != "/Inv/Item" || args[1] != "700,400" {
		t.Errorf("expected mixed endpoints preserved, got %v", args)
	}
}

func TestDragCmd_StepsFlag(t *testing.T) {
	send, params := mockSend("drag", t)
	if _, err := dragCmd([]string{"512,300", "700,400", "--steps", "16"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	// buildParams coerces numeric flag values to int.
	if (*params)["steps"] != 16 {
		t.Errorf("expected steps=16 (int), got %v (%T)", (*params)["steps"], (*params)["steps"])
	}
	args := (*params)["args"].([]string)
	if len(args) != 2 {
		t.Errorf("expected both coordinate positionals preserved, got %v", args)
	}
}
