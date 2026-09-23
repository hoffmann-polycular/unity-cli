// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
// See /LICENSE_GPL for the full license text.

package skill

import (
	"bytes"
	"os"
	"path/filepath"
	"testing"
)

// repoSkillPath is the file a contributor edits, and the one Claude Code
// loads when working inside this repository.
const repoSkillPath = "../../.claude/skills/unity-cli/SKILL.md"

// TestEmbeddedSkillMatchesRepoCopy guards the one hazard embedding
// introduces: the mirror silently falling behind the file people actually
// edit. Shipping a binary whose embedded skill is older than the repo's
// would recreate exactly the drift embedding exists to prevent.
func TestEmbeddedSkillMatchesRepoCopy(t *testing.T) {
	want, err := os.ReadFile(filepath.FromSlash(repoSkillPath))
	if err != nil {
		t.Fatalf("cannot read %s: %v", repoSkillPath, err)
	}
	if !bytes.Equal(want, content) {
		t.Fatalf("embedded SKILL.md is out of sync with %s\n\trun: go run ./tools/skillsync", repoSkillPath)
	}
}

func TestSumIsStableAndContentIsCopied(t *testing.T) {
	if Sum() != SumOf(Content()) {
		t.Error("Sum() and SumOf(Content()) disagree")
	}

	// Content must hand out a copy: a caller mutating it would otherwise
	// corrupt every later install from the same process.
	c := Content()
	if len(c) == 0 {
		t.Fatal("embedded skill is empty")
	}
	c[0] = 'X'
	if Content()[0] == 'X' {
		t.Error("Content() exposed the embedded bytes instead of a copy")
	}
}
