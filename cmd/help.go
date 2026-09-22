// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// COMMERCIAL LICENSE NOTICE:
// If you wish to use this code inside a non-GPL, proprietary software product,
// you must instead acquire a commercial license from the copyright holder.
//
// Contact: info@polycular.com | Website: https://www.polycular.com/

package cmd

import (
	"encoding/json"
	"fmt"
	"sort"
	"strings"

	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// helpWidth is the column the rendered registry help wraps at. Built-in help
// text is hand-wrapped to roughly the same width.
const helpWidth = 78

// toolSchema mirrors one entry of the `list` response
// (ToolDiscovery.GetToolSchemas on the connector side).
type toolSchema struct {
	Name        string           `json:"name"`
	Description string           `json:"description"`
	Group       string           `json:"group"`
	Parameters  []toolParamModel `json:"parameters"`
}

type toolParamModel struct {
	Name        string `json:"name"`
	Type        string `json:"type"`
	Description string `json:"description"`
	Required    bool   `json:"required"`
}

// helpTopic renders `unity-cli help <topic>`.
//
// Built-in topics are answered offline. Anything else is looked up in the
// connector's tool registry, so project-registered tools (and tool groups)
// are documented the same way built-in commands are — without having to dump
// the whole `list` JSON and grep it.
//
// send may be nil, in which case a one-shot connection is made only if the
// topic is not a built-in. Failing to reach Unity is not an error: the topic
// is simply reported as unknown, as it was before the registry fallback.
func helpTopic(topic string, send sendFn) {
	if printTopicHelp(topic) {
		return
	}
	if send == nil {
		send = oneShotSend()
	}
	if send != nil && printRegistryHelp(topic, send) {
		return
	}
	fmt.Printf("Unknown help topic: %s\n\nUse \"unity-cli --help\" for available commands.\n", topic)
}

// oneShotSend builds a minimal send closure for the pre-discovery commands
// (help) that may need the connector after all. Returns nil when no Unity
// instance can be reached.
func oneShotSend() sendFn {
	inst, err := client.DiscoverInstance(flagProject, flagPort)
	if err != nil {
		return nil
	}
	return func(command string, params interface{}) (*client.CommandResponse, error) {
		return client.Send(inst, command, params, flagTimeout)
	}
}

// printRegistryHelp looks topic up as a tool name, then as a group name, and
// renders whichever matched. Reports false when neither matched.
func printRegistryHelp(topic string, send sendFn) bool {
	// The filters are re-applied client-side: a connector predating them
	// ignores the parameters and answers with the whole registry, which must
	// not be mistaken for a match.
	for _, t := range fetchToolSchemas(send, "name", topic) {
		if strings.EqualFold(t.Name, topic) {
			printToolHelp(t)
			return true
		}
	}

	var group []toolSchema
	for _, t := range fetchToolSchemas(send, "group", topic) {
		if strings.EqualFold(t.Group, topic) {
			group = append(group, t)
		}
	}
	if len(group) > 0 {
		printGroupHelp(topic, group)
		return true
	}
	return false
}

// fetchToolSchemas asks the connector for the registry entries matching one
// filter. Any failure (Unity unreachable, no match) yields no entries.
func fetchToolSchemas(send sendFn, filter, value string) []toolSchema {
	resp, err := send("list", map[string]interface{}{filter: value})
	if err != nil || resp == nil || !resp.Success {
		return nil
	}
	var tools []toolSchema
	if err := json.Unmarshal(resp.Data, &tools); err != nil {
		return nil
	}
	return tools
}

// printToolHelp renders one registered tool the way printTopicHelp renders a
// built-in command: usage line, description, then the parameter table.
func printToolHelp(t toolSchema) {
	var required, optional []toolParamModel
	for _, p := range t.Parameters {
		if p.Required {
			required = append(required, p)
		} else {
			optional = append(optional, p)
		}
	}
	ordered := make([]toolParamModel, 0, len(t.Parameters))
	ordered = append(ordered, required...)
	ordered = append(ordered, optional...)

	usage := "unity-cli " + t.Name
	for _, p := range required {
		usage += " " + paramLabel(p)
	}
	for _, p := range optional {
		usage += " [" + paramLabel(p) + "]"
	}

	fmt.Printf("Usage: %s\n", wrapText(usage, helpWidth-7, "       "))
	if t.Description != "" {
		fmt.Printf("\n%s\n", wrapText(t.Description, helpWidth, ""))
	}

	if len(ordered) == 0 {
		fmt.Print("\nTakes no parameters.\n")
	} else {
		fmt.Print("\nParameters:\n")
		width := 0
		for _, p := range ordered {
			if n := len(paramLabel(p)); n > width {
				width = n
			}
		}
		indent := strings.Repeat(" ", width+4)
		for _, p := range ordered {
			desc := p.Description
			if p.Required {
				desc = strings.TrimSpace("(required) " + desc)
			}
			line := fmt.Sprintf("  %-*s  %s", width, paramLabel(p), wrapText(desc, helpWidth-len(indent), indent))
			fmt.Println(strings.TrimRight(line, " "))
		}
	}

	fmt.Print("\nRegistered tool")
	if t.Group != "" {
		fmt.Printf(" in group '%s' (see 'unity-cli help %s')", t.Group, t.Group)
	}
	fmt.Printf(", not a built-in command.\nJSON schema: unity-cli list %s\n", t.Name)
}

// printGroupHelp lists the tools a group contains, as an index into their
// individual help.
func printGroupHelp(group string, tools []toolSchema) {
	sort.Slice(tools, func(i, j int) bool { return tools[i].Name < tools[j].Name })

	width := 0
	for _, t := range tools {
		if len(t.Name) > width {
			width = len(t.Name)
		}
	}
	indent := strings.Repeat(" ", width+4)

	fmt.Printf("Registered tools in group '%s':\n\n", group)
	for _, t := range tools {
		desc := wrapText(firstSentence(t.Description), helpWidth-len(indent), indent)
		fmt.Println(strings.TrimRight(fmt.Sprintf("  %-*s  %s", width, t.Name, desc), " "))
	}
	fmt.Print("\nRun 'unity-cli help <tool>' for one tool's parameters.\n")
}

func paramLabel(p toolParamModel) string {
	return fmt.Sprintf("--%s <%s>", p.Name, friendlyType(p.Type))
}

// firstSentence keeps a group listing to one line per tool even when the tool
// documents itself in several sentences.
func firstSentence(desc string) string {
	if i := strings.Index(desc, ". "); i >= 0 {
		return desc[:i+1]
	}
	return desc
}

// wrapText word-wraps text, prefixing every line after the first with indent.
// Newlines in the source text are honoured as hard breaks.
func wrapText(text string, width int, indent string) string {
	if width < 20 {
		width = 20
	}
	var out []string
	for _, paragraph := range strings.Split(strings.ReplaceAll(text, "\r\n", "\n"), "\n") {
		line := ""
		for _, word := range strings.Fields(paragraph) {
			switch {
			case line == "":
				line = word
			case len(line)+1+len(word) <= width:
				line += " " + word
			default:
				out = append(out, line)
				line = word
			}
		}
		out = append(out, line)
	}
	return strings.Join(out, "\n"+indent)
}

// friendlyType maps the CLR type names the connector reports onto the names a
// CLI user expects to see.
func friendlyType(clr string) string {
	suffix := ""
	if strings.HasSuffix(clr, "[]") {
		clr, suffix = strings.TrimSuffix(clr, "[]"), "..."
	}
	switch clr {
	case "String":
		clr = "string"
	case "Boolean":
		clr = "bool"
	case "Int32", "Int64":
		clr = "int"
	case "Single", "Double":
		clr = "float"
	}
	return clr + suffix
}
