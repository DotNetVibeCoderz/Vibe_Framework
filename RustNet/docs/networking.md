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

// WiFi (existing API)
Wifi.Connect("MyNet", "password");
```

`Up` returns `false` (and logs the reason) instead of throwing when the
link cannot come up — connection loss is a normal condition on devices.

## Status & simulator

Interface state is visible without app code:

- `rustnet io` — JSON snapshot including every netif (`kind`, `up`, `ip`)
- VSCode command **RustNet: Open Simulator Panel** — live table

On the host simulator each interface hands out a deterministic address
(Ethernet 192.168.1.50, PPP 10.64.0.2, Cellular 100.66.0.2 with operator
"RustNet-Cell" at −67 dBm), so apps and tests get stable values. Real
chips implement `NetInterface` against their vendor stack (lwIP netif,
PPP daemon, or AT-command modem driver) without touching managed code.
