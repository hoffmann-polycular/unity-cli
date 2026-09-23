// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// See /LICENSE_GPL for the full license text.

package cmd

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/hoffmann-polycular/unity-cli/internal/skill"
)

// installedDir returns a temp dir holding a freshly installed skill.
func installedDir(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	if _, err := installSkill(dir, "v1.2.3", false); err != nil {
		t.Fatalf("install: %v", err)
	}
	return dir
}

func TestClaudeConfigDir_HonorsEnv(t *testing.T) {
	t.Setenv("CLAUDE_CONFIG_DIR", "/custom/config")
	dir, err := defaultSkillDir()
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join("/custom/config", "skills", "unity-cli")
	if dir != want {
		t.Errorf("got %q, want %q", dir, want)
	}

	// Unset falls back to ~/.claude, matching install.sh.
	t.Setenv("CLAUDE_CONFIG_DIR", "")
	dir, err = defaultSkillDir()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasSuffix(dir, filepath.Join(".claude", "skills", "unity-cli")) {
		t.Errorf("fallback should end in .claude/skills/unity-cli, got %q", dir)
	}
}

func TestInspectSkill_Missing(t *testing.T) {
	if got := inspectSkill(t.TempDir()); got != skillMissing {
		t.Errorf("got %v, want skillMissing", got)
	}
}

func TestInstallSkill_WritesContentAndMarker(t *testing.T) {
	dir := t.TempDir()
	action, err := installSkill(dir, "v1.2.3", false)
	if err != nil {
		t.Fatal(err)
	}
	if action != "installed" {
		t.Errorf("action = %q, want installed", action)
	}

	got, err := os.ReadFile(skillFilePath(dir))
	if err != nil {
		t.Fatal(err)
	}
	if skill.SumOf(got) != skill.Sum() {
		t.Error("installed content does not match the embedded skill")
	}
	m := readSkillMarker(dir)
	if m == nil || m.SHA256 != skill.Sum() || m.Version != "v1.2.3" {
		t.Errorf("marker not recorded correctly: %+v", m)
	}
	if inspectSkill(dir) != skillCurrent {
		t.Error("a freshly installed skill should read as current")
	}
}

func TestInstallSkill_Idempotent(t *testing.T) {
	dir := installedDir(t)
	action, err := installSkill(dir, "v1.2.3", false)
	if err != nil {
		t.Fatal(err)
	}
	if action != "unchanged" {
		t.Errorf("action = %q, want unchanged", action)
	}
}

// An untouched copy from an older version must be replaceable without a
// prompt — that is the whole point of the marker.
func TestInspectSkill_StaleWhenUnmodified(t *testing.T) {
	dir := installedDir(t)
	old := []byte("# an older skill\n")
	if err := os.WriteFile(skillFilePath(dir), old, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := writeSkillMarker(dir, "v1.0.0", skill.SumOf(old)); err != nil {
		t.Fatal(err)
	}

	if got := inspectSkill(dir); got != skillStale {
		t.Fatalf("got %v, want skillStale", got)
	}
	action, err := installSkill(dir, "v1.2.3", false)
	if err != nil {
		t.Fatalf("stale copy should update without --force: %v", err)
	}
	if action != "updated" {
		t.Errorf("action = %q, want updated", action)
	}
}

func TestInspectSkill_ModifiedIsNeverReplacedSilently(t *testing.T) {
	dir := installedDir(t)
	if err := os.WriteFile(skillFilePath(dir), []byte("# my own notes\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if got := inspectSkill(dir); got != skillModified {
		t.Fatalf("got %v, want skillModified", got)
	}

	if _, err := installSkill(dir, "v1.2.3", false); err == nil {
		t.Fatal("expected install to refuse a modified copy without --force")
	}
	// The user's file must survive the refusal.
	data, _ := os.ReadFile(skillFilePath(dir))
	if string(data) != "# my own notes\n" {
		t.Error("a refused install must leave the file untouched")
	}

	if _, err := installSkill(dir, "v1.2.3", true); err != nil {
		t.Fatalf("--force should replace it: %v", err)
	}
	bak, err := os.ReadFile(skillFilePath(dir) + ".bak")
	if err != nil || string(bak) != "# my own notes\n" {
		t.Error("--force must keep the replaced content as .bak")
	}
}

// A copy from the old curl installer has no marker, so we cannot prove it is
// ours: explicit install replaces it (with a backup), automatic sync does not.
func TestInspectSkill_UnmanagedWithoutMarker(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(skillFilePath(dir), []byte("# curled from main\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if got := inspectSkill(dir); got != skillUnmanaged {
		t.Fatalf("got %v, want skillUnmanaged", got)
	}

	if _, err := installSkill(dir, "v1.2.3", false); err != nil {
		t.Fatalf("explicit install should replace an unmanaged copy: %v", err)
	}
	if bak, err := os.ReadFile(skillFilePath(dir) + ".bak"); err != nil || !strings.Contains(string(bak), "curled") {
		t.Error("an unmanaged copy must be backed up before replacement")
	}
}

func TestInstallSkill_NeverWritesThroughASymlink(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(t.TempDir(), "SKILL.md")
	if err := os.WriteFile(target, []byte("# working copy\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, skillFilePath(dir)); err != nil {
		t.Skipf("symlinks unavailable: %v", err)
	}

	if got := inspectSkill(dir); got != skillSymlink {
		t.Fatalf("got %v, want skillSymlink", got)
	}
	if _, err := installSkill(dir, "v1.2.3", false); err == nil {
		t.Fatal("expected install to refuse to write through a symlink")
	}
	data, _ := os.ReadFile(target)
	if string(data) != "# working copy\n" {
		t.Fatal("the symlink target was modified — a contributor's working copy would be clobbered")
	}

	// --force replaces the link itself, still leaving the target alone.
	if _, err := installSkill(dir, "v1.2.3", true); err != nil {
		t.Fatalf("--force should replace the link: %v", err)
	}
	if data, _ = os.ReadFile(target); string(data) != "# working copy\n" {
		t.Error("--force must replace the link, not write through it")
	}
	if inspectSkill(dir) != skillCurrent {
		t.Error("after --force the directory should hold a real, current file")
	}
}
