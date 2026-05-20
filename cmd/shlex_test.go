// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

package cmd

import (
	"reflect"
	"testing"
)

func TestSplitPipeline_Basic(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want [][]string
	}{
		{"empty", "", nil},
		{"whitespace only", "   \t  ", nil},
		{"single token", "ls", [][]string{{"ls"}}},
		{"flag and value", "find --name Enemy", [][]string{{"find", "--name", "Enemy"}}},
		{"two segments", "find --plain | inspect", [][]string{
			{"find", "--plain"}, {"inspect"},
		}},
		{"three segments", "find --plain | !grep Sun | inspect :Light", [][]string{
			{"find", "--plain"}, {"!grep", "Sun"}, {"inspect", ":Light"},
		}},
		{"leading bang", "!ls -la", [][]string{{"!ls", "-la"}}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := splitPipeline(tc.in)
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if !reflect.DeepEqual(got, tc.want) {
				t.Errorf("\n  in:   %q\n  got:  %#v\n  want: %#v", tc.in, got, tc.want)
			}
		})
	}
}

func TestSplitPipeline_Quoting(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want [][]string
	}{
		{
			"double quoted with space",
			`find --name "Bossfight Lobby"`,
			[][]string{{"find", "--name", "Bossfight Lobby"}},
		},
		{
			"single quoted with space",
			`find --name 'My Game Object'`,
			[][]string{{"find", "--name", "My Game Object"}},
		},
		{
			"pipe inside quotes is literal",
			`set :Tag "foo|bar" | inspect`,
			[][]string{{"set", ":Tag", "foo|bar"}, {"inspect"}},
		},
		{
			"escaped pipe is literal",
			`echo foo\|bar`,
			[][]string{{"echo", "foo|bar"}},
		},
		{
			"escaped space inside word",
			`set /A\ B:Tag x`,
			[][]string{{"set", "/A B:Tag", "x"}},
		},
		{
			"double quote with backslash escape",
			`set :Msg "say \"hi\""`,
			[][]string{{"set", ":Msg", `say "hi"`}},
		},
		{
			"single quote treats backslash literally",
			`set :Path 'a\b'`,
			[][]string{{"set", ":Path", `a\b`}},
		},
		{
			"adjacent quoted segments concat",
			`echo "foo"'bar'baz`,
			[][]string{{"echo", "foobarbaz"}},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := splitPipeline(tc.in)
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if !reflect.DeepEqual(got, tc.want) {
				t.Errorf("\n  in:   %q\n  got:  %#v\n  want: %#v", tc.in, got, tc.want)
			}
		})
	}
}

func TestSplitPipeline_Errors(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want error
	}{
		{"unclosed double quote", `find "boss`, errUnclosedDouble},
		{"unclosed single quote", `find 'boss`, errUnclosedSingle},
		{"trailing backslash", `find \`, errDanglingEscape},
		{"leading pipe", `| inspect`, errEmptySegment},
		{"trailing pipe", `find |`, errEmptySegment},
		{"double pipe", `find | | inspect`, errEmptySegment},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := splitPipeline(tc.in)
			if err != tc.want {
				t.Errorf("\n  in:   %q\n  got err:  %v\n  want err: %v", tc.in, err, tc.want)
			}
		})
	}
}

func TestSplitPipeline_BangIsJustACharacter(t *testing.T) {
	// `!` is meaningful only because the dispatcher checks the first
	// token's first byte. The tokenizer itself treats it as ordinary.
	got, err := splitPipeline(`set :Tag "!Boss"`)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := [][]string{{"set", ":Tag", "!Boss"}}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %#v, want %#v", got, want)
	}
}
