# Cloud IoT connectivity

`RustNet.Cloud` provides device clients for the major IoT platforms over
the existing MQTT/HTTP stack. The protocol assembly (auth tokens, topic
conventions, payload shaping) — where bugs actually live — is pure managed
code, unit-tested against real crypto; the transport reuses the verified
MQTT/HTTP clients. Live connections need a TLS-capable build (ESP-IDF ships
mbedTLS); the virtual device exercises token signing and the connect path.

## Azure IoT Hub

MQTT with SAS-token auth. The SAS signature is HMAC-SHA256 over
`urlencode(resourceUri)\n{expiry}`, computed on-device via the
`RustNet.Security.Hmac` intrinsic.

```csharp
var hub = new AzureIotHub("my-hub.azure-devices.net", "device-01");
byte[] key = Convert.FromBase64String(devicePrimaryKeyBase64);
hub.Connect(key, RustNet.Sys.Rtc.Epoch() + 3600);   // SAS token + MQTT connect
hub.SubscribeCommands();                             // cloud-to-device
hub.SendTelemetry("{\"temp\":21.5}");                // device-to-cloud
```

Topics: telemetry `devices/{id}/messages/events/`, C2D
`devices/{id}/messages/devicebound/#`.

## AWS IoT Core

MQTT (mutual-TLS with an X.509 device cert in production) + Device Shadow.

```csharp
var aws = new AwsIotCore("abc123.iot.us-east-1.amazonaws.com", "thermostat");
aws.Connect();
aws.SubscribeShadowDelta();
aws.UpdateShadow("{\"temp\":21}");   // $aws/things/thermostat/shadow/update
```

## Google Cloud IoT Core

MQTT with a JWT in the password field. The client builds the JWT
header/claims (base64url) and signs HS256 with a shared secret; RS256/ES256
over the device key is the production integration point.

```csharp
var gcp = new GoogleIotCore("proj", "us-central1", "reg1", "dev-9");
gcp.Connect(secret, iat, iat + 3600);
gcp.SubscribeConfig();               // /devices/dev-9/config
gcp.SendTelemetry("{\"temp\":21}");  // /devices/dev-9/events
```

## IFTTT Webhooks (Maker)

Fire an applet event with up to three values over HTTP.

```csharp
var ifttt = new Ifttt("my-webhook-key");
ifttt.Trigger("temp_alert", "28.5", "kitchen", "");
```

## Security building blocks (`RustNet.Security`)

- `Hmac.Sha256Base64(byte[] key, string data)` — HMAC-SHA256 → base64
  (intrinsic; used for Azure SAS and GCP JWT signing)
- `Url.Encode(string)` — RFC 3986 percent-encoding (pure managed)

Template: `rustnet new cloud-telemetry <name>`.
