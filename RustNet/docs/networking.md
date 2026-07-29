# Networking: WiFi, Ethernet, PPP, Cellular

Network interfaces are modeled by the `NetInterface` HAL trait
(`rustnet-hal/src/netif.rs`): one implementation per link type, all
managed through the same `bring_up / bring_down / status` lifecycle. The
socket layer above (HTTP, MQTT, web server in `rustnet-net`) routes over
whichever interface is up.

## Managed API (`RustNet.Net`)

```csharp
// Wired Ethernet — DHCP by default, static optional
Ethernet.Up();                          // or Ethernet.Up("192.168.1.60", "192.168.1.1")
string ip = Ethernet.GetIp();
Ethernet.Down();

// PPP over a serial modem
Ppp.Up(uartPort: 1, username: "", password: "");

// Cellular (LTE/NB-IoT) — APN required
if (Cellular.Up("internet", "", ""))
{
    Console.WriteLine(Cellular.GetOperator());   // network operator name
    Console.WriteLine(Cellular.GetRssi());       // dBm, closer to 0 = better
}

// WiFi
Wifi.Connect("MyNet", "password");
bool joined = Wifi.IsConnected();
string ssid = Wifi.GetSsid();           // what the interface is *actually* on
string myIp = Wifi.GetIp();             // "" while unassigned
```

`Up` returns `false` (and logs the reason) instead of throwing when the
link cannot come up — connection loss is a normal condition on devices.

### `Wifi.GetSsid()` reports the join, not the request

`GetSsid` asks the WiFi `NetInterface` what it is associated with and only
falls back to whatever `Wifi.Connect` recorded if the interface reports
nothing. That distinction matters on ESP32, where **the firmware joins the
radio at boot** from credentials stored with `rustnet wifi --ssid <s> --psk
<p>` — managed `Wifi.Connect` is bookkeeping there and its SSID argument is
not what the radio used. `GetSsid`/`GetIp` are therefore the only way an app
can display the network it is really on. `GetIp` throws if the board exposes
no WiFi interface at all, so wrap it if you support such boards.

`Wifi.Connect` also forwards the SSID/PSK to the interface's `bring_up`; a
`NotSupported` result is expected and ignored on targets that own the join
themselves.

## Status & simulator

Interface state is visible without app code:

- `rustnet io` — JSON snapshot including every netif (`kind`, `up`, `ip`)
- VSCode command **RustNet: Open Simulator Panel** — live table

On the host simulator each interface hands out a deterministic address
(WiFi 192.168.1.40 at −55 dBm echoing the requested SSID, Ethernet
192.168.1.50, PPP 10.64.0.2, Cellular 100.66.0.2 with operator
"RustNet-Cell" at −67 dBm), so apps and tests get stable values. Real
chips implement `NetInterface` against their vendor stack (lwIP netif,
PPP daemon, or AT-command modem driver) without touching managed code.

The ESP32 firmware implements the WiFi interface read-only
(`firmware-esp32/src/board.rs`, `IdfStaNetif`): `status()` reports the live
SSID/RSSI from `esp_wifi_sta_get_ap_info` and the address from the
`WIFI_STA_DEF` netif, while `bring_up`/`bring_down` stay `NotSupported` on
purpose — the boot path owns the radio, and RNDP may be riding on it.
`templates/mqtt-dashboard` shows both values live on the panel.
