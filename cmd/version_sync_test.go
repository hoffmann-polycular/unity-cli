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

package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"testing"
)

// TestConnectorVersionsInSync asserts that the version string declared
// in the Unity package manifest matches the CONNECTOR_VERSION constant
// hard-coded into the connector's Heartbeat.cs.
//
// These two values MUST agree: the package.json version is what Unity
// Package Manager shows users, while CONNECTOR_VERSION is what the
// connector reports over the heartbeat — which the CLI then compares
// against its own build version (cmd/status.go checkConnectorVersion).
//
// Drift between them is a release-blocker: users would install
// package.json version X but the runtime check would compare against
// Heartbeat.cs version Y, and every command would abort with a
// confusing "connector version mismatch" error.
//
// Bump them together. Always.
func TestConnectorVersionsInSync(t *testing.T) {
	repoRoot := findRepoRoot(t)

	pkgVersion := readPackageJSONVersion(t, filepath.Join(repoRoot, "unity-connector", "package.json"))
	hbVersion := readHeartbeatVersion(t, filepath.Join(repoRoot, "unity-connector", "Editor", "Heartbeat.cs"))

	if pkgVersion != hbVersion {
		t.Fatalf("version drift between Unity package and connector runtime:\n"+
			"  unity-connector/package.json:        %q\n"+
			"  unity-connector/Editor/Heartbeat.cs: %q\n"+
			"\nBoth must be bumped together. See cmd/status.go checkConnectorVersion "+
			"for why this matters.", pkgVersion, hbVersion)
	}
}

// TestConnectorVersionFormat sanity-checks the version literal: it must
// be a non-empty, non-"dev" semver-shaped string. Catches typos like
// leaving the value as "0.3.18-wip" or accidentally clearing it.
func TestConnectorVersionFormat(t *testing.T) {
	repoRoot := findRepoRoot(t)
	v := readPackageJSONVersion(t, filepath.Join(repoRoot, "unity-connector", "package.json"))

	if v == "" {
		t.Fatal("package.json version is empty")
	}
	if v == "dev" {
		t.Fatal(`package.json version is "dev"; the Unity package must ship a concrete version`)
	}
	// Loose semver: MAJOR.MINOR.PATCH with optional -prerelease/+build.
	semverRe := regexp.MustCompile(`^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$`)
	if !semverRe.MatchString(v) {
		t.Fatalf("package.json version %q is not a valid semver (MAJOR.MINOR.PATCH[-pre][+build])", v)
	}
}

// findRepoRoot walks up from the test's working directory until it
// finds a go.mod, returning that directory. Tests run with CWD set to
// the package directory (cmd/), so we walk up to find the repo root.
func findRepoRoot(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatalf("os.Getwd: %v", err)
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "go.mod")); err == nil {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("could not find go.mod walking up from %s", dir)
		}
		dir = parent
	}
}

func readPackageJSONVersion(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	var manifest struct {
		Version string `json:"version"`
	}
	if err := json.Unmarshal(data, &manifest); err != nil {
		t.Fatalf("parse %s: %v", path, err)
	}
	return manifest.Version
}

// TestFlakeVersionsInSync asserts that both version occurrences inside
// flake.nix — the package `version` attribute and the `-X main.Version=`
// ldflag — match the canonical version in unity-connector/package.json.
func TestFlakeVersionsInSync(t *testing.T) {
	repoRoot := findRepoRoot(t)
	pkgVersion := readPackageJSONVersion(t, filepath.Join(repoRoot, "unity-connector", "package.json"))

	data, err := os.ReadFile(filepath.Join(repoRoot, "flake.nix"))
	if err != nil {
		t.Fatalf("read flake.nix: %v", err)
	}
	src := string(data)

	flakeVersion := extractFlakeField(t, src, `version\s*=\s*"([^"]+)"`, "version")
	ldflagVersion := extractFlakeField(t, src, `-X\s+main\.Version=([^"\s]+)`, "-X main.Version")

	if flakeVersion != pkgVersion {
		t.Errorf("flake.nix version %q != package.json %q", flakeVersion, pkgVersion)
	}
	if ldflagVersion != pkgVersion {
		t.Errorf("flake.nix ldflag -X main.Version=%q != package.json %q", ldflagVersion, pkgVersion)
	}
}

func extractFlakeField(t *testing.T, src, pattern, label string) string {
	t.Helper()
	m := regexp.MustCompile(pattern).FindStringSubmatch(src)
	if m == nil {
		t.Fatalf("could not find %q in flake.nix", label)
	}
	return m[1]
}

// heartbeatVersionRe extracts the literal from:
//
//	const string CONNECTOR_VERSION = "0.3.18";
//
// Whitespace tolerant; modifiers (public/private/static) may precede the const.
var heartbeatVersionRe = regexp.MustCompile(`const\s+string\s+CONNECTOR_VERSION\s*=\s*"([^"]+)"\s*;`)

func readHeartbeatVersion(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	m := heartbeatVersionRe.FindSubmatch(data)
	if m == nil {
		t.Fatalf("could not find `const string CONNECTOR_VERSION = \"...\";` in %s", path)
	}
	return string(m[1])
}
