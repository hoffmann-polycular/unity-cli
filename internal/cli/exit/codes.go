// Package exit defines the unity-cli exit-code contract.
//
// Stdout carries command result data only. Stderr carries human-readable
// errors, progress, and warnings. Exit codes let pipelines and CI branch
// on the kind of failure without scraping stderr.
package exit

import (
	"fmt"
	"os"
	"strings"
)

// Exit codes returned by the unity-cli binary. The 64+ range mirrors
// sysexits.h conventions.
const (
	OK        = 0  // Success.
	Runtime   = 1  // Generic runtime error.
	Ambiguous = 2  // Path matched more than one object.
	NotFound  = 3  // Path / object / asset not found.
	Unreach   = 4  // Unity unreachable (no instance, stale heartbeat, port in use).
	Busy      = 5  // Unity busy (compiling / reloading) when --no-wait.
	Usage     = 64 // Bad flags or missing args.
)

// CLIError carries an exit code alongside a human-readable message.
// When returned from a command, the binary emits the message to stderr
// and exits with Code.
type CLIError struct {
	Code int
	Msg  string
}

func (e *CLIError) Error() string { return e.Msg }

// New returns a *CLIError with the given code and formatted message.
func New(code int, format string, args ...interface{}) *CLIError {
	return &CLIError{Code: code, Msg: fmt.Sprintf(format, args...)}
}

// Wrap returns a *CLIError that preserves err's message under the given code.
// nil err returns nil. If err is already a *CLIError, it is returned unchanged.
func Wrap(code int, err error) *CLIError {
	if err == nil {
		return nil
	}
	if c, ok := err.(*CLIError); ok {
		return c
	}
	return &CLIError{Code: code, Msg: err.Error()}
}

// Fail writes a formatted message to stderr and exits with the given code.
// Reserved for fatal paths that cannot return an error (e.g. main()).
func Fail(code int, format string, args ...interface{}) {
	fmt.Fprintf(os.Stderr, "Error: "+format+"\n", args...)
	os.Exit(code)
}

// FromKind maps a connector errorKind string to an exit code.
// Unknown / empty kinds return Runtime.
func FromKind(kind string) int {
	switch kind {
	case "ambiguous":
		return Ambiguous
	case "not_found":
		return NotFound
	case "busy":
		return Busy
	case "usage":
		return Usage
	case "unreachable":
		return Unreach
	case "runtime":
		return Runtime
	default:
		return Runtime
	}
}

// ClassifyMessage heuristically maps an error message produced by the
// connector (or the Go client) to an exit code, used as a fallback when
// no explicit errorKind is present.
func ClassifyMessage(msg string) int {
	if msg == "" {
		return Runtime
	}
	m := strings.ToLower(msg)
	switch {
	case strings.Contains(m, "ambiguous"),
		strings.Contains(m, "multiple matches"),
		strings.Contains(m, "candidates:"):
		return Ambiguous
	case strings.Contains(m, "no root object matching"),
		strings.Contains(m, "no child matching"),
		strings.Contains(m, "no descendants matching"),
		strings.Contains(m, "not found"),
		strings.Contains(m, "no instance with"),
		strings.Contains(m, "no object with instance id"),
		strings.Contains(m, "does not exist"):
		return NotFound
	case strings.Contains(m, "compiling"),
		strings.Contains(m, "reloading"),
		strings.Contains(m, "domain reload"),
		strings.Contains(m, "is busy"):
		return Busy
	case strings.Contains(m, "no unity instance"),
		strings.Contains(m, "cannot connect to unity"),
		strings.Contains(m, "no active instance"),
		strings.Contains(m, "timed out waiting for unity"),
		strings.Contains(m, "not responding"):
		return Unreach
	}
	return Runtime
}
