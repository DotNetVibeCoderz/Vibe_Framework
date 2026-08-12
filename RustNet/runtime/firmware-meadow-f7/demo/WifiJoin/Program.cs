using RustNet.Core;
using RustNet.Net;
using RustNet.Threading;

namespace WifiJoin;

/// <summary>
/// Reports the Meadow's WiFi state from C#, through the ESP32 coprocessor.
/// </summary>
/// <remarks>
/// <para>
/// No SSID and no password appear anywhere in this file, and that is the
/// point. Credentials are configured on the device — <c>rustnet wifi --ssid
/// ... --psk ...</c> — and the firmware joins from flash at boot. An
/// application image is a file that gets copied, mailed and committed;
/// anything baked into it travels with it.
/// </para>
/// <para>
/// So the app asks rather than asserts. <see cref="Wifi.GetSsid"/> reports
/// the network actually associated, which on this board is the one the
/// coprocessor is on — not an echo of a value this code supplied.
/// </para>
/// </remarks>
internal static class Program
{
    private static void Main()
    {
        Console.WriteLine("Meadow F7 WiFi, via the ESP32 coprocessor");

        // Ten polls, two seconds apart. A join that is already done answers
        // the first one; a board that is still negotiating DHCP takes a few.
        for (int i = 0; i < 10; i++)
        {
            bool up = Wifi.IsConnected();
            string ssid = Wifi.GetSsid();
            string ip = Wifi.GetIp();

            if (up)
            {
                Console.WriteLine($"[{i}] on '{ssid}' as {ip}");
            }
            else
            {
                Console.WriteLine($"[{i}] not associated"
                    + " — configure with: rustnet wifi --ssid <name> --psk <password>");
            }

            Sleep.Ms(2000);
        }

        Console.WriteLine("done");
    }
}
