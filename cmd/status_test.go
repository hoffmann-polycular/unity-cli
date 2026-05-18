// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func writeInstanceFile(t *testing.T, inst client.Instance) string {
	t.Helper()
	home := t.TempDir()
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create instances dir: %v", err)
	}
	data, err := json.Marshal(inst)
	if err != nil {
		t.Fatalf("failed to marshal instance: %v", err)
	}
	// Use a fixed filename for testing
	path := filepath.Join(dir, "test.json")
	if err := os.WriteFile(path, data, 0644); err != nil {
		t.Fatalf("failed to write instance file: %v", err)
	}
	return home
}

func TestReadStatus_ValidFile(t *testing.T) {
	want := client.Instance{
		State:        "ready",
		ProjectPath:  "/home/user/MyProject",
		Port:         8090,
		PID:          os.Getpid(),
		UnityVersion: "6000.3.10f1",
		Timestamp:    1000000,
	}

	home := writeInstanceFile(t, want)
	t.Setenv("HOME", home)

	got, err := readStatus(8090)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.State != want.State {
		t.Errorf("State: got %q, want %q", got.State, want.State)
	}
	if got.Port != want.Port {
		t.Errorf("Port: got %d, want %d", got.Port, want.Port)
	}
	if got.ProjectPath != want.ProjectPath {
		t.Errorf("ProjectPath: got %q, want %q", got.ProjectPath, want.ProjectPath)
	}
}

func TestReadStatus_MissingFile(t *testing.T) {
	t.Setenv("HOME", t.TempDir())
	_, err := readStatus(9999)
	if err == nil {
		t.Error("expected error for missing status file")
	}
}

func TestReadStatus_InvalidJSON(t *testing.T) {
	home := t.TempDir()
	dir := filepath.Join(home, ".unity-cli", "instances")
	if err := os.MkdirAll(dir, 0755); err != nil {
		t.Fatalf("failed to create dir: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "test.json"), []byte("not json"), 0644); err != nil {
		t.Fatalf("failed to write file: %v", err)
	}
	t.Setenv("HOME", home)

	_, err := readStatus(8090)
	if err == nil {
		t.Error("expected error for invalid JSON")
	}
}
