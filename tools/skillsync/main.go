// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// See /LICENSE_GPL for the full license text.

// Command skillsync copies the repository's Claude Code skill into
// internal/skill/, where go:embed can reach it.
//
// .claude/skills/unity-cli/SKILL.md is the file contributors edit — it is
// what Claude Code loads when working inside this repo. go:embed cannot
// reference a path outside its own package directory, so the embedded copy
// is generated from it rather than being a second source of truth.
//
//	go run ./tools/skillsync            # regenerate the mirror
//	go run ./tools/skillsync --check    # fail if it is out of date
package main

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
)

const (
	src = ".claude/skills/unity-cli/SKILL.md"
	dst = "internal/skill/SKILL.md"
)

func main() {
	check := len(os.Args) > 1 && os.Args[1] == "--check"

	want, err := os.ReadFile(filepath.FromSlash(src))
	if err != nil {
		fmt.Fprintf(os.Stderr, "skillsync: cannot read %s: %v\n", src, err)
		fmt.Fprintln(os.Stderr, "skillsync: run this from the repository root")
		os.Exit(1)
	}

	got, err := os.ReadFile(filepath.FromSlash(dst))
	if err == nil && bytes.Equal(got, want) {
		if !check {
			fmt.Printf("skillsync: %s already up to date\n", dst)
		}
		return
	}

	if check {
		fmt.Fprintf(os.Stderr, "skillsync: %s is out of date with %s\n", dst, src)
		fmt.Fprintln(os.Stderr, "skillsync: run `go run ./tools/skillsync` and commit the result")
		os.Exit(1)
	}

	if err := os.WriteFile(filepath.FromSlash(dst), want, 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "skillsync: cannot write %s: %v\n", dst, err)
		os.Exit(1)
	}
	fmt.Printf("skillsync: updated %s from %s\n", dst, src)
}
