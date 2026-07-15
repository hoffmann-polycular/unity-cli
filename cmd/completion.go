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

package cmd

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// completionCmd emits a shell completion script for the given shell.
//
// Usage:
//
//	unity-cli completion <bash|zsh|fish|powershell>
//
// The emitted script defines a completion function that calls back into
// `unity-cli __complete <N> <args...>` to get candidates at completion time.
func completionCmd(args []string) error {
	if len(args) == 0 {
		return fmt.Errorf("usage: unity-cli completion <bash|zsh|fish|powershell>")
	}
	switch strings.ToLower(args[0]) {
	case "bash":
		fmt.Print(bashCompletionScript)
	case "zsh":
		fmt.Print(zshCompletionScript)
	case "fish":
		fmt.Print(fishCompletionScript)
	case "powershell", "pwsh":
		fmt.Print(powershellCompletionScript)
	default:
		return fmt.Errorf("unknown shell %q (use bash, zsh, fish, or powershell)", args[0])
	}
	return nil
}

// completeDispatchCmd is the hidden command shells call to get candidates.
//
// Usage:
//
//	unity-cli __complete <wordIndex> <word0> <word1> ... <wordN>
//
// wordIndex is the index of the word being completed (0-based, after the
// program name). Output is one candidate per line; empty output = no
// completions. Errors are silenced (printed to stderr only) so the shell
// stays responsive.
func completeDispatchCmd(args []string) error {
	// Optional leading "--shell <name>": when present, each candidate is quoted
	// for that shell so values containing spaces or metacharacters insert as a
	// single token. Scripts for shells that auto-quote (zsh, fish) omit it.
	shell := ""
	if len(args) >= 2 && args[0] == "--shell" {
		shell = args[1]
		args = args[2:]
	}

	if len(args) < 1 {
		return nil
	}
	idx := 0
	if n, err := parseInt(args[0]); err == nil {
		idx = n
	}
	words := args[1:]

	// Pad words so words[idx] is always valid.
	for len(words) <= idx {
		words = append(words, "")
	}
	current := words[idx]

	candidates := computeCandidates(idx, words, current)
	for _, c := range candidates {
		fmt.Println(quoteForShell(shell, c))
	}
	return nil
}

// quoteForShell makes a completion candidate safe to insert as a single token
// in the named shell. This is the external-shell counterpart to the REPL's
// renderCompletionSuffix: each shell inserts text under its own grammar, so
// the escaping can't be shared with the REPL (or between shells) — but keeping
// it here in Go means one testable place instead of four shell dialects. The
// generated scripts select the shell via the `--shell` flag. zsh's compadd and
// fish's completion engine quote candidates themselves, so they (and the empty
// default) pass through unchanged.
func quoteForShell(shell, s string) string {
	switch shell {
	case "bash":
		return bashEscapeWord(s)
	case "powershell", "pwsh":
		return powershellQuoteWord(s)
	default:
		return s
	}
}

// bashEscapeWord backslash-escapes the characters that are special in an
// unquoted bash word — whitespace plus the syntax/expansion metacharacters —
// so an inserted COMPREPLY value round-trips to the literal path. Characters
// that are part of the path grammar ("/", ":", "[", "]") are left alone.
func bashEscapeWord(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch r {
		case ' ', '\t', '\n', '"', '\'', '\\', '$', '`', ';', '|', '&', '(', ')', '<', '>':
			b.WriteByte('\\')
		}
		b.WriteRune(r)
	}
	return b.String()
}

// powershellQuoteWord wraps a candidate in single quotes (doubling any embedded
// single quote) when it contains whitespace or a character PowerShell would
// treat as a token boundary; simple candidates are returned unquoted. Inside a
// single-quoted string everything but ' is literal.
func powershellQuoteWord(s string) string {
	if !strings.ContainsAny(s, " \t\n\"'`$;,|&(){}@#<>") {
		return s
	}
	return "'" + strings.ReplaceAll(s, "'", "''") + "'"
}

func parseInt(s string) (int, error) {
	n := 0
	if s == "" {
		return 0, fmt.Errorf("empty")
	}
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("not a number")
		}
		n = n*10 + int(r-'0')
	}
	return n, nil
}

// computeCandidates is the dispatcher. Pure-Go static completions are
// returned directly; dynamic completions call out to Unity via the
// `complete_path` tool.
func computeCandidates(idx int, words []string, current string) []string {
	if idx == 0 {
		return prefixFilter(topLevelCommands, current)
	}

	cmd := words[0]
	prev := ""
	if idx > 0 {
		prev = words[idx-1]
	}

	// --flag=value completion: split on first '='
	if strings.HasPrefix(current, "--") && strings.Contains(current, "=") {
		eq := strings.Index(current, "=")
		flagName := current[2:eq]
		value := current[eq+1:]
		vals := flagValueCandidates(cmd, flagName, value)
		out := make([]string, 0, len(vals))
		for _, v := range vals {
			out = append(out, "--"+flagName+"="+v)
		}
		return out
	}

	// Completing a flag value (previous word was --flag, current is the value)
	if strings.HasPrefix(prev, "--") {
		flagName := prev[2:]
		if vals := flagValueCandidates(cmd, flagName, current); vals != nil {
			return vals
		}
	}

	// Completing a flag itself
	if strings.HasPrefix(current, "--") {
		return prefixFilter(flagsForCommand(cmd, words), current)
	}

	// Subcommand position
	if idx == 1 {
		if subs, ok := subcommands[cmd]; ok {
			return prefixFilter(subs, current)
		}
	}

	// Positional path completion
	return positionalCandidates(cmd, idx, words, current)
}

// --- static completion tables ---

var topLevelCommands = []string{
	"editor", "test", "exec", "ls", "find", "inspect", "get", "set", "invoke",
	"component", "select", "create", "rm", "cp", "mv", "reorder",
	"prefab", "scene", "console", "menu", "screenshot", "reserialize", "reimport",
	"guid", "path",
	"profiler", "status", "list", "update", "init", "interactive",
	"version", "help", "completion",
}

var subcommands = map[string][]string{
	"editor":     {"play", "stop", "pause", "refresh"},
	"prefab":     {"status", "diff", "apply", "revert", "create", "unpack", "variant", "open", "close"},
	"scene":      {"list", "open", "close", "save", "reload", "set-active", "new", "dirty"},
	"component":  {"list", "add", "remove"},
	"profiler":   {"hierarchy", "enable", "disable", "status", "clear"},
	"completion": {"bash", "zsh", "fish", "powershell"},
	"help":       {"editor", "ls", "find", "inspect", "get", "set", "invoke", "component", "select", "create", "rm", "cp", "mv", "reorder", "prefab", "scene", "console", "menu", "exec", "screenshot", "reserialize", "profiler", "test", "status", "list", "update", "init", "interactive", "custom-tools", "setup"},
}

var primitiveTypes = []string{
	"Empty", "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad",
}

var commonComponents = []string{
	"Transform", "Rigidbody", "Rigidbody2D", "BoxCollider", "SphereCollider",
	"CapsuleCollider", "MeshCollider", "BoxCollider2D", "CircleCollider2D",
	"MeshRenderer", "MeshFilter", "SkinnedMeshRenderer", "Camera", "Light",
	"AudioSource", "AudioListener", "Animator", "Animation", "Canvas",
	"CanvasGroup", "RectTransform", "Image", "Text", "Button", "TextMeshProUGUI",
	"ParticleSystem", "TrailRenderer", "LineRenderer", "NavMeshAgent",
}

var commonAssetTypes = []string{
	"Prefab", "Material", "Texture2D", "Mesh", "AudioClip", "AnimationClip",
	"Animator", "Shader", "Scene", "ScriptableObject", "Sprite", "Font",
	"MonoScript",
}

// flagsForCommand returns the flags accepted by the given command (in the
// current word context). Includes global flags.
func flagsForCommand(cmd string, words []string) []string {
	flags := append([]string{}, globalFlags...)
	specific, ok := commandFlags[cmd]
	if ok {
		flags = append(flags, specific...)
	}
	// For commands with subcommands, also include the subcommand-specific flags.
	if len(words) >= 2 {
		if subFlags, ok := subcommandFlags[cmd+" "+words[1]]; ok {
			flags = append(flags, subFlags...)
		}
	}
	return flags
}

var globalFlags = []string{
	"--port", "--project", "--timeout", "--help",
}

var commandFlags = map[string][]string{
	"ls":         {"-R", "--recursive", "--components", "--json", "--plain", "--null-delimited"},
	"find":       {"--name", "--regex", "--component", "--missing", "--tag", "--layer", "--prefab", "--has-overrides", "--is-prefab-instance", "--exact-component", "--max-depth", "--active", "--inactive", "--type", "--label", "--area", "--json", "--plain", "--null-delimited"},
	"inspect":    {"--overrides-only", "--json", "--plain"},
	"get":        {"--source", "--json"},
	"set":        {"--all", "--value", "--params"},
	"invoke":     {"--json", "--params"},
	"select":     {"--get", "--add", "--clear", "--json"},
	"create":     {"--prefab"},
	"rm":         {},
	"scene":      {"--mode", "--save", "--discard", "--as", "--json", "--plain"},
	"cp":         {"--depth", "--auto-suffix"},
	"mv":         {},
	"reorder":    {"--index", "--first", "--last", "--up", "--down", "--before", "--after"},
	"console":    {"--lines", "--type", "--stacktrace", "--clear"},
	"screenshot": {"--view", "--supersize", "--width", "--height", "--output-path", "-o"},
	"reimport":   {"--recursive"},
	"guid":       {"--json"},
	"path":       {"--json"},
	"test":       {"--mode", "--filter"},
	"exec":       {"--usings", "--csc", "--dotnet"},
	"init":       {"--local", "--upgrade", "--uninstall", "--wait"},
	"editor":     {"--wait", "--compile"},
	"update":     {"--check"},
}

var subcommandFlags = map[string][]string{
	"prefab unpack":      {"--completely"},
	"prefab close":       {"--discard"},
	"editor refresh":     {"--compile"},
	"editor play":        {"--wait"},
	"profiler hierarchy": {"--depth", "--root", "--frames", "--from", "--to", "--parent", "--min", "--sort", "--max", "--frame", "--thread"},
}

// flagValueCandidates returns completion candidates for known flag values.
// Returns nil when there's nothing to suggest (the shell will fall back to
// file completion).
func flagValueCandidates(cmd, flag, current string) []string {
	switch flag {
	case "view":
		return prefixFilter([]string{"scene", "game"}, current)
	case "mode":
		if cmd == "test" {
			return prefixFilter([]string{"EditMode", "PlayMode"}, current)
		}
		if cmd == "scene" {
			return prefixFilter([]string{"single", "additive", "additive-without-loading"}, current)
		}
	case "stacktrace":
		return prefixFilter([]string{"none", "user", "full"}, current)
	case "type":
		if cmd == "find" {
			return prefixFilter(commonAssetTypes, current)
		}
		if cmd == "console" {
			return prefixFilter([]string{"error", "warning", "log", "error,warning", "error,warning,log"}, current)
		}
	case "area":
		return prefixFilter([]string{"all", "assets", "packages"}, current)
	case "sort":
		return prefixFilter([]string{"total", "self", "calls"}, current)
	case "component", "missing":
		return prefixFilter(commonComponents, current)
	case "tag":
		return queryUnity("tag", current)
	case "layer":
		return queryUnity("layer", current)
	case "prefab":
		return queryUnity("asset", current)
	}
	return nil
}

// --- positional path completion ---

func positionalCandidates(cmd string, idx int, words []string, current string) []string {
	// positionals = words[1:idx+1] minus flags and their values
	positionals := collectPositionals(words[1:idx])
	posIdx := len(positionals) // index of the positional being completed

	switch cmd {
	case "ls", "inspect", "get", "rm", "select":
		return queryUnity("scene", current)
	case "guid", "reimport":
		// Both take asset paths.
		return queryUnity("asset", current)
	case "find":
		// Positional 0 narrows the search:
		//   "Assets/…" / "Packages/…" → asset-database scope
		//   "World/…" / etc.          → scene-hierarchy scope
		// Offer assets when the prefix looks like an asset path, scene
		// otherwise. Empty current word offers both via the union of
		// asset roots + scene roots.
		if posIdx == 0 {
			if strings.HasPrefix(current, "Assets") || strings.HasPrefix(current, "Packages") {
				return queryUnity("asset", current)
			}
			if current == "" {
				combined := append([]string{}, queryUnity("asset", "")...)
				combined = append(combined, queryUnity("scene", "")...)
				return combined
			}
			return queryUnity("scene", current)
		}
		return nil
	case "set":
		// First positional is path; second is value (no completion).
		if posIdx == 0 {
			return queryUnity("scene", current)
		}
	case "invoke":
		// First positional is path:Comp.Method; the rest are method args.
		if posIdx == 0 {
			return queryUnity("scene", current)
		}
	case "create":
		// create <type> <parentpath>/<name>  OR  create --prefab <asset> <parentpath>/<name>
		if posIdx == 0 {
			return prefixFilter(primitiveTypes, current)
		}
		if posIdx == 1 {
			return queryUnity("scene", current)
		}
	case "cp", "mv":
		if posIdx == 0 {
			return queryUnity("scene", current)
		}
		if posIdx == 1 {
			return queryUnity("scene-or-root", current)
		}
	case "reorder":
		if posIdx == 0 {
			return queryUnity("scene", current)
		}
	case "component":
		// component <list|add|remove> <path> [<type>]
		if posIdx == 0 { // path (after subcommand at words[1])
			return queryUnity("scene", current)
		}
		if posIdx == 1 && len(words) >= 2 && (words[1] == "add" || words[1] == "remove") {
			return prefixFilter(commonComponents, current)
		}
	case "prefab":
		sub := ""
		if len(words) >= 2 {
			sub = words[1]
		}
		switch sub {
		case "status", "diff", "apply", "revert", "unpack":
			if posIdx == 0 {
				return queryUnity("scene", current)
			}
		case "create":
			if posIdx == 0 {
				return queryUnity("scene", current)
			}
			if posIdx == 1 {
				return queryUnity("asset", current)
			}
		case "variant":
			return queryUnity("asset", current)
		case "open":
			return queryUnity("asset", current)
		}
	case "reserialize":
		return queryUnity("asset", current)
	case "menu":
		// Menu paths could be completed, but we don't expose an endpoint yet.
	case "completion":
		if posIdx == 0 {
			return prefixFilter([]string{"bash", "zsh", "fish", "powershell"}, current)
		}
	case "help":
		if posIdx == 0 {
			return prefixFilter(subcommands["help"], current)
		}
	}
	return nil
}

// collectPositionals walks the args, skipping --flag values, returning
// positional words only.
func collectPositionals(args []string) []string {
	out := make([]string, 0, len(args))
	for i := 0; i < len(args); i++ {
		a := args[i]
		if strings.HasPrefix(a, "--") {
			// "--flag=value" is a single token; "--flag value" consumes the next.
			if strings.Contains(a, "=") {
				continue
			}
			// Boolean flags don't consume a next token. Heuristic: if next
			// token starts with --, treat current as boolean. Otherwise consume.
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				// Check known boolean flags to avoid eating positionals.
				if !isKnownBooleanFlag(a) {
					i++
				}
			}
			continue
		}
		// Skip leading subcommand for cmds with subcommands (handled by caller).
		out = append(out, a)
	}
	return out
}

// --- helpers ---

func prefixFilter(candidates []string, prefix string) []string {
	if prefix == "" {
		out := make([]string, len(candidates))
		copy(out, candidates)
		return out
	}
	out := make([]string, 0, len(candidates))
	for _, c := range candidates {
		if strings.HasPrefix(c, prefix) {
			out = append(out, c)
		}
	}
	return out
}

// queryUnity calls the Unity-side complete_path tool. Failure → empty list
// (so the shell stays responsive even when Unity is closed).
func queryUnity(kind, prefix string) []string {
	inst, err := client.DiscoverInstance("", 0)
	if err != nil {
		return nil
	}
	params := map[string]interface{}{
		"kind":   kind,
		"prefix": prefix,
	}
	// Short timeout: completion must not hang.
	resp, err := client.Send(inst, "complete_path", params, 1500)
	if err != nil || resp == nil || !resp.Success {
		return nil
	}
	var s string
	if err := json.Unmarshal(resp.Data, &s); err != nil {
		// Some response shapes wrap as object; try .data.
		var wrapper map[string]interface{}
		if json.Unmarshal(resp.Data, &wrapper) == nil {
			if v, ok := wrapper["data"].(string); ok {
				s = v
			}
		}
	}
	if s == "" {
		return nil
	}
	lines := strings.Split(s, "\n")
	out := make([]string, 0, len(lines))
	for _, ln := range lines {
		ln = strings.TrimRight(ln, "\r")
		if ln != "" {
			out = append(out, ln)
		}
	}
	return out
}

// --- shell scripts ---

const bashCompletionScript = `# unity-cli bash completion
# Install: source <(unity-cli completion bash)
#      or: unity-cli completion bash > /etc/bash_completion.d/unity-cli

_unity_cli_complete() {
    local cur cword words
    cur="${COMP_WORDS[COMP_CWORD]}"
    words=("${COMP_WORDS[@]}")
    cword=$COMP_CWORD
    local idx=$((cword - 1))
    local out line
    # --shell bash → candidates come back backslash-escaped, ready to insert.
    out=$(unity-cli __complete --shell bash "$idx" "${words[@]:1}" 2>/dev/null)
    COMPREPLY=()
    # Read line-by-line so candidates with spaces survive intact. The prefix
    # test compares against the (unescaped) typed word, which is the common
    # case — escaped chars in a candidate appear only after the typed prefix.
    while IFS= read -r line; do
        [[ -z "$line" ]] && continue
        if [[ "$line" == "$cur"* ]]; then
            COMPREPLY+=("$line")
        fi
    done <<< "$out"
    # Suppress trailing space when the only candidate ends with / or :
    if [[ ${#COMPREPLY[@]} -eq 1 ]]; then
        local last="${COMPREPLY[0]: -1}"
        if [[ "$last" == "/" || "$last" == ":" ]]; then
            compopt -o nospace 2>/dev/null
        fi
    fi
    return 0
}
complete -F _unity_cli_complete unity-cli
`

const zshCompletionScript = `#compdef unity-cli
# unity-cli zsh completion
# Install: source <(unity-cli completion zsh)
#      or: unity-cli completion zsh > "${fpath[1]}/_unity-cli"

_unity_cli() {
    local idx=$(( CURRENT - 2 ))
    if (( idx < 0 )); then idx=0; fi
    local -a candidates
    local out
    out=$(unity-cli __complete "$idx" "${words[@]:1}" 2>/dev/null)
    candidates=("${(@f)out}")
    if (( ${#candidates[@]} == 0 )); then
        _files
        return
    fi
    compadd -- "${candidates[@]}"
}
compdef _unity_cli unity-cli
`

const fishCompletionScript = `# unity-cli fish completion
# Install: unity-cli completion fish | source
#      or: unity-cli completion fish > ~/.config/fish/completions/unity-cli.fish

function __unity_cli_complete
    set -l tokens (commandline -opc) (commandline -ct)
    # Drop the program name; idx is index within the rest.
    set -l args $tokens[2..-1]
    set -l idx (math (count $args) - 1)
    if test $idx -lt 0
        set idx 0
    end
    unity-cli __complete $idx $args 2>/dev/null
end

complete -c unity-cli -f -a "(__unity_cli_complete)"
`

const powershellCompletionScript = `# unity-cli PowerShell completion
# Install: Add-Content $PROFILE 'unity-cli completion powershell | Out-String | Invoke-Expression'

Register-ArgumentCompleter -Native -CommandName unity-cli -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)

    $tokens = $commandAst.CommandElements | ForEach-Object { $_.ToString() }
    if ($tokens.Count -lt 1) { return }
    # Drop program name.
    $args = @($tokens | Select-Object -Skip 1)
    $idx = $args.Count - 1
    if ($idx -lt 0) { $idx = 0 }

    # --shell powershell → candidates come back single-quoted when they need
    # it (spaces/metacharacters), ready to use as the inserted CompletionText.
    $out = & unity-cli __complete --shell powershell $idx @args 2>$null
    if (-not $out) { return }
    $out -split '\r?\n' | Where-Object { $_ } | ForEach-Object {
        $insert = $_
        # Strip the wrapping single quotes (and un-double '') for a clean menu label.
        $display = $insert -replace "^'(.*)'$", '$1' -replace "''", "'"
        [System.Management.Automation.CompletionResult]::new(
            $insert, $display, 'ParameterValue', $display)
    }
}
`
