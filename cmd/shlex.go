// unity-cli - Control the Unity Editor from the command line.
// Copyright (C) 2026  Tobias Hoffmann Polycular GmbH
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

package cmd

import (
	"errors"
	"strings"
)

// Hand-rolled shell-words tokenizer for the interactive REPL. The full
// `github.com/mvdan/sh` parser is overkill for our needs — we only
// support a small subset of POSIX shell syntax:
//
//   - Splits on whitespace.
//   - "..." and '...' group multiple words into one token.
//     Inside double quotes, \" \\ are recognized; everything else is
//     literal. Inside single quotes, even backslash is literal (matches
//     POSIX). This intentionally does NOT support $variable expansion —
//     the REPL has no variable scope.
//   - \ outside quotes escapes the next character literally (so \| is
//     a literal pipe, not a pipeline separator).
//   - | outside quotes splits the line into pipeline segments.
//
// shlex returns the segments as [][]string — outer list is segments,
// inner list is tokens within a segment. Empty input yields an empty
// slice (NOT a single empty segment).

var (
	errUnclosedDouble = errors.New("unclosed double quote")
	errUnclosedSingle = errors.New("unclosed single quote")
	errDanglingEscape = errors.New("trailing backslash")
	errEmptySegment   = errors.New("empty pipeline segment (unexpected `|`)")
)

// splitPipeline tokenizes a full REPL input line into pipeline segments,
// where each segment is the slice of words for one command. A trailing
// or leading unquoted `|` is an error (matches bash behavior).
//
// Returns (nil, nil) for blank input.
func splitPipeline(line string) ([][]string, error) {
	tokens, err := shlex(line, true)
	if err != nil {
		return nil, err
	}
	if len(tokens) == 0 {
		return nil, nil
	}

	var segments [][]string
	var current []string
	for _, t := range tokens {
		if t == pipeSentinel {
			if len(current) == 0 {
				return nil, errEmptySegment
			}
			segments = append(segments, current)
			current = nil
			continue
		}
		current = append(current, t)
	}
	if len(current) == 0 {
		return nil, errEmptySegment
	}
	segments = append(segments, current)
	return segments, nil
}

// shlex tokenizes a single line into words. When pipelineMode is true,
// unquoted `|` is emitted as a sentinel token (pipeSentinel) instead of
// being treated as a literal character — used by splitPipeline. When
// false, `|` is a literal character (used by the completion adapter).
const pipeSentinel = "\x00|"

// shlexForCompletion tokenizes a line for tab-completion. Unlike shlex it
// never errors: an unterminated quote or a trailing backslash on the final
// token is tolerated. The open quote (if any) is reported via openQuote
// ('"' or '\'', else 0) so the completer can complete *inside* the quote and
// close it. atWordStart is true when the cursor sits at the start of a fresh
// word — the input is empty or ends in unquoted, unescaped whitespace — i.e.
// the token being completed is empty. `|` is treated as a literal; the
// completer only ever sees a single pipeline segment.
func shlexForCompletion(line string) (tokens []string, openQuote byte, atWordStart bool) {
	var cur strings.Builder
	inWord := false
	atWordStart = true

	flush := func() {
		if inWord {
			tokens = append(tokens, cur.String())
			cur.Reset()
			inWord = false
		}
	}

	i, n := 0, len(line)
	for i < n {
		c := line[i]
		switch c {
		case ' ', '\t':
			flush()
			atWordStart = true
			i++

		case '\\':
			inWord = true
			atWordStart = false
			if i+1 >= n {
				i++ // dangling backslash: drop the incomplete escape
				break
			}
			cur.WriteByte(line[i+1])
			i += 2

		case '"':
			inWord = true
			atWordStart = false
			i++
			for i < n && line[i] != '"' {
				if line[i] == '\\' && i+1 < n {
					switch line[i+1] {
					case '"', '\\':
						cur.WriteByte(line[i+1])
						i += 2
						continue
					}
				}
				cur.WriteByte(line[i])
				i++
			}
			if i >= n {
				openQuote = '"'
			} else {
				i++ // skip closing "
			}

		case '\'':
			inWord = true
			atWordStart = false
			i++
			for i < n && line[i] != '\'' {
				cur.WriteByte(line[i])
				i++
			}
			if i >= n {
				openQuote = '\''
			} else {
				i++ // skip closing '
			}

		default:
			cur.WriteByte(c)
			inWord = true
			atWordStart = false
			i++
		}
	}
	flush()
	return tokens, openQuote, atWordStart
}

func shlex(line string, pipelineMode bool) ([]string, error) {
	var tokens []string
	var cur strings.Builder
	inWord := false

	flush := func() {
		if inWord {
			tokens = append(tokens, cur.String())
			cur.Reset()
			inWord = false
		}
	}

	i := 0
	n := len(line)
	for i < n {
		c := line[i]
		switch c {
		case ' ', '\t':
			flush()
			i++

		case '|':
			if pipelineMode {
				flush()
				tokens = append(tokens, pipeSentinel)
				i++
				continue
			}
			cur.WriteByte(c)
			inWord = true
			i++

		case '\\':
			if i+1 >= n {
				return nil, errDanglingEscape
			}
			cur.WriteByte(line[i+1])
			inWord = true
			i += 2

		case '"':
			inWord = true
			i++
			for i < n && line[i] != '"' {
				if line[i] == '\\' && i+1 < n {
					switch line[i+1] {
					case '"', '\\':
						cur.WriteByte(line[i+1])
						i += 2
						continue
					}
				}
				cur.WriteByte(line[i])
				i++
			}
			if i >= n {
				return nil, errUnclosedDouble
			}
			i++ // skip closing "

		case '\'':
			inWord = true
			i++
			for i < n && line[i] != '\'' {
				cur.WriteByte(line[i])
				i++
			}
			if i >= n {
				return nil, errUnclosedSingle
			}
			i++ // skip closing '

		default:
			cur.WriteByte(c)
			inWord = true
			i++
		}
	}
	flush()
	return tokens, nil
}
