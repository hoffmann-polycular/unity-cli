// MIT Copyright (c) 2025 DevBookOfArray
// See /LICENSE-MIT for the full MIT license text.



package main

import (
	"fmt"
	"os"

	"github.com/youngwoocho02/unity-cli/cmd"
	"github.com/youngwoocho02/unity-cli/internal/cli/exit"
)

var Version = "dev"

func init() {
	cmd.Version = Version
}

func main() {
	err := cmd.Execute()
	if err == nil {
		os.Exit(exit.OK)
	}
	if cliErr, ok := err.(*exit.CLIError); ok {
		if cliErr.Msg != "" {
			fmt.Fprintf(os.Stderr, "Error: %s\n", cliErr.Msg)
		}
		os.Exit(cliErr.Code)
	}
	fmt.Fprintf(os.Stderr, "Error: %v\n", err)
	os.Exit(exit.Runtime)
}
