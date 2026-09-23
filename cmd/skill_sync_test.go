// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// See /LICENSE_GPL for the full license text.

package cmd

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/hoffmann-polycular/unity-cli/internal/skill"
)

// staleInstall returns a dir holding an older copy that unity-cli wrote and
// nobody has touched since.
func staleInstall(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	old := []byte("# skill from an older release\n")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(skillFilePath(dir), old, 0o644); err != nil {
		t.Fatal(err)
	}
	if err := writeSkillMarker(dir, "v0.1.0", skill.SumOf(old)); err != nil {
		t.Fatal(err)
	}
	return dir
}

func TestSyncSkill_RepairsOurOwnStaleCopy(t *testing.T) {
	dir := staleInstall(t)
	var out bytes.Buffer

	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncUpdated {
		t.Fatalf("got %v, want skillSyncUpdated", got)
	}
	data, _ := os.ReadFile(skillFilePath(dir))
	if skill.SumOf(data) != skill.Sum() {
		t.Error("the stale copy was not replaced with the embedded skill")
	}
	if !strings.Contains(out.String(), "updated to v9.9.9") {
		t.Errorf("the update should be mentioned, got: %q", out.String())
	}

	// Second run is a no-op and says nothing.
	out.Reset()
	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncNothing {
		t.Errorf("got %v, want skillSyncNothing on a current copy", got)
	}
	if out.Len() != 0 {
		t.Errorf("a current skill should produce no output, got: %q", out.String())
	}
}

func TestSyncSkill_NeverRewritesAModifiedCopy(t *testing.T) {
	dir := staleInstall(t)
	mine := []byte("# I edited this\n")
	if err := os.WriteFile(skillFilePath(dir), mine, 0o644); err != nil {
		t.Fatal(err)
	}

	var out bytes.Buffer
	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncNotified {
		t.Fatalf("got %v, want skillSyncNotified", got)
	}
	data, _ := os.ReadFile(skillFilePath(dir))
	if !bytes.Equal(data, mine) {
		t.Fatal("automatic sync overwrote a hand-edited skill")
	}
	if !strings.Contains(out.String(), "--force") {
		t.Errorf("the notice should say how to replace it, got: %q", out.String())
	}
}

func TestSyncSkill_NeverRewritesAnUnmanagedCopy(t *testing.T) {
	dir := t.TempDir()
	theirs := []byte("# curled from main long ago\n")
	if err := os.WriteFile(skillFilePath(dir), theirs, 0o644); err != nil {
		t.Fatal(err)
	}

	var out bytes.Buffer
	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncNotified {
		t.Fatalf("got %v, want skillSyncNotified", got)
	}
	if data, _ := os.ReadFile(skillFilePath(dir)); !bytes.Equal(data, theirs) {
		t.Fatal("automatic sync overwrote a copy of unknown provenance")
	}
}

func TestSyncSkill_LeavesASymlinkAlone(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(t.TempDir(), "SKILL.md")
	if err := os.WriteFile(target, []byte("# contributor working copy\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, skillFilePath(dir)); err != nil {
		t.Skipf("symlinks unavailable: %v", err)
	}

	var out bytes.Buffer
	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncNothing {
		t.Fatalf("got %v, want skillSyncNothing", got)
	}
	if data, _ := os.ReadFile(target); string(data) != "# contributor working copy\n" {
		t.Fatal("sync wrote through a symlink into someone's working copy")
	}
	if out.Len() != 0 {
		t.Errorf("a symlinked skill should be silent, got: %q", out.String())
	}
}

// Never install unprompted: --with-skill is opt-in, and a user who declined
// it should not find the file appearing later.
func TestSyncSkill_DoesNotInstallWhenAbsent(t *testing.T) {
	dir := t.TempDir()
	var out bytes.Buffer
	if got := syncSkill(dir, "v9.9.9", true, &out); got != skillSyncNothing {
		t.Fatalf("got %v, want skillSyncNothing", got)
	}
	if _, err := os.Stat(skillFilePath(dir)); !os.IsNotExist(err) {
		t.Error("sync installed a skill the user never asked for")
	}
}

func TestSyncSkill_NoticeIsRateLimited(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(skillFilePath(dir), []byte("# unmanaged\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	if got := syncSkill(dir, "v9.9.9", false, &out); got != skillSyncNothing {
		t.Fatalf("got %v, want skillSyncNothing when the notice is rate-limited", got)
	}
	if out.Len() != 0 {
		t.Errorf("expected silence within the rate-limit window, got: %q", out.String())
	}
}

func TestSkillNoticeCacheRoundTrip(t *testing.T) {
	path := filepath.Join(t.TempDir(), "nested", "skill-check.json")
	if got := loadSkillNotice(path); !got.IsZero() {
		t.Errorf("a missing cache should read as the zero time, got %v", got)
	}
	now := time.Now().Truncate(time.Second)
	saveSkillNotice(path, now)
	if got := loadSkillNotice(path); !got.Equal(now) {
		t.Errorf("got %v, want %v", got, now)
	}
}

func TestMaybeSyncSkill_DisabledByEnv(t *testing.T) {
	dir := staleInstall(t)
	t.Setenv(skillSyncDisabledEnv, "1")
	t.Setenv("CLAUDE_CONFIG_DIR", filepath.Dir(filepath.Dir(dir)))

	maybeSyncSkill()

	data, _ := os.ReadFile(skillFilePath(dir))
	if skill.SumOf(data) == skill.Sum() {
		t.Error("the opt-out was ignored")
	}
}
