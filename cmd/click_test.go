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

func firstArg(t *testing.T, params *map[string]interface{}) string {
	t.Helper()
	args, ok := (*params)["args"].([]string)
	if !ok || len(args) == 0 {
		t.Fatalf("expected args positional, got %v", (*params)["args"])
	}
	return args[0]
}

func TestClickCmd_ElementPath(t *testing.T) {
	send, params := mockSend("click", t)
	if _, err := clickCmd([]string{"/World/UI/Button"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := firstArg(t, params); got != "/World/UI/Button" {
		t.Errorf("expected location=/World/UI/Button, got %q", got)
	}
}

func TestClickCmd_Coordinate(t *testing.T) {
	send, params := mockSend("click", t)
	// A bare "X,Y" is one shell token and must survive as a single positional.
	if _, err := clickCmd([]string{"512,300"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got := firstArg(t, params); got != "512,300" {
		t.Errorf("expected location=512,300, got %q", got)
	}
}

func TestClickCmd_ButtonFlag(t *testing.T) {
	send, params := mockSend("click", t)
	if _, err := clickCmd([]string{"/World/UI/Button", "--button", "right"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["button"] != "right" {
		t.Errorf("expected button=right, got %v", (*params)["button"])
	}
	// The location positional must not be swallowed by the value flag.
	if got := firstArg(t, params); got != "/World/UI/Button" {
		t.Errorf("expected location preserved, got %q", got)
	}
}

func TestClickCmd_NormalizedAndFlipAreBooleans(t *testing.T) {
	send, params := mockSend("click", t)
	// --normalized / --flip are boolean: they must NOT consume the coordinate.
	if _, err := clickCmd([]string{"--normalized", "--flip", "0.5,0.5"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["normalized"] != true {
		t.Errorf("expected normalized=true, got %v", (*params)["normalized"])
	}
	if (*params)["flip"] != true {
		t.Errorf("expected flip=true, got %v", (*params)["flip"])
	}
	if got := firstArg(t, params); got != "0.5,0.5" {
		t.Errorf("expected coordinate positional preserved, got %q", got)
	}
}

func TestClickCmd_JSONTranslatesToFormat(t *testing.T) {
	send, params := mockSend("click", t)
	if _, err := clickCmd([]string{"512,300", "--json"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["format"] != "json" {
		t.Errorf("expected format=json, got %v", (*params)["format"])
	}
}
