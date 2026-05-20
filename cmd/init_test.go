// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// makeFakeProject builds a minimum-viable Unity project layout in t.TempDir():
// ProjectSettings/, Packages/manifest.json with optional pre-seeded deps.
func makeFakeProject(t *testing.T, seedManifest string) string {
	t.Helper()
	dir := t.TempDir()
	if err := os.Mkdir(filepath.Join(dir, "ProjectSettings"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(dir, "Packages"), 0o755); err != nil {
		t.Fatal(err)
	}
	if seedManifest == "" {
		seedManifest = `{"dependencies":{}}`
	}
	if err := os.WriteFile(filepath.Join(dir, "Packages", "manifest.json"),
		[]byte(seedManifest), 0o644); err != nil {
		t.Fatal(err)
	}
	return dir
}

// readDeps re-reads the manifest from disk and returns its dependencies map.
func readDeps(t *testing.T, projectDir string) map[string]string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(projectDir, "Packages", "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	var m map[string]map[string]string
	if err := json.Unmarshal(data, &m); err != nil {
		t.Fatal(err)
	}
	return m["dependencies"]
}

// withVersion swaps the package-level Version for the duration of a test.
func withVersion(t *testing.T, v string) {
	t.Helper()
	orig := Version
	Version = v
	t.Cleanup(func() { Version = orig })
}

func TestInit_InstallsGitURL_OnReleaseBuild(t *testing.T) {
	withVersion(t, "v0.3.18")
	dir := makeFakeProject(t, "")

	if err := initCmd([]string{dir}); err != nil {
		t.Fatalf("initCmd: %v", err)
	}

	deps := readDeps(t, dir)
	got := deps["com.polycular.unity-cli-connector"]
	want := "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18"
	if got != want {
		t.Errorf("dependency mismatch:\n  got:  %q\n  want: %q", got, want)
	}
}

func TestInit_PrependsVTag(t *testing.T) {
	// CLI Version without leading 'v' should still produce a 'v'-prefixed git ref.
	withVersion(t, "0.3.18")
	dir := makeFakeProject(t, "")

	if err := initCmd([]string{dir}); err != nil {
		t.Fatalf("initCmd: %v", err)
	}
	got := readDeps(t, dir)["com.polycular.unity-cli-connector"]
	if !strings.HasSuffix(got, "#v0.3.18") {
		t.Errorf("expected ref to end with #v0.3.18, got %q", got)
	}
}

func TestInit_DevBuildRefusesGit(t *testing.T) {
	withVersion(t, "dev")
	dir := makeFakeProject(t, "")

	err := initCmd([]string{dir})
	if err == nil {
		t.Fatal("expected dev-build install to fail; got nil")
	}
	if !strings.Contains(err.Error(), "--local") {
		t.Errorf("dev-build error should suggest --local, got: %v", err)
	}
}

func TestInit_DevBuildWithLocalSucceeds(t *testing.T) {
	withVersion(t, "dev")
	dir := makeFakeProject(t, "")

	// Build a fake connector source with a package.json so chooseInitSource accepts it.
	connector := t.TempDir()
	if err := os.WriteFile(filepath.Join(connector, "package.json"),
		[]byte(`{"name":"x","version":"0.0.0"}`), 0o644); err != nil {
		t.Fatal(err)
	}

	if err := initCmd([]string{dir, "--local", connector}); err != nil {
		t.Fatalf("initCmd: %v", err)
	}

	got := readDeps(t, dir)["com.polycular.unity-cli-connector"]
	if !strings.HasPrefix(got, "file:") {
		t.Errorf("expected file: prefix, got %q", got)
	}
	// Path inside the value must use forward slashes (Unity manifest convention).
	if strings.Contains(got, `\`) {
		t.Errorf("file: URI contains backslashes (should be forward slashes): %q", got)
	}
}

func TestInit_LocalPathMissingPackageJSONRejected(t *testing.T) {
	withVersion(t, "v0.3.18")
	dir := makeFakeProject(t, "")
	empty := t.TempDir() // no package.json

	err := initCmd([]string{dir, "--local", empty})
	if err == nil {
		t.Fatal("expected error when --local path has no package.json")
	}
	if !strings.Contains(err.Error(), "package.json") {
		t.Errorf("error should mention package.json, got: %v", err)
	}
}

func TestInit_IdempotentOnExactMatch(t *testing.T) {
	withVersion(t, "v0.3.18")
	src := "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18"
	seed := `{"dependencies":{"com.polycular.unity-cli-connector":"` + src + `","com.unity.ide.rider":"3.0.0"}}`
	dir := makeFakeProject(t, seed)

	if err := initCmd([]string{dir}); err != nil {
		t.Fatalf("idempotent init should succeed: %v", err)
	}

	deps := readDeps(t, dir)
	if deps["com.polycular.unity-cli-connector"] != src {
		t.Errorf("entry was modified: %q", deps["com.polycular.unity-cli-connector"])
	}
	if deps["com.unity.ide.rider"] != "3.0.0" {
		t.Errorf("unrelated dependency was disturbed: %v", deps)
	}
}

func TestInit_RefusesToOverwriteWithoutUpgrade(t *testing.T) {
	withVersion(t, "v0.3.19")
	stale := "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18"
	seed := `{"dependencies":{"com.polycular.unity-cli-connector":"` + stale + `"}}`
	dir := makeFakeProject(t, seed)

	err := initCmd([]string{dir})
	if err == nil {
		t.Fatal("expected refusal without --upgrade")
	}
	if !strings.Contains(err.Error(), "--upgrade") {
		t.Errorf("error should mention --upgrade, got: %v", err)
	}

	// Manifest must NOT have been touched.
	if readDeps(t, dir)["com.polycular.unity-cli-connector"] != stale {
		t.Errorf("manifest was modified despite refusal")
	}
}

func TestInit_UpgradeRewrites(t *testing.T) {
	withVersion(t, "v0.3.19")
	stale := "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18"
	seed := `{"dependencies":{"com.polycular.unity-cli-connector":"` + stale + `"}}`
	dir := makeFakeProject(t, seed)

	if err := initCmd([]string{dir, "--upgrade"}); err != nil {
		t.Fatalf("initCmd --upgrade: %v", err)
	}
	got := readDeps(t, dir)["com.polycular.unity-cli-connector"]
	if !strings.HasSuffix(got, "#v0.3.19") {
		t.Errorf("upgrade did not bump: got %q", got)
	}
}

func TestInit_Uninstall(t *testing.T) {
	withVersion(t, "v0.3.18")
	src := "https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#v0.3.18"
	seed := `{"dependencies":{"com.polycular.unity-cli-connector":"` + src + `","com.unity.ide.rider":"3.0.0"}}`
	dir := makeFakeProject(t, seed)

	if err := initCmd([]string{dir, "--uninstall"}); err != nil {
		t.Fatalf("uninstall: %v", err)
	}

	deps := readDeps(t, dir)
	if _, present := deps["com.polycular.unity-cli-connector"]; present {
		t.Error("connector dependency still present after --uninstall")
	}
	if deps["com.unity.ide.rider"] != "3.0.0" {
		t.Errorf("unrelated dependency was disturbed: %v", deps)
	}
}

func TestInit_UninstallNoOpWhenAbsent(t *testing.T) {
	withVersion(t, "v0.3.18")
	dir := makeFakeProject(t, `{"dependencies":{"com.unity.ide.rider":"3.0.0"}}`)

	if err := initCmd([]string{dir, "--uninstall"}); err != nil {
		t.Fatalf("uninstall on absent entry should succeed: %v", err)
	}
}

func TestInit_RejectsNonUnityDirectory(t *testing.T) {
	withVersion(t, "v0.3.18")
	dir := t.TempDir() // no ProjectSettings/, no Packages/

	err := initCmd([]string{dir})
	if err == nil {
		t.Fatal("expected error on non-Unity directory")
	}
}

func TestInit_PreservesScopedRegistries(t *testing.T) {
	// Verifies the forward-compat promise: top-level keys we don't recognize
	// must survive a write/edit cycle untouched.
	withVersion(t, "v0.3.18")
	seed := `{
  "dependencies": {
    "com.unity.ide.rider": "3.0.0"
  },
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.openupm"]
    }
  ]
}`
	dir := makeFakeProject(t, seed)

	if err := initCmd([]string{dir}); err != nil {
		t.Fatalf("initCmd: %v", err)
	}

	data, err := os.ReadFile(filepath.Join(dir, "Packages", "manifest.json"))
	if err != nil {
		t.Fatal(err)
	}
	var got map[string]interface{}
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatalf("parse: %v", err)
	}

	regs, ok := got["scopedRegistries"].([]interface{})
	if !ok || len(regs) != 1 {
		t.Fatalf("scopedRegistries lost or malformed: %v", got["scopedRegistries"])
	}
	first, _ := regs[0].(map[string]interface{})
	if first["url"] != "https://package.openupm.com" {
		t.Errorf("scopedRegistries content corrupted: %v", first)
	}
}

func TestInit_RejectsMutuallyExclusiveFlags(t *testing.T) {
	withVersion(t, "v0.3.18")
	dir := makeFakeProject(t, "")

	cases := [][]string{
		{dir, "--upgrade", "--uninstall"},
		{dir, "--local", "/some/path", "--uninstall"},
	}
	for _, c := range cases {
		if err := initCmd(c); err == nil {
			t.Errorf("expected refusal for args %v", c)
		}
	}
}
