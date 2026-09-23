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

// skill_sync.go keeps an installed skill current without anyone having to
// remember to do it.
//
// A notice alone was not enough: the manual step documented in
// docs/installation.md went unperformed for four months on the author's own
// machine, because a stale skill produces no symptom a user can see. So the
// one case we can prove is safe — our own copy, unmodified, merely old — is
// repaired silently. Everything else is only reported: a file we cannot
// prove we wrote is the user's, not ours.
package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"
)

// skillNoticeInterval rate-limits the advisory for copies we will not touch.
// It fires after a command the user ran for another reason, so daily is
// plenty to be noticed without becoming background noise.
const skillNoticeInterval = 24 * time.Hour

// skillSyncDisabledEnv turns the automatic update off for users who would
// rather unity-cli never wrote into their Claude config directory.
const skillSyncDisabledEnv = "UNITY_CLI_NO_SKILL_SYNC"

type skillSyncOutcome int

const (
	skillSyncNothing skillSyncOutcome = iota
	skillSyncUpdated
	skillSyncNotified
)

// syncSkill applies the policy for one installed copy and reports what it
// did. allowNotice carries the caller's rate-limit decision so this stays
// testable without a clock or a cache file.
func syncSkill(dir, version string, allowNotice bool, out io.Writer) skillSyncOutcome {
	switch inspectSkill(dir) {
	case skillStale:
		// Ours, untouched, out of date — the only case safe to repair
		// unasked, and the common one after an upgrade.
		if _, err := installSkill(dir, version, false); err != nil {
			return skillSyncNothing
		}
		_, _ = fmt.Fprintf(out, "\nunity-cli skill updated to %s (%s)\n", version, skillFilePath(dir))
		return skillSyncUpdated

	case skillModified:
		if !allowNotice {
			return skillSyncNothing
		}
		_, _ = fmt.Fprintf(out, "\nYour unity-cli skill is older than %s and has local edits, so it was left alone.\n"+
			"Run \"unity-cli skill install --force\" to replace it (a .bak is kept).\n", version)
		return skillSyncNotified

	case skillUnmanaged:
		if !allowNotice {
			return skillSyncNothing
		}
		_, _ = fmt.Fprintf(out, "\nYour unity-cli skill is older than %s. It was installed before unity-cli\n"+
			"tracked it, so it is not updated automatically.\n"+
			"Run \"unity-cli skill install\" to update it (a .bak is kept).\n", version)
		return skillSyncNotified
	}

	// Missing (never opted in), current, or a symlink someone else owns.
	return skillSyncNothing
}

// maybeSyncSkill is the entry point called after a command completes.
// It never fails a command and never blocks: no network is involved, and the
// work is a hash of a 12KB file.
func maybeSyncSkill() {
	if Version == "dev" {
		// A dev build's embedded skill is whatever is checked out; it has
		// no business rewriting the user's installed copy. Contributors use
		// `unity-cli skill install` explicitly.
		return
	}
	if os.Getenv(skillSyncDisabledEnv) != "" {
		return
	}
	// Same rule as the update notice: when stdout is piped the caller is
	// parsing output, and neither a message nor a surprise write belongs in
	// that run.
	if !stdoutIsTerminalFn() {
		return
	}

	dir, err := defaultSkillDir()
	if err != nil {
		return
	}

	cachePath := skillCacheFilePath()
	last := loadSkillNotice(cachePath)
	now := time.Now()
	allowNotice := now.Sub(last) >= skillNoticeInterval

	if syncSkill(dir, Version, allowNotice, os.Stderr) == skillSyncNotified {
		saveSkillNotice(cachePath, now)
	}
}

// skillNoticeCache remembers when the advisory last fired, so a copy we will
// not touch does not produce a message after every command.
type skillNoticeCache struct {
	NotifiedAt int64 `json:"notified_at"`
}

func skillCacheFilePath() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".unity-cli", "skill-check.json")
}

func loadSkillNotice(path string) time.Time {
	if path == "" {
		return time.Time{}
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return time.Time{}
	}
	var c skillNoticeCache
	if json.Unmarshal(data, &c) != nil {
		return time.Time{}
	}
	return time.Unix(c.NotifiedAt, 0)
}

func saveSkillNotice(path string, at time.Time) {
	if path == "" {
		return
	}
	_ = os.MkdirAll(filepath.Dir(path), 0o755)
	data, err := json.Marshal(skillNoticeCache{NotifiedAt: at.Unix()})
	if err != nil {
		return
	}
	_ = os.WriteFile(path, data, 0o644)
}
