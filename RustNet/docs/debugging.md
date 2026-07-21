# Source-level debugging

RustNet apps can be debugged **on the device** (or the virtual device) from
VSCode, with breakpoints, stepping, call stack and locals — all driven by the
IL interpreter's built-in debug support.

## How it fits together

```
VSCode  ──DAP(stdio)──▶  rustnet-debugger  ──RNDP──▶  device (interpreter)
                          (DAP adapter)               set/clear BP, continue,
                          maps source⇄IL              step, state, stack, locals
```

- The **MetadataProcessor** emits an RNX *debug section*: per method, a list of
  `(IL offset → source line)` sequence points read from the app's portable PDB.
  (IL offsets are preserved because token rewriting is in-place.)
- The **interpreter** already supports breakpoints (`set/clear_breakpoint`),
  single-stepping (`single_step`) and pausing (`RunExit::Paused`), plus a
  source-line-aware `stack_trace()` and `top_locals_display()`.
- The **firmware** exposes these over RNDP: `CMD_DEBUG_SET_BP` / `CLEAR_BP` /
  `CONTINUE` / `STEP` / `STATE` / `STACK` / `LOCALS`. The app thread applies
  breakpoint changes live, snapshots the stack and locals on pause, and
  single-steps on a step-resume.
- **`RnxDebugInfo`** (in `RustNet.Deploy`) parses the debug section so a
  front-end can map a source line to a `(method, IL offset)` breakpoint site
  and back.
- **`rustnet-debugger`** is a Debug Adapter Protocol server. VSCode launches it
  over stdio; it compiles + flashes the app, sets breakpoints, starts it, and
  translates DAP requests (`setBreakpoints`, `stackTrace`, `scopes`,
  `variables`, `continue`, `next`) to RNDP, emitting `stopped`/`terminated`
  events as the interpreter pauses and exits.

## Using it from VSCode

The RustNet extension contributes a `rustnet` debug type. Add a launch config
(the extension offers a snippet):

```jsonc
{
  "type": "rustnet",
  "request": "launch",
  "name": "RustNet: Debug on device",
  "program": "${workspaceFolder}/bin/Debug/net10.0/${workspaceFolderBasename}.dll",
  "device": "tcp:127.0.0.1:7878",
  "key": "${workspaceFolder}/keys/rustnet-signing.key"
}
```

`device` and `key` default to the `rustnet.device` / `rustnet.signingKey`
settings when omitted. The device must already be provisioned with the matching
public key (`rustnet provision`). Point `rustnet.debuggerPath` at the built
`rustnet-debugger` (an executable, or a `.dll` run via `dotnet`).

Set breakpoints in the C# source, press F5: the app is flashed and run on the
(virtual) device, and execution stops at your breakpoints with the call stack
and locals available. Step (F10) and continue (F5) as usual.

## Limits

Locals are shown positionally (`local_0`, `local_1`, …) — the RNX carries no
local *names*. Source files are matched by line number only (single-file apps
map cleanly). Watch expressions and setting variables are not implemented.
