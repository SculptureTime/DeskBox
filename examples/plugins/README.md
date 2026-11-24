# DeskBox native plugin reference fixtures

These are standalone Rust executables for validating the DeskBox v1 plugin protocol.
They are intentionally **not** members of the main `native/` Cargo workspace.

## Requirements

- Rust 1.96+
- Windows x64/ARM64 matching the DeskBox build

Both examples only depend on `serde` and `serde_json`.

## Timestamp plugin (`ts`)

Build:

```powershell
cd examples\plugins\timestamp-rust
cargo build --release

$dst = Join-Path $env:LOCALAPPDATA "DeskBox\Plugins\ts"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item .\target\release\ts.exe $dst
Copy-Item .\plugin.json $dst
```

Queries:

- `ts` — current Unix seconds, milliseconds and UTC time
- `ts 1790000000` — parse seconds
- `ts 1790000000000` — parse milliseconds

All results use the host `copy` action.

## Transport / translate-test plugin (`tr`)

This is deliberately a **transport stress harness**, not a production translator.
It has no network/API dependency, which makes failures deterministic while validating
DeskBox debounce, cancellation, req_id multiplexing, timeout reset and stderr draining.

Build/install:

```powershell
cd examples\plugins\translate-test-rust
cargo build --release

$dst = Join-Path $env:LOCALAPPDATA "DeskBox\Plugins\tr"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item .\target\release\tr.exe $dst
Copy-Item .\plugin.json $dst
```

Useful queries:

- `tr hello` — immediate copy result
- `tr sleep:800 old`, then quickly `tr new` — newer response can arrive first; host must suppress stale UI
- `tr timeout` — sleeps 10 seconds; with the manifest's 1500 ms timeout, two requests should reset the process
- `tr stderr:10000 hello` — floods stderr; host must keep draining without deadlock
- `tr crash` — exits with code 42; next request should cold-start a fresh process
- `tr oversize` — emits a >256 KiB protocol line; host should treat it as protocol violation and kill/reset
- `tr plugin-action` — returns `action.type=plugin`; Enter triggers an `execute` callback
- `tr keep-open` — copy action with `keep_open=true`; repeated Enter should continue to work

## Manual host checks

1. No plugin process exists before its keyword is first used.
2. Killing DeskBox kills plugin children through the Job Object.
3. An idle process is reclaimed after the host idle timeout.
4. Plugin stdout contains protocol only; diagnostics go to stderr.
