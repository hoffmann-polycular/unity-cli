// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"fmt"
	"os"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// editorCmd controls Unity play mode and asset database.
// resolve is needed for waitForReady so compile polling can follow the current project instance.
func editorCmd(args []string, send sendFn, resolve instanceResolver) (*client.CommandResponse, error) {
	if len(args) == 0 {
		return nil, fmt.Errorf("usage: unity-cli editor <play|stop|pause|refresh>")
	}

	action := args[0]
	flags := parseSubFlags(args[1:])

	switch action {
	case "play":
		_, wait := flags["wait"]
		resp, err := send("manage_editor", map[string]interface{}{
			"action":              "play",
			"wait_for_completion": wait,
		})
		if !wait {
			return resp, err
		}
		return awaitPlayModeTransition(resp, err, resolve,
			map[string]bool{"playing": true, "paused": true},
			"Entered play mode (confirmed after domain reload).")

	case "stop":
		_, wait := flags["wait"]
		resp, err := send("manage_editor", map[string]interface{}{
			"action":              "stop",
			"wait_for_completion": wait,
		})
		if !wait {
			return resp, err
		}
		return awaitPlayModeTransition(resp, err, resolve,
			map[string]bool{"ready": true},
			"Exited play mode (confirmed after domain reload).")

	case "pause":
		return send("manage_editor", map[string]interface{}{"action": "pause"})

	case "refresh":
		_, compile := flags["compile"]
		_, force := flags["force"]
		params := map[string]interface{}{}
		if force {
			params["force"] = true
			params["mode"] = "force"
		}
		if compile {
			params["compile"] = "request"
			resp, err := send("refresh_unity", params)
			if err != nil {
				return nil, err
			}
			if !resp.Success {
				return resp, nil
			}
			hasErrors := waitForReady(resolve)
			if hasErrors {
				return nil, fmt.Errorf("compilation finished with errors (check unity-cli console)")
			}
			resp.Message = "Refresh and compilation completed."
			return resp, nil
		}
		return send("refresh_unity", params)

	default:
		return nil, fmt.Errorf("unknown editor action: %s\nAvailable: play, stop, pause, refresh", action)
	}
}

// awaitPlayModeTransition reconciles a --wait play/stop response with Unity's
// domain reload. When "Reload Domain" is enabled (the Editor default), entering
// or exiting play mode tears down the C# app domain — so the connector's
// wait_for_completion await and its HTTP response die with the domain and the
// send returns a "connection closed" error. That's expected, not a failure:
// we poll the heartbeat back through the reload until the editor reports the
// target state, and only surface the original error if it never gets there.
//
// When domain reload is disabled, no reload happens, the connector answers
// directly (sendErr == nil), and we return that response unchanged.
func awaitPlayModeTransition(
	resp *client.CommandResponse, sendErr error,
	resolve instanceResolver, want map[string]bool, confirmedMsg string,
) (*client.CommandResponse, error) {
	// The transition dropped the connection when either the send errored
	// outright or the client saw the connection close before a response body —
	// both happen when a domain reload tears down the connector mid-request.
	dropped := sendErr != nil || (resp != nil && resp.ConnectionClosed)
	if !dropped {
		// No reload (domain reload disabled) — the connector answered directly.
		return resp, sendErr
	}

	// Poll the heartbeat back through the reload. If the editor reaches the
	// target state it worked; otherwise (e.g. it actually died) time out and
	// surface the original error.
	fmt.Fprintln(os.Stderr, "Connection dropped (domain reload) — waiting for the editor to come back...")
	if waitForPlayState(resolve, want) {
		return &client.CommandResponse{Success: true, Message: confirmedMsg}, nil
	}
	if sendErr != nil {
		return nil, sendErr
	}
	return nil, fmt.Errorf("timed out waiting for the editor to reach play state after domain reload")
}
