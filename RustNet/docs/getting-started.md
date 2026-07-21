# Getting Started with RustNet

## Prerequisites

- Rust 1.75+ (`cargo`)
- .NET SDK 10
- Node 18+ (only for the VSCode extension)

## 1. Build the stack

```bash
cargo build -p rustnet-firmware        # device firmware (host variant)
dotnet build dotnet/RustNet.slnx       # libraries + tools
```

## 2. Start a virtual device

```bash
./target/debug/rustnet-firmware              # persistent (.rustnet-device dir)
./target/debug/rustnet-firmware --ephemeral  # RAM only
./target/debug/rustnet-firmware --port 9000 --home /path/to/state
```

The virtual device is the real firmware with the RNDP protocol served
over TCP instead of USB/UART — every tool works identically against it.

## 3. Provision device security

```bash
rustnet keys generate --out keys
rustnet provision --key keys/rustnet-signing.pub
```

The device now only accepts apps and firmware signed with your key.

## 4. Create and run an app

```bash
rustnet new sensor-logger MyLogger
cd MyLogger
RUSTNET_SDK=/path/to/RustNet dotnet build
rustnet flash bin/Debug/net10.0/MyLogger.dll --name mylogger \
        --key ../keys/rustnet-signing.key --start
rustnet logs --follow
rustnet data pull temperature.csv
```

`rustnet templates` lists all templates (sensor logger, weather check,
calculator, XoX game, display testing, filesystem test, wifi+mqtt).

## 5. Everyday commands

```bash
rustnet info                     # device identity
rustnet apps list                # installed apps
rustnet profile --watch          # live CPU/heap/GC counters
rustnet display capture -o s.ppm # screenshot the display
rustnet config set api.key XYZ   # encrypted at rest on the device
rustnet wifi --ssid Home --psk secret
rustnet ota push fw.bin --key keys/rustnet-signing.key
rustnet ota confirm              # or: rustnet ota rollback
rustnet ota campaign fw.bin --fleet devices.txt --key keys/rustnet-signing.key \
    --canary 1 --abort-after 2 --confirm   # staged rollout across a fleet
rustnet debug bp 12 0            # breakpoint at method 12, IL offset 0
rustnet debug stack              # stack trace while paused
```

## 6. GUI and editor

- **Workbench**: `dotnet run --project dotnet/tools/RustNet.Workbench` —
  device dashboard, app flashing, config, WiFi, boot image, live logs,
  profiler, display viewer, OTA, multi-chip firmware builds.
- **VSCode**: `cd vscode-extension && npm install && npm run compile`,
  then F5. Commands under "RustNet:" in the palette.

## 7. Packages

```bash
cd my-driver && rustnet pkg init my-driver
rustnet pkg pack && rustnet pkg publish
rustnet pkg search neopixel
rustnet pkg install rustnet-driver-neopixel
```

See `docs/packages.md` for the manifest and registry layout.
