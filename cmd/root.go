// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026 Tobias Hoffmann Polycular GmbH
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
// 
// THIRD-PARTY NOTICE:
// This file contains code originally derived from unity-cli/youngwoocho02 DevBookOfArray,
// used under the terms of the MIT License. 
// The MIT permission notice applies strictly to those original portions. 
// 
// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package cmd

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/cli/exit"
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

var Version = "dev"

var (
	flagPort                  int
	flagProject               string
	flagTimeout               int
	flagIgnoreVersionMismatch bool
)

func Execute() error {
	flag.IntVar(&flagPort, "port", 0, "Select Unity instance by active heartbeat port")
	flag.StringVar(&flagProject, "project", "", "Select Unity instance by project path")
	flag.IntVar(&flagTimeout, "timeout", 120000, "Request timeout in milliseconds")
	flag.BoolVar(&flagIgnoreVersionMismatch, "ignore-version-mismatch", false, "Run even when CLI and connector versions differ")

	flag.Usage = func() { printHelp() }

	args := os.Args[1:]
	flagArgs, cmdArgs := splitArgs(args)
	if err := flag.CommandLine.Parse(flagArgs); err != nil {
		return exit.New(exit.Usage, "flag parse error: %v", err)
	}

	if len(cmdArgs) == 0 {
		printHelp()
		return nil
	}

	category := cmdArgs[0]
	subArgs := cmdArgs[1:]

	// --help / -h on any command
	for _, a := range subArgs {
		if a == "--help" || a == "-h" {
			printTopicHelp(category)
			return nil
		}
	}

	switch category {
	case "help", "--help", "-h":
		if len(subArgs) > 0 {
			printTopicHelp(subArgs[0])
		} else {
			printHelp()
		}
		return nil
	case "version", "--version", "-v":
		fmt.Println("unity-cli " + Version)
		return nil
	case "completion":
		return completionCmd(subArgs)
	case "__complete":
		return completeDispatchCmd(subArgs)
	case "update":
		return updateCmd(subArgs)
	case "status":
		inst, err := discoverStatusInstance(flagProject, flagPort)
		if err != nil {
			return exit.Wrap(exit.Unreach, err)
		}
		statusErr := statusCmd(inst)
		printUpdateNotice()
		return exit.Wrap(exit.Runtime, statusErr)
	}

	inst, err := client.DiscoverInstance(flagProject, flagPort)
	if err != nil {
		return exit.Wrap(exit.Unreach, err)
	}

	targetProject := flagProject
	if flagPort == 0 && targetProject == "" {
		targetProject = inst.ProjectPath
	}

	resolve := func() (*client.Instance, error) {
		if flagPort > 0 {
			return client.DiscoverInstance("", flagPort)
		}
		return client.DiscoverInstance(targetProject, 0)
	}

	alive, err := waitForAlive(resolve, flagTimeout)
	if err != nil {
		return exit.Wrap(exit.Unreach, err)
	}
	if err := checkConnectorVersion(alive, Version, flagIgnoreVersionMismatch); err != nil {
		return exit.Wrap(exit.Runtime, err)
	}

	timeout := flagTimeout
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		inst, err := resolve()
		if err != nil {
			return nil, exit.Wrap(exit.Unreach, err)
		}
		if err := checkConnectorVersion(inst, Version, flagIgnoreVersionMismatch); err != nil {
			return nil, exit.Wrap(exit.Runtime, err)
		}
		resp, err := client.Send(inst, command, params, timeout)
		if err != nil {
			return nil, exit.Wrap(exit.Unreach, err)
		}
		return resp, nil
	}

	var resp *client.CommandResponse

	switch category {
	case "editor":
		resp, err = editorCmd(subArgs, send, resolve)
	case "test":
		currentInst, resolveErr := resolve()
		if resolveErr != nil {
			return exit.Wrap(exit.Unreach, resolveErr)
		}
		if err := checkConnectorVersion(currentInst, Version, flagIgnoreVersionMismatch); err != nil {
			return exit.Wrap(exit.Runtime, err)
		}
		testSend := func(command string, params interface{}) (*client.CommandResponse, error) {
			r, sendErr := client.Send(currentInst, command, params, 0)
			if sendErr != nil {
				return nil, exit.Wrap(exit.Unreach, sendErr)
			}
			return r, nil
		}
		resp, err = testCmd(subArgs, testSend, currentInst.Port)
	case "exec":
		subArgs = readStdinIfPiped(subArgs)
		var params map[string]interface{}
		params, err = buildParams(subArgs, nil)
		if err == nil {
			resp, err = send("exec", params)
		}
	case "ls":
		resp, err = lsCmd(subArgs, send)
	case "find":
		resp, err = findCmd(subArgs, send)
	case "inspect":
		resp, err = inspectCmd(subArgs, send)
	case "get":
		resp, err = getCmd(subArgs, send)
	case "set":
		resp, err = setCmd(subArgs, send)
	case "component":
		resp, err = componentCmd(subArgs, send)
	case "select":
		resp, err = selectCmd(subArgs, send)
	case "create":
		resp, err = createCmd(subArgs, send)
	case "rm":
		resp, err = rmCmd(subArgs, send)
	case "cp":
		resp, err = cpCmd(subArgs, send)
	case "mv":
		resp, err = mvCmd(subArgs, send)
	case "reorder":
		resp, err = reorderCmd(subArgs, send)
	case "prefab":
		resp, err = prefabCmd(subArgs, send)
	case "scene":
		resp, err = sceneCmd(subArgs, send)
	case "screenshot":
		resp, err = screenshotCmd(subArgs, send)
	case "reimport":
		resp, err = reimportCmd(subArgs, send)
	case "guid":
		resp, err = guidCmd(subArgs, send)
	case "path":
		resp, err = pathCmd(subArgs, send)
	default:
		var params map[string]interface{}
		params, err = buildParams(subArgs, nil)
		if err == nil {
			resp, err = send(category, params)
		}
	}

	if err != nil {
		return err
	}

	printResponse(resp)

	printUpdateNotice()

	if !resp.Success {
		code := exit.FromKind(resp.ErrorKind)
		if resp.ErrorKind == "" {
			code = exit.ClassifyMessage(resp.Message)
		}
		// printResponse already wrote the error to stderr; emit a code-only
		// CLIError so main() does not duplicate it.
		return &exit.CLIError{Code: code, Msg: ""}
	}

	// Per §4.6: multi-target operations with mixed success/failure print
	// successful values to stdout and per-target errors to stderr, then
	// exit non-zero.
	if resp.PartialFailure {
		if resp.Stderr != "" {
			fmt.Fprintln(os.Stderr, resp.Stderr)
		}
		return &exit.CLIError{Code: exit.Runtime, Msg: ""}
	}

	return nil
}

// sendFn is the function signature for sending a command to Unity.
// Injected into each command function so they can be tested without a real Unity connection.
type sendFn func(command string, params interface{}) (*client.CommandResponse, error)

func printResponse(resp *client.CommandResponse) {
	if !resp.Success {
		msg := resp.Message
		if msg == "" {
			msg = "unknown error"
		}
		if len(resp.Data) > 0 && string(resp.Data) != "null" {
			fmt.Fprintf(os.Stderr, "Error: %s\nDetails: %s\n", msg, string(resp.Data))
		} else {
			fmt.Fprintf(os.Stderr, "Error: %s\n", msg)
		}
		return
	}

	if len(resp.Data) > 0 && string(resp.Data) != "null" {
		var pretty interface{}
		if json.Unmarshal(resp.Data, &pretty) == nil {
			// If data is a plain string, print it raw (preserves newlines for tree output etc.)
			if s, ok := pretty.(string); ok {
				fmt.Println(s)
			} else {
				b, _ := json.MarshalIndent(pretty, "", "  ")
				fmt.Println(string(b))
			}
		} else {
			fmt.Println(string(resp.Data))
		}
	} else if resp.Message != "" {
		fmt.Println(resp.Message)
	}
}

// parseSubFlags parses --key value and --flag (boolean) pairs from subcommand args.
// Non-flag args (no "--" prefix) are silently ignored.
func parseSubFlags(args []string) map[string]string {
	flags := map[string]string{}
	for i := 0; i < len(args); i++ {
		a := args[i]
		if strings.HasPrefix(a, "--") {
			key := a[2:]
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				flags[key] = args[i+1]
				i++
			} else {
				flags[key] = "true"
			}
		}
	}
	return flags
}

// buildParams parses --flag value pairs and positional args from args and merges with base params.
func buildParams(args []string, base map[string]interface{}) (map[string]interface{}, error) {
	params := map[string]interface{}{}
	for k, v := range base {
		params[k] = v
	}

	var positional []string
	flags := map[string]string{}
	for i := 0; i < len(args); i++ {
		a := args[i]
		if strings.HasPrefix(a, "--") {
			key := a[2:]
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "--") {
				flags[key] = args[i+1]
				i++
			} else {
				flags[key] = "true"
			}
		} else {
			positional = append(positional, a)
		}
	}

	if raw, ok := flags["params"]; ok {
		if jsonErr := json.Unmarshal([]byte(raw), &params); jsonErr != nil {
			return nil, fmt.Errorf("invalid JSON in --params: %w", jsonErr)
		}
	}
	for k, v := range flags {
		if k == "params" {
			continue
		}
		if _, exists := params[k]; exists {
			continue
		}
		if n, err := strconv.Atoi(v); err == nil {
			params[k] = n
		} else if v == "true" {
			params[k] = true
		} else if v == "false" {
			params[k] = false
		} else {
			params[k] = v
		}
	}

	if len(positional) > 0 {
		params["args"] = positional
	}

	return params, nil
}

// readStdinIfPiped reads stdin when piped and prepends it as the first positional arg.
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

// splitArgs separates global flags from subcommand args.
// Global flags must be parsed by flag.CommandLine before the subcommand runs.
func splitArgs(args []string) (flags, commands []string) {
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--ignore-version-mismatch":
			flags = append(flags, args[i])
		case "--port", "--project", "--timeout":
			flags = append(flags, args[i])
			if i+1 < len(args) {
				i++
				flags = append(flags, args[i])
			}
		default:
			commands = append(commands, args[i])
		}
	}
	return
}

func printHelp() {
	fmt.Print(`unity-cli ` + Version + ` — Control Unity Editor from the command line

Usage:  unity-cli <command> [args...]
        unity-cli help <command>      detailed help for one command

Path grammar
  bare | ./X        child of each selected object
  .                 the selection itself
  ../X              one level up from selection
  /World/Player     absolute Hierarchy path (across every loaded scene)
  Assets/Foo.prefab        asset on disk
  Assets/Foo.prefab//Hat   sub-object inside the asset
  ProjectSettings/Physics.gravity      project setting
  :GameObject.name | :Importer.x       virtual components on a target
  #14352                   pinned instance ID

Selection is the working directory. Paths fan out across multi-selection.

Scene navigation
  ls [-R] [path]              list Hierarchy children (or scene roots)
  find [path] [filters]       search loaded scenes or the asset database
  inspect <path>              dump GameObject / Component / property
  select [<path>...]          set / read / clear the Editor's Selection

Properties
  get <path>:Comp.prop        read one property value
  set <path>:Comp.prop <v>    write one property value (registers Undo)
  find ... | get :Comp.prop   broadcast read across stdin paths
  find ... | set :Comp.prop v broadcast write (one Undo group)

Hierarchy mutation
  create <type> <p>/<n>       create empty/primitive (or --prefab <asset>)
  create <type> /<n>          create at scene root
  rm <path>                   destroy a GameObject and its children
  cp <src> <dst>              copy a subtree (--depth N, --auto-suffix)
  mv <src> <dst>              reparent and/or rename
  reorder <path> --up|--down|--index|--first|--last|--before|--after <n>
  component list|add|remove <path> [<type>]

Prefabs
  prefab status|diff|apply|revert|create|unpack|variant|open|close

Scenes (load / save / activate)
  scene list|open|close|save|reload|set-active|new|dirty

Assets
  find Assets/<glob>          search the asset database
  guid <assetpath>...         asset path → GUID
  path <guid>...              GUID → asset path
  reimport <path> [--recursive]   re-run the import pipeline

Editor
  editor play|stop|pause|refresh [--wait|--compile]
  console [--lines N] [--type ...] [--stacktrace ...] [--clear]
  menu "<path>"               execute a Unity menu item
  screenshot [--view scene|game] [--width N] [--height N] [-o file]
  reserialize [path...]       force YAML reserialization
  profiler hierarchy|enable|disable|status|clear
  exec "<C# code>"            arbitrary C# (return for output)

Diagnostics
  status                      show Unity state
  test [--mode EditMode|PlayMode] [--filter ...]
  list                        list every registered tool

Tooling
  completion bash|zsh|fish|powershell
  update [--check]
  help <command>              detailed reference for one command

Global flags
  --port <N>                  pick a Unity instance by heartbeat port
  --project <path>            pick a Unity instance by project path
  --timeout <ms>              request timeout (default 120000)
  --ignore-version-mismatch   skip CLI/connector version check

Notes
  - Multi-target paths fan out across the current selection. Stdin paths
    work for get, set, inspect, rm, select, component, prefab, reimport.
  - Default output: ls/find/get/cp/mv/create/component emit canonical
    paths (one per line); use --json for structured records.
  - Unity must be running with the Connector package installed.

Run 'unity-cli list' to see every registered tool (including custom ones).
`)
}

func printTopicHelp(topic string) {
	switch topic {
	case "editor":
		fmt.Print(`Usage: unity-cli editor <play|stop|pause|refresh> [options]

Subcommands:
  play [--wait]       Enter play mode
                      --wait blocks until Unity fully enters play mode.
                      Without --wait, returns immediately after requesting.
  stop                Exit play mode. No effect if not playing.
  pause               Toggle pause. Only works during play mode.
  refresh             Refresh AssetDatabase (reimport changed assets).
                      Blocked in play mode unless --force is set.
    --compile         Recompile scripts and wait until compilation finishes.
    --force           Allow refresh during play mode and force asset update.

Examples:
  unity-cli editor play --wait
  unity-cli editor stop
  unity-cli editor refresh --compile
  unity-cli editor refresh --force
`)
	case "find":
		fmt.Print(`Usage: unity-cli find [<path>] [filters] [output options]

Unified find command. Mode is determined by the first positional argument:

  find                   (no positional)        → search all loaded scenes
  find World/Enemies     (scene path)           → restrict to a subtree
  find Assets/...        (asset-database path)  → search asset database

--- Scene mode ---

Search GameObjects across loaded scenes. With a scene path positional, the
search is restricted to the descendants of that GameObject (the scope itself
is not returned). All filters AND-combine. --component and --missing may be
repeated.

Filters:
  --name <glob>           Name glob (e.g. "Enemy*", "Spawn?_*")
  --component <type>      Require a component of this type (may repeat)
  --missing <type>        Exclude objects that have this component (may repeat)
  --tag <tag>             Match only objects with this tag
  --layer <name>          Match only objects on this layer (layer name)
  --prefab <assetpath>    Match only prefab-instance roots of this asset
  --has-overrides         Only prefab instances with any override
  --is-prefab-instance    Only prefab-instance roots (any source asset)
  --exact-component       --component/--missing match the exact type only
                          (default: subclasses also match — e.g. --component
                          Renderer matches MeshRenderer)
  --max-depth N           Limit recursion: 1 = scope's immediate children
                          only; default unlimited (full subtree)
  --active                Only active-in-hierarchy objects
  --inactive              Only inactive-in-hierarchy objects

--- Asset mode ---

Search the project asset database. The first positional is the path scope:
  find Assets/              search all assets
  find Assets/Prefabs/      restrict to a subfolder
  find Assets/Prefabs/E*    path glob (wildcards trigger glob matching)
  find Packages/            search package assets

Asset filters:
  --name <pattern>        Name filter (partial match or glob with * / ?)
  --type <type>           Asset type (Material, Mesh, Prefab, Texture2D, …)
  --label <label>         Asset label (Unity's label system)
  --area <all|assets|packages>  Search area (default: all)

--- Output (both modes) ---
  --json                  Structured JSON (jq-friendly)
  --plain                 One path per line (xargs/grep-friendly)
  --null-delimited        \0-separated paths (xargs -0 for paths with spaces)

Examples:
  unity-cli find --name "Enemy*"
  unity-cli find World/Enemies --name "Boss*"
  unity-cli find World/UI --component Image
  unity-cli find --component MeshRenderer --missing Collider
  unity-cli find --component Rigidbody --component AudioSource
  unity-cli find --prefab Assets/Prefabs/Enemy.prefab --has-overrides
  unity-cli find --component Light --plain | xargs -I{} unity-cli inspect {}:Light

  unity-cli find Assets/
  unity-cli find Assets/Prefabs/ --type Prefab
  unity-cli find Assets/Prefabs/Enemy* --type Prefab
  unity-cli find Assets/ --name "Metal*" --type Material
  unity-cli find Assets/ --label Hero --plain
  unity-cli find Assets/ --type Prefab --plain | xargs -I{} unity-cli inspect {}
`)
	case "prefab":
		fmt.Print(`Usage: unity-cli prefab <subcommand> <args...>

Prefab lifecycle, override inspection, and authoring. All mutating
subcommands run in InteractionMode.AutomatedAction (no modal dialogs).

Subcommands:
  status <path>                 Show the prefab connection for a GameObject:
                                source asset path, asset type (Regular /
                                Variant / Model), instance status, override
                                counts (property mods, added / removed
                                components, added GameObjects), and the
                                nested-prefab chain.

  diff <path>                   Show the override delta between an instance
                                and its prefab asset, git-style:
                                  ~ <path>:<comp>.<prop>   <from> → <to>
                                  + <path>:<comp>          (added component)
                                  - <path>:<comp>          (removed component)
                                  + <child path>           (added GameObject)
                                Default-overridden properties (Transform
                                position, GameObject name, etc.) are filtered.

  apply <path>                  Push overrides from instance back to source.
  apply <path>:<comp>           Apply only this component's overrides.
  apply <path>:<comp>.<prop>    Apply only this property's override.

  revert <path>                 Discard overrides; pull source values onto
                                the instance.
  revert <path>:<comp>          Revert one component's overrides.
  revert <path>:<comp>.<prop>   Revert one property override.

  create <scenepath> <asset>    Save a scene GameObject as a new prefab
                                asset, then connect the scene object as an
                                instance of the new prefab. Asset path must
                                start with 'Assets/' and the destination
                                folder must already exist (".prefab"
                                extension is appended if missing).

  unpack <path>                 Break the prefab connection on an instance.
                                The objects stay in place but are no longer
                                tied to the source asset.
    --completely                Unpack all nested prefab layers, not just
                                the outermost. Without this, nested prefab
                                instances inside the unpacked root remain
                                connected.

  variant <source> <newasset>   Create a prefab variant of an existing
                                prefab asset. The variant inherits from
                                <source> and overrides apply on top.
                                <newasset> follows the same rules as
                                'create' (Assets/-rooted, folder must exist,
                                ".prefab" appended if missing).

  open <assetpath>              Enter prefab editing mode for the asset.
                                While the stage is open, ls / find /
                                inspect / etc. resolve paths under the
                                prefab root (just like the Hierarchy window
                                shows only the prefab's contents).

  close                         Exit prefab editing mode. Saves any
                                pending changes back to the source asset.
    --discard                   Exit without saving — discards in-stage
                                edits since the last save.

Options:
  --json                        Structured JSON output

Examples:
  unity-cli prefab status World/Enemy[0]
  unity-cli prefab diff World/Enemy[0]
  unity-cli prefab apply World/Enemy[0]:Rigidbody.mass
  unity-cli prefab apply World/Enemy[0]:Rigidbody
  unity-cli prefab revert World/Enemy[0]
  unity-cli prefab create World/Player Assets/Prefabs/Player.prefab
  unity-cli prefab unpack World/Enemy[0]
  unity-cli prefab unpack World/Boss --completely
  unity-cli prefab variant Assets/Prefabs/Enemy.prefab Assets/Prefabs/EnemyElite.prefab
  unity-cli prefab open Assets/Prefabs/Enemy.prefab
  unity-cli ls                                    # now lists prefab contents
  unity-cli set Enemy:Rigidbody.mass 5.0          # edit inside the prefab
  unity-cli prefab close                          # save and return to scene
  unity-cli prefab close --discard                # throw away changes
  unity-cli prefab diff World/Enemy[0] --json | jq '.entries[] | select(.op == "modify")'
  unity-cli find --has-overrides --plain | xargs -I{} unity-cli prefab revert {}

Notes:
  - apply / revert at the instance root act on the entire instance.
  - apply / revert with :Component target only that component's overrides.
  - apply / revert with :Component.prop target a single property — fails
    cleanly if the property has no override.
  - create uses PrefabUtility.SaveAsPrefabAssetAndConnect, so the scene
    GameObject becomes a connected instance of the new asset.
  - variant uses SaveAsPrefabAsset on a temporary connected instance, so
    the result is a true variant (not a copy).
  - open changes Editor state — subsequent unity-cli calls in the same
    Unity instance see the prefab stage. close restores the previous stage.
`)
	case "scene":
		fmt.Print(`Usage: unity-cli scene <subcommand> [args...]

Manage loaded scenes. Wraps EditorSceneManager: open, save, close, reload,
set-active, new, dirty. Identifier resolution is asset-path-first, name
fallback; ambiguous names fail loudly with candidates listed.

Subcommands:
  list                          List all loaded scenes. Active scene is
                                prefixed with '*'. Modified scenes are
                                marked '(modified)' in human output.

  open <assetpath>              Open a scene from disk. Default mode is
                                single (replaces all currently-loaded
                                scenes).
    --mode single               Replace all loaded scenes (default).
    --mode additive             Add to the loaded set.
    --mode additive-without-loading
                                Add the scene reference without loading
                                its contents (lightweight registration).

  close <pathOrName>            Close a loaded scene (removes it from the
                                Hierarchy). Fails on the last loaded scene
                                (Unity requires at least one).
    --save                      Save first if the scene is dirty.
    --discard                   Throw away unsaved changes silently.

  save [<pathOrName>]           Save a loaded scene to disk (defaults to
                                the active scene). Requires --as when the
                                scene has no asset path yet.
    --as <newassetpath>         Save to a different path ('Save As…').

  reload [<pathOrName>]         Discard live state and reopen from disk.
                                Refuses on unsaved changes unless --save
                                or --discard is given.
    --save                      Save unsaved changes before reloading.
    --discard                   Throw away unsaved changes silently.

  set-active <pathOrName>       Make a loaded scene the active scene.

  new                           Create a new untitled scene (replaces all
                                loaded scenes; refuses on dirty state).
    --as <assetpath>            Save the new scene to disk immediately.

  dirty [<pathOrName>]          Print 'true' / 'false' for the scene's
                                modified state.

Options:
  --json                        Structured JSON output
  --plain                       Pipe-friendly output (one path per line for list)

Examples:
  unity-cli scene list
  unity-cli scene open Assets/Scenes/Main.unity
  unity-cli scene open Assets/Scenes/UI.unity --mode additive
  unity-cli scene set-active Main
  unity-cli scene save
  unity-cli scene save UI --as Assets/Scenes/UI_backup.unity
  unity-cli scene close UI --save
  unity-cli scene close UI --discard
  unity-cli scene reload Main
  unity-cli scene new --as Assets/Scenes/Empty.unity
  unity-cli scene dirty
  unity-cli scene list --plain | head -1 | unity-cli scene set-active

Notes:
  - Scene name vs. asset path: both work as identifiers. Asset path is
    unambiguous; name fails on ambiguity (lists matching paths).
  - open(single), new, and reload guard against silent data loss — they
    refuse when any loaded scene is dirty. Save first.
  - close on the last loaded scene is refused by Unity.
  - SaveScene saves AssetDatabase entries; no extra refresh needed.
`)
	case "rm":
		fmt.Print(`Usage: unity-cli rm <path>
       echo <path> | unity-cli rm
       find ... --plain | unity-cli rm

Destroy GameObjects (and their children).

v3: fan-out is the default. A selection-anchored path with multi-selection
deletes every resolved object. All deletions are wrapped in a single Undo
group — one Ctrl-Z reverses the operation.

Modes:
  rm .                    Destroy the current selection (one or many).
  rm ./Temp               Destroy each selection's Temp child.
  rm /World/Enemies/Old   Destroy a single absolute path.
  rm (stdin)              Batch mode: read paths from stdin, delete each.

Examples:
  unity-cli rm .                                     # delete the selection
  unity-cli rm /World/Enemies/OldSpawn
  unity-cli find --name "Temp_*" --plain | unity-cli rm
  unity-cli ls /World/Enemies --plain | unity-cli rm

Notes:
  - Destroying a GameObject automatically destroys all its children.
  - Attempting to delete a non-existent path is an error (even in batch mode).
  - In batch mode and fan-out mode, failures on one path do not halt
    processing of others; the response reports both deleted and failed counts.
`)
	case "create":
		fmt.Print(`Usage: unity-cli create <type> <parentpath>/<name>
       unity-cli create --prefab <assetpath> <parentpath>/<name>

Create a new GameObject, primitive, or prefab instance. Returns the
canonical path of the created object, enabling pipes to set, component
add, select, or inspect.

All creations register with Undo so the Editor's undo stack records them.

Types (for non-prefab creation):
  Empty                       An empty GameObject (no components)
  Cube, Sphere, Capsule,
  Cylinder, Plane, Quad       Primitives (with mesh, collider, materials)

Prefabs:
  --prefab <assetpath>        Instantiate a prefab asset. Respects the
                              prefab hierarchy and override settings.

Path format:
  <parentpath>/<name>         Parent must exist. Name is the new object's
                              display name. Can be nested: World/Enemies/Foo
                              creates Foo as a child of Enemies.

Examples:
  unity-cli create Empty World/Enemies/SpawnPoint
  unity-cli create Cube World/Level/Platform
  unity-cli create Sphere World/Sky/Sun
  unity-cli create --prefab Assets/Prefabs/Enemy.prefab World/Enemies/Enemy_01
  unity-cli create Quad World/UI/Canvas/Background | unity-cli set --value "1 1 1"
  unity-cli create Empty World/Parent | xargs -I{} unity-cli component add {} Rigidbody

Notes:
  - Primitives created with GameObject.CreatePrimitive() include a default
    Collider and Material. Remove or modify as needed with 'component remove'.
  - Parent path must exist; create won't auto-create intermediate parents.
  - Object is created at the parent's position/rotation/scale.
`)
	case "cp":
		fmt.Print(`Usage: unity-cli cp <src> <dst> [--depth N] [--auto-suffix [format]]

Copy a GameObject to a new location in the hierarchy. Returns the canonical
path of the new object, ready to pipe into set / component add / select.

Destination forms:
  parent/name      Copy under <parent>, named <name>.
  parent/          Copy under <parent>, keep the source's own name.
  /name            Copy at the scene root (no parent), named <name>.
  /                Copy at the scene root, keep the source's own name.

Default: deep copy of the entire subtree, no name conflict handling
(Unity allows duplicate sibling names).

Options:
  --depth <N>            Descendant layers to include:
                           0  → object only, no children
                           1  → object + immediate children
                           N  → N levels deep
                         Omitted = full deep copy (Unity default).

  --auto-suffix          On a sibling-name collision, append a numeric
                         suffix using Unity's default format:
                           Player → Player (1) → Player (2) → …
                         No suffix is added when the desired name is free.

  --auto-suffix <format> Custom suffix format. Use {n} as the index
                         placeholder, e.g.:
                           "_{n}"  → Player_1, Player_2
                           ".{n}"  → Player.1, Player.2

Behavior:
  - Registers a single Undo entry; one Ctrl+Z reverses the whole copy.
  - Prefab connections are NOT preserved — the result is a standalone
    GameObject with no link to the source asset. Use 'prefab create' to
    derive a new prefab from a copy.

Examples:
  unity-cli cp World/Player World/Player2
  unity-cli cp World/Player World/Backup/
  unity-cli cp World/Player World/PlayerStub --depth 0
  unity-cli cp World/Boss World/BossEcho --depth 2
  unity-cli cp World/Enemy World/Wave/Enemy --auto-suffix
  unity-cli cp World/Enemy World/Wave/Enemy --auto-suffix "_{n}"
  unity-cli cp World/Player/Hat /Hat
  unity-cli cp World/Player /
  unity-cli cp World/Player World/Player2 | \
      xargs -I{} unity-cli set {}:Transform.position "0 0 5"
`)
	case "mv":
		fmt.Print(`Usage: unity-cli mv <src> <dst>

Move and/or rename a GameObject in one operation. Mirrors a Hierarchy drag
combined with F2 rename. Emits the canonical path of the moved object.

Destination forms:
  parent/name      Move under <parent>, rename to <name>.
  parent/          Move under <parent>, keep the current name.
  /name            Move to the scene root (no parent), rename to <name>.
  /                Move to the scene root, keep the current name.

A pure rename in place is 'mv A/X A/Y'. A non-root destination parent must
already exist. Moving an object into one of its own descendants is rejected.
The scene-root forms keep the object in its current scene.

Behavior:
  - Single Undo entry covers both reparent and rename.
  - Prefab instance connection is preserved (same as dragging in the
    Hierarchy window).

Examples:
  unity-cli mv World/Player World/Hero                       # rename in place
  unity-cli mv World/Enemies/Boss World/Bosses/              # reparent
  unity-cli mv World/Enemies/Boss World/Bosses/FinalBoss     # both
  unity-cli mv World/Player/Hat /Hat                         # promote to scene root
  unity-cli mv World/Player /                                # to scene root, keep name
  unity-cli find --name "Temp_*" --plain | \
      xargs -I{} unity-cli mv {} World/Trash/
`)
	case "reorder":
		fmt.Print(`Usage: unity-cli reorder <path> <op>

Reorder a GameObject among its siblings, or a Component on its GameObject.
Mode is chosen by the path:
  - Plain hierarchy path        → reorder among siblings (Transform sibling index)
  - Path with :Component suffix → reorder the component on its GameObject

Operations (mutually exclusive — pick exactly one):
  --index <N>            Absolute 0-based position. Clamped to valid range.
  --first                Move to first position.
  --last                 Move to last position.
  --up [N]               Shift up by N (default 1). Clamped to range.
  --down [N]             Shift down by N (default 1). Clamped to range.
  --before <name>        Insert immediately before the sibling/component
                         with this name (sibling GameObject name, or
                         component type name like "Rigidbody").
  --after <name>         Insert immediately after.

Behavior:
  - Sibling reorder uses Transform.SetSiblingIndex; one Undo entry.
  - Component reorder uses ComponentUtility.MoveComponentUp/Down — Unity's
    only public API for this — so absolute targets are reached by stepping.
  - Transform / RectTransform cannot be reordered (always at index 0).
  - For sibling-name collisions, --before/--after match the first sibling
    with that name. Use --index for full disambiguation.
  - Out-of-range targets are clamped, not errors. A no-op (already at the
    target index) returns success with status="noop".

Examples:
  unity-cli reorder UI/Button --first
  unity-cli reorder UI/Button --last
  unity-cli reorder UI/Button --index 2
  unity-cli reorder UI/Button --up
  unity-cli reorder UI/Button --down 3
  unity-cli reorder UI/Button --before Label
  unity-cli reorder UI/Button --after Background

  # Component reorder
  unity-cli reorder World/Player:Rigidbody --up
  unity-cli reorder World/Player:AudioSource --first
  unity-cli reorder World/Player:Collider --before Rigidbody
`)
	case "select":
		fmt.Print(`Usage: unity-cli select <path>...
       unity-cli select --get
       unity-cli select --add <path>...
       unity-cli select --clear
       echo <path> | unity-cli select

Bridge between the Editor's Selection (Hierarchy/Inspector highlight) and
the terminal. Enables Hierarchy-driven workflows and visual feedback from
CLI commands.

v3: each path positional resolves through the v3 path grammar (including
selection-relative anchors) and the union becomes the new selection.
'./Hat' with three Players selected selects all three Hat children.

Modes:
  select <path>               Set Editor selection to the resolved targets.
  select --get                Print all currently selected objects' paths
                              (one per line). Empty when nothing selected.
  select --add <path>         Add resolved targets to the current selection.
  select --clear              Deselect everything (clear selection).

Stdin piping works when no positional path is supplied:
  find --component Light --plain | head -1 | select

Examples:
  unity-cli select /World/Player
  unity-cli select ./Hat                              # fan-out: each selection's Hat
  unity-cli select --get
  unity-cli select --get | unity-cli inspect
  unity-cli select --add /World/Enemy
  unity-cli select --clear
  unity-cli find --component Light --plain | head -1 | unity-cli select

Notes:
  - Selection updates are reflected immediately in the Hierarchy window.
  - Piping works with find, ls, get, or any tool emitting canonical paths.
`)
	case "component":
		fmt.Print(`Usage: unity-cli component <list|add|remove> <path> [<type>]

Add, remove, or list Components on a GameObject. Mirrors the Inspector's
"Add Component" and context-menu Remove affordances — all writes register
with Undo and dirty the target.

Subcommands:
  list   <path>                 Print every component on the object.
                                Names get [n] suffixes only when the
                                same type appears more than once.
  add    <path> <type>          Add a component. Prints the canonical
                                "path:Type[n]" of the new instance, ready
                                to pipe into get / set / inspect.
  remove <path> <type>[<n>]     Remove a component. The [n] index is
                                required when the GameObject has more
                                than one component of that type.

Type resolution accepts simple names (Rigidbody), namespaced names
(UnityEngine.UI.Image), or user-script class names. Same lookup as
'find --component'.

Options:
  --json                        Structured JSON output

Examples:
  unity-cli component list World/Player
  unity-cli component add World/Player Rigidbody
  unity-cli component add World/Player AudioSource
  unity-cli component remove World/Player AudioSource[1]
  unity-cli component add World/Player Rigidbody | unity-cli set --value 5.0
  unity-cli find --name "Enemy*" --plain | xargs -I{} unity-cli component add {} AudioSource

Notes:
  - Transform / RectTransform cannot be removed (Unity refuses).
  - Adding a [DisallowMultipleComponent] type when one is already present
    fails loudly.
  - [RequireComponent] dependencies are auto-added by Unity on add, and
    block remove until the dependent is gone.
`)
	case "get":
		fmt.Print(`Usage: unity-cli get <path> [options]

Read a single serialized-property value. The path must include both a
component and a property, optionally drilling into sub-fields.

v3 fan-out: a selection-anchored path with multi-selection emits one line
per target, prefixed with the canonical path. Single-target retains the
scalar-only output.

Default output is pipe-friendly:
  - Scalars print raw                 (3.14, true, "hello")
  - Vectors / colors print components space-separated   (1 2 3)
  - Object references print canonical paths
  - Null references print "null"

So 'get | set' and 'get | inspect' chains work without quoting tricks.

Path (v3):
  :Transform.position                Selection's transform position
  :Rigidbody.mass                    Selection's Rigidbody mass (fan-out)
  /World/Player:Transform.position   Absolute path
  ./Hat:MeshRenderer.enabled         Selection's Hat child (fan-out)
  /World/Enemy[1]:Rigidbody.mass     Disambiguate duplicate siblings
  #14352:Transform.localScale        Resolve by instance ID
  ProjectSettings/Physics.gravity    Project setting

Options:
  --source                    Read the prefab source value instead of the
                              (possibly overridden) instance value
  --json                      Wrap value with path/component/type metadata

Examples:
  unity-cli get :Transform.position
  unity-cli get :Rigidbody.mass                    # fan-out: one per selected
  unity-cli get /World/Player:Transform.position
  unity-cli get ProjectSettings/Physics.gravity
  unity-cli get /World/A:Transform.position | unity-cli set /World/B:Transform.position
`)
	case "set":
		fmt.Print(`Usage: unity-cli set <path> <value>
       unity-cli set <path> --value <value>
       echo <value> | unity-cli set <path>

Write a single serialized-property value. Goes through SerializedObject,
so prefab overrides register and Undo works exactly like an Inspector edit.

v3: fan-out is the default. A multi-target path broadcasts the value to
every resolved object, all writes share one Undo group, and per-target
failures are reported but do not stop other writes.

The value can be supplied as a positional, --value, or piped via stdin.
Piped form is the target of 'get | set' round-trips.

Path (v3):
  Same grammar as 'get' / 'inspect'. Must include :Component.property,
  or be a 'ProjectSettings/Group.prop' path. Selection-anchored paths
  (bare, ./, ../) implicitly fan out across multi-selection.

Value (type-aware, permissive):
  Scalars       42       3.14       true       "hello"
  Vectors       "1 2 3"   "1,2,3"   "{"x":1,"y":2,"z":3}"   "[1,2,3]"
  Colors        "#ff0000"   "#ff0000ff"   "1 0 0 1"   "1,0,0,1"
  Quaternions   "0 90 0"  (Euler degrees, 3 components)
                "0 0.7 0 0.7"  (raw, 4 components)
  Enums         "Awake"   1
  Object refs   "Assets/Prefabs/Enemy.prefab"
                "#14352"
                "/World/Other/Target"   (Hierarchy path)
  Null          null   none   ""

JSON-shaped values go through --params:
  unity-cli set :Transform.position \
    --params '{"value":{"x":1,"y":2,"z":3}}'

Examples:
  unity-cli set :Transform.position.x 1.5
  unity-cli set :Transform.position "1 2 3"
  unity-cli set :Rigidbody.mass 5.0                   # fan-out across selection
  unity-cli set ./Hat:MeshRenderer.enabled false      # one per selected
  unity-cli set /World/Player:Renderer.material "Assets/Mats/Red.mat"
  unity-cli set /World/Enemy:AIScript.target /World/Player
  unity-cli set /World/Enemy:AIScript.target null
  unity-cli set ProjectSettings/Physics.gravity "0 -20 0"
  unity-cli get /World/A:Transform.position | unity-cli set /World/B:Transform.position

Notes:
  - Composite properties (Generic / ManagedReference) must be set via
    their leaf fields, not as a whole.
  - The target object is marked dirty automatically.
`)
	case "inspect":
		fmt.Print(`Usage: unity-cli inspect <path> [options]

Dump the Inspector view of whatever the path resolves to:
  - GameObject path        → object info + every component's properties
  - GameObject:Component   → that component's serialized properties
  - GameObject:Comp.prop   → a single property value (drills into sub-fields
                             with further .name segments, e.g. .position.x)
  - ProjectSettings/Group  → a Project Settings group's properties

Multi-target paths (e.g. selection-relative paths under multi-selection)
emit one block per target, in selection order.

Path grammar (v3 — full reference: unity-cli-path-contract-v3.md):
  .                           Selection itself
  :Component[.prop]           Component / property on the selection
  ./Hat                       Child Hat of each selected object (fan-out)
  ..                          Parent of selection
  /World/Player               Absolute Hierarchy path
  /World/Enemy[1]             Disambiguate duplicate sibling names
  #14352                      Unity instance ID
  Assets/Foo.prefab//Sub      Sub-object inside an asset
  ProjectSettings/Physics     Project Settings group

Options:
  --overrides-only            Only show values overridden from the prefab
  --json                      Structured JSON output

Examples:
  unity-cli inspect .                                 # the selection
  unity-cli inspect :Rigidbody                        # selection's Rigidbody
  unity-cli inspect /World/Player
  unity-cli inspect /World/Player:Transform.position
  unity-cli inspect ProjectSettings/Physics
  unity-cli inspect ProjectSettings/Physics.gravity
  unity-cli inspect Assets/Prefabs/Enemy.prefab//Weapon
`)
	case "ls":
		fmt.Print(`Usage: unity-cli ls [<path>] [options]

List the children of a GameObject. With no path, lists root objects across
all loaded scenes (matches the Hierarchy window).

Path grammar (v3 — full reference: unity-cli-path-contract-v3.md):
  (no path)                   Hierarchy roots
  .                           Selection itself (children of)
  ./X or X                    Selection's child X (fan-out across multi-selection)
  ..                          Parent of selection
  /World/Player               Absolute Hierarchy path
  Assets/Foo.prefab           Asset (sub-objects with //)
  #14352                      Unity instance ID

Options:
  -R, --recursive             Descend into descendants
  -c, --components            Include each object's component type list
  --json                      Structured JSON output (jq-friendly)
  --plain                     One canonical path per line (xargs/grep-friendly)
  --null-delimited            \0-separated paths (xargs -0 for paths with spaces)

Examples:
  unity-cli ls                                        # Hierarchy roots
  unity-cli ls .                                      # children of selection
  unity-cli ls /World/Player
  unity-cli ls -R /World --components
  unity-cli ls -R --plain | grep Enemy
`)
	case "console":
		fmt.Print(`Usage: unity-cli console [options]

Read Unity console log entries.

Options:
  --lines <N>          Limit to N entries
  --type <types>       Comma-separated log types: error, warning, log (default: error,warning,log)
  --stacktrace <mode>  none: first line only
                        user: with stack trace, internal frames filtered (default)
                        full: raw message including all frames
  --clear              Clear console

Examples:
  unity-cli console
  unity-cli console --lines 20 --type error,warning,log
  unity-cli console --stacktrace user
  unity-cli console --type error --stacktrace full
  unity-cli console --clear
`)
	case "exec":
		fmt.Print(`Usage: unity-cli exec "<code>" [options]

Execute C# code inside Unity Editor. Full access to UnityEngine,
UnityEditor, and all loaded assemblies.

Use 'return' to get output. Add --usings for types outside default namespaces.

Options:
  --usings <ns1,ns2>   Add extra using directives
  --csc <path>         Path to csc compiler (csc.dll or csc.exe). Auto-detected if omitted.
  --dotnet <path>      Path to dotnet runtime. Auto-detected if omitted.

Default usings: System, System.Collections.Generic, System.IO, System.Linq,
  System.Reflection, System.Threading.Tasks, UnityEngine,
  UnityEngine.SceneManagement, UnityEditor, UnityEditor.SceneManagement,
  UnityEditorInternal

Examples:
  unity-cli exec "return 1+1;"
  unity-cli exec "return Application.dataPath;"
  echo 'return EditorSceneManager.GetActiveScene().name;' | unity-cli exec
  echo 'Debug.Log("hello"); return null;' | unity-cli exec
  unity-cli exec "return World.All.Count;" --usings Unity.Entities

Stdin:
  Pipe code via stdin to avoid shell escaping issues.
  echo '<code>' | unity-cli exec [--usings ns1,ns2]

Notes:
  - Use 'return' for output, 'return null;' for void operations
`)
	case "menu":
		fmt.Print(`Usage: unity-cli menu "<path>"

Execute a Unity menu item by its path.

Examples:
  unity-cli menu "File/Save Project"
  unity-cli menu "Assets/Refresh"
  unity-cli menu "Window/General/Console"

Note: File/Quit is blocked for safety.
`)
	case "screenshot":
		fmt.Print(`Usage: unity-cli screenshot [options]

Capture a screenshot of the Unity editor.

Options:
  --view <mode>      scene (default), game
  --width <N>        Image width in pixels (default: 1920)
  --height <N>       Image height in pixels (default: 1080)
  --output-path <path>  Output path, absolute or relative to project root
  -o <path>             Short form of --output-path
                        (default: Screenshots/screenshot.png)

Examples:
  unity-cli screenshot
  unity-cli screenshot --view game
  unity-cli screenshot --view scene --width 3840 --height 2160
  unity-cli screenshot --output-path captures/my_scene.png
  unity-cli screenshot -o captures/my_scene.png
`)
	case "reimport":
		fmt.Print(`Usage: unity-cli reimport <path>...
       unity-cli reimport <folder> --recursive
       find ... --plain | unity-cli reimport

Force Unity to re-run the import pipeline (TextureImporter, ModelImporter,
AudioImporter, etc.) on one or more assets. Different from:
  - reserialize: rewrites the file through Unity's YAML serializer; does
                 NOT re-run the importer.
  - editor refresh: project-wide AssetDatabase refresh.

Common use cases:
  - After an external tool rewrites source files on disk and Unity hasn't
    noticed yet.
  - Recover from a partially-imported asset (corruption, interrupted import).
  - Note: 'set <asset>:Importer.* <v>' does NOT need a follow-up reimport —
    Unity re-imports automatically when the meta file changes.

Options:
  --recursive                    Walk into folders, reimport every asset.

Examples:
  unity-cli reimport Assets/Textures/Foo.png
  unity-cli reimport Assets/Textures/ --recursive
  unity-cli reimport Assets/Tex/A.png Assets/Tex/B.png Assets/Tex/C.png
  unity-cli find Assets/Sprites/ --type Texture2D --plain | unity-cli reimport

Notes:
  - The whole batch is wrapped in AssetDatabase.StartAssetEditing /
    StopAssetEditing so Unity defers the import work once and runs it in
    one pass — significantly faster than reimporting each file individually.
  - Reimport is not undoable. (The Importer property writes that triggered
    it are; the resulting reimport is not.)
`)
	case "guid":
		fmt.Print(`Usage: unity-cli guid <assetpath>...
       find Assets/... --plain | unity-cli guid

Translate an asset path to its GUID (a 32-char hex identifier Unity stores
in the asset's .meta file and references everywhere from scenes, prefabs,
and serialized fields).

Output:
  - Plain (default): one GUID per input line, in input order.
  - --json: array of {input, output} records (errors get an 'error' field).

Unresolvable inputs emit an empty line on stdout and a reason on stderr;
exit code is non-zero when any input failed.

Examples:
  unity-cli guid Assets/Prefabs/Player.prefab
  unity-cli guid Assets/Foo.png Assets/Bar.png
  unity-cli find Assets/Scenes/ --type Scene --plain | unity-cli guid
  unity-cli guid Assets/Foo.png --json | jq -r '.results[0].output'

See also: unity-cli path <guid>   (the inverse direction).
`)
	case "path":
		fmt.Print(`Usage: unity-cli path <guid>...
       unity-cli guid Assets/Foo.png | unity-cli path

Translate a GUID back to its asset path. Inverse of 'unity-cli guid'.

Output:
  - Plain (default): one asset path per input line, in input order.
  - --json: array of {input, output} records (errors get an 'error' field).

Inputs must be 32-char hex strings (lowercase or uppercase, no dashes —
Unity stores GUIDs as 32 contiguous hex chars in .meta files).

Examples:
  unity-cli path 1a2b3c4d5e6f7081020304050607080a
  unity-cli path $(unity-cli guid Assets/Foo.png)
  cat scene-references.txt | unity-cli path     # one GUID per line

See also: unity-cli guid <assetpath>   (the inverse direction).
`)
	case "reserialize":
		fmt.Print(`Usage: unity-cli reserialize [path...]

Force Unity to reserialize assets through its own YAML serializer.
Run after editing .prefab, .unity, .asset, or .mat files as text.
No arguments = reserialize the entire project.

Examples:
  unity-cli reserialize
  unity-cli reserialize Assets/Prefabs/Player.prefab
  unity-cli reserialize Assets/Scenes/Main.unity Assets/Scenes/Lobby.unity
`)
	case "profiler":
		fmt.Print(`Usage: unity-cli profiler <subcommand> [options]

Subcommands:
  hierarchy             Top-level profiler samples (last frame)
    --depth <N>         Recursive depth (0=unlimited, default: 1)
    --root <name>       Set root by name (substring match, searches full tree)
    --frames <N>        Average over last N frames (flat output, sorted by time)
    --from <N>          Start frame index for range average
    --to <N>            End frame index for range average
    --parent <ID>       Drill into item by ID
    --min <ms>          Filter items below threshold
    --sort <col>        Sort by: total (default), self, calls
    --max <N>           Max children per level (default: 30)
    --frame <N>         Specific frame index
    --thread <N>        Thread index (0=main)
  enable                Start profiler recording
  disable               Stop profiler recording
  status                Show profiler state
  clear                 Clear all captured frames

Examples:
  unity-cli profiler hierarchy --depth 3
  unity-cli profiler hierarchy --root SimulationSystem --depth 3
  unity-cli profiler hierarchy --frames 30 --min 0.5 --sort self
  unity-cli profiler enable
`)
	case "test":
		fmt.Print(`Usage: unity-cli test [options]

Run Unity tests via the Test Runner API.

Options:
  --mode <EditMode|PlayMode>    Test mode (default: EditMode)
  --filter <name>               Filter by namespace, class, or full test name
                                Must be the full path (e.g. MyNamespace.MyClass)
  --allow-dirty-scenes          Run even when open scenes have unsaved changes
  --auto-save-scenes            Save dirty open scenes before running tests

EditMode tests hold the connection open and return results directly.
PlayMode tests return immediately and poll a results file (domain reload safe).
By default, tests are blocked when any open scene has unsaved changes.

Requires the Unity Test Framework package (com.unity.test-framework).

Examples:
  unity-cli test
  unity-cli test --mode PlayMode
  unity-cli test --auto-save-scenes
  unity-cli test --filter MyNamespace.MyTests
  unity-cli test --mode EditMode --filter MyNamespace.MyTests.SpecificTest
`)
	case "list":
		fmt.Print(`Usage: unity-cli list

List all registered tools (built-in + custom) with parameter schemas.

Example:
  unity-cli list
`)
	case "status":
		fmt.Print(`Usage: unity-cli status

Show the current Unity Editor state: port, project path, version, PID.
Reports "not responding" if heartbeat is older than 3 seconds.

Example:
  unity-cli status
`)
	case "update":
		fmt.Print(`Usage: unity-cli update [options]

Update the CLI binary to the latest release from GitHub.

Options:
  --check              Check for updates without installing

Examples:
  unity-cli update
  unity-cli update --check
`)
	case "completion":
		fmt.Print(`Usage: unity-cli completion <bash|zsh|fish|powershell>

Print a shell completion script. Source it (or install it system-wide) to
enable tab-completion for unity-cli commands, flags, and live Unity paths.

Static completions (work without Unity running):
  - Top-level commands and subcommands
  - Flags per command
  - Known flag values (--mode, --view, --stacktrace, --area, --sort, ...)
  - Primitive types (create), common component types

Dynamic completions (require a running Unity instance):
  - GameObject hierarchy paths           "World/Pl<TAB>"
  - Component suffixes                   "World/Player:<TAB>"
  - Asset paths under Assets/, Packages/ "Assets/Prefabs/<TAB>"
  - Tags and layers                      "find --tag <TAB>"

If Unity is not running, dynamic completions silently return nothing — the
shell falls back to its default file completion. Static completions still
work.

Installation:

  # Bash (per-session)
  source <(unity-cli completion bash)

  # Bash (persistent)
  unity-cli completion bash >> ~/.bashrc

  # Zsh (per-session)
  source <(unity-cli completion zsh)

  # Zsh (persistent — drop into a directory in $fpath)
  unity-cli completion zsh > "${fpath[1]}/_unity-cli"

  # Fish (per-session)
  unity-cli completion fish | source

  # Fish (persistent)
  unity-cli completion fish > ~/.config/fish/completions/unity-cli.fish

  # PowerShell (persistent — add to $PROFILE)
  unity-cli completion powershell >> $PROFILE
  # Or per-session:
  unity-cli completion powershell | Out-String | Invoke-Expression

Examples (after installing):
  unity-cli ls World/<TAB>                    # children of World
  unity-cli set World/Player:<TAB>            # components on Player
  unity-cli cp World/Player /<TAB>            # scene-root candidates
  unity-cli find Assets/<TAB>                 # asset paths
  unity-cli find Assets/ --type <TAB>         # known asset types
  unity-cli prefab open Assets/<TAB>          # prefab assets

Notes:
  - Dynamic completion uses a 1.5s timeout; slow Unity recompiles can
    make completions appear empty until the Editor finishes work.
  - Object names with spaces work: shells quote them automatically.
`)
	case "custom-tools", "custom", "tools":
		fmt.Print(`How to write custom tools for unity-cli

Custom tools are C# classes that run inside Unity Editor. The CLI
discovers them automatically via reflection.

Create a static class with [UnityCliTool] in any Editor assembly:

    using UnityCliConnector;
    using Newtonsoft.Json.Linq;

    [UnityCliTool(Description = "Spawn an enemy at a position")]
    public static class SpawnEnemy
    {
        public class Parameters
        {
            [ToolParameter("X world position", Required = true)]
            public float X { get; set; }
        }

        public static object HandleCommand(JObject parameters)
        {
            float x = parameters["x"]?.Value<float>() ?? 0;
            var go = Object.Instantiate(prefab, new Vector3(x, 0, 0), Quaternion.identity);
            return new SuccessResponse("Spawned", new { name = go.name });
        }
    }

Rules:
  - Class must be static
  - Must have: public static object HandleCommand(JObject parameters)
  - Return SuccessResponse(message, data) or ErrorResponse(message)
  - Add Parameters class with [ToolParameter] for discoverability
  - Class name auto-converts to snake_case (SpawnEnemy → spawn_enemy)
  - Override name: [UnityCliTool(Name = "my_name")]
  - Runs on Unity main thread — all Unity APIs are safe
  - Discovered on Editor start and after every script recompilation
  - Duplicate tool names are detected and logged as errors (first wins)
`)
	case "setup", "install":
		fmt.Print(`Installation and Unity setup

CLI Installation:
  # Linux / macOS
  curl -fsSL https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.sh | sh

  # Windows (PowerShell)
  irm https://raw.githubusercontent.com/hoffmann-polycular/unity-cli/main/install.ps1 | iex

  # Go install (any platform)
  go install github.com/hoffmann-polycular/unity-cli@latest

Unity Setup:
  1. Window → Package Manager → + → Add package from git URL
  2. Paste: https://github.com/hoffmann-polycular/unity-cli.git?path=unity-connector
  The Connector starts automatically when Unity opens.

Verify:
  unity-cli list
`)
	default:
		fmt.Printf("Unknown help topic: %s\n\nUse \"unity-cli --help\" for available commands.\n", topic)
	}
}
