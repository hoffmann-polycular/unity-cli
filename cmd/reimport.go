package cmd

import (
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// reimportCmd forces Unity to re-run the import pipeline on one or more
// assets. Accepts positional paths, stdin paths (one per line), or a mix.
// Different from `reserialize` (which rewrites the file through Unity's
// YAML serializer) and from `editor refresh` (which is project-wide).
func reimportCmd(args []string, send sendFn) (*client.CommandResponse, error) {
	// Peel positionals out of args so we can fold stdin in cleanly when
	// no positionals were given. Flag args (like --recursive) pass through.
	var positionals []string
	var flagArgs []string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if len(a) > 1 && a[0] == '-' {
			flagArgs = append(flagArgs, a)
			continue
		}
		positionals = append(positionals, a)
	}

	if len(positionals) == 0 {
		// Stdin paths drive batch reimport — same convention as `rm`.
		stdinPaths := readStdinPaths()
		if len(stdinPaths) > 0 {
			positionals = stdinPaths
		}
	}

	combined := append(append([]string{}, flagArgs...), positionals...)
	params, err := buildParams(combined, nil)
	if err != nil {
		return nil, err
	}
	return send("reimport", params)
}
