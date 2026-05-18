// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package cmd

import (
	"fmt"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

// editorCmd controls Unity play mode and asset database.
// port is needed for waitForReady (refresh --compile blocks until compilation finishes).
func editorCmd(args []string, send sendFn, port int) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli editor <play|stop|pause|refresh>")
	}

	action := args[0]
	flags := parseSubFlags(args[1:])

	switch action {
	case "play":
		_, wait := flags["wait"]
		return send("manage_editor", map[string]interface{}{
			"action":              "play",
			"wait_for_completion": wait,
		})

	case "stop":
		return send("manage_editor", map[string]interface{}{"action": "stop"})

	case "pause":
		return send("manage_editor", map[string]interface{}{"action": "pause"})

	case "refresh":
		_, compile := flags["compile"]
		if compile {
			resp, err := send("refresh_unity", map[string]interface{}{
				"compile": "request",
			})
			if err != nil {
				return nil, err
			}
			hasErrors := waitForReady(port)
			if hasErrors {
				return nil, fmt.Errorf("compilation finished with errors (check unity-cli console)")
			}
			resp.Message = "Refresh and compilation completed."
			return resp, nil
		}
		return send("refresh_unity", map[string]interface{}{})

	default:
		return nil, fmt.Errorf("unknown editor action: %s\nAvailable: play, stop, pause, refresh", action)
	}
}
