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
	"strings"
	"testing"
)

// unity-connector/package.json is the single source of truth for the version.
// The connector reads it at runtime (Heartbeat.GetConnectorVersion via
// PackageInfo) and the flake derives from it (see TestFlakeVersionsInSync), so
// there is no second literal to drift against — the old
// TestConnectorVersionsInSync (package.json vs a CONNECTOR_VERSION constant) is
// gone along with that constant.

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

// TestFlakeVersionsInSync asserts that the flake derives its version from
// unity-connector/package.json rather than carrying its own literal, and that
// the injected main.Version is derived from that attribute.
//
// The ldflag deliberately reads `-X main.Version=v${version}`: the leading "v"
// is added programmatically so the binary's reported version always tracks the
// package.json-derived attr and can never drift to a stale literal. We assert
// the derivation is intact, not a numeric value.
func TestFlakeVersionsInSync(t *testing.T) {
	repoRoot := findRepoRoot(t)

	data, err := os.ReadFile(filepath.Join(repoRoot, "flake.nix"))
	if err != nil {
		t.Fatalf("read flake.nix: %v", err)
	}
	src := string(data)

	if !strings.Contains(src, `fromJSON`) ||
		!strings.Contains(src, `./unity-connector/package.json`) {
		t.Errorf("flake.nix should derive `version` from unity-connector/package.json " +
			"(builtins.fromJSON (builtins.readFile ./unity-connector/package.json)).version; " +
			"found a different/hard-coded form")
	}

	ldflag := extractFlakeField(t, src, `-X\s+main\.Version=([^"\s]+)`, "-X main.Version")
	if ldflag != "v${version}" {
		t.Errorf("flake.nix ldflag -X main.Version=%q is not derived from the version attr; "+
			"expected the literal %q so it tracks the checked attr with a leading v", ldflag, "v${version}")
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
