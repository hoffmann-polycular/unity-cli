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

// args.go is the single home for CLI input parsing: the boolean-flag
// registry, splitting args into positionals vs flags, and reading paths
// from stdin. Every command handler routes flag/positional parsing through
// the helpers here so that "does this flag consume the next token?" has one
// authoritative answer instead of being re-decided (and re-bugged) per
// command.

package cmd

import (
	"io"
	"os"
	"strings"
)

// knownBooleanFlags lists every user-facing flag that takes no value, so the
// parser knows not to greedily swallow the following token as the flag's
// value. This is the single source of truth consulted by both argument
// parsing (buildParams, splitFlagsAndPositionals) and shell completion.
//
// Flags are listed in their user-facing hyphenated form. buildParams
// normalises hyphens to underscores when building the wire params, so only
// the hyphen form needs to appear here.
var knownBooleanFlags = map[string]bool{
	"--recursive": true, "-R": true, "--components": true,
	"--json": true, "--plain": true, "--null-delimited": true,
	"--has-overrides": true, "--is-prefab-instance": true, "--exact-component": true, "--active": true, "--inactive": true,
	"--overrides-only": true, "--source": true, "--all": true,
	"--get": true, "--clear": true, "--wait": true, "--compile": true,
	"--check": true, "--completely": true, "--discard": true,
	"--auto-suffix": true, "--help": true,
	"--first": true, "--last": true,
	"--upgrade": true, "--uninstall": true,
	"--force": true, "--allow-dirty-scenes": true, "--auto-save-scenes": true,
	"--normalized": true, "--flip": true,
}

func isKnownBooleanFlag(flag string) bool {
	return knownBooleanFlags[flag]
}

// splitFlagsAndPositionals separates bare positional arguments from flags,
// keeping each value-taking flag paired with its value. A token starting
// with '-' (but not a lone "-") is treated as a flag; it consumes the
// following token as its value UNLESS the flag is a known boolean or the
// next token is itself a flag. This is the single boolean-aware splitter
// every command routes through, so that "--boolflag <positional>" never
// silently swallows the positional as a flag value.
func splitFlagsAndPositionals(args []string) (positionals, flags []string) {
	for i := 0; i < len(args); i++ {
		a := args[i]
		if len(a) > 1 && strings.HasPrefix(a, "-") {
			flags = append(flags, a)
			if !isKnownBooleanFlag(a) && i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				flags = append(flags, args[i+1])
				i++
			}
			continue
		}
		positionals = append(positionals, a)
	}
	return positionals, flags
}

// flagsToMap folds a flag slice produced by splitFlagsAndPositionals into a
// name→value map. Boolean flags (and any flag not immediately followed by a
// value) map to "true". Flag names are returned without their leading
// dashes. The boolean-vs-value decision was already made by the splitter;
// this only re-pairs by adjacency, relying on the invariant that a value
// never starts with '-'.
func flagsToMap(flags []string) map[string]string {
	m := map[string]string{}
	for i := 0; i < len(flags); i++ {
		key := strings.TrimLeft(flags[i], "-")
		if i+1 < len(flags) && !strings.HasPrefix(flags[i+1], "-") {
			m[key] = flags[i+1]
			i++
		} else {
			m[key] = "true"
		}
	}
	return m
}

// translateJSONFlag rewrites a bare `--json` to `--format json` so it
// arrives at buildParams as a value flag (which buildParams understands).
// Used by command wrappers that otherwise pass args straight to buildParams.
func translateJSONFlag(args []string) []string {
	out := make([]string, 0, len(args)+1)
	for _, a := range args {
		if a == "--json" {
			out = append(out, "--format", "json")
			continue
		}
		out = append(out, a)
	}
	return out
}

// readStdinPaths reads newline-separated paths from stdin when piped,
// trimming whitespace and dropping blank lines. Returns nil when stdin is a
// terminal or empty. This drives batch/fan-out mode across commands.
func readStdinPaths() []string {
	info, err := os.Stdin.Stat()
	if err != nil {
		return nil
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return nil
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil || len(data) == 0 {
		return nil
	}
	text := strings.TrimRight(string(data), "\r\n")
	var paths []string
	for _, line := range strings.Split(text, "\n") {
		line = strings.TrimSpace(line)
		if line != "" {
			paths = append(paths, line)
		}
	}
	return paths
}

// readStdinIfPiped reads stdin as a single blob when piped and prepends it as
// the first positional arg. Unlike readStdinPaths it does NOT split into
// lines — used by `exec`, where piped stdin is one (possibly multi-line)
// code snippet.
func readStdinIfPiped(args []string) []string {
	info, err := os.Stdin.Stat()
	if err != nil {
		return args
	}
	if info.Mode()&os.ModeCharDevice != 0 {
		return args // interactive terminal, not piped
	}
	data, err := io.ReadAll(os.Stdin)
	if err != nil || len(data) == 0 {
		return args
	}
	code := strings.TrimRight(string(data), "\n\r")
	return append([]string{code}, args...)
}
