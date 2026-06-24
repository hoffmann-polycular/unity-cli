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

package cmd

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/ergochat/readline"
	"github.com/hoffmann-polycular/unity-cli/internal/cli/exit"
	"github.com/hoffmann-polycular/unity-cli/internal/client"
)

// interactiveCmd enters the REPL. See `unity-cli help interactive` and
// docs/commands.md for the user-facing contract.
//
// Lifecycle:
//  1. Parse flags + optional positional project.
//  2. Initialize session (sticky --project, --port, --timeout).
//  3. Best-effort resolve initial Unity instance (warn if missing).
//  4. Build an ergochat/readline.Instance bound to stdin/stdout/stderr
//     with persistent history.
//  5. Loop: read line, tokenize, dispatch (built-in or pipeline).
func interactiveCmd(args []string) error {
	session, err := newReplSession(args)
	if err != nil {
		return err
	}

	historyFile := defaultHistoryPath()
	rl, err := readline.NewFromConfig(&readline.Config{
		Prompt:                 session.prompt(),
		HistoryFile:            historyFile,
		HistoryLimit:           1000,
		InterruptPrompt:        "^C",
		EOFPrompt:              "exit",
		DisableAutoSaveHistory: false,
		AutoComplete:           replCompleter{},
	})
	if err != nil {
		return exit.New(exit.Runtime, "failed to start interactive mode: %v", err)
	}
	defer func() { _ = rl.Close() }()

	// One-time banner. Skip if we already errored out above.
	session.printBanner(rl.Stdout())

	for {
		rl.SetPrompt(session.prompt())
		line, err := rl.ReadLine()
		if err != nil {
			// readline.ErrInterrupt = Ctrl-C: clear and continue. We
			// intentionally do not double-tap to exit (some other REPLs do);
			// Ctrl-D at empty prompt is the documented exit mechanism.
			if errors.Is(err, readline.ErrInterrupt) {
				continue
			}
			// io.EOF = Ctrl-D: exit cleanly.
			if errors.Is(err, io.EOF) {
				_, _ = fmt.Fprintln(rl.Stdout())
				return nil
			}
			return exit.New(exit.Runtime, "readline error: %v", err)
		}

		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}

		if quit, runErr := session.handleLine(line); runErr != nil {
			fmt.Fprintln(os.Stderr, "Error:", runErr)
			// Continue the REPL; only fatal terminal errors exit the loop.
		} else if quit {
			return nil
		}

		// Redraw the prompt after the command's output. If a command emitted
		// stray control bytes that the terminal echoed back, this clears the
		// visible mess; the prompt itself is re-emitted on the next ReadLine()
		// call regardless.
		rl.Refresh()
	}
}

// replSession holds everything that survives between REPL prompts.
type replSession struct {
	// sticky selection (mutated by `use`):
	targetProject string
	targetPort    int

	// fixed at startup:
	timeout         int
	ignoreVerMatch  bool
	suppressBanner  bool
	currentInstance *client.Instance // best-effort; may be nil when no Unity is reachable
}

func newReplSession(args []string) (*replSession, error) {
	s := &replSession{
		targetProject:  flagProject,
		targetPort:     flagPort,
		timeout:        flagTimeout,
		ignoreVerMatch: flagIgnoreVersionMismatch,
	}

	// Accept a single optional positional: project path or substring.
	for i := 0; i < len(args); i++ {
		a := args[i]
		switch a {
		case "--help", "-h":
			printTopicHelp("interactive")
			s.suppressBanner = true
			return nil, exit.New(exit.OK, "")
		default:
			if strings.HasPrefix(a, "--") {
				return nil, exit.New(exit.Usage, "unknown flag for interactive: %s", a)
			}
			if s.targetProject == "" && s.targetPort == 0 {
				s.targetProject = a
			} else {
				return nil, exit.New(exit.Usage, "unexpected extra argument: %q", a)
			}
		}
	}

	// Best-effort initial resolve. If it fails, the REPL still starts;
	// the user can run `use` to set a target later.
	if inst, err := s.resolveOnce(); err == nil {
		s.currentInstance = inst
		if vErr := checkConnectorVersion(inst, Version, s.ignoreVerMatch); vErr != nil {
			fmt.Fprintln(os.Stderr, "Warning:", vErr)
		}
	}
	return s, nil
}

// resolveOnce performs a single instance lookup using the session's
// sticky --project / --port preferences. Returns the freshest match.
func (s *replSession) resolveOnce() (*client.Instance, error) {
	if s.targetPort > 0 {
		return client.DiscoverInstance("", s.targetPort)
	}
	return client.DiscoverInstance(s.targetProject, 0)
}

func (s *replSession) prompt() string {
	if s.currentInstance == nil {
		return "unity-cli (no project)> "
	}
	name := filepath.Base(strings.TrimRight(s.currentInstance.ProjectPath, "/\\"))
	if name == "" || name == "." || name == "/" {
		name = "unity-cli"
	}
	if len(name) > 20 {
		name = name[:17] + "..."
	}
	return name + "> "
}

func (s *replSession) printBanner(out io.Writer) {
	if s.suppressBanner {
		return
	}
	_, _ = fmt.Fprintln(out, "unity-cli interactive - Copyright (C) 2026 Polycular GmbH ")
	_, _ = fmt.Fprintln(out, "This program comes with ABSOLUTELY NO WARRANTY")
	_, _ = fmt.Fprintln(out, "This is free software, and you are welcome to redistribute it under the conditions of the GNU GPLv3.")
	if s.currentInstance == nil {
		_, _ = fmt.Fprintln(out, "no Unity instance connected.")
		_, _ = fmt.Fprintln(out, "Run `use <project>` to bind one. Type `exit` to quit.")
		return
	}
	_, _ = fmt.Fprintf(out, "Connected to Unity (port %d, project %s, connector %s)\n",
		s.currentInstance.Port,
		s.currentInstance.ProjectPath,
		connectorVersionLabel(s.currentInstance.ConnectorVersion),
	)
	_, _ = fmt.Fprintln(out, "Type `help`, `exit`, or any unity-cli subcommand without the `unity-cli` prefix.")
	_, _ = fmt.Fprintln(out, "Prefix shell commands with `!` to drop to the host shell.")
}

// handleLine processes one input line. Returns (quit, error).
// quit=true means the REPL should exit cleanly.
func (s *replSession) handleLine(line string) (bool, error) {
	segments, err := splitPipeline(line)
	if err != nil {
		return false, err
	}
	if len(segments) == 0 {
		return false, nil
	}

	// Single-segment built-ins. Pipelines never invoke built-ins
	// (refused below).
	if len(segments) == 1 {
		first := segments[0][0]
		switch first {
		case "exit", "quit":
			return true, nil
		case "clear":
			return false, s.builtinClear()
		case "use":
			return false, s.builtinUse(segments[0][1:])
		}
	} else {
		// Check no segment uses a built-in as its first token.
		for i, seg := range segments {
			switch seg[0] {
			case "exit", "quit", "clear", "use":
				return false, fmt.Errorf("built-in `%s` cannot be used inside a pipeline (segment %d)", seg[0], i+1)
			}
		}
	}

	return false, s.runPipeline(segments)
}

// --------------------------------------------------------------------
// Built-ins
// --------------------------------------------------------------------

func (s *replSession) builtinClear() error {
	// ANSI clear screen + home. Same as bash's `clear` on terminals
	// that support it; on dumb terminals it's a no-op visually.
	fmt.Print("\x1b[H\x1b[2J")
	return nil
}

func (s *replSession) builtinUse(args []string) error {
	// `use`              → print current binding
	// `use <project>`    → set sticky project, clear port
	// `use --port N`     → set sticky port, clear project
	// `use --clear`      → unbind both
	if len(args) == 0 {
		if s.currentInstance == nil {
			fmt.Println("not bound to any Unity instance")
			return nil
		}
		fmt.Printf("project: %s\nport:    %d\nconnector: %s\n",
			s.currentInstance.ProjectPath,
			s.currentInstance.Port,
			connectorVersionLabel(s.currentInstance.ConnectorVersion))
		return nil
	}

	switch args[0] {
	case "--clear":
		s.targetProject = ""
		s.targetPort = 0
		s.currentInstance = nil
		fmt.Println("unbound")
		return nil
	case "--port":
		if len(args) < 2 {
			return fmt.Errorf("--port requires a value")
		}
		var p int
		if _, err := fmt.Sscanf(args[1], "%d", &p); err != nil || p <= 0 {
			return fmt.Errorf("invalid port: %q", args[1])
		}
		s.targetPort = p
		s.targetProject = ""
	default:
		s.targetProject = args[0]
		s.targetPort = 0
	}

	inst, err := s.resolveOnce()
	if err != nil {
		s.currentInstance = nil
		return fmt.Errorf("set selection but could not resolve a matching instance: %v", err)
	}
	if vErr := checkConnectorVersion(inst, Version, s.ignoreVerMatch); vErr != nil {
		fmt.Fprintln(os.Stderr, "Warning:", vErr)
	}
	s.currentInstance = inst
	fmt.Printf("bound: %s (port %d)\n", inst.ProjectPath, inst.Port)
	return nil
}

// --------------------------------------------------------------------
// Pipeline execution
// --------------------------------------------------------------------

func (s *replSession) runPipeline(segments [][]string) error {
	prev := ""
	for i, seg := range segments {
		captureOut := i < len(segments)-1
		stdinPiped := i > 0

		// Forgiveness: strip a leading `unity-cli` so doc-pasted lines work.
		// Applied per-segment, since each pipeline stage is its own command.
		if seg[0] == "unity-cli" {
			if len(seg) == 1 {
				return fmt.Errorf("segment %d is just `unity-cli` with no subcommand", i+1)
			}
			seg = seg[1:]
		}

		var (
			out string
			err error
		)

		if strings.HasPrefix(seg[0], "!") {
			out, err = s.runShellSegment(seg, prev, stdinPiped, captureOut)
		} else {
			out, err = s.runUnitySegment(seg, prev, stdinPiped, captureOut)
		}

		if err != nil {
			return err
		}
		prev = out
	}
	return nil
}

// runShellSegment shells out via sh -c (Unix) or cmd /c (Windows).
// Redirection (`>`, `>>`, `<`, `2>`) and globbing happen in the host
// shell — we don't try to implement them ourselves.
func (s *replSession) runShellSegment(seg []string, stdin string, stdinPiped, captureOut bool) (string, error) {
	if seg[0] == "!" {
		return "", fmt.Errorf("`!` must be followed by a shell command, e.g. `!ls -la`")
	}

	// Strip the leading `!` from the first token only. Subsequent tokens
	// belong to the shell command as-is.
	first := strings.TrimPrefix(seg[0], "!")
	parts := append([]string{first}, seg[1:]...)
	// Rejoin into a single line for the shell. We use shell-safe
	// requoting of tokens that originated from quoted user input — but
	// since we don't preserve the user's original quoting through shlex,
	// best-effort is to assume nothing fancy and re-emit space-joined.
	// This is the documented limitation; users who need clever shell
	// quoting should write the whole pipe segment inside one quoted
	// string.
	cmdline := strings.Join(parts, " ")

	var shell, flag string
	if runtime.GOOS == "windows" {
		shell = "cmd"
		flag = "/c"
	} else {
		shell = "sh"
		flag = "-c"
	}
	cmd := exec.Command(shell, flag, cmdline)
	if stdinPiped {
		cmd.Stdin = strings.NewReader(stdin)
	} else {
		cmd.Stdin = os.Stdin
	}
	cmd.Stderr = os.Stderr

	if captureOut {
		var buf bytes.Buffer
		cmd.Stdout = &buf
		if err := cmd.Run(); err != nil {
			return "", fmt.Errorf("shell command failed: %v", err)
		}
		return buf.String(), nil
	}
	cmd.Stdout = os.Stdout
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("shell command failed: %v", err)
	}
	return "", nil
}

// runUnitySegment dispatches a unity-cli subcommand. When piped input
// is supplied, os.Stdin is temporarily replaced with a pipe carrying
// that string so existing readStdinPaths/readStdinIfPiped code works
// unmodified. When captureOut is true, os.Stdout is replaced with a
// pipe so we can collect the output for the next segment.
func (s *replSession) runUnitySegment(seg []string, stdin string, stdinPiped, captureOut bool) (string, error) {
	category := seg[0]
	subArgs := seg[1:]

	// Build per-command closures bound to the session's sticky state.
	send, resolve := s.commandClosures()

	// Special-case the pre-discovery commands a user might reasonably
	// type from inside the REPL. We dispatch them directly without
	// going through dispatchOnline (which expects an online instance).
	if isInteractiveBuiltinCommand(category) {
		return s.runInProcessNonOnline(category, subArgs, stdin, stdinPiped, captureOut)
	}

	// In a pipeline (other than the last segment) we need a string we
	// can hand to the next segment. dispatchOnline + printResponse
	// write to os.Stdout, so we swap that out and copy the result.
	swap, finish, err := newStdioSwap(stdin, stdinPiped, captureOut)
	if err != nil {
		return "", err
	}
	swap()

	var resp *client.CommandResponse
	resp, err = dispatchOnline(category, subArgs, send, resolve)
	if err == nil && resp != nil {
		printResponse(resp)
	}

	out := finish()

	if err != nil {
		// Strip exit.CLIError wrapping in REPL — show only the user message.
		var cliErr *exit.CLIError
		if errors.As(err, &cliErr) {
			if cliErr.Msg == "" {
				return out, nil // already printed via stderr
			}
			return out, errors.New(cliErr.Msg)
		}
		return out, err
	}

	if resp != nil && !resp.Success {
		// printResponse already wrote to stderr — surface a non-fatal error.
		return out, nil
	}
	if resp != nil && resp.PartialFailure && resp.Stderr != "" {
		fmt.Fprintln(os.Stderr, resp.Stderr)
	}
	return out, nil
}

// commandClosures returns the (send, resolve) pair dispatchOnline expects,
// bound to this session's sticky selection.
func (s *replSession) commandClosures() (sendFn, instanceResolver) {
	resolve := func() (*client.Instance, error) {
		return s.resolveOnce()
	}
	timeout := s.timeout
	ignoreVer := s.ignoreVerMatch
	send := func(command string, params interface{}) (*client.CommandResponse, error) {
		inst, err := resolve()
		if err != nil {
			return nil, exit.Wrap(exit.Unreach, err)
		}
		if err := checkConnectorVersion(inst, Version, ignoreVer); err != nil {
			return nil, exit.Wrap(exit.Runtime, err)
		}
		// Refresh cache so the prompt reflects whatever we just talked to.
		s.currentInstance = inst
		resp, err := client.Send(inst, command, params, timeout)
		if err != nil {
			return nil, exit.Wrap(exit.Unreach, err)
		}
		return resp, nil
	}
	return send, resolve
}

// isInteractiveBuiltinCommand identifies non-online commands a user
// might still want to type inside the REPL (e.g. `help`, `status`).
func isInteractiveBuiltinCommand(category string) bool {
	switch category {
	case "help", "--help", "-h", "version", "--version", "-v",
		"status", "list", "init":
		return true
	}
	return false
}

func (s *replSession) runInProcessNonOnline(category string, subArgs []string, stdin string, stdinPiped, captureOut bool) (string, error) {
	swap, finish, err := newStdioSwap(stdin, stdinPiped, captureOut)
	if err != nil {
		return "", err
	}
	swap()

	switch category {
	case "help", "--help", "-h":
		if len(subArgs) > 0 {
			printTopicHelp(subArgs[0])
		} else {
			printHelp()
		}
	case "version", "--version", "-v":
		fmt.Println("unity-cli " + Version)
	case "list":
		// `list` IS an online command (it asks the connector for tool defs),
		// but we keep the case here as a placeholder reminder — the actual
		// dispatch happens via dispatchOnline. Should never reach here.
		_ = subArgs
	case "status":
		// status reuses the session's selection.
		if inst, err := s.resolveOnce(); err == nil {
			_ = statusCmd(inst)
		} else {
			fmt.Fprintln(os.Stderr, "no Unity instance found:", err)
		}
	case "init":
		// `init` is filesystem-only — safe to run in the REPL.
		if err := initCmd(subArgs); err != nil {
			fmt.Fprintln(os.Stderr, "Error:", err)
		}
	}
	return finish(), nil
}

// --------------------------------------------------------------------
// Stdio capture helper
// --------------------------------------------------------------------

// newStdioSwap returns (swap, finish, err). swap() installs temporary
// os.Stdin/os.Stdout replacements according to the (stdinPiped, captureOut)
// flags; finish() restores them and returns the captured stdout (empty
// if captureOut was false). Stderr is never redirected.
//
// Implementation notes (don't simplify without thinking):
//
//   - The stdin pipe is prefilled SYNCHRONOUSLY in swap(): we write the
//     entire input then close the write end before handing control to
//     the command. This avoids a writer goroutine that could outlive
//     the segment and leak FDs / interfere with the next swap.
//     If the input exceeds the OS pipe buffer (64 KiB on most systems),
//     we fall back to a goroutine; that path is rare in interactive use.
//
//   - The stdout pipe DOES need a drainer goroutine because we don't
//     know how much the command will write until it exits. finish()
//     waits for the drainer (via drainDone) before restoring os.Stdout,
//     so the drainer never outlives the swap.
//
//   - We never touch os.Stderr — errors go straight to the terminal.
//     This is important inside readline: stray prompt-corrupting
//     escape sequences from libraries that write to stderr stay out of
//     our pipe buffer entirely.
func newStdioSwap(stdin string, stdinPiped, captureOut bool) (func(), func() string, error) {
	var (
		origStdin, origStdout *os.File
		stdinR, stdinW        *os.File
		stdoutR, stdoutW      *os.File
		captured              bytes.Buffer
		drainDone             chan struct{}
		stdinDone             chan struct{} // only used for the >64KB slow path
		err                   error
	)

	if stdinPiped {
		stdinR, stdinW, err = os.Pipe()
		if err != nil {
			return nil, nil, err
		}
	}
	if captureOut {
		stdoutR, stdoutW, err = os.Pipe()
		if err != nil {
			if stdinR != nil {
				_ = stdinR.Close()
				_ = stdinW.Close()
			}
			return nil, nil, err
		}
	}

	swap := func() {
		if stdinR != nil {
			origStdin = os.Stdin
			os.Stdin = stdinR

			// Fast path: try a single synchronous Write. If the input
			// fits in the pipe buffer, this completes immediately and
			// we close the writer here — no background goroutine, no
			// resource that can outlive the segment.
			if len(stdin) <= stdinFastPathLimit {
				_, _ = io.WriteString(stdinW, stdin)
				_ = stdinW.Close()
			} else {
				// Slow path: large input may block on Write until the
				// reader drains the buffer. Use a goroutine, but track
				// it via stdinDone so finish() can wait for it.
				stdinDone = make(chan struct{})
				go func() {
					defer close(stdinDone)
					_, _ = io.WriteString(stdinW, stdin)
					_ = stdinW.Close()
				}()
			}
		}
		if stdoutW != nil {
			origStdout = os.Stdout
			os.Stdout = stdoutW
			drainDone = make(chan struct{})
			go func() {
				_, _ = io.Copy(&captured, stdoutR)
				close(drainDone)
			}()
		}
	}

	finish := func() string {
		if stdoutW != nil {
			_ = stdoutW.Close()
			<-drainDone
			_ = stdoutR.Close()
			os.Stdout = origStdout
		}
		if stdinR != nil {
			// If the command didn't drain its stdin, anything still in
			// the pipe needs to go SOMEWHERE before we close it. Drain
			// to /dev/null so the writer (if it's still blocked) can
			// finish and exit cleanly. This is paranoia: if we close
			// stdinR while the writer is mid-Write, the writer gets a
			// pipe-broken error and exits — but we still want to wait
			// for that to happen so we never have a stray goroutine
			// running into the next swap.
			if stdinDone != nil {
				// Force the writer to finish by draining anything it
				// still wants to push. Then wait for it to exit.
				go func() { _, _ = io.Copy(io.Discard, stdinR) }()
				<-stdinDone
			}
			_ = stdinR.Close()
			os.Stdin = origStdin
		}
		return captured.String()
	}

	return swap, finish, nil
}

// stdinFastPathLimit is the largest input we'll synchronously push into
// the stdin pipe without a writer goroutine. 32 KiB is well under the
// pipe buffer on every supported platform (Linux 64 KiB, macOS 16 KiB
// for unread, Windows ~4 KiB but the kernel grows it on demand). Most
// interactive pipelines pass tiny payloads (a list of paths); this
// path covers ~all real usage.
const stdinFastPathLimit = 32 * 1024

// --------------------------------------------------------------------
// Tab completion adapter
// --------------------------------------------------------------------

type replCompleter struct{}

// Do plugs the existing computeCandidates into ergochat/readline's
// AutoCompleter interface. readline gives us the full line up to the
// cursor; we tokenize, identify the word being completed, and return
// the candidates (truncated to the user-typed prefix length, as readline
// requires).
func (replCompleter) Do(line []rune, pos int) ([][]rune, int) {
	prefix := string(line[:pos])
	tokens, err := shlex(prefix, false)
	if err != nil {
		return nil, 0
	}

	// Trailing whitespace = starting a new (empty) word.
	trailingSpace := pos > 0 && (line[pos-1] == ' ' || line[pos-1] == '\t')
	var current string
	idx := len(tokens)
	if !trailingSpace && len(tokens) > 0 {
		current = tokens[len(tokens)-1]
		idx = len(tokens) - 1
	}

	// Pad words so words[idx] is valid for computeCandidates.
	words := append([]string{}, tokens...)
	for len(words) <= idx {
		words = append(words, "")
	}

	candidates := computeCandidates(idx, words, current)

	// readline expects each returned []rune to be the *suffix* to append
	// (everything after the user-typed prefix), and `length` to be the
	// length of the typed prefix. So if current = "fi" and candidate =
	// "find", we return ([]rune("nd"), 2).
	prefLen := len([]rune(current))
	out := make([][]rune, 0, len(candidates))
	for _, c := range candidates {
		if !strings.HasPrefix(c, current) {
			continue
		}
		out = append(out, []rune(c[len(current):]))
	}
	return out, prefLen
}

// --------------------------------------------------------------------
// Misc
// --------------------------------------------------------------------

func defaultHistoryPath() string {
	home, err := os.UserHomeDir()
	if err != nil || home == "" {
		return ""
	}
	dir := filepath.Join(home, ".unity-cli")
	_ = os.MkdirAll(dir, 0o755)
	return filepath.Join(dir, "history")
}
