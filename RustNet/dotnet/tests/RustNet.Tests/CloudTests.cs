using System.Security.Cryptography;
using System.Text;
using RustNet.Cloud;
using RustNet.Security;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The cloud clients are mostly deterministic protocol assembly — the part
/// where bugs actually live. These tests validate that logic against
/// ground truth (real HMAC-SHA256 for the Azure SAS token / Google JWT),
/// so the on-device path (which swaps in the HMAC intrinsic) is trustworthy.
/// </summary>
public class CloudTests
{
    [Fact]
    public void UrlEncodeMatchesRfc3986()
    {
        Assert.Equal("a%20b", Url.Encode("a b"));
        Assert.Equal("hub.azure-devices.net%2Fdevices%2Fdev1",
            Url.Encode("hub.azure-devices.net/devices/dev1"));
        Assert.Equal("keep-_.~", Url.Encode("keep-_.~"));
    }

    [Fact]
    public void AzureSasTokenMatchesRealHmac()
    {
        // Reproduce Azure's documented SAS scheme with real crypto and
        // check the client assembles an identical token.
        var hub = new AzureIotHub("myhub.azure-devices.net", "sensor-01");
        byte[] key = Encoding.UTF8.GetBytes("supersecretdevicekey");
        long expiry = 1_800_000_000;

        string stringToSign = AzureIotHub.StringToSign(hub.ResourceUri, expiry);
        string expectedSig;
        using (var h = new HMACSHA256(key))
        {
            expectedSig = System.Convert.ToBase64String(
                h.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        }
        string token = AzureIotHub.BuildSasToken(hub.ResourceUri, expectedSig, expiry);

        Assert.StartsWith("SharedAccessSignature sr=", token);
        Assert.Contains("myhub.azure-devices.net%2Fdevices%2Fsensor-01", token);
        Assert.Contains("&se=1800000000", token);
        Assert.Contains("&sig=" + Url.Encode(expectedSig), token);
        // Username Azure expects on the MQTT CONNECT.
        Assert.Equal("myhub.azure-devices.net/sensor-01/?api-version=2021-04-12", hub.Username);
        Assert.Equal("devices/sensor-01/messages/events/", hub.TelemetryTopic);
    }

    [Fact]
    public void AwsShadowTopicsAndEnvelope()
    {
        var aws = new AwsIotCore("abc123.iot.us-east-1.amazonaws.com", "thermostat");
        Assert.Equal("$aws/things/thermostat/shadow/update", aws.ShadowUpdateTopic);
        Assert.Equal("$aws/things/thermostat/shadow/update/delta", aws.ShadowDeltaTopic);
        Assert.Equal("{\"state\":{\"reported\":{\"temp\":21}}}",
            AwsIotCore.ShadowReported("{\"temp\":21}"));
    }

    [Fact]
    public void GoogleJwtMatchesRealHmacAndClientId()
    {
        var gcp = new GoogleIotCore("my-project", "us-central1", "reg1", "dev-9");
        Assert.Equal(
            "projects/my-project/locations/us-central1/registries/reg1/devices/dev-9",
            gcp.ClientId);
        Assert.Equal("/devices/dev-9/events", gcp.EventsTopic);

        byte[] secret = Encoding.UTF8.GetBytes("shared-secret");
        long iat = 1_800_000_000, exp = 1_800_003_600;
        string signingInput = gcp.JwtSigningInput(iat, exp);

        // Header + claims are valid base64url JSON.
        string[] parts = signingInput.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.Equal("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", DecodeB64Url(parts[0]));
        Assert.Contains("\"aud\":\"my-project\"", DecodeB64Url(parts[1]));

        // The HS256 signature the client builds must equal real crypto.
        string jwt = BuildJwtWithRealHmac(gcp, secret, iat, exp);
        string expected;
        using (var h = new HMACSHA256(secret))
        {
            byte[] sig = h.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
            expected = System.Convert.ToBase64String(sig)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }
        Assert.Equal(string.Concat(signingInput, ".", expected), jwt);
    }

    [Fact]
    public void IftttPathAndBody()
    {
        var ifttt = new Ifttt("mywebhookkey");
        Assert.Equal("maker.ifttt.com", ifttt.Host);
        Assert.Equal("/trigger/temp_alert/with/key/mywebhookkey", ifttt.Path("temp_alert"));
        Assert.Equal("{\"value1\":\"28.5\",\"value2\":\"kitchen\",\"value3\":\"\"}",
            Ifttt.Body("28.5", "kitchen", ""));
    }

    // Reproduce GoogleIotCore.CreateJwtHs256 using real HMAC (the device
    // version calls the Hmac.Sha256Base64 intrinsic instead).
    private static string BuildJwtWithRealHmac(GoogleIotCore gcp, byte[] secret, long iat, long exp)
    {
        string signingInput = gcp.JwtSigningInput(iat, exp);
        string sigStd;
        using (var h = new HMACSHA256(secret))
        {
            sigStd = System.Convert.ToBase64String(
                h.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        }
        string sigUrl = sigStd.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return string.Concat(signingInput, ".", sigUrl);
    }

    private static string DecodeB64Url(string s)
    {
        string b = s.Replace('-', '+').Replace('_', '/');
        switch (b.Length % 4)
        {
            case 2: b += "=="; break;
            case 3: b += "="; break;
        }
        return Encoding.UTF8.GetString(System.Convert.FromBase64String(b));
    }
}
