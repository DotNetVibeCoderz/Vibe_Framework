# Desktop simulator

The firmware's default `chip-host` build **is** the simulator: a full
virtual device (interpreter, RNDP server, VFS, display framebuffer,
simulated buses) running as a desktop process. Everything the tools do
against real hardware works against it.

```bash
cargo build -p rustnet-firmware
./target/debug/rustnet-firmware --ephemeral          # random port, throwaway home
./target/debug/rustnet-firmware --port 7878 --home ~/.rustnet-dev
```

## Simulated peripherals

| Peripheral | Behavior |
|---|---|
| GPIO 0–47 | in-memory pins, edge interrupts, drivable externally |
| UART 0 | loopback; 1–2 capture TX / injectable RX |
| I2C/SPI | attachable simulated devices |
| ADC | settable raw values (default mid-scale) |
| CAN 0–1 | loopback mode + acceptance filters + injectable RX |
| Modbus | in-firmware RTU slave, unit id 1 |
| 1-Wire 0–1 | simulated DS18B20 at 25.5 °C on bus 0 |
| RTC | settable clock (defaults to 2026-01-01) + alarm |
| Watchdog | timeout tracking with `expired` introspection |
| ExtMemory | 2 MiB QSPI NOR (real erase/write semantics) + 1 MiB SDRAM |
| NetIf | wifi/ethernet/ppp/cellular with deterministic addresses |
| Display | RGB565 framebuffer, captured over RNDP |
| Signal | records generated trains; injectable capture/echo |

## Inspecting the device

- `rustnet info` — chip/board/version/uptime JSON
- `rustnet io` — **I/O snapshot JSON**: pin levels, CAN RX depth, netif
  states, watchdog, display geometry (RNDP command 0x53)
- `rustnet logs -n 200` / `--follow`
- `rustnet display capture -o screen.ppm`

## VSCode simulator panel

Command palette → **RustNet: Open Simulator Panel** (with
**RustNet: Start Virtual Device** to launch one). The panel polls the
device once a second and shows:

- the live display framebuffer (pixel-accurate canvas)
- GPIO pin states (green = high)
- network interfaces table + CAN RX depth + watchdog state
- the rolling log tail

Configuration: `rustnet.cliPath`, `rustnet.device`
(`tcp:127.0.0.1:7878` or `serial:COM5:115200`), `rustnet.signingKey`.
The Workbench GUI offers the same views plus flashing/OTA controls.
