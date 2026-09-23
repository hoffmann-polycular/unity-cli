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

// Package skill carries the Claude Code skill that teaches an agent how to
// drive unity-cli.
//
// The skill is compiled into the binary rather than fetched at install time.
// That makes the pairing structural: the guidance ships inside the artifact
// it documents, so a given unity-cli can only ever install the skill written
// for it. Every install path — the curl installer, the Nix flake, `go
// install`, a release asset — carries the binary, and therefore carries a
// version-matched skill, with no network fetch and no tag to get wrong.
//
// SKILL.md here is a generated mirror of .claude/skills/unity-cli/SKILL.md,
// which is the file a contributor (and Claude Code, inside this repo) edits.
// The embed directive cannot reach outside its own package directory,
// hence the copy. Regenerate it with `go run ./tools/skillsync`; TestEmbeddedSkillMatchesRepoCopy
// and the release gate fail when the two drift.
package skill

import (
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
)

//go:embed SKILL.md
var content []byte

// Content returns the embedded SKILL.md bytes.
func Content() []byte {
	out := make([]byte, len(content))
	copy(out, content)
	return out
}

// Sum returns the SHA-256 of the embedded skill, hex-encoded. It is the
// identity used to tell an installed copy apart from the current one — no
// version stamp inside the document, and no network call, are needed.
func Sum() string {
	return SumOf(content)
}

// SumOf returns the hex-encoded SHA-256 of data, so callers can hash an
// installed file with the same function that hashes the embedded one.
func SumOf(data []byte) string {
	h := sha256.Sum256(data)
	return hex.EncodeToString(h[:])
}
