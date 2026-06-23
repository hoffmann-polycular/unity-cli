// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func captureStderr(t *testing.T, fn func()) string {
	t.Helper()
	orig := os.Stderr
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatalf("pipe: %v", err)
	}
	os.Stderr = w
	t.Cleanup(func() { os.Stderr = orig })

	fn()

	_ = w.Close()
	data, err := io.ReadAll(r)
	if err != nil {
		t.Fatalf("read stderr: %v", err)
	}
	return string(data)
}

func prepareVersionCheckEnv(t *testing.T, version string) string {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)

	origVersion := Version
	Version = version
	t.Cleanup(func() { Version = origVersion })

	origFetch := fetchLatestReleaseFn
	t.Cleanup(func() { fetchLatestReleaseFn = origFetch })

	// Under `go test` stdout is a pipe, not a terminal. Force the notice's
	// terminal gate on so the notice logic is exercised; the suppression path
	// has its own dedicated test.
	origTerm := stdoutIsTerminalFn
	stdoutIsTerminalFn = func() bool { return true }
	t.Cleanup(func() { stdoutIsTerminalFn = origTerm })

	return filepath.Join(home, ".unity-cli", "version-check.json")
}

func TestPrintUpdateNotice_UsesCachedOutdatedNoticeWithinInterval(t *testing.T) {
	path := prepareVersionCheckEnv(t, "v0.3.10")
	saveCache(path, &versionCache{
		CheckedAt: time.Now().Unix(),
		Latest:    "v0.3.11",
		Outdated:  true,
	})

	fetchCalled := false
	fetchLatestReleaseFn = func() (*ghRelease, error) {
		fetchCalled = true
		return &ghRelease{TagName: "v9.9.9"}, nil
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if fetchCalled {
		t.Fatal("expected no remote fetch while cache interval is still valid")
	}
	if count := strings.Count(output, "Update available:"); count != 1 {
		t.Fatalf("expected 1 notice, got %d: %q", count, output)
	}
	if !strings.Contains(output, "v0.3.10 → v0.3.11") {
		t.Fatalf("expected cached latest in notice, got %q", output)
	}
}

func TestPrintUpdateNotice_RefreshesCacheAndPrintsOnceWhenStillOutdated(t *testing.T) {
	path := prepareVersionCheckEnv(t, "v0.3.10")
	saveCache(path, &versionCache{
		CheckedAt: time.Now().Add(-2 * checkInterval).Unix(),
		Latest:    "v0.3.11",
		Outdated:  true,
	})

	fetchLatestReleaseFn = func() (*ghRelease, error) {
		return &ghRelease{TagName: "v0.3.12"}, nil
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if count := strings.Count(output, "Update available:"); count != 1 {
		t.Fatalf("expected 1 notice, got %d: %q", count, output)
	}
	if !strings.Contains(output, "v0.3.10 → v0.3.12") {
		t.Fatalf("expected refreshed latest in notice, got %q", output)
	}

	loaded, err := loadCache(path)
	if err != nil {
		t.Fatalf("loadCache: %v", err)
	}
	if loaded.Latest != "v0.3.12" || !loaded.Outdated {
		t.Fatalf("unexpected cache after refresh: %+v", loaded)
	}
}

func TestPrintUpdateNotice_PreservesCachedNoticeOnFetchFailure(t *testing.T) {
	path := prepareVersionCheckEnv(t, "v0.3.10")
	before := time.Now().Add(-2 * checkInterval).Unix()
	saveCache(path, &versionCache{
		CheckedAt: before,
		Latest:    "v0.3.11",
		Outdated:  true,
	})

	fetchLatestReleaseFn = func() (*ghRelease, error) {
		return nil, errors.New("network down")
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if count := strings.Count(output, "Update available:"); count != 1 {
		t.Fatalf("expected 1 notice, got %d: %q", count, output)
	}
	if !strings.Contains(output, "v0.3.10 → v0.3.11") {
		t.Fatalf("expected cached latest in notice, got %q", output)
	}

	loaded, err := loadCache(path)
	if err != nil {
		t.Fatalf("loadCache: %v", err)
	}
	if loaded.Latest != "v0.3.11" || !loaded.Outdated {
		t.Fatalf("unexpected cache after failed refresh: %+v", loaded)
	}
	if loaded.CheckedAt <= before {
		t.Fatalf("expected CheckedAt to be refreshed, got %d <= %d", loaded.CheckedAt, before)
	}
}

func TestPrintUpdateNotice_SkipsDevVersion(t *testing.T) {
	path := prepareVersionCheckEnv(t, "dev")
	fetchCalled := false
	fetchLatestReleaseFn = func() (*ghRelease, error) {
		fetchCalled = true
		return &ghRelease{TagName: "v0.3.11"}, nil
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if fetchCalled {
		t.Fatal("expected dev version to skip remote fetch")
	}
	if output != "" {
		t.Fatalf("expected no notice for dev version, got %q", output)
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatalf("expected no cache file for dev version")
	}
}

func TestPrintUpdateNotice_SuppressedWhenStdoutNotTerminal(t *testing.T) {
	path := prepareVersionCheckEnv(t, "v0.3.10")
	stdoutIsTerminalFn = func() bool { return false }

	saveCache(path, &versionCache{
		CheckedAt: time.Now().Unix(),
		Latest:    "v0.3.11",
		Outdated:  true,
	})

	fetchCalled := false
	fetchLatestReleaseFn = func() (*ghRelease, error) {
		fetchCalled = true
		return &ghRelease{TagName: "v9.9.9"}, nil
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if fetchCalled {
		t.Fatal("expected no remote fetch when stdout is not a terminal")
	}
	if output != "" {
		t.Fatalf("expected no notice when stdout is not a terminal, got %q", output)
	}
}

func TestPrintUpdateNotice_NoNoticeWhenVersionsEqualIgnoringVPrefix(t *testing.T) {
	// Reproduces the reported bug: a flake build reports "0.4.1" (no leading v)
	// while the release tag is "v0.4.1". These must be treated as equal.
	path := prepareVersionCheckEnv(t, "0.4.1")

	fetchLatestReleaseFn = func() (*ghRelease, error) {
		return &ghRelease{TagName: "v0.4.1"}, nil
	}

	output := captureStderr(t, func() {
		printUpdateNotice()
	})

	if output != "" {
		t.Fatalf("expected no notice for equal versions, got %q", output)
	}

	loaded, err := loadCache(path)
	if err != nil {
		t.Fatalf("loadCache: %v", err)
	}
	if loaded.Outdated {
		t.Fatalf("expected Outdated=false for equal versions, got %+v", loaded)
	}
}

func TestCompareVersions(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"0.4.1", "v0.4.1", 0},
		{"v0.4.1", "0.4.1", 0},
		{"0.4.1", "0.4.2", -1},
		{"0.4.10", "0.4.9", 1},
		{"1.0.0", "0.9.9", 1},
		{"0.4", "0.4.0", 0},
		{"0.4.1-rc1", "0.4.1-rc1", 0},
	}
	for _, c := range cases {
		if got := compareVersions(c.a, c.b); got != c.want {
			t.Errorf("compareVersions(%q, %q) = %d, want %d", c.a, c.b, got, c.want)
		}
	}
}

func TestIsOutdated(t *testing.T) {
	if isOutdated("0.4.1", "v0.4.1") {
		t.Error("equal versions (ignoring v prefix) must not be outdated")
	}
	if !isOutdated("0.4.1", "v0.4.2") {
		t.Error("newer release must be outdated")
	}
	if isOutdated("0.4.2", "v0.4.1") {
		t.Error("older release must not be outdated")
	}
	if isOutdated("0.4.1", "") {
		t.Error("empty latest must not be outdated")
	}
}

func TestLoadSaveCache(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cache.json")

	c := &versionCache{
		CheckedAt: time.Now().Unix(),
		Latest:    "v1.2.3",
		Outdated:  true,
	}
	saveCache(path, c)

	loaded, err := loadCache(path)
	if err != nil {
		t.Fatalf("loadCache: %v", err)
	}
	if loaded.CheckedAt != c.CheckedAt {
		t.Errorf("CheckedAt = %d, want %d", loaded.CheckedAt, c.CheckedAt)
	}
	if loaded.Latest != c.Latest {
		t.Errorf("Latest = %q, want %q", loaded.Latest, c.Latest)
	}
	if loaded.Outdated != c.Outdated {
		t.Errorf("Outdated = %v, want %v", loaded.Outdated, c.Outdated)
	}
}

func TestLoadCacheMissing(t *testing.T) {
	_, err := loadCache("/nonexistent/path/cache.json")
	if err == nil {
		t.Error("expected error for missing cache file")
	}
}

func TestLoadCacheCorrupt(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cache.json")
	_ = os.WriteFile(path, []byte("not json"), 0644)

	_, err := loadCache(path)
	if err == nil {
		t.Error("expected error for corrupt cache file")
	}
}

func TestSaveCacheCreatesDir(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "sub", "deep", "cache.json")

	c := &versionCache{CheckedAt: 123, Latest: "v2.0.0", Outdated: true}
	saveCache(path, c)

	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("file not created: %v", err)
	}
	var loaded versionCache
	if err := json.Unmarshal(data, &loaded); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if loaded.CheckedAt != 123 {
		t.Errorf("CheckedAt = %d, want 123", loaded.CheckedAt)
	}
	if loaded.Latest != "v2.0.0" {
		t.Errorf("Latest = %q, want %q", loaded.Latest, "v2.0.0")
	}
	if !loaded.Outdated {
		t.Error("Outdated = false, want true")
	}
}
