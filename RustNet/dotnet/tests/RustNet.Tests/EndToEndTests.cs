using System.Diagnostics;
using RustNet.Deploy;
using RustNet.MetadataProcessor;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// The golden path: C# app -> MetadataProcessor -> signed RNX -> virtual
/// device (Rust firmware over RNDP/TCP) -> interpreter runs it -> logs and
/// files prove it. Requires the firmware binary; build it with
/// `cargo build -p rustnet-firmware` (tests skip when missing).
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly Process? _firmware;
    private readonly int _port;

    private static string? FindFirmware()
    {
        string? env = Environment.GetEnvironmentVariable("RUSTNET_FIRMWARE");
        if (env is not null && File.Exists(env))
        {
            return env;
        }
        // Walk up from test bin dir to the repo root.
        //
        // Both file names, because a cargo build on Linux produces
        // `rustnet-firmware` with no extension. Looking only for the `.exe`
        // did not fail there — every end-to-end test simply *skipped*, and the
        // run went green having exercised none of them. A missing dependency
        // that turns tests off is worse than one that turns them red.
        string[] names = ["rustnet-firmware.exe", "rustnet-firmware"];
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (string profile in new[] { "debug", "release" })
            {
                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir.FullName, "target", profile, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            dir = dir.Parent;
        }
        return null;
    }

    public EndToEndTests()
    {
        string? fw = FindFirmware();
        if (fw is null)
        {
            return; // tests will skip
        }
        _port = 17000 + Random.Shared.Next(1000);
        _firmware = Process.Start(new ProcessStartInfo
        {
            FileName = fw,
            Arguments = $"--port {_port} --ephemeral",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        // Wait for the listening banner.
        string? line = _firmware!.StandardOutput.ReadLine();
        Assert.Contains("listening", line);
    }

    private RndpClient Client() => RndpClient.Connect($"tcp:127.0.0.1:{_port}");

    [SkippableFact]
    public void FullDeviceLifecycle()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        using var client = Client();

        // 1. Ping + info
        Assert.Equal(1, client.Ping());
        string info = client.Info();
        Assert.Contains("host-sim", info);

        // 2. Provision signing key
        var (priv, pub) = Signing.GenerateKeypair();
        client.ProvisionKey(pub);

        // 3. Compile the C# sample to RNX, seal, flash
        string dll = Path.Combine(AppContext.BaseDirectory, "SampleApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        byte[] sealedApp = Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv);
        client.FlashApp("sample", sealedApp);
        Assert.Contains("\"name\":\"sample\"", client.ListApps());

        // 4. Run it and wait for completion in the logs
        client.StartApp("sample");
        string logs = "";
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            logs = client.GetLogs(200);
            if (logs.Contains("exited") || logs.Contains("crashed"))
            {
                break;
            }
        }

        // 5. The interpreter really executed the C# code
        Assert.DoesNotContain("crashed", logs);
        Assert.Contains("SampleApp starting", logs);
        Assert.Contains("fib(16)=987", logs);
        Assert.Contains("sum=55", logs);
        Assert.Contains("gps sats=8", logs);
        // v0.2 features: exceptions, collections, delegates/LINQ, regex, interpolation
        Assert.Contains("finally ran", logs);
        Assert.Contains("caught: boom", logs);
        Assert.Contains("listSum=150 count=5", logs);
        Assert.Contains("dict beta=2 has alpha=True", logs);
        Assert.Contains("evenSum=150", logs);
        Assert.Contains("sb:42", logs);
        Assert.Contains("regex=True", logs);
        Assert.Contains("interp temp=21C", logs);
        Assert.Contains("SampleApp finished", logs);
        Assert.Contains("sample app done", logs);

        // 6. The app's file write is visible through the data channel
        string fileContent = System.Text.Encoding.UTF8.GetString(client.ReadData("sample.txt"));
        Assert.StartsWith("adc=", fileContent);

        // 7. Perf counters moved
        Assert.DoesNotContain("\"il_instructions\":0,", client.GetPerf());

        // 8. Config + wifi + boot image round trips
        client.SetConfig("cloud.token", "tok-123");
        Assert.Equal("tok-123", client.GetConfig("cloud.token"));
        client.ConfigureWifi("TestNet", "pass1234");
        Assert.Equal("TestNet", client.GetConfig("wifi.ssid"));
        byte[] img = new byte[2 * 2 * 2];
        client.SetBootImage(2, 2, img);
        Assert.Equal(4 + img.Length, client.GetBootImage().Length);

        // 9. OTA with a signed firmware image
        byte[] fwImage = Signing.Seal(ImageKind.Firmware, ChipFamily.HostSim,
            "new firmware payload"u8.ToArray(), priv);
        client.OtaUpdate(fwImage);
        Assert.Equal("B", client.OtaConfirm());

        // 10. Cleanup
        client.EraseApp("sample");
        Assert.Equal("[]", client.ListApps());
    }

    [SkippableFact]
    public void SysAppFeatureMatrix()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        using var client = Client();
        var (priv, pub) = Signing.GenerateKeypair();
        try
        {
            client.ProvisionKey(pub);
        }
        catch (DeviceException)
        {
            Skip.If(true, "device already provisioned by a parallel test");
        }

        string dll = Path.Combine(AppContext.BaseDirectory, "SysApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        byte[] sealedApp = Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv);
        client.FlashApp("sysapp", sealedApp);
        client.StartApp("sysapp");
        string logs = "";
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            logs = client.GetLogs(300);
            if (logs.Contains("exited") || logs.Contains("crashed"))
            {
                break;
            }
        }

        Assert.DoesNotContain("crashed", logs);
        // Field buses
        Assert.Contains("can rx id=291 len=3", logs);
        Assert.Contains("modbus reg100=1234", logs);
        Assert.Contains("modbus coil5=1", logs);
        Assert.Contains("onewire temp=2550", logs);
        // Networking
        Assert.Contains("eth ip=192.168.1.50 up=True", logs);
        Assert.Contains("cell op=RustNet-Cell rssi=-67", logs);
        // Database
        Assert.Contains("db count=3 hottest=attic", logs);
        // Secondary index (v1.0): indexed equality lookup
        Assert.Contains("db indexed room=attic", logs);
        // WAL persistence (v1.0): data survives reopen
        Assert.Contains("db reopened count=3", logs);
        // System: RTC, watchdog, external memory, power, device info
        Assert.Contains("rtc now=2026-08-08 12:00:00", logs);
        Assert.Contains("watchdog running=True", logs);
        Assert.Contains("extmem kind=qspi-flash b0=171", logs);
        Assert.Contains("wake reason=power-on", logs);
        Assert.Contains("device chip=host-sim", logs);
        // Signal control
        Assert.Contains("sonar mm=99", logs);
        // Serializers + streams + UI
        Assert.Contains("json temp=21.5 tags=2", logs);
        Assert.Contains("xml interval=30 name=boiler", logs);
        Assert.Contains("device=boiler-1", logs);
        Assert.Contains("stream int=42 str=stream", logs);
        Assert.Contains("ui title=Boiler", logs);
        Assert.Contains("SysApp finished", logs);

        // The IO-state snapshot (simulator panel) reflects the app's work.
        string io = client.IoState();
        Assert.Contains("\"kind\":\"ethernet\",\"up\":true", io);
        Assert.Contains("\"display\":{\"width\":160,\"height\":128}", io);

        client.EraseApp("sysapp");
    }

    [SkippableFact]
    public void LangAppFeatureMatrix()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        using var client = Client();
        var (priv, pub) = Signing.GenerateKeypair();
        client.ProvisionKey(pub);

        string dll = Path.Combine(AppContext.BaseDirectory, "LangApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        byte[] sealedApp = Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv);
        client.FlashApp("langapp", sealedApp);
        client.StartApp("langapp");
        string logs = "";
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            logs = client.GetLogs(300);
            if (logs.Contains("exited") || logs.Contains("crashed"))
            {
                break;
            }
        }

        Assert.DoesNotContain("crashed", logs);
        // Inheritance + virtual dispatch + ToString overrides
        Assert.Contains("name=circle str=<circle>", logs);
        Assert.Contains("name=square str=<square>", logs);
        // Interface dispatch
        Assert.Contains("total=7", logs);
        Assert.Contains("square s=2", logs);
        // Casts along the chain
        Assert.Contains("is-circle", logs);
        Assert.Contains("as-miss", logs);
        Assert.Contains("iface-isinst", logs);
        // Inherited fields + base ctor
        Assert.Contains("sum=42", logs);
        // User generics (erased)
        Assert.Contains("box=42,gen", logs);
        // Exception filters
        Assert.Contains("filtered-2", logs);
        Assert.DoesNotContain("wrong-filter", logs);
        // async/await
        Assert.Contains("async a=42", logs);
        Assert.Contains("async b=86", logs);
        Assert.Contains("async caught=async-boom", logs);
        Assert.Contains("async done", logs);
        // reflection: GetType() + Type.Name/BaseType (v0.9)
        Assert.Contains("reflect name=Circle base=Shape", logs);
        Assert.Contains("reflect str=String", logs);
        // reflection: member enumeration (GetMethods/GetMethod/MethodInfo.Name)
        Assert.Contains("reflect method=Name area=Area", logs);
        // reflection: typeof(T) — identity + Name/Namespace (v0.9)
        Assert.Contains("typeof name=Circle ns=LangApp", logs);
        Assert.Contains("typeof same=yes int=Int32", logs);
        // reflection: MethodInfo.Invoke (non-void + void/boxed-arg) (v0.9)
        Assert.Contains("invoke area=12 scaled=48", logs);
        // reflection: Type.GetFields + FieldInfo.Name/GetValue/SetValue (v0.9)
        Assert.Contains("field name=Radius val=4 now=5 count=1", logs);
        // reflection: Type.GetProperties + PropertyInfo.Name/GetValue/SetValue (v0.9)
        Assert.Contains("prop name=Color val=red count=1", logs);
        // reflection: custom attributes (Type.GetCustomAttributes, ctor + named args) (v0.9)
        Assert.Contains("attr count=1 label=shape rank=7", logs);
        // inline-array span lowering: string.Concat with 5 parts (v0.9)
        Assert.Contains("concat5=circle|square|end", logs);
        Assert.Contains("LangApp finished", logs);

        client.EraseApp("langapp");
    }

    /// <summary>Provision, flash and run one test app; returns its logs once it exits.</summary>
    private string RunApp(string assembly, string name, int timeoutSeconds, Action<RndpClient, byte[]>? beforeStart = null)
    {
        using var client = Client();
        var (priv, pub) = Signing.GenerateKeypair();
        client.ProvisionKey(pub);
        beforeStart?.Invoke(client, priv);
        string dll = Path.Combine(AppContext.BaseDirectory, assembly);
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        client.FlashApp(name, Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv));
        client.StartApp(name);
        string logs = "";
        for (int i = 0; i < timeoutSeconds * 10; i++)
        {
            Thread.Sleep(100);
            logs = client.GetLogs(300);
            if (logs.Contains("exited") || logs.Contains("crashed"))
            {
                break;
            }
        }
        client.EraseApp(name);
        return logs;
    }

    [SkippableFact]
    public void LibAppFeatureMatrix()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        // The NPC sample module is a cdylib built by `cargo build -p rustnet-npc-sample`.
        string? module = FindNativeSample();
        string logs = RunApp("LibApp.dll", "libapp", 120, (client, priv) =>
        {
            if (module is not null)
            {
                byte[] sealedModule = Signing.Seal(ImageKind.Native, ChipFamily.HostSim, File.ReadAllBytes(module), priv);
                client.FlashData("native/sample.rnsb", sealedModule);
            }
        });

        Assert.DoesNotContain("crashed", logs);
        // Math surface the libraries need (banker's rounding, Log with a base, culture-invariant format)
        Assert.Contains("math tanh=462 asin=524 sign=-1 log2=3000 round=2 trunc=-2", logs);
        Assert.Contains("invariant=0.5", logs);
        // Lambdas in two classes (nested <>c types named through their owner) + null on an unassigned field
        Assert.Contains("dialect2 nan=yes inf=yes char=xQy longidx=9 groups=10,15", logs);
        Assert.Contains("dialect lambdas=30,35 nullfield=null notnull=no type=LambdaOwnerA", logs);
        // GNSS NMEA decoder (v1.1)
        Assert.Contains("gnss lat=48117 lon=11517 alt=545400 sats=8", logs);
        Assert.Contains("gnss date=1994-03-23 time=12:35:19 kmh=41485 inview=8 pos=2 bad=1", logs);
        // RustNet.ML (v1.1)
        Assert.Contains("ml linear w0=3000 w1=-2000 b=5000", logs);
        Assert.Contains("ml acc softmax=1000 bayes=1000 knn=1000", logs);
        Assert.Contains("ml kmeans distinct=1 iters=ok", logs);
        Assert.Contains("ml xor acc=1000 copy=1000", logs);
        Assert.Contains("ml recommend u0=2 u3=5", logs);
        // TensorFlow Lite (v1.1): TensorFlow-converted models, outputs checked against TensorFlow's interpreter
        Assert.Contains("tflite cnn ops=", logs);
        Assert.Contains("in=1x12x12x1 type=9", logs);
        AssertTfliteMatchesTensorFlow(logs);
        Assert.Contains("sum=1000", logs);
        Assert.Contains("tflite garbage=rejected", logs);
        // NPC (v1.4): signed native module loaded and called from C#
        if (module is not null)
        {
            Assert.Contains("npc sum=100042", logs);
            Assert.Contains("npc crc32=3421780262", logs); // CRC-32 check value of "123456789" (0xCBF43926)
            Assert.Contains("npc reverse=teNtsuR", logs);
            Assert.Contains("npc fail=code-7", logs);
            Assert.Contains("npc missing=reported", logs);
        }
        else
        {
            Assert.Contains("npc absent", logs);
        }
        Assert.Contains("LibApp finished", logs);
    }

    /// <summary>The int8 CNN's output on the device, against TensorFlow's reference (±1 quantisation step).</summary>
    private static void AssertTfliteMatchesTensorFlow(string logs)
    {
        var line = logs.Split('\n').First(l => l.Contains("tflite cnn out="));
        string values = line[(line.IndexOf("out=", StringComparison.Ordinal) + 4)..].Split(' ')[0];
        int[] got = values.Split(',').Select(int.Parse).ToArray();
        string dir = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(dir, "runtime", "rustnet-tflite")))
        {
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("repo root");
        }
        byte[] want = File.ReadAllBytes(Path.Combine(dir, "runtime", "rustnet-tflite", "tests", "models", "cnn_int8.out"));
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
        {
            Assert.InRange(got[i], (sbyte)want[i] - 1, (sbyte)want[i] + 1);
        }
        // The float MLP's probabilities, against TensorFlow's.
        byte[] mlp = File.ReadAllBytes(Path.Combine(dir, "runtime", "rustnet-tflite", "tests", "models", "mlp_float.out"));
        for (int i = 0; i < 3; i++)
        {
            int milli = (int)Math.Round(BitConverter.ToSingle(mlp, i * 4) * 1000.0);
            Assert.Contains($"p{i}={milli}", logs);
        }
    }

    private static string? FindNativeSample()
    {
        string file = OperatingSystem.IsWindows() ? "rustnet_npc_sample.dll"
            : OperatingSystem.IsMacOS() ? "librustnet_npc_sample.dylib" : "librustnet_npc_sample.so";
        string? fw = FindFirmware();
        if (fw is null)
        {
            return null;
        }
        string candidate = Path.Combine(Path.GetDirectoryName(fw)!, file);
        return File.Exists(candidate) ? candidate : null;
    }

    [SkippableFact]
    public void NativeModuleSignedByAnotherKeyIsRefused()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        string? module = FindNativeSample();
        Skip.If(module is null, "rustnet-npc-sample not built");
        var (otherKey, _) = Signing.GenerateKeypair();
        string logs = RunApp("LibApp.dll", "libapp2", 120, (client, _) =>
        {
            byte[] forged = Signing.Seal(ImageKind.Native, ChipFamily.HostSim, File.ReadAllBytes(module!), otherKey);
            client.FlashData("native/sample.rnsb", forged);
        });
        Assert.Contains("npc absent", logs);
        Assert.Contains("signature check failed", logs);
        Assert.DoesNotContain("npc sum=", logs);
    }

    /// <summary>
    /// The community driver packages (packages/rustnet-driver-*), one app per
    /// category, each driving every driver against bus simulators preloaded
    /// with datasheet values. Running them on the device proves the drivers
    /// compile to RNX and behave the same on the interpreter as on the CLR.
    /// </summary>
    [SkippableTheory]
    [InlineData("DriversEnvironmentApp.dll")]
    [InlineData("DriversMotionApp.dll")]
    [InlineData("DriversDisplaysApp.dll")]
    [InlineData("DriversActuatorsApp.dll")]
    public void DriverPackagesRunOnTheDevice(string app)
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        string logs = RunApp(app, "drivers", 180);
        Assert.DoesNotContain("crashed", logs);
        Assert.DoesNotContain("FAIL", logs);
        Assert.Matches(@"drivers finished passed=[1-9]\d* failed=0", logs);
    }

    [SkippableFact]
    public void DebuggerBreakpointCycle()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        using var client = Client();
        var (priv, pub) = Signing.GenerateKeypair();
        client.ProvisionKey(pub);

        string dll = Path.Combine(AppContext.BaseDirectory, "SampleApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);

        // Map a real source line to a breakpoint site via the RNX debug info.
        var di = RnxDebugInfo.Parse(rnx);
        Skip.If(di.EntryMethod is null, "no entry method");
        var entry = di.Methods[(int)di.EntryMethod!.Value];
        Skip.If(entry.Points.Count == 0, "entry method has no sequence points");
        (uint il, uint line) = entry.Points[0];
        uint method = entry.Index;

        byte[] sealedApp = Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv);
        client.FlashApp("dbg", sealedApp);

        // Set the breakpoint before the app runs, then start it.
        client.DebugSetBreakpoint(method, il);
        client.StartApp("dbg");

        // Wait until the interpreter pauses at the breakpoint.
        (uint Method, uint IlOffset)? paused = null;
        for (int i = 0; i < 100 && paused is null; i++)
        {
            Thread.Sleep(50);
            paused = client.DebugState();
        }
        Assert.NotNull(paused);
        Assert.Equal(method, paused!.Value.Method);
        Assert.Equal(il, paused.Value.IlOffset);

        // The stack names the entry method and reports the source line.
        string stack = client.DebugStack();
        Assert.Contains(di.SimpleName(method), stack);
        Assert.Contains($"line {line}", stack);
        // Locals are snapshotted (Main has at least one local).
        string locals = client.DebugLocals();
        Assert.Contains("local_", locals);

        // Single-step advances to a new site, still paused.
        client.DebugStep();
        (uint Method, uint IlOffset)? stepped = null;
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(50);
            stepped = client.DebugState();
            if (stepped is not null && stepped.Value.IlOffset != il)
            {
                break;
            }
        }
        Assert.NotNull(stepped);
        Assert.NotEqual(il, stepped!.Value.IlOffset);

        // Clear the breakpoint and continue to completion.
        client.DebugClearBreakpoint(method, il);
        client.DebugContinue();
        string logs = "";
        for (int i = 0; i < 100; i++)
        {
            Thread.Sleep(100);
            logs = client.GetLogs(200);
            if (logs.Contains("exited") || logs.Contains("crashed"))
            {
                break;
            }
        }
        Assert.DoesNotContain("crashed", logs);
        Assert.Contains("SampleApp finished", logs);
        Assert.Null(client.DebugState());

        client.EraseApp("dbg");
    }

    [SkippableFact]
    public void FleetOtaCampaign()
    {
        string? fw = FindFirmware();
        Skip.If(fw is null, "rustnet-firmware binary not built");

        var (priv, pub) = Signing.GenerateKeypair();
        var procs = new List<Process>();
        var specs = new List<string>();
        try
        {
            // Spin up two independent virtual devices and provision both.
            int basePort = 18000 + Random.Shared.Next(1000);
            for (int i = 0; i < 2; i++)
            {
                int port = basePort + (i * 7);
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = fw,
                    Arguments = $"--port {port} --ephemeral",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                })!;
                Assert.Contains("listening", p.StandardOutput.ReadLine());
                procs.Add(p);
                string spec = $"tcp:127.0.0.1:{port}";
                specs.Add(spec);
                using var c = RndpClient.Connect(spec);
                c.ProvisionKey(pub);
            }

            byte[] fwImage = Signing.Seal(ImageKind.Firmware, ChipFamily.HostSim,
                "fleet firmware payload"u8.ToArray(), priv);

            // Run a canary-gated campaign that pushes + confirms each device.
            var policy = new OtaCampaignPolicy { CanarySize = 1, AbortAfterFailures = 1 };
            var result = OtaCampaign.Run(specs, policy, spec =>
            {
                using var c = RndpClient.Connect(spec);
                c.OtaUpdate(fwImage);
                c.OtaConfirm();
                return new DeviceOutcome(spec, OtaStatus.Confirmed);
            });

            Assert.False(result.Aborted);
            Assert.Equal(2, result.Succeeded);
            Assert.All(result.Outcomes, o => Assert.Equal(OtaStatus.Confirmed, o.Status));
        }
        finally
        {
            foreach (var p in procs)
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
                p.Dispose();
            }
        }
    }

    [SkippableFact]
    public void TamperedAppIsRejected()
    {
        Skip.If(_firmware is null, "rustnet-firmware binary not built");
        using var client = Client();
        var (priv, pub) = Signing.GenerateKeypair();
        try
        {
            client.ProvisionKey(pub);
        }
        catch (DeviceException)
        {
            // Already provisioned by the other test on this device instance —
            // signing below uses a mismatched key then, which must also fail.
        }
        string dll = Path.Combine(AppContext.BaseDirectory, "SampleApp.dll");
        byte[] rnx = RnxCompiler.Compile(dll, out _);
        byte[] sealedApp = Signing.Seal(ImageKind.App, ChipFamily.HostSim, rnx, priv);
        sealedApp[^10] ^= 0x55; // corrupt the signature
        var ex = Assert.Throws<DeviceException>(() => client.FlashApp("evil", sealedApp));
        Assert.Contains("signature", ex.Message);
    }

    public void Dispose()
    {
        if (_firmware is not null && !_firmware.HasExited)
        {
            _firmware.Kill(entireProcessTree: true);
        }
        _firmware?.Dispose();
    }
}
