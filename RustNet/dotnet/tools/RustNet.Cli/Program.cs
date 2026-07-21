using RustNet.Cli;

return Cli.Run(args);

namespace RustNet.Cli
{
    public static class Cli
    {
        public static int Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return args.Length == 0 ? 2 : 0;
            }
            try
            {
                return Dispatch(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static int Dispatch(string[] args)
        {
            var rest = args.Skip(1).ToArray();
            return args[0] switch
            {
                "info" => DeviceCommands.Info(rest),
                "io" => DeviceCommands.Io(rest),
                "probe" => DeviceCommands.Probe(rest),
                "logs" => DeviceCommands.Logs(rest),
                "profile" => DeviceCommands.Profile(rest),
                "reboot" => DeviceCommands.Reboot(rest),
                "keys" => DeviceCommands.Keys(rest),
                "provision" => DeviceCommands.Provision(rest),
                "apps" => DeviceCommands.Apps(rest),
                "flash" => BuildCommands.Flash(rest),
                "build" => BuildCommands.Build(rest),
                "run" => BuildCommands.Run(rest),
                "data" => DeviceCommands.Data(rest),
                "config" => DeviceCommands.Config(rest),
                "wifi" => DeviceCommands.Wifi(rest),
                "bootimg" => DeviceCommands.BootImage(rest),
                "display" => DeviceCommands.Display(rest),
                "ota" => DeviceCommands.Ota(rest),
                "debug" => DeviceCommands.Debug(rest),
                "pkg" => PkgCommands.Dispatch(rest),
                "new" => TemplateCommands.New(rest),
                "templates" => TemplateCommands.List(rest),
                "firmware" => FirmwareCommands.Dispatch(rest),
                var other => Unknown(other),
            };
        }

        private static int Unknown(string cmd)
        {
            Console.Error.WriteLine($"unknown command '{cmd}' — run 'rustnet help'");
            return 2;
        }

        public static string? Opt(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        public static bool Flag(string[] args, string name) => args.Contains(name);

        public static string[] Positional(string[] args)
        {
            var result = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith('-'))
                {
                    bool takesValue = args[i] is not ("--follow" or "--watch" or "--release" or "--yes");
                    if (takesValue)
                    {
                        i++; // skip its value
                    }
                    continue;
                }
                result.Add(args[i]);
            }
            return result.ToArray();
        }

        public static string DeviceSpec(string[] args)
            => Opt(args, "--device") ?? Environment.GetEnvironmentVariable("RUSTNET_DEVICE") ?? "tcp:127.0.0.1:7878";

        private static void PrintHelp()
        {
            Console.WriteLine("""
                RustNet CLI — .NET on microcontrollers, powered by a Rust runtime

                Device (all take --device tcp:host:port | serial:COM3[:baud]):
                  info                          device identity & status
                  logs [--follow] [-n 100]      device log buffer
                  profile [--watch]             CPU/heap/GC/instruction counters
                  reboot                        restart the device
                  display capture -o out.ppm    screenshot the (virtual) display

                Security:
                  keys generate --out <dir>     create RSA-2048 signing keypair
                  provision --key <pub.der>     burn the public key into the device
                  config set <key> <value>      encrypted device config
                  config get <key>
                  wifi --ssid <s> --psk <p>     store WiFi credentials

                Apps:
                  build <app.dll> [-o app.rnx]  compile .NET assembly to RNX
                  flash <dll|rnx> --name <n> --key <priv.der> [--chip host-sim]
                  run <name>                    start app and follow logs
                  apps list|start|stop|erase [name]
                  data push <local> <remote>    upload data/asset files
                  data pull <remote> [local]

                Firmware:
                  firmware build --chip esp32|stm32|ti|nxp|host [--release]
                  firmware list                 built firmware images
                  firmware run [--port 7878]    start the virtual device
                  ota push <file> --key <priv.der> [--chip ...]; ota confirm|rollback
                  bootimg set <file.rgb565> --width W --height H | bootimg get -o out.bin

                Developer:
                  new <template> <ProjectName>  create project from template
                  templates                     list available templates
                  debug bp <method#> <ilOffset> | debug stack
                  pkg init|pack|publish|list|search|install ...
                """);
        }
    }
}
