# RNDP — RustNet Device Protocol

Framed request/response protocol between tools and devices. Transports:
TCP (virtual device), USB-CDC or UART (hardware), and **BLE**
(`ble:<address>`). Reference implementations: `runtime/firmware/src/proto.rs`
(device, Rust) and `dotnet/tools/RustNet.Deploy/Rndp.cs` (host, C#).

Over BLE the RNDP byte stream is fragmented into ATT-MTU-sized GATT packets and
reassembled by `BleTransport` (`RustNet.Deploy`), so the framing above is
unchanged — only the packetisation differs. The radio itself is supplied by the
platform through an `IBlePacketLink` registered on
`TransportFactory.BleLinkProvider`.

## Frame

```
0x52 0x4E | code:u8 | len:u32 LE | payload[len] | crc:u16 LE
```

CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) over `code|len|payload`.
Requests carry command codes; responses carry status `0x00 OK` /
`0x01 ERR` (payload = UTF-8 message).

## Commands

| Code | Command | Payload → Response |
|---|---|---|
| 0x01 | PING | — → protocol version (u8) |
| 0x02 | INFO | — → JSON (chip, board, version, uptime, apps, wifi, ...) |
| 0x03 | PROVISION_KEY | RSA public key (PKCS#1 DER); once only |
| 0x10 | LIST_APPS | — → JSON array {name,size,active} |
| 0x11 | FLASH_APP | name_len:u8, name, RNSB container → verifies signature |
| 0x12 | ERASE_APP | name |
| 0x13 | START_APP | name (re-verifies before executing) |
| 0x14 | STOP_APP | — |
| 0x20 | FLASH_DATA | path_len:u16, path, bytes (lands under /data) |
| 0x21 | READ_DATA | path → bytes |
| 0x30 | SET_CONFIG | "key\nvalue" (AES-encrypted at rest) |
| 0x31 | GET_CONFIG | key → value |
| 0x32 | WIFI_CONFIG | "ssid\npsk" (errors on WiFi-less chips) |
| 0x40 | GET_LOGS | max:u32 → text lines |
| 0x41 | GET_PERF | — → JSON counters |
| 0x50 | SET_BOOT_IMAGE | w:u16, h:u16, RGB565 pixels |
| 0x51 | GET_BOOT_IMAGE | — → same layout |
| 0x52 | GET_DISPLAY | — → w:u16, h:u16, RGB565 LE framebuffer |
| 0x53 | IO_STATE | — → JSON: pin levels, CAN RX depth, netif states, watchdog, display geometry (simulator panel) |
| 0x60-0x64 | OTA_BEGIN / DATA / END / CONFIRM / ROLLBACK | streamed A/B update |
| 0x70 | DEBUG_SET_BP | method:u32, il_offset:u32 (applies at app start) |
| 0x73 | DEBUG_STACK | — → stack trace lines (only while paused) |
| 0x7F | REBOOT | — |

## Signed image container (RNSB)

```
"RNSB" | version:u16 | kind:u8 | chip:u8 | payload_len:u32 | sig_len:u32
| payload | signature
```

kind: 0 firmware, 1 app, 2 data, 3 boot image. chip: 0 any, 1 esp32,
2 stm32, 3 ti, 4 nxp, 5 host-sim. Signature: RSA PKCS#1 v1.5 + SHA-256
over header (sig_len zeroed) + payload.
