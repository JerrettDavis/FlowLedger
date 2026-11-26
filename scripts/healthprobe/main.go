// healthprobe is a tiny static HTTP probe for container healthchecks.
//
// The .NET chiseled runtime images have no shell, curl or wget, so Docker healthchecks cannot
// exec anything inside them. This binary is built in the Dockerfiles (CGO disabled, no libc
// dependency) and copied into the runtime image.
//
// Usage: healthprobe URL [timeout-seconds]
// Exit 0 when the URL answers 2xx within the timeout, 1 otherwise. Redirects are not followed.
package main

import (
	"fmt"
	"net/http"
	"os"
	"strconv"
	"time"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: healthprobe URL [timeout-seconds]")
		os.Exit(2)
	}
	timeout := 3 * time.Second
	if len(os.Args) > 2 {
		if s, err := strconv.Atoi(os.Args[2]); err == nil && s > 0 {
			timeout = time.Duration(s) * time.Second
		}
	}
	client := &http.Client{
		Timeout: timeout,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	resp, err := client.Get(os.Args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, "healthprobe:", err)
		os.Exit(1)
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		fmt.Fprintln(os.Stderr, "healthprobe: status", resp.StatusCode)
		os.Exit(1)
	}
}
