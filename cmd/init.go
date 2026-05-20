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
	"fmt"
	"os"
	"path/filepath"

	"github.com/hoffmann-polycular/unity-cli/internal/cli/exit"
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// initCmd installs (or removes) the Unity-CLI connector UPM package into
// a Unity project by editing its Packages/manifest.json directly.
//
// This command intentionally does NOT talk to a running Editor — it works
// purely on the filesystem, because the typical use case is bootstrapping
// a project that doesn't have the connector yet. After editing the
// manifest, Unity (if running) will detect the change on focus, or on the
// next refresh, and import the package itself.
//
// Source selection:
//
//	release CLI (Version is a real semver, e.g. "v0.3.18"):
//	    git URL pinned to that tag — version-matched by construction.
//	dev CLI (Version == "dev"):
//	    refuses git installs; --local <path> is required.
//
// See AGENTS.md "Init from dev builds" for the rationale.
func initCmd(args []string) error {
	var (
		positional string
		localPath  string
		upgrade    bool
		uninstall  bool
		wait       bool
	)

	for i := 0; i < len(args); i++ {
		a := args[i]
		switch a {
		case "--help", "-h":
			printTopicHelp("init")
			return nil
		case "--local":
			if i+1 >= len(args) {
				return exit.New(exit.Usage, "--local requires a path argument")
			}
			localPath = args[i+1]
			i++
		case "--upgrade":
			upgrade = true
		case "--uninstall":
			uninstall = true
		case "--wait":
			wait = true
		default:
			if positional != "" {
				return exit.New(exit.Usage, "unexpected extra positional argument: %q", a)
			}
			positional = a
		}
	}

	if upgrade && uninstall {
		return exit.New(exit.Usage, "--upgrade and --uninstall are mutually exclusive")
	}
	if uninstall && localPath != "" {
		return exit.New(exit.Usage, "--uninstall and --local are mutually exclusive")
	}

	// Resolve the project path: positional > --project > walk up from CWD.
	projectDir, err := resolveProjectDir(positional, flagProject)
	if err != nil {
		return err
	}
	manifestPath := filepath.Join(projectDir, "Packages", "manifest.json")
	if _, err := os.Stat(manifestPath); err != nil {
		return exit.New(exit.NotFound, "no Packages/manifest.json under %s — is this really a Unity project?", projectDir)
	}

	const pkgName = "com.polycular.unity-cli-connector"

	manifest, err := readManifest(manifestPath)
	if err != nil {
		return err
	}

	if uninstall {
		return doUninstall(manifest, manifestPath, pkgName)
	}

	// Decide install source.
	source, err := chooseInitSource(localPath)
	if err != nil {
		return err
	}

	existing, hasExisting := getDependency(manifest, pkgName)
	if hasExisting {
		if existing == source {
			fmt.Printf("%s is already installed at %q in %s — nothing to do.\n",
				pkgName, existing, manifestPath)
			return maybeWait(projectDir, wait)
		}
		if !upgrade {
			return exit.New(exit.Usage,
				"%s is already installed pointing at %q.\n"+
					"To replace it with %q, rerun with --upgrade.",
				pkgName, existing, source)
		}
		fmt.Printf("Upgrading %s\n  from: %s\n    to: %s\n", pkgName, existing, source)
	} else {
		fmt.Printf("Installing %s @ %s\n", pkgName, source)
	}

	setDependency(manifest, pkgName, source)
	if err := writeManifest(manifestPath, manifest); err != nil {
		return err
	}
	fmt.Printf("Updated %s\n", manifestPath)
	fmt.Println("Open the Unity Editor on this project to import the connector.")

	return maybeWait(projectDir, wait)
}

// resolveProjectDir picks a project path from (in priority order) the
// positional arg, the global --project flag, or by walking up from CWD
// looking for a Unity project signature (ProjectSettings/ + Packages/).
// Returns an absolute, cleaned path.
func resolveProjectDir(positional, projectFlag string) (string, error) {
	candidate := positional
	if candidate == "" {
		candidate = projectFlag
	}

	if candidate != "" {
		abs, err := filepath.Abs(candidate)
		if err != nil {
			return "", exit.New(exit.Usage, "cannot resolve project path %q: %v", candidate, err)
		}
		if !looksLikeUnityProject(abs) {
			return "", exit.New(exit.NotFound, "%s does not look like a Unity project (missing ProjectSettings/ or Packages/)", abs)
		}
		return abs, nil
	}

	cwd, err := os.Getwd()
	if err != nil {
		return "", exit.New(exit.Runtime, "cannot read working directory: %v", err)
	}
	dir := cwd
	for {
		if looksLikeUnityProject(dir) {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", exit.New(exit.NotFound,
				"could not find a Unity project from %s upward.\n"+
					"Pass a project path positionally or via --project.", cwd)
		}
		dir = parent
	}
}

func looksLikeUnityProject(dir string) bool {
	if fi, err := os.Stat(filepath.Join(dir, "ProjectSettings")); err != nil || !fi.IsDir() {
		return false
	}
	if fi, err := os.Stat(filepath.Join(dir, "Packages")); err != nil || !fi.IsDir() {
		return false
	}
	return true
}

// chooseInitSource decides what string to write into manifest.json:
//   - --local <path>: write "file:<abs-path>" (allowed on any build).
//   - dev CLI build, no --local: refuse — install would point at a
//     non-existent git tag.
//   - release CLI build: pin to the CLI's own version tag.
func chooseInitSource(localPath string) (string, error) {
	if localPath != "" {
		abs, err := filepath.Abs(localPath)
		if err != nil {
			return "", exit.New(exit.Usage, "cannot resolve --local path %q: %v", localPath, err)
		}
		if fi, err := os.Stat(abs); err != nil || !fi.IsDir() {
			return "", exit.New(exit.NotFound, "--local path %s is not an accessible directory", abs)
		}
		// Sanity: the path should at least contain a package.json — otherwise
		// Unity will reject it with a confusing error.
		if _, err := os.Stat(filepath.Join(abs, "package.json")); err != nil {
			return "", exit.New(exit.NotFound, "--local path %s has no package.json — point me at the unity-connector/ directory", abs)
		}
		// Unity expects forward slashes in file: URIs even on Windows.
		return "file:" + filepath.ToSlash(abs), nil
	}

	if normalizeVersion(Version) == "dev" {
		return "", exit.New(exit.Usage,
			"`unity-cli init` from a dev build cannot pin to a git tag.\n"+
				"Pass --local <path-to-your-unity-connector-checkout> instead,\n"+
				"or use a release build of unity-cli (see `unity-cli update`).")
	}

	tag := Version
	if len(tag) > 0 && tag[0] != 'v' && tag[0] != 'V' {
		tag = "v" + tag
	}
	return fmt.Sprintf("https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector#%s", tag), nil
}

func doUninstall(manifest map[string]interface{}, manifestPath, pkgName string) error {
	if _, ok := getDependency(manifest, pkgName); !ok {
		fmt.Printf("%s is not installed in %s — nothing to do.\n", pkgName, manifestPath)
		return nil
	}
	removeDependency(manifest, pkgName)
	if err := writeManifest(manifestPath, manifest); err != nil {
		return err
	}
	fmt.Printf("Removed %s from %s\n", pkgName, manifestPath)
	return nil
}

// maybeWait, when wait is true, polls until a heartbeat from a Unity
// instance matching this project shows up, then runs the standard
// connector-version check on it. Bounded by the global --timeout flag.
func maybeWait(projectDir string, wait bool) error {
	if !wait {
		return nil
	}
	fmt.Fprintf(os.Stderr, "Waiting for connector heartbeat from %s ...\n", projectDir)
	resolve := func() (*client.Instance, error) {
		return client.DiscoverInstance(projectDir, 0)
	}
	inst, err := waitForAlive(resolve, flagTimeout)
	if err != nil {
		return exit.New(exit.Unreach,
			"%v.\nOpen the Unity Editor on this project to import the connector, then run `unity-cli status`.", err)
	}
	if err := checkConnectorVersion(inst, Version, flagIgnoreVersionMismatch); err != nil {
		return exit.Wrap(exit.Runtime, err)
	}
	fmt.Printf("Connector ready: port %d, version %s\n", inst.Port, connectorVersionLabel(inst.ConnectorVersion))
	return nil
}

// ============================================================
// Manifest JSON read / mutate / write.
//
// Unity's Packages/manifest.json is a small JSON object with (typically)
// `dependencies` and optionally `scopedRegistries`. Go's encoding/json
// sorts map keys alphabetically on marshal, which happens to match
// Unity's own conventional ordering, so we don't need a custom ordered
// map type.
// ============================================================

func readManifest(path string) (map[string]interface{}, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, exit.New(exit.Runtime, "cannot read %s: %v", path, err)
	}
	var m map[string]interface{}
	if err := json.Unmarshal(data, &m); err != nil {
		return nil, exit.New(exit.Runtime, "cannot parse %s: %v", path, err)
	}
	if m == nil {
		m = map[string]interface{}{}
	}
	return m, nil
}

func writeManifest(path string, manifest map[string]interface{}) error {
	buf, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return exit.New(exit.Runtime, "cannot serialize manifest: %v", err)
	}
	buf = append(buf, '\n')
	if err := os.WriteFile(path, buf, 0o644); err != nil {
		return exit.New(exit.Runtime, "cannot write %s: %v", path, err)
	}
	return nil
}

func getDependency(manifest map[string]interface{}, name string) (string, bool) {
	deps, ok := manifest["dependencies"].(map[string]interface{})
	if !ok {
		return "", false
	}
	v, ok := deps[name].(string)
	return v, ok
}

func setDependency(manifest map[string]interface{}, name, value string) {
	deps, ok := manifest["dependencies"].(map[string]interface{})
	if !ok {
		deps = map[string]interface{}{}
		manifest["dependencies"] = deps
	}
	deps[name] = value
}

func removeDependency(manifest map[string]interface{}, name string) {
	deps, ok := manifest["dependencies"].(map[string]interface{})
	if !ok {
		return
	}
	delete(deps, name)
}
