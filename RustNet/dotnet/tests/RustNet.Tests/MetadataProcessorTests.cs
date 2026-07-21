using RustNet.Deploy;
using RustNet.MetadataProcessor;
using Xunit;

namespace RustNet.Tests;

public class MetadataProcessorTests
{
    private static string SampleAppDll =>
        Path.Combine(AppContext.BaseDirectory, "SampleApp.dll");

    [Fact]
    public void CompilesSampleAppToRnx()
    {
        byte[] rnx = RnxCompiler.Compile(SampleAppDll, out _);
        Assert.True(rnx.Length > 64, "rnx suspiciously small");
        Assert.Equal("RNX1"u8.ToArray(), rnx[..4]);
        Assert.Equal(6, BitConverter.ToUInt16(rnx, 4)); // RNX v6: custom attributes
    }

    [Fact]
    public void GenericMembersCanonicalizeToArityNames()
    {
        byte[] rnx = RnxCompiler.Compile(SampleAppDll, out _);
        string blob = System.Text.Encoding.UTF8.GetString(rnx);
        Assert.Contains("System.Collections.Generic.List`1::Add(object)", blob);
        Assert.Contains("System.Collections.Generic.Dictionary`2::ContainsKey(object)", blob);
        Assert.Contains("System.Linq.Enumerable::Where(object,object)", blob);
        Assert.Contains("System.Func`2::.ctor(object,i)", blob);
    }

    [Fact]
    public void RnxContainsEntryPointAndInternalCalls()
    {
        byte[] rnx = RnxCompiler.Compile(SampleAppDll, out _);
        string blob = System.Text.Encoding.UTF8.GetString(rnx);
        // Canonical names of internal calls the interpreter/host must serve.
        Assert.Contains("System.Console::WriteLine(string)", blob);
        Assert.Contains("RustNet.Hal.Gpio::Write(i4,bool)", blob);
        Assert.Contains("RustNet.IO.FileSystem::WriteAllText(string,string)", blob);
        // Driver code compiled from RustNet.Devices is merged in.
        Assert.Contains("RustNet.Devices.GpsNmeaParser", blob);
    }

    [Fact]
    public void CrcMatchesFirmwareTestVector()
    {
        // CRC-16/CCITT-FALSE("123456789") = 0x29B1 (same vector as the Rust side)
        Assert.Equal(0x29B1, RndpFrame.Crc16("123456789"u8.ToArray()));
    }

    [Fact]
    public void FrameRoundtrips()
    {
        var frame = new RndpFrame(Cmd.FlashApp, [1, 2, 3, 4, 5]);
        byte[] encoded = frame.Encode();
        int used = RndpFrame.TryDecode(encoded, out var decoded);
        Assert.Equal(encoded.Length, used);
        Assert.Equal(frame.Code, decoded!.Code);
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public void SealedImageHasExpectedLayout()
    {
        var (priv, _) = Signing.GenerateKeypair();
        byte[] payload = [10, 20, 30];
        byte[] sealedImage = Signing.Seal(ImageKind.App, ChipFamily.HostSim, payload, priv);
        Assert.Equal((byte)'R', sealedImage[0]);
        Assert.Equal((byte)'B', sealedImage[3]);
        Assert.Equal((byte)ImageKind.App, sealedImage[6]);
        Assert.Equal((byte)ChipFamily.HostSim, sealedImage[7]);
        Assert.Equal(3u, BitConverter.ToUInt32(sealedImage, 8));
        uint sigLen = BitConverter.ToUInt32(sealedImage, 12);
        Assert.Equal(16 + 3 + (int)sigLen, sealedImage.Length);
        Assert.Equal(256u, sigLen); // RSA-2048
    }
}
