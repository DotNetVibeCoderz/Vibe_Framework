using RustNet.IO;
using RustNet.Sys;

namespace FlashStress;

/// <summary>
/// Forces flashfs compactions in a single-sector store: a 64-byte file rewritten
/// far more times than the sector holds records for, each write read back and
/// checked. A compaction holds every live file in RAM, erases the sector and
/// writes them back, so the provisioning key and the flashed app must come
/// through every one of them — check with a reboot afterwards.
/// </summary>
internal static class Program
{
    private const int Rounds = 3000;
    // Small on purpose: a managed byte[] costs 16 bytes per element on this
    // interpreter, and the board has a 96 KB heap. 3000 rewrites of ~100-byte
    // records fill the 128 KB sector two or three times over.
    private const int Size = 64;

    private static void Main()
    {
        Console.WriteLine($"flashstress: {Rounds} rewrites of a {Size}-byte file");
        byte[] data = new byte[Size];
        int bad = 0;
        long started = Uptime.Ms();
        for (int round = 0; round < Rounds; round++)
        {
            for (int i = 0; i < Size; i++)
            {
                data[i] = (byte)(i * 7 + round);
            }

            try
            {
                FileSystem.WriteAllBytes("/stress.bin", data);
                byte[] back = FileSystem.ReadAllBytes("/stress.bin");
                if (back.Length != Size || back[0] != data[0] || back[Size - 1] != data[Size - 1] || back[Size / 2] != data[Size / 2])
                {
                    bad++;
                    Console.WriteLine($"  round {round}: read back wrong");
                }
            }
            catch (Exception ex)
            {
                bad++;
                Console.WriteLine($"  round {round}: {ex.Message}");
            }
        }

        FileSystem.Delete("/stress.bin");
        Console.WriteLine($"flashstress: {Rounds - bad}/{Rounds} rounds ok in {Uptime.Ms() - started} ms");
    }
}
