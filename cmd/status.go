// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package cmd

import (
	"fmt"
	"os"
	"time"

	"github.com/youngwoocho02/unity-cli/internal/client"
)

func statusCmd(inst *client.Instance) error {
	status, err := readStatus(inst.Port)
	if err != nil {
		return fmt.Errorf("no status for port %d — Unity may not be running", inst.Port)
	}

	age := time.Since(time.UnixMilli(status.Timestamp))
	if age > 3*time.Second {
		fmt.Fprintf(os.Stderr, "Unity (port %d): not responding (last heartbeat %s ago)\n", status.Port, age.Truncate(time.Second))
		return nil
	}

	fmt.Printf("Unity (port %d): %s\n", status.Port, status.State)
	fmt.Printf("  Project: %s\n", status.ProjectPath)
	fmt.Printf("  Version: %s\n", status.UnityVersion)
	fmt.Printf("  PID:     %d\n", status.PID)
	return nil
}

// readStatus finds the instance file matching the given port (any state).
func readStatus(port int) (*client.Instance, error) {
	return client.FindByPort(port)
}

// readActiveStatus finds the active (non-stopped) instance on the given port.
func readActiveStatus(port int) (*client.Instance, error) {
	return client.FindActiveByPort(port)
}

// waitForAlive reads the current timestamp, then polls until a newer one appears.
func waitForAlive(port int, timeoutMs int) error {
	baseline := time.Now().UnixMilli()
	if status, err := readActiveStatus(port); err == nil {
		baseline = status.Timestamp
	}

	// Already fresh — check if timestamp was updated within the last second
	if time.Now().UnixMilli()-baseline < 1000 {
		return nil
	}

	fmt.Fprintf(os.Stderr, "Waiting for Unity...\n")

	deadline := time.Now().Add(time.Duration(timeoutMs) * time.Millisecond)
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		status, err := readActiveStatus(port)
		if err != nil {
			continue
		}
		if status.Timestamp > baseline {
			fmt.Fprintf(os.Stderr, "Unity is ready.\n")
			return nil
		}
	}

	return fmt.Errorf("timed out waiting for Unity (port %d)", port)
}

// waitForReady polls indefinitely until the heartbeat state becomes "ready".
// Returns true if compilation had errors.
func waitForReady(port int) bool {
	fmt.Fprintf(os.Stderr, "Waiting for compilation...\n")

	deadline := time.Now().Add(5 * time.Minute)
	for time.Now().Before(deadline) {
		time.Sleep(500 * time.Millisecond)
		status, err := readActiveStatus(port)
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
