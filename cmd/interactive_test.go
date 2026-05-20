// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

package cmd

import (
	"os"
	"runtime"
	"strings"
	"testing"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

func TestNewStdioSwap_CapturesStdout(t *testing.T) {
	swap, finish, err := newStdioSwap("", false, true)
	if err != nil {
		t.Fatal(err)
	}
	swap()
	// Use fmt.Println-ish APIs via Stdout directly.
	_, _ = os.Stdout.WriteString("hello\nworld\n")
	out := finish()
	if out != "hello\nworld\n" {
		t.Errorf("captured %q, want %q", out, "hello\nworld\n")
	}
}

func TestNewStdioSwap_PipesStdin(t *testing.T) {
	swap, finish, err := newStdioSwap("/A\n/B\n", true, true)
	if err != nil {
		t.Fatal(err)
	}
	swap()

	buf := make([]byte, 32)
	n, _ := os.Stdin.Read(buf)
	_, _ = os.Stdout.Write(buf[:n])

	out := finish()
	if !strings.Contains(out, "/A") || !strings.Contains(out, "/B") {
		t.Errorf("stdin->stdout passthrough lost data: %q", out)
	}
}

func TestNewStdioSwap_NoCaptureLeavesStdoutAlone(t *testing.T) {
	// Confirm os.Stdout is untouched when captureOut=false. We can't
	// assert on actual stdout content (test framework owns it), but we
	// can compare the *os.File pointer before/after.
	orig := os.Stdout
	swap, finish, err := newStdioSwap("", false, false)
	if err != nil {
		t.Fatal(err)
	}
	swap()
	if os.Stdout != orig {
		t.Error("os.Stdout was swapped despite captureOut=false")
	}
	finish()
}

// Pipeline routing: the only piece we can unit-test without a live
// connector is segment classification (shell vs unity-cli) and the
// `unity-cli` prefix-stripping forgiveness. The actual dispatch goes
// through dispatchOnline which needs an instance.

func TestPipelineSegmentClassification(t *testing.T) {
	cases := []struct {
		in      string
		isShell []bool // one bool per segment
	}{
		{"find --plain", []bool{false}},
		{"find --plain | inspect", []bool{false, false}},
		{"find --plain | !grep Sun", []bool{false, true}},
		{"!ls -la", []bool{true}},
		{"!ls Assets/ | inspect", []bool{true, false}},
		{"find --plain | !grep Sun | inspect :Light", []bool{false, true, false}},
	}
	for _, tc := range cases {
		t.Run(tc.in, func(t *testing.T) {
			segs, err := splitPipeline(tc.in)
			if err != nil {
				t.Fatalf("splitPipeline: %v", err)
			}
			if len(segs) != len(tc.isShell) {
				t.Fatalf("got %d segments, want %d", len(segs), len(tc.isShell))
			}
			for i, seg := range segs {
				got := strings.HasPrefix(seg[0], "!")
				if got != tc.isShell[i] {
					t.Errorf("segment %d (%q): isShell=%v, want %v", i, seg, got, tc.isShell[i])
				}
			}
		})
	}
}

func TestUnityPrefixIsStrippedInPipeline(t *testing.T) {
	// The REPL strips a leading `unity-cli` token for doc-pasted lines.
	// We can't drive runPipeline without a session, but we can confirm
	// the tokenizer leaves the prefix in place — it's the pipeline
	// runner that strips it.
	segs, err := splitPipeline("unity-cli find --plain | unity-cli inspect")
	if err != nil {
		t.Fatal(err)
	}
	if len(segs) != 2 {
		t.Fatalf("got %d segments", len(segs))
	}
	if segs[0][0] != "unity-cli" || segs[1][0] != "unity-cli" {
		t.Errorf("expected `unity-cli` prefix to remain after tokenizing; got %v", segs)
	}
}

func TestPrompt_NoInstance(t *testing.T) {
	s := &replSession{}
	if got := s.prompt(); got != "unity-cli (no project)> " {
		t.Errorf("got %q", got)
	}
}

func TestPrompt_TruncatesLongProjectName(t *testing.T) {
	inst := &client.Instance{ProjectPath: "/home/user/very-long-project-name-here", Port: 12345}
	s := &replSession{currentInstance: inst}
	got := s.prompt()
	if !strings.HasSuffix(got, "> ") {
		t.Errorf("prompt missing trailing `> `: %q", got)
	}
	if len(got) > 25 {
		t.Errorf("prompt too long, not truncated: %q (len=%d)", got, len(got))
	}
}

func TestPrompt_UsesBaseName(t *testing.T) {
	sep := "/"
	if runtime.GOOS == "windows" {
		sep = "\\"
	}
	inst := &client.Instance{ProjectPath: "/home/user" + sep + "MyGame", Port: 1}
	s := &replSession{currentInstance: inst}
	got := s.prompt()
	if !strings.HasPrefix(got, "MyGame") {
		t.Errorf("expected prompt to start with `MyGame`, got %q", got)
	}
}

func TestHandleLine_ExitQuit(t *testing.T) {
	s := &replSession{}
	for _, word := range []string{"exit", "quit", "exit ", "  quit  "} {
		quit, err := s.handleLine(strings.TrimSpace(word))
		if err != nil {
			t.Errorf("%q: unexpected error %v", word, err)
		}
		if !quit {
			t.Errorf("%q: expected quit=true", word)
		}
	}
}

func TestHandleLine_BuiltinInPipelineRefused(t *testing.T) {
	s := &replSession{}
	quit, err := s.handleLine("use | inspect")
	if err == nil {
		t.Fatal("expected refusal")
	}
	if !strings.Contains(err.Error(), "pipeline") {
		t.Errorf("error should mention pipeline: %v", err)
	}
	if quit {
		t.Error("quit should be false")
	}
}

func TestHandleLine_ParseError(t *testing.T) {
	s := &replSession{}
	if _, err := s.handleLine(`find "unterminated`); err == nil {
		t.Error("expected parse error")
	}
}
