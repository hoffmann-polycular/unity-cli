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
	"os"
	"testing"
	"time"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

func TestSplitFlagsAndPositionals(t *testing.T) {
	tests := []struct {
		name          string
		args          []string
		wantPositions []string
		wantFlags     []string
	}{
		{"empty", nil, nil, nil},
		{"positionals only", []string{"/World/A", "/World/B"}, []string{"/World/A", "/World/B"}, nil},
		{"value flag pairs", []string{"--mode", "EditMode"}, nil, []string{"--mode", "EditMode"}},
		{
			"boolean flag does not swallow following positional",
			[]string{"--has-overrides", "/"},
			[]string{"/"},
			[]string{"--has-overrides"},
		},
		{
			"unregistered flag greedily pairs (best-effort)",
			[]string{"--filter", "MyTest"},
			nil,
			[]string{"--filter", "MyTest"},
		},
		{
			"boolean flag before value flag",
			[]string{"--wait", "--mode", "PlayMode"},
			nil,
			[]string{"--wait", "--mode", "PlayMode"},
		},
		{
			"positional then boolean flag",
			[]string{"/World/Player", "--null-delimited"},
			[]string{"/World/Player"},
			[]string{"--null-delimited"},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			pos, flags := splitFlagsAndPositionals(tt.args)
			if !sliceEqual(pos, tt.wantPositions) {
				t.Errorf("positionals = %v, want %v", pos, tt.wantPositions)
			}
			if !sliceEqual(flags, tt.wantFlags) {
				t.Errorf("flags = %v, want %v", flags, tt.wantFlags)
			}
		})
	}
}

// TestParseSubFlags_BooleanAware locks in the fix for the class of bug where a
// boolean flag followed by a positional swallowed the positional as its value.
func TestParseSubFlags_BooleanAware(t *testing.T) {
	// `test --allow-dirty-scenes SomeFilter`: allow-dirty-scenes is boolean,
	// so it must resolve to "true" and NOT consume "SomeFilter".
	got := parseSubFlags([]string{"--allow-dirty-scenes", "SomeFilter"})
	if got["allow-dirty-scenes"] != "true" {
		t.Errorf("allow-dirty-scenes = %q, want \"true\"", got["allow-dirty-scenes"])
	}
	if _, present := got["SomeFilter"]; present {
		t.Errorf("positional SomeFilter leaked into flags: %v", got)
	}

	// `editor refresh --force`: force is boolean.
	got = parseSubFlags([]string{"--force"})
	if got["force"] != "true" {
		t.Errorf("force = %q, want \"true\"", got["force"])
	}
}

// TestExplicitTargetIgnoresOpenStdin: scripts and agent shells hand the CLI a
// non-terminal stdin that never reaches EOF. A command given an explicit
// target path must not wait on it.
func TestExplicitTargetIgnoresOpenStdin(t *testing.T) {
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = w.Close(); _ = r.Close() }()
	orig := os.Stdin
	os.Stdin = r
	defer func() { os.Stdin = orig }()

	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true}, nil
	}
	cases := []struct {
		name string
		run  func() error
	}{
		{"get", func() error { _, err := getCmd([]string{"/A:Transform.localPosition"}, send); return err }},
		{"inspect", func() error { _, err := inspectCmd([]string{"/A"}, send); return err }},
		{"invoke", func() error { _, err := invokeCmd([]string{"/A:Foo.Bar"}, send); return err }},
		{"component list", func() error { _, err := componentCmd([]string{"list", "/A"}, send); return err }},
		{"component add", func() error { _, err := componentCmd([]string{"add", "/A", "Light"}, send); return err }},
		{"prefab diff", func() error { _, err := prefabCmd([]string{"diff", "/A"}, send); return err }},
		{"exec", func() error { _, err := dispatchOnline("exec", []string{"return 1;"}, send, nil); return err }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			done := make(chan error, 1)
			go func() { done <- tc.run() }()
			select {
			case err := <-done:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(2 * time.Second):
				t.Fatal("blocked reading stdin despite an explicit target")
			}
		})
	}
}
