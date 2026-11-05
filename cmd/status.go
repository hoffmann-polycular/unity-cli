// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/hoffmann-polycular/unity-cli/internal/cli/exit"
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

type instanceResolver func() (*client.Instance, error)

var statusPollInterval = 500 * time.Millisecond

func statusCmd(inst *client.Instance) error {
	status, err := readStatus(inst.Port)
	if err != nil {
		return fmt.Errorf("no status for port %d — Unity may not be running", inst.Port)
	}

	age := time.Since(time.UnixMilli(status.Timestamp))
	if age > 3*time.Second {
		fmt.Fprintf(os.Stderr, "Unity (port %d): not responding (last heartbeat %s ago)\n", status.Port, age.Truncate(time.Second))
		return checkConnectorVersion(status, Version, flagIgnoreVersionMismatch)
	}

	fmt.Printf("Unity (port %d): %s\n", status.Port, status.State)
	fmt.Printf("  Project: %s\n", status.ProjectPath)
	fmt.Printf("  Version: %s\n", status.UnityVersion)
	fmt.Printf("  Connector: %s\n", connectorVersionLabel(status.ConnectorVersion))
	fmt.Printf("  PID:     %d\n", status.PID)
	return checkConnectorVersion(status, Version, flagIgnoreVersionMismatch)
}

func discoverStatusInstance(project string, port int) (*client.Instance, error) {
	if port > 0 {
		return client.FindByPort(port)
	}
	return client.DiscoverInstance(project, 0)
}

// readStatus finds the instance file matching the given port (any state).
func readStatus(port int) (*client.Instance, error) {
	return client.FindByPort(port)
}

func connectorVersionLabel(version string) string {
	if strings.TrimSpace(version) == "" {
		return "unknown"
	}
	return version
}

func checkConnectorVersion(inst *client.Instance, cliVersion string, ignoreMismatch bool) error {
	if normalizeVersion(cliVersion) == "dev" {
		return nil
	}
	if ignoreMismatch {
		return nil
	}
	if inst == nil {
		return nil
	}

	connectorVersion := strings.TrimSpace(inst.ConnectorVersion)
	if connectorVersion == "" {
		return fmt.Errorf("connector version is unknown; update the Unity Connector package to match unity-cli %s, or rerun with --ignore-version-mismatch", cliVersion)
	}
	if normalizeVersion(connectorVersion) != normalizeVersion(cliVersion) {
		return fmt.Errorf("connector version mismatch: unity-cli %s, connector %s. Update both to the same release, or rerun with --ignore-version-mismatch", cliVersion, connectorVersion)
	}
	return nil
}

func normalizeVersion(version string) string {
	version = strings.TrimSpace(version)
	version = strings.TrimPrefix(version, "v")
	version = strings.TrimPrefix(version, "V")
	return version
}

// waitForAlive resolves the current target instance, then polls until a newer heartbeat appears.
// This keeps following the same project even if Unity rebinds to a new port during reload.
func waitForAlive(resolve instanceResolver, timeoutMs int) (*client.Instance, error) {
	baseline := time.Now().UnixMilli()
	inst, err := resolve()
	if err == nil {
		baseline = inst.Timestamp
		// Already fresh — check if timestamp was updated within the last second
		if time.Now().UnixMilli()-baseline < 1000 {
			return inst, nil
		}
	}

	fmt.Fprintf(os.Stderr, "Waiting for Unity...\n")

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
		inst, err = resolve()
		if err != nil {
			continue
		}
		if inst.Timestamp > baseline {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return inst, nil
		}
	}

	return nil, fmt.Errorf("timed out waiting for Unity")
}

// playModeWaitTimeout bounds how long we poll the heartbeat back through the
// domain reload that entering/exiting play mode triggers. A reload recompiles
// nothing new (scripts are already built) so it's normally seconds; the ceiling
// only guards a genuinely dead editor. Overridable in tests.
var playModeWaitTimeout = 2 * time.Minute

// playAbortSettle is how long the heartbeat must keep reporting an abort
// state before waitForPlayState believes it, bridging the moments around a
// domain reload where the state has not caught up yet. Overridable in tests.
var playAbortSettle = 3 * time.Second

type playStateOutcome int

const (
	playStateTimedOut playStateOutcome = iota
	playStateReached
	playStateAborted
)

// waitForPlayState polls the heartbeat until the editor reports one of the
// wanted states, tolerating the connection loss and instance-file churn of the
// domain reload that entering/exiting play mode causes. A heartbeat written
// after the wait began that keeps reporting an abort state (edit mode while
// entering play mode) for playAbortSettle means the transition was cancelled.
func waitForPlayState(resolve instanceResolver, want, abort map[string]bool) playStateOutcome {
	since := time.Now().UnixMilli()
	deadline := time.Now().Add(playModeWaitTimeout)
	var abortSeen time.Time
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
		status, err := resolve()
		if err != nil || status == nil {
			continue // file briefly stale/unreadable mid-reload — keep polling.
		}
		if want[status.State] {
			return playStateReached
		}
		if !abort[status.State] || status.Timestamp <= since {
			abortSeen = time.Time{}
			continue
		}
		if abortSeen.IsZero() {
			abortSeen = time.Now()
		} else if time.Since(abortSeen) >= playAbortSettle {
			return playStateAborted
		}
	}
	return playStateTimedOut
}

// reloadWaitTimeout bounds how long sendThroughReload waits for a connector
// that refuses connections. Overridable in tests.
var reloadWaitTimeout = 2 * time.Minute

// reloadsOnPurpose lists the commands whose own domain reload (play/stop,
// refresh --compile) may cut their reply off; their callers handle that.
var reloadsOnPurpose = map[string]bool{"manage_editor": true, "refresh_unity": true}

// sendThroughReload sends a command and, when the connector refuses the
// connection, waits for it to come back and resends. A domain reload
// (entering play mode, a recompile) takes the HTTP server down while the
// heartbeat can still look fresh, so the command right after `editor play`
// would otherwise fail with a raw dial error. A refused dial never reached
// the editor, so resending cannot run the command twice. A connection closed
// mid-request is an error instead: that command may already have run.
func sendThroughReload(resolve instanceResolver, ignoreVersion bool, command string, params interface{}, timeoutMs int) (*client.CommandResponse, error) {
	inst, err := resolve()
	if err != nil {
		return nil, exit.Wrap(exit.Unreach, err)
	}
	waited := false
	deadline := time.Now().Add(reloadWaitTimeout)
	for {
		if err := checkConnectorVersion(inst, Version, ignoreVersion); err != nil {
			return nil, exit.Wrap(exit.Runtime, err)
		}
		resp, err := client.Send(inst, command, params, timeoutMs)
		if err == nil && resp.ConnectionClosed && !reloadsOnPurpose[command] {
			// Elsewhere the reload may have struck before or after the command
			// ran, so resending could run it twice.
			return nil, exit.Wrap(exit.Unreach, fmt.Errorf(
				"the connection closed before Unity answered %q, most likely for a domain reload (entering play mode, a recompile); it may or may not have run", command))
		}
		if err == nil {
			return resp, nil
		}
		if !client.IsDialError(err) || time.Now().After(deadline) {
			return nil, exit.Wrap(exit.Unreach, err)
		}
		if !waited {
			fmt.Fprintln(os.Stderr, "Unity is not accepting connections (domain reload?) — waiting for it to come back...")
			waited = true
		}
		time.Sleep(statusPollInterval)
		if next, resolveErr := resolve(); resolveErr == nil {
			inst = next
		}
	}
}

// waitForReady polls indefinitely until the heartbeat state becomes "ready".
// Returns true if compilation had errors.
func waitForReady(resolve instanceResolver) bool {
	fmt.Fprintf(os.Stderr, "Waiting for compilation...\n")

	deadline := time.Now().Add(5 * time.Minute)
	for time.Now().Before(deadline) {
		time.Sleep(statusPollInterval)
		status, err := resolve()
		if err != nil {
			continue
		}
		if status.State == "ready" {
			if status.CompileErrors {
				fmt.Fprintf(os.Stderr, "Compilation finished with errors.\n")
			} else {
				fmt.Fprintf(os.Stderr, "Compilation complete.\n")
			}
			return status.CompileErrors
		}
	}

	fmt.Fprintf(os.Stderr, "Timed out waiting for compilation (5m).\n")
	return true
}
