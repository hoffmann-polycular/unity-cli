package main

import "testing"

func TestExtractAndReplaceVersion(t *testing.T) {
	src := "{\n  \"name\": \"com.polycular.unity-cli-connector\",\n  \"version\": \"0.4.3\",\n  \"displayName\": \"x\"\n}\n"
	got, err := extractVersion(src)
	if err != nil || got != "0.4.3" {
		t.Fatalf("extractVersion = %q, %v", got, err)
	}
	out := replaceVersion(src, "0.5.0")
	if v, _ := extractVersion(out); v != "0.5.0" {
		t.Fatalf("after replace, version = %q", v)
	}
	// Only the version line changed; everything else identical.
	wantOut := "{\n  \"name\": \"com.polycular.unity-cli-connector\",\n  \"version\": \"0.5.0\",\n  \"displayName\": \"x\"\n}\n"
	if out != wantOut {
		t.Fatalf("replaceVersion altered more than the version:\n got: %q\nwant: %q", out, wantOut)
	}
}

func TestResolveTarget(t *testing.T) {
	cases := []struct {
		cur, spec, want string
		ok              bool
	}{
		{"0.4.3", "patch", "0.4.4", true},
		{"0.4.3", "minor", "0.5.0", true},
		{"0.4.3", "major", "1.0.0", true},
		{"0.4.3", "1.2.3", "1.2.3", true},
		{"0.4.3", "nope", "", false},
		{"0.4.3", "1.2", "", false},
	}
	for _, c := range cases {
		got, err := resolveTarget(c.cur, c.spec)
		if c.ok && (err != nil || got != c.want) {
			t.Errorf("resolveTarget(%q,%q) = %q,%v want %q", c.cur, c.spec, got, err, c.want)
		}
		if !c.ok && err == nil {
			t.Errorf("resolveTarget(%q,%q) = %q, want error", c.cur, c.spec, got)
		}
	}
}
