// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// See /LICENSE_GPL for the full license text.

package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

func okResponse(t *testing.T, data interface{}) *client.CommandResponse {
	t.Helper()
	resp := &client.CommandResponse{Success: true, Message: "done"}
	if data != nil {
		raw, err := json.Marshal(data)
		if err != nil {
			t.Fatalf("marshal test data: %v", err)
		}
		resp.Data = raw
	}
	return resp
}

func TestCallerOutputPath(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want string
	}{
		{"short flag", []string{"-o", "captures/shot.png"}, "captures/shot.png"},
		{"long flag", []string{"--output-path", "/tmp/shot.png"}, "/tmp/shot.png"},
		{"custom tool file flag", []string{"loc", "export", "--file", "out.csv"}, "out.csv"},
		{"underscore spelling", []string{"--output_file", "out.csv"}, "out.csv"},
		{"first flag wins", []string{"--out", "a.csv", "--file", "b.csv"}, "a.csv"},
		{"no output flag", []string{"/World/Player:Rigidbody.mass", "25"}, ""},
		// A format value is not a path, and a hierarchy path is not a file.
		{"non-path value", []string{"--output", "json"}, ""},
		{"hierarchy path value", []string{"--file", "/World/Player"}, ""},
		{"valueless flag", []string{"--file", "--json"}, ""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := callerOutputPath(tc.args); got != tc.want {
				t.Errorf("callerOutputPath(%v) = %q, want %q", tc.args, got, tc.want)
			}
		})
	}
}

func TestReportedOutputPath(t *testing.T) {
	resp := okResponse(t, map[string]interface{}{"path": "/abs/shot.png", "view": "game"})
	if got := reportedOutputPath(resp); got != "/abs/shot.png" {
		t.Errorf("got %q, want /abs/shot.png", got)
	}

	// A hierarchy path under `path` (what create/mv return) is not a file.
	resp = okResponse(t, map[string]interface{}{"path": "/World/Terrain/Rock"})
	if got := reportedOutputPath(resp); got != "" {
		t.Errorf("got %q, want empty for a hierarchy path", got)
	}

	// Plain-string and list payloads carry no keys to read.
	resp = okResponse(t, []string{"/World/A", "/World/B"})
	if got := reportedOutputPath(resp); got != "" {
		t.Errorf("got %q, want empty for a list payload", got)
	}
}

func TestVerifyOutputFile_SilentWhenFileExists(t *testing.T) {
	project := t.TempDir()
	if err := os.WriteFile(filepath.Join(project, "out.csv"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	resp := okResponse(t, map[string]interface{}{"path": filepath.Join(project, "out.csv")})
	if w := verifyOutputFile([]string{"--file", "out.csv"}, resp, project); w != "" {
		t.Errorf("expected no warning, got: %s", w)
	}
}

func TestVerifyOutputFile_WarnsWhenFileMissing(t *testing.T) {
	project := t.TempDir()
	missing := filepath.Join(t.TempDir(), "elsewhere", "out.csv")
	resp := okResponse(t, map[string]interface{}{"path": missing})

	w := verifyOutputFile([]string{"--file", missing}, resp, project)
	if w == "" {
		t.Fatal("expected a warning for a file the caller cannot see")
	}
	if !strings.Contains(w, missing) {
		t.Errorf("warning should name the path %q, got: %s", missing, w)
	}
	if !strings.Contains(w, project) {
		t.Errorf("warning should point at the project root %q, got: %s", project, w)
	}
}

func TestVerifyOutputFile_RelativePathResolvedAgainstProjectRoot(t *testing.T) {
	project := t.TempDir()
	if err := os.MkdirAll(filepath.Join(project, "Screenshots"), 0o755); err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(project, "Screenshots", "shot.png")
	if err := os.WriteFile(target, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	// The connector resolves relative output paths against the project root,
	// not the caller's cwd, and the response carries no path key.
	if w := verifyOutputFile([]string{"-o", "Screenshots/shot.png"}, okResponse(t, nil), project); w != "" {
		t.Errorf("expected no warning, got: %s", w)
	}
}

func TestVerifyOutputFile_IgnoresCommandsWithoutOutputFlag(t *testing.T) {
	// `create` reports a hierarchy path that will never exist on disk.
	resp := okResponse(t, map[string]interface{}{"path": "/World/Terrain/Rock.001"})
	if w := verifyOutputFile([]string{"Cube", "/World/Terrain/Rock.001"}, resp, t.TempDir()); w != "" {
		t.Errorf("expected no warning without an output flag, got: %s", w)
	}
}

func TestVerifyOutputFile_IgnoresFailedResponse(t *testing.T) {
	resp := &client.CommandResponse{Success: false, Message: "boom"}
	if w := verifyOutputFile([]string{"--file", "/nope/out.csv"}, resp, t.TempDir()); w != "" {
		t.Errorf("expected no warning for a failed command, got: %s", w)
	}
}
