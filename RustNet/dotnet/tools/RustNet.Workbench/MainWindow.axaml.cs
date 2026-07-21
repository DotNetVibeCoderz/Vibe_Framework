using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RustNet.Deploy;
using RustNet.MetadataProcessor;

namespace RustNet.Workbench;

public partial class MainWindow : Window
{
    private string _deviceSpec = TransportFactory.DefaultSpec;
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (ChkFollow.IsChecked == true)
            {
                OnRefreshLogs(this, new RoutedEventArgs());
            }
            if (ChkPerfWatch.IsChecked == true)
            {
                OnRefreshPerf(this, new RoutedEventArgs());
            }
        };
        _timer.Start();
    }

    private RndpClient Client() => RndpClient.Connect(_deviceSpec);

    /// <summary>Run device I/O off the UI thread; marshal result/error back.</summary>
    private void Device(Func<RndpClient, string> work, Action<string>? onDone = null)
    {
        string spec = _deviceSpec;
        Task.Run(() =>
        {
            string result;
            bool ok = true;
            try
            {
                using var client = RndpClient.Connect(spec);
                result = work(client);
            }
            catch (Exception ex)
            {
                result = ex.Message;
                ok = false;
            }
            Dispatcher.UIThread.Post(() =>
            {
                LblStatus.Text = ok ? "ok" : $"error: {result}";
                LblStatus.Foreground = ok ? Avalonia.Media.Brushes.LightGreen : Avalonia.Media.Brushes.OrangeRed;
                if (ok)
                {
                    onDone?.Invoke(result);
                }
            });
        });
    }

    // ---- connection / device tab ----

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        _deviceSpec = string.IsNullOrWhiteSpace(TxtDevice.Text) ? TransportFactory.DefaultSpec : TxtDevice.Text!;
        Device(c => c.Info(), info =>
        {
            TxtInfo.Text = info;
            LblStatus.Text = "connected";
            OnRefreshApps(sender, e);
        });
    }

    private void OnRefreshInfo(object? sender, RoutedEventArgs e)
        => Device(c => c.Info(), info => TxtInfo.Text = info);

    private void OnReboot(object? sender, RoutedEventArgs e)
        => Device(c =>
        {
            c.Reboot();
            return "rebooted";
        });

    private void OnGenerateKeys(object? sender, RoutedEventArgs e)
    {
        try
        {
            string dir = string.IsNullOrWhiteSpace(TxtKeyDir.Text) ? "." : TxtKeyDir.Text!;
            Directory.CreateDirectory(dir);
            var (priv, pub) = Signing.GenerateKeypair();
            File.WriteAllBytes(Path.Combine(dir, "rustnet-signing.key"), priv);
            File.WriteAllBytes(Path.Combine(dir, "rustnet-signing.pub"), pub);
            LblKeysInfo.Text = $"keypair written to {Path.GetFullPath(dir)} (keep the .key file secret)";
        }
        catch (Exception ex)
        {
            LblKeysInfo.Text = $"error: {ex.Message}";
        }
    }

    private void OnProvision(object? sender, RoutedEventArgs e)
    {
        string dir = string.IsNullOrWhiteSpace(TxtKeyDir.Text) ? "." : TxtKeyDir.Text!;
        string pubPath = Path.Combine(dir, "rustnet-signing.pub");
        if (!File.Exists(pubPath))
        {
            LblKeysInfo.Text = $"{pubPath} not found — generate keys first";
            return;
        }
        byte[] pub = File.ReadAllBytes(pubPath);
        Device(c =>
        {
            c.ProvisionKey(pub);
            return "provisioned";
        }, _ => LblKeysInfo.Text = "device provisioned with the public key");
    }

    // ---- apps tab ----

    private void OnRefreshApps(object? sender, RoutedEventArgs e)
        => Device(c => c.ListApps(), json =>
        {
            LstApps.Items.Clear();
            foreach (string entry in json.Trim('[', ']').Split("},", StringSplitOptions.RemoveEmptyEntries))
            {
                string cleaned = entry.Trim().TrimEnd('}') + "}";
                if (cleaned.Length > 2)
                {
                    LstApps.Items.Add(cleaned);
                }
            }
        });

    private string? SelectedAppName()
    {
        string? row = LstApps.SelectedItem as string;
        if (row is null)
        {
            return null;
        }
        int idx = row.IndexOf("\"name\":\"", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        int start = idx + 8;
        int end = row.IndexOf('"', start);
        return end > start ? row[start..end] : null;
    }

    private void OnFlashApp(object? sender, RoutedEventArgs e)
    {
        string? path = TxtAppPath.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LblStatus.Text = "app file not found";
            return;
        }
        string name = string.IsNullOrWhiteSpace(TxtAppName.Text)
            ? Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
            : TxtAppName.Text!;
        string chipName = (CmbChip.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "host-sim";
        string keyDir = string.IsNullOrWhiteSpace(TxtKeyDir.Text) ? "." : TxtKeyDir.Text!;
        string keyPath = Path.Combine(keyDir, "rustnet-signing.key");
        if (!File.Exists(keyPath))
        {
            LblStatus.Text = $"signing key not found at {keyPath} (Device tab -> Generate)";
            return;
        }
        Task.Run(() =>
        {
            try
            {
                byte[] rnx = path.EndsWith(".rnx", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllBytes(path)
                    : RnxCompiler.Compile(path, out _);
                byte[] sealedApp = Signing.Seal(ImageKind.App, Signing.ParseChip(chipName), rnx,
                    File.ReadAllBytes(keyPath));
                using var client = Client();
                client.FlashApp(name, sealedApp);
                Dispatcher.UIThread.Post(() =>
                {
                    LblStatus.Text = $"flashed '{name}' ({sealedApp.Length} bytes)";
                    OnRefreshApps(sender, e);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => LblStatus.Text = $"error: {ex.Message}");
            }
        });
    }

    private void OnStartApp(object? sender, RoutedEventArgs e)
    {
        string? name = SelectedAppName();
        if (name is null)
        {
            LblStatus.Text = "select an app first";
            return;
        }
        Device(c =>
        {
            c.StartApp(name);
            return $"'{name}' started";
        }, _ => OnRefreshApps(sender, e));
    }

    private void OnStopApp(object? sender, RoutedEventArgs e)
        => Device(c =>
        {
            c.StopApp();
            return "stopped";
        }, _ => OnRefreshApps(sender, e));

    private void OnEraseApp(object? sender, RoutedEventArgs e)
    {
        string? name = SelectedAppName();
        if (name is null)
        {
            LblStatus.Text = "select an app first";
            return;
        }
        Device(c =>
        {
            c.EraseApp(name);
            return $"'{name}' erased";
        }, _ => OnRefreshApps(sender, e));
    }

    // ---- data tab ----

    private void OnDataPush(object? sender, RoutedEventArgs e)
    {
        string? local = TxtDataLocal.Text;
        string? remote = TxtDataRemote.Text;
        if (string.IsNullOrWhiteSpace(local) || !File.Exists(local) || string.IsNullOrWhiteSpace(remote))
        {
            LblStatus.Text = "need an existing local file and a remote path";
            return;
        }
        byte[] data = File.ReadAllBytes(local);
        Device(c =>
        {
            c.FlashData(remote!, data);
            return $"pushed {data.Length} bytes to {remote}";
        });
    }

    private void OnDataPull(object? sender, RoutedEventArgs e)
    {
        string? remote = TxtDataRemote.Text;
        if (string.IsNullOrWhiteSpace(remote))
        {
            LblStatus.Text = "remote path required";
            return;
        }
        Device(c => System.Text.Encoding.UTF8.GetString(c.ReadData(remote!)),
            content => TxtDataOut.Text = content);
    }

    // ---- config / wifi tab ----

    private void OnConfigSet(object? sender, RoutedEventArgs e)
    {
        string? key = TxtCfgKey.Text;
        string? value = TxtCfgValue.Text;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        Device(c =>
        {
            c.SetConfig(key!, value ?? "");
            return "stored (encrypted)";
        }, msg => LblCfgOut.Text = msg);
    }

    private void OnConfigGet(object? sender, RoutedEventArgs e)
    {
        string? key = TxtCfgKey.Text;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        Device(c => c.GetConfig(key!), v => LblCfgOut.Text = $"{key} = {v}");
    }

    private void OnWifiSave(object? sender, RoutedEventArgs e)
    {
        string? ssid = TxtSsid.Text;
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return;
        }
        string psk = TxtPsk.Text ?? "";
        Device(c =>
        {
            c.ConfigureWifi(ssid!, psk);
            return $"wifi '{ssid}' saved";
        }, msg => LblCfgOut.Text = msg);
    }

    // ---- boot image tab ----

    private void OnBootSet(object? sender, RoutedEventArgs e)
    {
        string? file = TxtBootFile.Text;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)
            || !ushort.TryParse(TxtBootW.Text, out ushort w) || !ushort.TryParse(TxtBootH.Text, out ushort h))
        {
            LblBootOut.Text = "need an existing file plus width and height";
            return;
        }
        byte[] data = File.ReadAllBytes(file);
        Device(c =>
        {
            c.SetBootImage(w, h, data);
            return $"boot image set ({w}x{h})";
        }, msg => LblBootOut.Text = msg);
    }

    private void OnBootGet(object? sender, RoutedEventArgs e)
        => Device(c =>
        {
            byte[] img = c.GetBootImage();
            string path = Path.GetFullPath("bootimg.bin");
            File.WriteAllBytes(path, img);
            return $"saved to {path} ({img.Length} bytes)";
        }, msg => LblBootOut.Text = msg);

    // ---- display tab ----

    private void OnCaptureDisplay(object? sender, RoutedEventArgs e)
    {
        string spec = _deviceSpec;
        Task.Run(() =>
        {
            try
            {
                using var client = RndpClient.Connect(spec);
                var (w, h, pixels) = client.GetDisplay();
                Dispatcher.UIThread.Post(() =>
                {
                    var bmp = new WriteableBitmap(new Avalonia.PixelSize(w, h), new Avalonia.Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    using (var fb = bmp.Lock())
                    {
                        unsafe
                        {
                            byte* dst = (byte*)fb.Address;
                            for (int i = 0; i < w * h; i++)
                            {
                                ushort px = BitConverter.ToUInt16(pixels, i * 2);
                                dst[i * 4 + 0] = (byte)((px & 0x1F) << 3);          // B
                                dst[i * 4 + 1] = (byte)(((px >> 5) & 0x3F) << 2);   // G
                                dst[i * 4 + 2] = (byte)(((px >> 11) & 0x1F) << 3);  // R
                                dst[i * 4 + 3] = 255;
                            }
                        }
                    }
                    ImgDisplay.Source = bmp;
                    LblDisplayInfo.Text = $"{w}x{h} captured at {DateTime.Now:HH:mm:ss}";
                    LblStatus.Text = "ok";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => LblDisplayInfo.Text = ex.Message);
            }
        });
    }

    // ---- logs / profiler tabs ----

    private void OnRefreshLogs(object? sender, RoutedEventArgs e)
        => Device(c => c.GetLogs(500), logs =>
        {
            TxtLogs.Text = logs;
            TxtLogs.CaretIndex = logs.Length;
        });

    private void OnRefreshPerf(object? sender, RoutedEventArgs e)
        => Device(c => c.GetPerf(), perf => TxtPerf.Text = perf.Replace(",", ",\n"));

    // ---- OTA tab ----

    private void OnOtaPush(object? sender, RoutedEventArgs e)
    {
        string? file = TxtOtaFile.Text;
        string? key = TxtOtaKey.Text;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)
            || string.IsNullOrWhiteSpace(key) || !File.Exists(key))
        {
            LblOtaOut.Text = "need firmware payload and private key files";
            return;
        }
        byte[] payload = File.ReadAllBytes(file);
        byte[] keyBytes = File.ReadAllBytes(key);
        string spec = _deviceSpec;
        Task.Run(() =>
        {
            try
            {
                byte[] sealedFw = Signing.Seal(ImageKind.Firmware, ChipFamily.HostSim, payload, keyBytes);
                using var client = RndpClient.Connect(spec);
                client.OtaUpdate(sealedFw, (done, total) => Dispatcher.UIThread.Post(() =>
                    PrgOta.Value = done * 100.0 / total));
                Dispatcher.UIThread.Post(() =>
                    LblOtaOut.Text = "update verified + staged; confirm after checking device health");
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => LblOtaOut.Text = $"error: {ex.Message}");
            }
        });
    }

    private void OnOtaConfirm(object? sender, RoutedEventArgs e)
        => Device(c => c.OtaConfirm(), slot => LblOtaOut.Text = $"active slot confirmed: {slot}");

    private void OnOtaRollback(object? sender, RoutedEventArgs e)
        => Device(c => c.OtaRollback(), slot => LblOtaOut.Text = $"rolled back to slot: {slot}");

    // ---- firmware tab ----

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cargo.toml"))
                && Directory.Exists(Path.Combine(dir.FullName, "runtime")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private void OnFwBuild(object? sender, RoutedEventArgs e)
    {
        string chip = (CmbFwChip.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "host";
        string? root = FindRepoRoot();
        if (root is null)
        {
            TxtFwOut.Text = "RustNet repo (Cargo.toml) not found above the Workbench binary";
            return;
        }
        TxtFwOut.Text = $"building firmware variant '{chip}'...\n";
        Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cargo",
                Arguments = $"build -p rustnet-firmware --no-default-features --features chip-{chip}",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            try
            {
                using var proc = Process.Start(psi)!;
                string output = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Dispatcher.UIThread.Post(() =>
                    TxtFwOut.Text += output + (proc.ExitCode == 0 ? "\nBUILD OK" : "\nBUILD FAILED"));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => TxtFwOut.Text += $"error: {ex.Message}");
            }
        });
    }

    private void OnFwList(object? sender, RoutedEventArgs e)
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            TxtFwOut.Text = "repo not found";
            return;
        }
        string dir = Path.Combine(root, "artifacts", "firmware");
        string target = Path.Combine(root, "target");
        var lines = new List<string>();
        if (Directory.Exists(dir))
        {
            lines.AddRange(Directory.GetFiles(dir).Select(f =>
                $"{Path.GetFileName(f),-44} {new FileInfo(f).Length / 1024,6} KiB"));
        }
        foreach (string profile in new[] { "debug", "release" })
        {
            string exe = Path.Combine(target, profile, "rustnet-firmware.exe");
            if (File.Exists(exe))
            {
                lines.Add($"target/{profile}/rustnet-firmware.exe {new FileInfo(exe).Length / 1024,6} KiB");
            }
        }
        TxtFwOut.Text = lines.Count > 0 ? string.Join('\n', lines) : "no firmware images built yet";
    }
}
