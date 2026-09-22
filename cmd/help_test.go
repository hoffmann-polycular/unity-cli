// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.

package cmd

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"strings"
	"testing"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

func captureStdout(t *testing.T, fn func()) string {
	t.Helper()
	orig := os.Stdout
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatalf("pipe: %v", err)
	}
	os.Stdout = w
	t.Cleanup(func() { os.Stdout = orig })

	fn()

	_ = w.Close()
	data, err := io.ReadAll(r)
	if err != nil {
		t.Fatalf("read stdout: %v", err)
	}
	return string(data)
}

// registrySend returns a sendFn that answers `list` with tools, applying the
// name/group filter the way the connector does.
func registrySend(tools []toolSchema) sendFn {
	return func(command string, params interface{}) (*client.CommandResponse, error) {
		if command != "list" {
			return nil, errors.New("unexpected command " + command)
		}
		filters, _ := params.(map[string]interface{})
		var matched []toolSchema
		for _, t := range tools {
			if name, ok := filters["name"].(string); ok && !strings.EqualFold(name, t.Name) {
				continue
			}
			if group, ok := filters["group"].(string); ok && !strings.EqualFold(group, t.Group) {
				continue
			}
			matched = append(matched, t)
		}
		if len(matched) == 0 {
			return &client.CommandResponse{Success: false, Message: "no match", ErrorKind: "not_found"}, nil
		}
		data, err := json.Marshal(matched)
		if err != nil {
			return nil, err
		}
		return &client.CommandResponse{Success: true, Data: data}, nil
	}
}

var testTools = []toolSchema{
	{
		Name:        "loc_export",
		Description: "Export localizable content to a CSV. Scope comes from project settings.",
		Group:       "loc",
		Parameters: []toolParamModel{
			{Name: "file", Type: "String", Description: "Output path", Required: true},
			{Name: "targets", Type: "String[]", Description: "Target languages"},
		},
	},
	{
		Name:        "loc_import",
		Description: "Import a localization CSV.",
		Group:       "loc",
	},
	{Name: "ungrouped_tool", Description: "No group at all."},
}

func TestHelpTopicPrefersBuiltIn(t *testing.T) {
	send := func(string, interface{}) (*client.CommandResponse, error) {
		t.Fatal("built-in topic must not hit the connector")
		return nil, nil
	}
	out := captureStdout(t, func() { helpTopic("ls", send) })
	if !strings.HasPrefix(out, "Usage: unity-cli ls") {
		t.Fatalf("expected built-in ls help, got %q", out)
	}
}

func TestHelpTopicRendersRegisteredTool(t *testing.T) {
	out := captureStdout(t, func() { helpTopic("loc_export", registrySend(testTools)) })

	for _, want := range []string{
		"Usage: unity-cli loc_export --file <string> [--targets <string...>]",
		"(required) Output path",
		"--targets <string...>",
		"Registered tool in group 'loc'",
		"JSON schema: unity-cli list loc_export",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in:\n%s", want, out)
		}
	}
}

func TestHelpTopicRendersGroup(t *testing.T) {
	out := captureStdout(t, func() { helpTopic("loc", registrySend(testTools)) })

	if !strings.Contains(out, "Registered tools in group 'loc':") {
		t.Errorf("expected group header, got:\n%s", out)
	}
	for _, want := range []string{"loc_export", "loc_import"} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in:\n%s", want, out)
		}
	}
	// A group listing keeps one line per tool: only the first sentence.
	if strings.Contains(out, "Scope comes from project settings.") {
		t.Errorf("group listing should elide trailing sentences:\n%s", out)
	}
}

func TestHelpTopicUnknown(t *testing.T) {
	out := captureStdout(t, func() { helpTopic("no_such_thing", registrySend(testTools)) })
	if !strings.Contains(out, "Unknown help topic: no_such_thing") {
		t.Errorf("expected unknown-topic message, got:\n%s", out)
	}
}

// A connector predating the list filters ignores them and answers with the
// whole registry. That must not be read as a match for any topic.
func TestHelpTopicIgnoresUnfilteredRegistry(t *testing.T) {
	unfiltered := func(command string, params interface{}) (*client.CommandResponse, error) {
		data, err := json.Marshal(testTools)
		if err != nil {
			return nil, err
		}
		return &client.CommandResponse{Success: true, Data: data}, nil
	}

	out := captureStdout(t, func() { helpTopic("no_such_thing", unfiltered) })
	if !strings.Contains(out, "Unknown help topic: no_such_thing") {
		t.Errorf("stale connector reply must not match, got:\n%s", out)
	}

	// A real name still resolves out of the unfiltered dump.
	out = captureStdout(t, func() { helpTopic("loc_import", unfiltered) })
	if !strings.Contains(out, "Usage: unity-cli loc_import") {
		t.Errorf("expected loc_import help, got:\n%s", out)
	}
}

func TestPrintToolHelpNoParameters(t *testing.T) {
	out := captureStdout(t, func() { printToolHelp(testTools[2]) })
	if !strings.Contains(out, "Takes no parameters.") {
		t.Errorf("expected no-parameter note, got:\n%s", out)
	}
	if strings.Contains(out, "in group") {
		t.Errorf("ungrouped tool must not claim a group:\n%s", out)
	}
}

func TestFriendlyType(t *testing.T) {
	cases := map[string]string{
		"String":    "string",
		"Boolean":   "bool",
		"Int32":     "int",
		"Single":    "float",
		"String[]":  "string...",
		"Vector3":   "Vector3",
		"Vector3[]": "Vector3...",
	}
	for in, want := range cases {
		if got := friendlyType(in); got != want {
			t.Errorf("friendlyType(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestWrapText(t *testing.T) {
	got := wrapText("one two three four five six", 20, "..")
	want := "one two three four\n..five six"
	if got != want {
		t.Errorf("wrapText = %q, want %q", got, want)
	}

	// Widths below the floor are clamped, so a deep indent cannot squeeze the
	// text into a one-word-per-line column.
	if got := wrapText("one two three four five", 4, ""); got != wrapText("one two three four five", 20, "") {
		t.Errorf("wrapText should clamp narrow widths, got %q", got)
	}

	// Hard breaks in the source survive.
	if got := wrapText("a\nb", 40, ""); got != "a\nb" {
		t.Errorf("wrapText newline = %q", got)
	}

	// A word longer than the width is not split.
	if got := wrapText("short supercalifragilistic", 10, ""); got != "short\nsupercalifragilistic" {
		t.Errorf("wrapText long word = %q", got)
	}
}

func TestFirstSentence(t *testing.T) {
	if got := firstSentence("One. Two."); got != "One." {
		t.Errorf("firstSentence = %q", got)
	}
	if got := firstSentence("No trailing sentence"); got != "No trailing sentence" {
		t.Errorf("firstSentence = %q", got)
	}
	// A period that is not a sentence break (version numbers, paths).
	if got := firstSentence("Reads Assets/Foo.asset for a value"); got != "Reads Assets/Foo.asset for a value" {
		t.Errorf("firstSentence = %q", got)
	}
}

func TestPrintTopicHelpReportsUnknown(t *testing.T) {
	out := captureStdout(t, func() {
		if printTopicHelp("definitely_not_a_topic") {
			t.Error("unknown topic must report false")
		}
	})
	if out != "" {
		t.Errorf("unknown topic must print nothing, got %q", out)
	}
	captureStdout(t, func() {
		if !printTopicHelp("exec") {
			t.Error("exec must be a built-in topic")
		}
	})
}
