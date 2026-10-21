using RustNet.Native;

namespace NpcDemo;

/// <summary>
/// Calls the four procedures of runtime/npc-sample-mcu on a microcontroller
/// (RP2040 or Meadow F7), after `rustnet native build` + `native push`. Prints
/// one line per check; every line should end in "ok".
/// </summary>
public static class Program
{
    public static void Main()
    {
        Console.WriteLine("npc demo: supported=" + (Npc.IsSupported() ? "yes" : "no"));
        int[] values = new int[3];
        values[0] = 40;
        values[1] = 2;
        values[2] = 100000;
        int sum = new NativeProcedure("sample", "rustnet_sum_u32").InvokeInt32(values);
        Console.WriteLine($"npc sum={sum} " + (sum == 100042 ? "ok" : "BAD"));

        byte[] crc = new NativeProcedure("sample", "rustnet_crc32").Invoke(System.Text.Encoding.UTF8.GetBytes("123456789"));
        long c = (crc[0] | (crc[1] << 8) | (crc[2] << 16) | (crc[3] << 24)) & 0xFFFFFFFFL;
        Console.WriteLine($"npc crc32={c} " + (c == 3421780262L ? "ok" : "BAD"));

        string rev = System.Text.Encoding.UTF8.GetString(new NativeProcedure("sample", "rustnet_reverse").Invoke(System.Text.Encoding.UTF8.GetBytes("RustNet")));
        Console.WriteLine("npc reverse=" + rev + " " + (rev == "teNtsuR" ? "ok" : "BAD"));

        try
        {
            new NativeProcedure("sample", "rustnet_fail").Invoke(new byte[0]);
            Console.WriteLine("npc fail BAD (no error)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("npc fail " + (ex.Message.Contains("-7") ? "ok" : "BAD " + ex.Message));
        }
    }
}
