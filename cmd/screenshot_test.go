// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"testing"
)

func TestScreenshotCmd_Defaults(t *testing.T) {
	send, params := mockSend("screenshot", t)
	if _, err := screenshotCmd(nil, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	// A bare screenshot sends no view/output_path/supersize so the connector
	// picks its own defaults (game view, timestamped path).
	for _, k := range []string{"view", "output_path", "supersize", "width", "height"} {
		if _, ok := (*params)[k]; ok {
			t.Errorf("expected %q to be absent for bare screenshot, got %v", k, (*params)[k])
		}
	}
}

func TestScreenshotCmd_ShortOutputNormalized(t *testing.T) {
	send, params := mockSend("screenshot", t)
	if _, err := screenshotCmd([]string{"-o", "captures/shot.png"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["output_path"] != "captures/shot.png" {
		t.Errorf("expected output_path=captures/shot.png, got %v", (*params)["output_path"])
	}
}

func TestScreenshotCmd_LongOutputPath(t *testing.T) {
	send, params := mockSend("screenshot", t)
	if _, err := screenshotCmd([]string{"--output-path", "captures/shot.png"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["output_path"] != "captures/shot.png" {
		t.Errorf("expected output_path=captures/shot.png, got %v", (*params)["output_path"])
	}
}

func TestScreenshotCmd_ViewCameraPath(t *testing.T) {
	send, params := mockSend("screenshot", t)
	if _, err := screenshotCmd([]string{"--view", "/World/MainCamera"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["view"] != "/World/MainCamera" {
		t.Errorf("expected view=/World/MainCamera, got %v", (*params)["view"])
	}
}

func TestScreenshotCmd_SupersizeCoercedToInt(t *testing.T) {
	send, params := mockSend("screenshot", t)
	if _, err := screenshotCmd([]string{"--view", "game", "--supersize", "2"}, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["view"] != "game" {
		t.Errorf("expected view=game, got %v", (*params)["view"])
	}
	if (*params)["supersize"] != 2 {
		t.Errorf("expected supersize=2 (int), got %v (%T)", (*params)["supersize"], (*params)["supersize"])
	}
}

func TestScreenshotCmd_WidthHeightForCameraView(t *testing.T) {
	send, params := mockSend("screenshot", t)
	args := []string{"--view", "/World/Cam", "--width", "1280", "--height", "720"}
	if _, err := screenshotCmd(args, send); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if (*params)["width"] != 1280 {
		t.Errorf("expected width=1280 (int), got %v (%T)", (*params)["width"], (*params)["width"])
	}
	if (*params)["height"] != 720 {
		t.Errorf("expected height=720 (int), got %v (%T)", (*params)["height"], (*params)["height"])
	}
}
