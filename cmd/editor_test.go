// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// swapPollTiming shrinks the heartbeat poll interval and play-mode wait timeout
// so reconnect/timeout tests run in milliseconds. Returns a restore func.
func swapPollTiming() func() {
	origInterval := statusPollInterval
	origTimeout := playModeWaitTimeout
	origSettle := playAbortSettle
	statusPollInterval = time.Millisecond
	playModeWaitTimeout = 50 * time.Millisecond
	playAbortSettle = 5 * time.Millisecond
	return func() {
		statusPollInterval = origInterval
		playModeWaitTimeout = origTimeout
		playAbortSettle = origSettle
	}
}

func TestEditorCmd_Play(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"play"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "play" {
		t.Errorf("expected action=play, got %v", (*params)["action"])
	}
	if (*params)["wait_for_completion"] != true {
		t.Errorf("expected play to wait by default, got wait_for_completion=%v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_PlayNoWait(t *testing.T) {
	for _, action := range []string{"play", "stop"} {
		send, params := mockSend("manage_editor", t)
		resolve := func() (*client.Instance, error) { return nil, nil }
		if _, err := editorCmd([]string{action, "--no-wait"}, send, resolve); err != nil {
			t.Fatalf("%s: unexpected error: %v", action, err)
		}
		if (*params)["wait_for_completion"] != false {
			t.Errorf("%s --no-wait: expected wait_for_completion=false, got %v", action, (*params)["wait_for_completion"])
		}
	}
}

func TestEditorCmd_PlayFocusDefaultsToEditorSetting(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"play"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if _, ok := (*params)["play_focus"]; ok {
		t.Errorf("expected no play_focus without a flag, got %v", (*params)["play_focus"])
	}
}

func TestEditorCmd_PlayFocusFlags(t *testing.T) {
	for _, focus := range []string{"focused", "unfocused", "maximized"} {
		send, params := mockSend("manage_editor", t)
		resolve := func() (*client.Instance, error) { return nil, nil }
		if _, err := editorCmd([]string{"play", "--" + focus}, send, resolve); err != nil {
			t.Fatalf("--%s: unexpected error: %v", focus, err)
		}
		if (*params)["play_focus"] != focus {
			t.Errorf("--%s: expected play_focus=%s, got %v", focus, focus, (*params)["play_focus"])
		}
	}
}

func TestEditorCmd_PlayFocusFlagsMutuallyExclusive(t *testing.T) {
	send := func(string, interface{}) (*client.CommandResponse, error) {
		t.Fatal("send must not be called when focus flags conflict")
		return nil, nil
	}
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"play", "--unfocused", "--maximized"}, send, resolve); err == nil {
		t.Fatal("expected an error for conflicting focus flags")
	}
}

// A domain reload on play tears down the connection; the connector's response
// never arrives. The CLI should poll the heartbeat back through the reload and
// succeed once the editor reports play mode.
func TestEditorCmd_PlayWait_ReconnectAfterReload(t *testing.T) {
	defer swapPollTiming()()
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return nil, fmt.Errorf("connection closed before response")
	}
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "playing"}, nil
	}
	resp, err := editorCmd([]string{"play"}, send, resolve)
	if err != nil {
		t.Fatalf("expected reconnect to succeed, got error: %v", err)
	}
	if resp == nil || !resp.Success {
		t.Fatalf("expected a success response after reconnect, got %+v", resp)
	}
}

// The common reload case: the client reports the drop as a "success" response
// with ConnectionClosed set (empty body after 200 OK), not as a send error.
// The CLI must still poll back through the reload.
func TestEditorCmd_PlayWait_ReconnectOnConnectionClosedSentinel(t *testing.T) {
	defer swapPollTiming()()
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, ConnectionClosed: true,
			Message: "manage_editor sent (connection closed before response)"}, nil
	}
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "playing"}, nil
	}
	resp, err := editorCmd([]string{"play"}, send, resolve)
	if err != nil {
		t.Fatalf("expected reconnect to succeed, got error: %v", err)
	}
	if resp == nil || !resp.Success || resp.ConnectionClosed {
		t.Fatalf("expected a confirmed success response, got %+v", resp)
	}
}

// When the editor never reaches play mode (e.g. it actually died), the wait
// times out and the original send error is surfaced rather than a false success.
func TestEditorCmd_PlayWait_TimeoutSurfacesError(t *testing.T) {
	defer swapPollTiming()()
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return nil, fmt.Errorf("connection refused")
	}
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready"}, nil // never flips to playing
	}
	if _, err := editorCmd([]string{"play"}, send, resolve); err == nil {
		t.Fatal("expected the original send error to surface after timeout")
	}
}

// stop has the same domain-reload drop on exiting play mode.
func TestEditorCmd_StopWait_ReconnectAfterReload(t *testing.T) {
	defer swapPollTiming()()
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return nil, fmt.Errorf("connection closed before response")
	}
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready"}, nil
	}
	resp, err := editorCmd([]string{"stop"}, send, resolve)
	if err != nil {
		t.Fatalf("expected reconnect to succeed, got error: %v", err)
	}
	if resp == nil || !resp.Success {
		t.Fatalf("expected a success response after reconnect, got %+v", resp)
	}
}

// A play request whose connection dropped for the reload, after which the
// heartbeat settles back in edit mode, was aborted: an error, not a timeout.
func TestEditorCmd_Play_AbortedAfterReload(t *testing.T) {
	defer swapPollTiming()()
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: true, ConnectionClosed: true}, nil
	}
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready", Timestamp: time.Now().UnixMilli()}, nil
	}
	_, err := editorCmd([]string{"play"}, send, resolve)
	if err == nil || !strings.Contains(err.Error(), "aborted") {
		t.Fatalf("expected an aborted error, got %v", err)
	}
}

// The connector reports an abort it saw before any reload as a failed
// response; it must reach the caller unchanged.
func TestEditorCmd_Play_AbortedBeforeReload(t *testing.T) {
	send := func(string, interface{}) (*client.CommandResponse, error) {
		return &client.CommandResponse{Success: false, Message: "Play mode was aborted before it started:\nboom"}, nil
	}
	resolve := func() (*client.Instance, error) { return nil, nil }
	resp, err := editorCmd([]string{"play"}, send, resolve)
	if err != nil || resp == nil || resp.Success || !strings.Contains(resp.Message, "boom") {
		t.Fatalf("expected the connector's abort response, got resp=%+v err=%v", resp, err)
	}
}

func TestEditorCmd_Stop(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"stop"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "stop" {
		t.Errorf("expected action=stop, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Pause(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"pause"}, send, resolve); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "pause" {
		t.Errorf("expected action=pause, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Refresh(t *testing.T) {
	send, _ := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"refresh"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
}

func TestEditorCmd_RefreshForce(t *testing.T) {
	send, params := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	if _, err := editorCmd([]string{"refresh", "--force"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
	if (*params)["force"] != true {
		t.Errorf("expected force=true, got %v", (*params)["force"])
	}
	if (*params)["mode"] != "force" {
		t.Errorf("expected mode=force, got %v", (*params)["mode"])
	}
}

func TestEditorCmd_RefreshCompileForce(t *testing.T) {
	send, params := mockSend("refresh_unity", t)
	resolve := func() (*client.Instance, error) {
		return &client.Instance{State: "ready"}, nil
	}
	if _, err := editorCmd([]string{"refresh", "--compile", "--force"}, send, resolve); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
	if (*params)["compile"] != "request" {
		t.Errorf("expected compile=request, got %v", (*params)["compile"])
	}
	if (*params)["force"] != true {
		t.Errorf("expected force=true, got %v", (*params)["force"])
	}
	if (*params)["mode"] != "force" {
		t.Errorf("expected mode=force, got %v", (*params)["mode"])
	}
}

func TestEditorCmd_RefreshCompileFailureDoesNotWait(t *testing.T) {
	resolveCalled := false
	send := func(cmd string, params interface{}) (*client.CommandResponse, error) {
		if cmd != "refresh_unity" {
			t.Errorf("send called with command %q, want refresh_unity", cmd)
		}
		return &client.CommandResponse{Success: false, Message: "blocked"}, nil
	}
	resolve := func() (*client.Instance, error) {
		resolveCalled = true
		return &client.Instance{State: "ready"}, nil
	}

	resp, err := editorCmd([]string{"refresh", "--compile"}, send, resolve)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if resp == nil || resp.Success {
		t.Fatalf("expected failed response, got %+v", resp)
	}
	if resolveCalled {
		t.Error("expected refresh failure to skip compilation wait")
	}
}

func TestEditorCmd_EmptyArgs(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	_, err := editorCmd(nil, send, resolve)
	if err == nil {
		t.Error("expected error for empty args")
	}
}

func TestEditorCmd_UnknownAction(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	resolve := func() (*client.Instance, error) { return nil, nil }
	_, err := editorCmd([]string{"fly"}, send, resolve)
	if err == nil {
		t.Error("expected error for unknown action")
	}
}
