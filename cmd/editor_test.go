// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package cmd

import "testing"

func TestEditorCmd_Play(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	if _, err := editorCmd([]string{"play"}, send, 0); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "play" {
		t.Errorf("expected action=play, got %v", (*params)["action"])
	}
	if (*params)["wait_for_completion"] != false {
		t.Errorf("expected wait_for_completion=false, got %v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_PlayWait(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	if _, err := editorCmd([]string{"play", "--wait"}, send, 0); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["wait_for_completion"] != true {
		t.Errorf("expected wait_for_completion=true, got %v", (*params)["wait_for_completion"])
	}
}

func TestEditorCmd_Stop(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	if _, err := editorCmd([]string{"stop"}, send, 0); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "stop" {
		t.Errorf("expected action=stop, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Pause(t *testing.T) {
	send, params := mockSend("manage_editor", t)
	if _, err := editorCmd([]string{"pause"}, send, 0); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["action"] != "pause" {
		t.Errorf("expected action=pause, got %v", (*params)["action"])
	}
}

func TestEditorCmd_Refresh(t *testing.T) {
	send, _ := mockSend("refresh_unity", t)
	if _, err := editorCmd([]string{"refresh"}, send, 0); err != nil {
		t.Errorf("unexpected error: %v", err)
	}
}

func TestEditorCmd_EmptyArgs(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	_, err := editorCmd(nil, send, 0)
	if err == nil {
		t.Error("expected error for empty args")
	}
}

func TestEditorCmd_UnknownAction(t *testing.T) {
	send, _ := mockSend("manage_editor", t)
	_, err := editorCmd([]string{"fly"}, send, 0)
	if err == nil {
		t.Error("expected error for unknown action")
	}
}
