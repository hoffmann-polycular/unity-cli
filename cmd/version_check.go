// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const checkInterval = 1 * time.Hour

var fetchLatestReleaseFn = fetchLatestRelease

// stdoutIsTerminalFn reports whether stdout is an interactive terminal. It is a
// variable so tests can override it. We gate the update notice on this so that
// piped/redirected output (agents, scripts, jq, grep) stays clean and
// line-based processing is never broken by a stray notice.
var stdoutIsTerminalFn = stdoutIsTerminal

func stdoutIsTerminal() bool {
	fi, err := os.Stdout.Stat()
	if err != nil {
		return false
	}
	return fi.Mode()&os.ModeCharDevice != 0
}

// compareVersions compares two dotted versions, returning -1, 0, or 1. A leading
// "v" is ignored (see normalizeVersion). Each segment is compared by its leading integer; any malformed
// or non-numeric input falls back to a string comparison so we never panic on an
// unexpected tag.
func compareVersions(a, b string) int {
	a = normalizeVersion(a)
	b = normalizeVersion(b)
	if a == b {
		return 0
	}

	as := strings.Split(a, ".")
	bs := strings.Split(b, ".")
	n := len(as)
	if len(bs) > n {
		n = len(bs)
	}

	for i := 0; i < n; i++ {
		ai, aok := segmentValue(as, i)
		bi, bok := segmentValue(bs, i)
		if !aok || !bok {
			return strings.Compare(a, b)
		}
		if ai != bi {
			if ai < bi {
				return -1
			}
			return 1
		}
	}
	return 0
}

// segmentValue returns the leading integer of segment i (missing segments are 0).
func segmentValue(segs []string, i int) (int, bool) {
	if i >= len(segs) {
		return 0, true
	}
	s := segs[i]
	// Take only the leading run of digits so suffixes like "1-rc1" still parse.
	end := 0
	for end < len(s) && s[end] >= '0' && s[end] <= '9' {
		end++
	}
	if end == 0 {
		return 0, false
	}
	v, err := strconv.Atoi(s[:end])
	if err != nil {
		return 0, false
	}
	return v, true
}

// isOutdated reports whether latest is a strictly newer release than current.
func isOutdated(current, latest string) bool {
	return latest != "" && compareVersions(current, latest) < 0
}

type versionCache struct {
	CheckedAt int64  `json:"checked_at"`
	Latest    string `json:"latest,omitempty"`
	Outdated  bool   `json:"outdated,omitempty"`
}

func cacheFilePath() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".unity-cli", "version-check.json")
}

func loadCache(path string) (*versionCache, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var c versionCache
	if err := json.Unmarshal(data, &c); err != nil {
		return nil, err
	}
	return &c, nil
}

func saveCache(path string, c *versionCache) {
	dir := filepath.Dir(path)
	_ = os.MkdirAll(dir, 0755)
	data, err := json.Marshal(c)
	if err != nil {
		return
	}
	_ = os.WriteFile(path, data, 0644)
}

// printUpdateNotice checks for a newer version and prints a notice if available.
// Silently does nothing on any error (no network, bad cache, etc.).
func printUpdateNotice() {
	if Version == "dev" {
		return
	}

	// Only surface the notice on an interactive terminal. When stdout is piped
	// or redirected the caller is processing output programmatically and a
	// notice would corrupt line-based parsing.
	if !stdoutIsTerminalFn() {
		return
	}

	path := cacheFilePath()
	if path == "" {
		return
	}

	now := time.Now().Unix()
	cache, _ := loadCache(path)
	latestNotice := ""

	if cache != nil && isOutdated(Version, cache.Latest) {
		latestNotice = cache.Latest
	}

	if cache != nil && now-cache.CheckedAt < int64(checkInterval.Seconds()) {
		if latestNotice != "" {
			printNotice(Version, latestNotice)
		}
		return
	}

	// Fetch from GitHub
	release, err := fetchLatestReleaseFn()
	if err != nil {
		// Network error — save timestamp so we don't retry immediately
		if cache != nil {
			cache.CheckedAt = now
			saveCache(path, cache)
		} else {
			saveCache(path, &versionCache{CheckedAt: now})
		}
		if latestNotice != "" {
			printNotice(Version, latestNotice)
		}
		return
	}

	nextCache := &versionCache{
		CheckedAt: now,
		Latest:    release.TagName,
		Outdated:  isOutdated(Version, release.TagName),
	}
	saveCache(path, nextCache)

	if nextCache.Outdated {
		latestNotice = release.TagName
	} else {
		latestNotice = ""
	}

	if latestNotice != "" {
		printNotice(Version, latestNotice)
	}
}

func printNotice(current, latest string) {
	fmt.Fprintf(os.Stderr, "\nUpdate available: %s → %s\nRun \"unity-cli update\" to upgrade.\n", current, latest)
}
