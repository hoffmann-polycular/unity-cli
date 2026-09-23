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

// outputs.go implements post-write verification for commands that write a
// file to a caller-supplied path.
//
// The Editor is a separate process and does not necessarily share the
// caller's view of the filesystem: a Flatpak or Snap Unity Hub, a
// containerised or WSL shell driving a host Editor, and systemd's
// PrivateTmp all give the two sides different directories behind the same
// absolute path. A write then succeeds inside the Editor's filesystem and
// the command reports success naming a path where, for the caller, nothing
// exists. Only the client can notice this — the Editor's own File.Exists
// says True — so the check lives here.
package cmd

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// outputFlagNames lists the flag names through which a caller hands a
// command the path of a file to write, normalised by normalizeFlagName.
// Built-in `screenshot` uses -o / --output-path; project-registered
// [UnityCliTool]s choose their own spelling, so this covers the
// conventional ones rather than a fixed set of commands.
var outputFlagNames = map[string]bool{
	"o": true, "out": true, "output": true,
	"output_path": true, "output_file": true, "outfile": true,
	"file": true, "file_path": true, "save_path": true,
	"dest": true, "destination": true,
}

// outputDataKeys lists response-data keys through which a tool reports the
// path it actually wrote, preferred over the caller's spelling because the
// connector has already resolved it to an absolute path. Consulted only
// once an output flag has been seen on the command line: on its own, a
// `path` key overwhelmingly holds a scene or asset path, not a file on the
// caller's disk.
var outputDataKeys = []string{
	"path", "file", "file_path", "filePath",
	"output_path", "outputPath", "output",
	"saved_to", "savedTo", "written_to", "writtenTo",
}

// normalizeFlagName strips leading dashes and folds a flag name to the
// lower-case underscore spelling used by outputFlagNames (and by the wire
// params, which buildParams normalises the same way).
func normalizeFlagName(name string) string {
	return strings.ToLower(strings.ReplaceAll(strings.TrimLeft(name, "-"), "-", "_"))
}

// looksLikeOutputFile rejects values that are plainly not paths to a file,
// so that a non-path value under an output-ish flag (`--output json`) or a
// hierarchy path (`--file /World/Player`) never triggers a warning. An
// output file effectively always carries an extension; requiring one trades
// a few missed warnings for no false ones.
func looksLikeOutputFile(value string) bool {
	if value == "" || value == "true" || value == "false" {
		return false
	}
	if strings.ContainsAny(value, "\n\r") {
		return false
	}
	return filepath.Ext(value) != ""
}

// callerOutputPath returns the output-file path this invocation asked for,
// or "" when it named none. The first output flag in command-line order
// wins, so the result does not depend on map ordering.
func callerOutputPath(subArgs []string) string {
	_, flags := splitFlagsAndPositionals(subArgs)
	for i := 0; i < len(flags); i++ {
		if !outputFlagNames[normalizeFlagName(flags[i])] {
			continue
		}
		// splitFlagsAndPositionals guarantees a flag's value never starts
		// with '-', so adjacency alone re-pairs name and value.
		if i+1 < len(flags) && !strings.HasPrefix(flags[i+1], "-") {
			if looksLikeOutputFile(flags[i+1]) {
				return flags[i+1]
			}
		}
	}
	return ""
}

// reportedOutputPath returns the path a successful response names as
// written, or "" when the response carries no such key.
func reportedOutputPath(resp *client.CommandResponse) string {
	if resp == nil || len(resp.Data) == 0 {
		return ""
	}
	var obj map[string]json.RawMessage
	if err := json.Unmarshal(resp.Data, &obj); err != nil {
		return ""
	}
	for _, key := range outputDataKeys {
		raw, ok := obj[key]
		if !ok {
			continue
		}
		var s string
		if json.Unmarshal(raw, &s) != nil {
			continue
		}
		if looksLikeOutputFile(s) {
			return s
		}
	}
	return ""
}

// resolveOutputCandidates expands one path into every location the caller
// could reasonably find it: itself when absolute, otherwise relative to the
// project root (how the connector resolves relative paths) and to the
// caller's working directory.
func resolveOutputCandidates(path, projectPath string) []string {
	if path == "" {
		return nil
	}
	if filepath.IsAbs(path) {
		return []string{filepath.Clean(path)}
	}
	var out []string
	if projectPath != "" {
		out = append(out, filepath.Join(projectPath, path))
	}
	if cwd, err := os.Getwd(); err == nil {
		out = append(out, filepath.Join(cwd, path))
	}
	return out
}

// verifyOutputFile returns a warning for the caller when a command reported
// success after being handed an output path, yet no file is visible at any
// location that path could mean. Returns "" when the invocation wrote no
// caller-named file, or when the file is there.
func verifyOutputFile(subArgs []string, resp *client.CommandResponse, projectPath string) string {
	if resp == nil || !resp.Success {
		return ""
	}
	asked := callerOutputPath(subArgs)
	if asked == "" {
		return ""
	}
	reported := reportedOutputPath(resp)

	candidates := append(
		resolveOutputCandidates(reported, projectPath),
		resolveOutputCandidates(asked, projectPath)...,
	)
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return ""
		}
	}

	shown := reported
	if shown == "" || !filepath.IsAbs(shown) {
		if len(candidates) > 0 {
			shown = candidates[0]
		} else {
			shown = asked
		}
	}

	var b strings.Builder
	fmt.Fprintf(&b, "Warning: %s was reported written, but no file exists there from here.\n", shown)
	b.WriteString("  The Editor is a separate process and may not share this shell's view of the\n")
	b.WriteString("  filesystem (a sandboxed Unity Hub, a container, or WSL — a private /tmp is the\n")
	b.WriteString("  usual cause), so the write can land in a different directory of the same name.\n")
	if projectPath != "" {
		fmt.Fprintf(&b, "  The project directory is the one location both sides agree on: re-run with a\n  path under %s.", projectPath)
	} else {
		b.WriteString("  The project directory is the one location both sides agree on: re-run with a\n  path under the project root.")
	}
	return b.String()
}

// warnOutputFile runs verifyOutputFile and prints any warning to stderr.
// Never changes the exit status: the command itself succeeded, and only the
// caller's ability to read the file back is in doubt.
func warnOutputFile(subArgs []string, resp *client.CommandResponse, projectPath string) {
	if w := verifyOutputFile(subArgs, resp, projectPath); w != "" {
		fmt.Fprintln(os.Stderr, w)
	}
}
