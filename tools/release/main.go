// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Command release cuts a new unity-cli version.
//
// unity-connector/package.json is the single source of truth: the connector
// reads it at runtime (PackageInfo) and flake.nix derives from it, so this tool
// only needs to bump that one file, then tag.
//
// Usage:
//
//	go run ./tools/release <X.Y.Z | patch | minor | major> [--dry-run] [--push]
//
// It bumps package.json, runs `go test ./cmd/...`, commits just that file, and
// creates the vX.Y.Z tag. Pushing is left to you unless --push is given.
package main

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "release: "+err.Error())
		os.Exit(1)
	}
}

func run(args []string) error {
	var dryRun, push, check, skipChecks bool
	var spec string
	for _, a := range args {
		switch a {
		case "--dry-run":
			dryRun = true
		case "--push":
			push = true
		case "--check":
			check = true
		case "--skip-checks":
			skipChecks = true
		case "-h", "--help":
			fmt.Println("usage: go run ./tools/release <X.Y.Z | patch | minor | major> [--dry-run] [--push] [--skip-checks]")
			fmt.Println("       go run ./tools/release --check   (run CI-equivalent checks only, no bump)")
			return nil
		default:
			if strings.HasPrefix(a, "-") {
				return fmt.Errorf("unknown flag %q", a)
			}
			if spec != "" {
				return fmt.Errorf("unexpected extra argument %q", a)
			}
			spec = a
		}
	}

	root, err := repoRoot()
	if err != nil {
		return err
	}

	// --check: run the CI-equivalent gate and stop. No version arg required.
	if check {
		fmt.Println("running CI-equivalent checks…")
		if err := preflight(root); err != nil {
			return err
		}
		fmt.Println("✓ all checks passed")
		return nil
	}

	if spec == "" {
		return fmt.Errorf("missing version argument (X.Y.Z, patch, minor, or major)")
	}

	pkgPath := filepath.Join(root, "unity-connector", "package.json")
	data, err := os.ReadFile(pkgPath)
	if err != nil {
		return fmt.Errorf("read %s: %w", pkgPath, err)
	}

	cur, err := extractVersion(string(data))
	if err != nil {
		return err
	}
	next, err := resolveTarget(cur, spec)
	if err != nil {
		return err
	}
	if next == cur {
		return fmt.Errorf("version is already %s — nothing to do", cur)
	}
	tag := "v" + next

	fmt.Printf("current: %s\nnext:    %s  (tag %s)\n", cur, next, tag)
	if dryRun {
		fmt.Println("--dry-run: no files changed, no checks run, no commit/tag created")
		return nil
	}

	if exists, _ := tagExists(root, tag); exists {
		return fmt.Errorf("tag %s already exists", tag)
	}

	// Gate on the same checks CI runs, before touching anything — a failure
	// here leaves the tree unchanged.
	if skipChecks {
		fmt.Println("--skip-checks: NOT running preflight (CI will still gate the tag)")
	} else if err := preflight(root); err != nil {
		return err
	}

	updated := replaceVersion(string(data), next)
	if err := os.WriteFile(pkgPath, []byte(updated), 0o644); err != nil {
		return fmt.Errorf("write %s: %w", pkgPath, err)
	}
	fmt.Printf("bumped %s → %s\n", relPkg, next)

	if err := runCmd(root, "git", "commit", relPkg, "-m", "chore: increase version to "+next); err != nil {
		return fmt.Errorf("git commit: %w", err)
	}
	if err := runCmd(root, "git", "tag", tag); err != nil {
		return fmt.Errorf("git tag: %w", err)
	}
	fmt.Printf("committed and tagged %s\n", tag)

	if push {
		if err := runCmd(root, "git", "push"); err != nil {
			return fmt.Errorf("git push: %w", err)
		}
		if err := runCmd(root, "git", "push", "origin", tag); err != nil {
			return fmt.Errorf("git push tag: %w", err)
		}
		fmt.Println("pushed branch and tag")
	} else {
		fmt.Printf("\nnext: git push && git push origin %s\n", tag)
	}
	return nil
}

const relPkg = "unity-connector/package.json"

// preflight runs the same gates CI does (the `test` job: build/vet/test, and
// the `lint` job: gofmt + golangci-lint), so a release can't be cut that would
// fail CI. golangci-lint is used when installed; the gofmt check always runs
// (it's the only formatter the .golangci.yml enables, so it's faithful even
// without golangci-lint).
func preflight(root string) error {
	// Format first — instant feedback, and the gate that bites most often.
	fmt.Println("• gofmt -l")
	unformatted, err := gofmtList(root)
	if err != nil {
		return err
	}
	if len(unformatted) > 0 {
		return fmt.Errorf("gofmt: %d file(s) need formatting (run: gofmt -w <files>):\n  %s",
			len(unformatted), strings.Join(unformatted, "\n  "))
	}

	for _, step := range [][]string{
		{"go", "build", "./..."},
		{"go", "vet", "./..."},
		{"go", "test", "./..."},
	} {
		fmt.Printf("• %s\n", strings.Join(step, " "))
		if err := runCmd(root, step[0], step[1:]...); err != nil {
			return fmt.Errorf("%s failed: %w", strings.Join(step, " "), err)
		}
	}

	if _, err := exec.LookPath("golangci-lint"); err == nil {
		fmt.Println("• golangci-lint run")
		if err := runCmd(root, "golangci-lint", "run"); err != nil {
			return fmt.Errorf("golangci-lint run failed: %w", err)
		}
	} else {
		fmt.Println("• golangci-lint not found — skipping (CI still runs it)")
	}
	return nil
}

// gofmtList returns the gofmt-unformatted .go files under root, excluding the
// vendored tree (which we don't own and CI doesn't lint).
func gofmtList(root string) ([]string, error) {
	cmd := exec.Command("gofmt", "-l", ".")
	cmd.Dir = root
	out, err := cmd.Output()
	if err != nil {
		return nil, fmt.Errorf("gofmt: %w", err)
	}
	var bad []string
	for _, line := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		norm := strings.ReplaceAll(line, "\\", "/")
		if norm == "vendor" || strings.HasPrefix(norm, "vendor/") || strings.Contains(norm, "/vendor/") {
			continue
		}
		bad = append(bad, line)
	}
	return bad, nil
}

var versionLineRe = regexp.MustCompile(`("version"\s*:\s*")([^"]+)(")`)

func extractVersion(s string) (string, error) {
	m := versionLineRe.FindStringSubmatch(s)
	if m == nil {
		return "", fmt.Errorf("could not find a \"version\" field in package.json")
	}
	return m[2], nil
}

// replaceVersion swaps the version value while preserving the file's exact
// formatting (only the first "version" occurrence is touched).
func replaceVersion(s, next string) string {
	done := false
	return versionLineRe.ReplaceAllStringFunc(s, func(m string) string {
		if done {
			return m
		}
		done = true
		sub := versionLineRe.FindStringSubmatch(m)
		return sub[1] + next + sub[3]
	})
}

// resolveTarget turns the user's spec into a concrete version: an explicit
// X.Y.Z, or a patch/minor/major bump of the current version.
func resolveTarget(cur, spec string) (string, error) {
	switch spec {
	case "patch", "minor", "major":
		maj, min, pat, err := parseSemverCore(cur)
		if err != nil {
			return "", fmt.Errorf("current version %q: %w", cur, err)
		}
		switch spec {
		case "major":
			maj, min, pat = maj+1, 0, 0
		case "minor":
			min, pat = min+1, 0
		case "patch":
			pat++
		}
		return fmt.Sprintf("%d.%d.%d", maj, min, pat), nil
	default:
		if _, _, _, err := parseSemverCore(spec); err != nil {
			return "", fmt.Errorf("invalid version %q (want X.Y.Z, patch, minor, or major): %w", spec, err)
		}
		return spec, nil
	}
}

// parseSemverCore parses a strict MAJOR.MINOR.PATCH (no pre-release/build) —
// enough for the bump arithmetic; the release tool only ships concrete cores.
func parseSemverCore(v string) (maj, min, pat int, err error) {
	parts := strings.Split(v, ".")
	if len(parts) != 3 {
		return 0, 0, 0, fmt.Errorf("not MAJOR.MINOR.PATCH")
	}
	out := [3]int{}
	for i, p := range parts {
		n, e := strconv.Atoi(p)
		if e != nil || n < 0 {
			return 0, 0, 0, fmt.Errorf("non-numeric component %q", p)
		}
		out[i] = n
	}
	return out[0], out[1], out[2], nil
}

func repoRoot() (string, error) {
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "go.mod")); err == nil {
			return dir, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return "", fmt.Errorf("could not find go.mod (run from the repo)")
		}
		dir = parent
	}
}

func tagExists(root, tag string) (bool, error) {
	cmd := exec.Command("git", "tag", "--list", tag)
	cmd.Dir = root
	out, err := cmd.Output()
	if err != nil {
		return false, err
	}
	return strings.TrimSpace(string(out)) == tag, nil
}

func runCmd(dir, name string, args ...string) error {
	cmd := exec.Command(name, args...)
	cmd.Dir = dir
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}
