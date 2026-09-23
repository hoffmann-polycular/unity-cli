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
//
// COMMERCIAL LICENSE NOTICE:
// If you wish to use this code inside a non-GPL, proprietary software product,
// you must instead acquire a commercial license from the copyright holder.
//
// Contact: info@polycular.com | Website: https://www.polycular.com/

// skill.go installs and inspects the Claude Code skill embedded in this
// binary (internal/skill).
//
// The skill is a document Claude reads, so a stale copy fails silently: the
// agent is simply taught an older unity-cli, and nothing in any command's
// output says so. Detection therefore cannot rely on the user noticing.
// Every installed copy is paired with a marker recording the hash we wrote,
// which is what lets a later run tell "old but untouched" (safe to replace)
// apart from "the user edited this" (never replace without being asked).
package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/cli/exit"
	"github.com/hoffmann-polycular/unity-cli/internal/skill"
)

// skillState classifies an installed copy against the embedded one.
type skillState int

const (
	// skillMissing — nothing installed. Never auto-install: not every user
	// wants the skill, and `--with-skill` is opt-in.
	skillMissing skillState = iota
	// skillSymlink — the file is a symlink, so something outside unity-cli
	// owns it (a contributor pointing it at a working copy, a dotfiles
	// repo). Writing through it would edit that target.
	skillSymlink
	// skillCurrent — byte-identical to the embedded skill.
	skillCurrent
	// skillStale — differs from embedded, but still matches the marker we
	// wrote, so it is our own older copy, untouched. Safe to replace.
	skillStale
	// skillModified — differs from embedded and from its marker: edited
	// since we wrote it. Only ever replaced with --force.
	skillModified
	// skillUnmanaged — differs from embedded with no marker at all: an
	// older installer's curl, or a hand-placed file. Provenance unknown, so
	// automatic action stays off; an explicit install replaces it (keeping
	// a .bak).
	skillUnmanaged
)

func (s skillState) String() string {
	switch s {
	case skillMissing:
		return "not installed"
	case skillSymlink:
		return "symlink (managed outside unity-cli)"
	case skillCurrent:
		return "up to date"
	case skillStale:
		return "out of date"
	case skillModified:
		return "locally modified"
	case skillUnmanaged:
		return "out of date (provenance unknown)"
	}
	return "unknown"
}

// skillMarker records what unity-cli last wrote, so a later run can tell its
// own untouched copy from one the user has edited.
type skillMarker struct {
	Version string `json:"version"`
	SHA256  string `json:"sha256"`
}

const skillMarkerName = ".unity-cli-skill.json"

// claudeConfigDir mirrors the installer scripts: CLAUDE_CONFIG_DIR when set,
// else ~/.claude. Keeping the two in step matters — a mismatch writes the
// skill where Claude Code never looks.
func claudeConfigDir() (string, error) {
	if dir := strings.TrimSpace(os.Getenv("CLAUDE_CONFIG_DIR")); dir != "" {
		return dir, nil
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return "", fmt.Errorf("cannot locate home directory: %w", err)
	}
	return filepath.Join(home, ".claude"), nil
}

// defaultSkillDir is where Claude Code loads a user-level skill from.
func defaultSkillDir() (string, error) {
	dir, err := claudeConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "skills", "unity-cli"), nil
}

func skillFilePath(dir string) string   { return filepath.Join(dir, "SKILL.md") }
func skillMarkerPath(dir string) string { return filepath.Join(dir, skillMarkerName) }

func readSkillMarker(dir string) *skillMarker {
	data, err := os.ReadFile(skillMarkerPath(dir))
	if err != nil {
		return nil
	}
	var m skillMarker
	if json.Unmarshal(data, &m) != nil {
		return nil
	}
	return &m
}

func writeSkillMarker(dir, version, sum string) error {
	data, err := json.MarshalIndent(skillMarker{Version: version, SHA256: sum}, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(skillMarkerPath(dir), append(data, '\n'), 0o644)
}

// inspectSkill classifies the copy installed in dir. Any unreadable state is
// reported as skillMissing: this feeds an advisory path that must never turn
// a permissions quirk into a failed command.
func inspectSkill(dir string) skillState {
	path := skillFilePath(dir)

	// Lstat first — Stat would follow the link and we would overwrite its
	// target believing we owned the file.
	info, err := os.Lstat(path)
	if err != nil {
		return skillMissing
	}
	if info.Mode()&os.ModeSymlink != 0 {
		return skillSymlink
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return skillMissing
	}
	sum := skill.SumOf(data)
	if sum == skill.Sum() {
		return skillCurrent
	}

	marker := readSkillMarker(dir)
	switch {
	case marker == nil:
		return skillUnmanaged
	case marker.SHA256 == sum:
		return skillStale
	default:
		return skillModified
	}
}

// installSkill writes the embedded skill into dir and records the marker.
// It reports what it did, for callers that phrase their own message.
func installSkill(dir, version string, force bool) (string, error) {
	state := inspectSkill(dir)

	switch state {
	case skillCurrent:
		return "unchanged", nil
	case skillSymlink:
		if !force {
			target, _ := filepath.EvalSymlinks(skillFilePath(dir))
			return "", fmt.Errorf("%s is a symlink to %s — refusing to write through it (use --force to replace the link)",
				skillFilePath(dir), target)
		}
		// --force replaces the link itself, never its target.
		if err := os.Remove(skillFilePath(dir)); err != nil {
			return "", fmt.Errorf("cannot remove symlink: %w", err)
		}
	case skillModified:
		if !force {
			return "", fmt.Errorf("%s has local modifications — use --force to replace it (a .bak is kept)",
				skillFilePath(dir))
		}
	}

	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", fmt.Errorf("cannot create %s: %w", dir, err)
	}

	// Anything we did not write ourselves gets a backup before it goes.
	if state == skillModified || state == skillUnmanaged {
		if err := os.Rename(skillFilePath(dir), skillFilePath(dir)+".bak"); err != nil {
			return "", fmt.Errorf("cannot back up existing skill: %w", err)
		}
	}

	if err := os.WriteFile(skillFilePath(dir), skill.Content(), 0o644); err != nil {
		return "", fmt.Errorf("cannot write %s: %w", skillFilePath(dir), err)
	}
	if err := writeSkillMarker(dir, version, skill.Sum()); err != nil {
		// The skill is in place; only drift detection is degraded.
		fmt.Fprintf(os.Stderr, "Warning: skill installed but marker not written: %v\n", err)
	}

	if state == skillMissing {
		return "installed", nil
	}
	return "updated", nil
}

// skillCmd implements `unity-cli skill <install|status>`. It needs no Editor,
// so Execute dispatches it before instance discovery.
func skillCmd(args []string) error {
	sub := ""
	rest := args
	if len(args) > 0 && !strings.HasPrefix(args[0], "-") {
		sub, rest = args[0], args[1:]
	}

	flags := parseSubFlags(rest)
	_, force := flags["force"]

	dir, err := defaultSkillDir()
	if err != nil {
		return exit.Wrap(exit.Runtime, err)
	}
	if p, ok := flags["path"]; ok && p != "true" {
		dir = p
	}

	switch sub {
	case "", "status":
		return skillStatusCmd(dir)
	case "install", "sync", "update":
		action, err := installSkill(dir, Version, force)
		if err != nil {
			return exit.Wrap(exit.Runtime, err)
		}
		switch action {
		case "unchanged":
			fmt.Printf("Skill already up to date (%s)\n", skillFilePath(dir))
		default:
			fmt.Printf("Skill %s: %s\n", action, skillFilePath(dir))
		}
		return nil
	default:
		return exit.New(exit.Usage, "unknown skill subcommand %q (expected install or status)", sub)
	}
}

func skillStatusCmd(dir string) error {
	state := inspectSkill(dir)
	path := skillFilePath(dir)

	fmt.Printf("Skill:    %s\n", path)
	fmt.Printf("State:    %s\n", state)
	if state == skillSymlink {
		if target, err := filepath.EvalSymlinks(path); err == nil {
			fmt.Printf("Target:   %s\n", target)
		}
	}
	if m := readSkillMarker(dir); m != nil {
		fmt.Printf("Installed: %s\n", m.Version)
	}
	fmt.Printf("Embedded: %s (sha256 %s)\n", Version, skill.Sum()[:12])

	switch state {
	case skillMissing:
		fmt.Println("\nRun \"unity-cli skill install\" to install it.")
	case skillStale, skillUnmanaged:
		fmt.Println("\nRun \"unity-cli skill install\" to update it.")
	case skillModified:
		fmt.Println("\nRun \"unity-cli skill install --force\" to replace it (a .bak is kept).")
	case skillSymlink:
		fmt.Println("\nSomething outside unity-cli owns this file; it is left alone.")
	}
	return nil
}
