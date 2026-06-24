// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

package cmd

import "testing"

func TestQuoteForShell_Bash(t *testing.T) {
	cases := map[string]string{
		"/Simple":                 "/Simple",                 // no specials → unchanged
		"/Directional Light":      `/Directional\ Light`,     // space escaped
		"/A/B C/D":                `/A/B\ C/D`,               // slash preserved, space escaped
		"/SceneSetup/":            "/SceneSetup/",            // trailing slash preserved
		"/World/Enemy[1]":         "/World/Enemy[1]",         // path grammar [] untouched
		"/Player:Light.intensity": "/Player:Light.intensity", // ':' '.' untouched
		"/Weird$(rm -rf)/x":       `/Weird\$\(rm\ -rf\)/x`,   // injection chars escaped
		"/Tab\tName":              "/Tab\\\tName",            // tab escaped
	}
	for in, want := range cases {
		if got := quoteForShell("bash", in); got != want {
			t.Errorf("bash %q: got %q want %q", in, got, want)
		}
	}
}

func TestQuoteForShell_PowerShell(t *testing.T) {
	cases := map[string]string{
		"/Simple":            "/Simple",              // no specials → unchanged
		"/World/Enemy[1]":    "/World/Enemy[1]",      // [] not a PS boundary → unquoted
		"/Directional Light": "'/Directional Light'", // space → single-quoted
		"/A B/C D/":          "'/A B/C D/'",          // trailing slash kept inside quotes
		"/It's Here":         "'/It''s Here'",        // embedded ' doubled
		"/Cost$5 Item":       "'/Cost$5 Item'",       // $ literal inside single quotes
	}
	for _, shell := range []string{"powershell", "pwsh"} {
		for in, want := range cases {
			if got := quoteForShell(shell, in); got != want {
				t.Errorf("%s %q: got %q want %q", shell, in, got, want)
			}
		}
	}
}

func TestQuoteForShell_Passthrough(t *testing.T) {
	for _, shell := range []string{"", "zsh", "fish"} {
		if got := quoteForShell(shell, "/Directional Light"); got != "/Directional Light" {
			t.Errorf("%q should pass through, got %q", shell, got)
		}
	}
}
