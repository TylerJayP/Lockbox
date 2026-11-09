using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lockbox.App.ViewModels;
using Lockbox.App.Views;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Model;

/// <summary>
/// Renders the screenshots used in README.md, with made-up sample data (no real names or accounts).
///   dotnet run --project tools/Lockbox.Screenshots -- readme docs/images
/// </summary>
internal static class ReadmeShots
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var tmp = Directory.CreateTempSubdirectory("lockbox-readme-").FullName;
        var fast = new KdfParameters(KdfParameters.MinMemoryKiB, 1);

        void Pump()
        {
            for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
        }
        void Shot(Window w, string name)
        {
            Pump();
            var path = Path.Combine(outDir, name + ".png");
            w.CaptureRenderedFrame()?.Save(path);
            Console.WriteLine("saved " + path);
        }

        var key = KeyFile.Generate();
        var v = Vault.Create(Path.Combine(tmp, "Personal.lbx"), "orbit-velvet-cactus-mango-ripple-anchor", key, fast);
        Entry Add(Entry e) => v.AddEntry(e);
        void Rotate(Guid id, params string[] passwords)
        {
            foreach (var p in passwords) { var c = v.Entries.First(e => e.Id == id).Clone(); c.Password = p; v.UpdateEntry(c); }
        }

        var gh = Add(new Entry { Title = "GitHub", Url = "https://github.com", Username = "alex-rivera", Password = "Vq7#pL2!xRz9@mK4wT", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp, Tags = ["dev"] });
        Rotate(gh.Id, "gh-Spring-2025!x", "Vq7#pL2!xRz9@mK4wT-26");
        Add(new Entry { Title = "Gmail", Url = "https://mail.google.com", Username = "alex.rivera@example.com", Password = "Kp4!wZ8#tQ2$mN7&", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.HardwareKey, Reprompt = true });
        Add(new Entry { Title = "Netflix", Url = "netflix.com", Username = "alex.rivera@example.com", Password = "sunshine1" });
        Add(new Entry { Title = "Steam", Url = "store.steampowered.com", Username = "arivera_gg", Password = "Blue-Falcon-2019!", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp });
        Add(new Entry { Title = "Epic Games", Username = "alex.rivera@example.com", Password = "Blue-Falcon-2019!" });
        Add(new Entry { Title = "Discord", Username = "arivera", Password = "N8$wq!Lz0#Rt5vYp", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp });
        Add(new Entry { Title = "Spotify", Url = "spotify.com", Username = "alex.rivera@example.com", Password = "Zx9!kQ#2mN8@pL4w" });
        Add(new Entry { Type = ItemType.Card, Title = "Everyday Visa", Card = new CardData { CardholderName = "Alex Rivera", Number = "4111 1111 1111 1111", Expiry = "08/29", SecurityCode = "123", Pin = "4821" }, Tags = ["banking"] });
        Add(new Entry { Type = ItemType.Card, Title = "Travel Amex", Reprompt = true, Card = new CardData { CardholderName = "Alex Rivera", Number = "378282246310005", Expiry = "02/28", SecurityCode = "1234" } });
        Add(new Entry
        {
            Type = ItemType.WiFi, Title = "Home Wi-Fi", Password = "Tundra-Pickle-Saddle-77",
            Wifi = new WifiData { NetworkName = "Rivera Home 5G", Security = WifiSecurity.Wpa3 },
            Fields = [new CustomField("Router admin page", "192.168.1.1", false), new CustomField("Router admin password", "rtr-Admin-9921!", true)],
        });
        Add(new Entry
        {
            Type = ItemType.SecureNote, Title = "Gym locker & safe", Notes = "Gym locker: 12-34-08 (left, right, left)\nSpare key is with a neighbor.",
            Fields = [new CustomField("Safe combination", "48-17-92", true), new CustomField("Locker number", "214", false)],
        });
        v.AddAttachment(gh.Id, "github-recovery-codes.txt", "recovery codes"u8.ToArray());
        v.SetupRecovery("separate-recovery-phrase-lunar-otter", fast);
        v.SetBackupCodes(gh.Id, "a1b2c-d3e4f\ng5h6i-j7k8l\nm9n0o-p1q2r\ns3t4u-v5w6x");
        v.Save();
        v.LockRecovery();

        var w = new MainWindow { Width = 1200, Height = 780 };
        var vm = (MainWindowViewModel)w.DataContext!;
        w.Show();

        // Unlock screen
        vm.Navigate(new UnlockViewModel(vm) { VaultPath = @"C:\Users\alex\Documents\Personal.lbx", KeyFilePath = @"E:\Personal.lbxkey" });
        Shot(w, "unlock");

        var vvm = new VaultViewModel(vm, v, key) { Status = "Saved at 9:41 PM." };
        vm.OnUnlocked(vvm);

        // Hero: a login with password history open
        vvm.Selected = vvm.Items.First(i => i.Title == "GitHub");
        vvm.Editor!.HistoryOpen = true;
        Shot(w, "vault");

        vvm.Selected = vvm.Items.First(i => i.Title == "Everyday Visa");
        Shot(w, "card");

        vvm.Selected = vvm.Items.First(i => i.Title == "Home Wi-Fi");
        vvm.Editor!.ToggleWifiQrCommand.Execute(null);
        Shot(w, "wifi");

        vvm.Selected = vvm.Items.First(i => i.Title == "Travel Amex");
        Shot(w, "protected");

        vm.Navigate(new CheckupViewModel(vvm));
        Shot(w, "checkup");

        var imp = new ImportViewModel(vvm)
        {
            SourceText = "===== GAMES =====\nRoblox | arivera_gg | R0blox!Pass\nXbox, alex.rivera@outlook.com, Xb0x-Pass\n\nAmazon\nemail: alex.rivera@example.com\npassword: Amaz0n-2024!\n2FA: authenticator app\nBackup codes:\n1234 5678\n2345 6789\n",
        };
        vm.Navigate(imp);
        imp.FindLoginsCommand.Execute(null);
        Shot(w, "import");

        vvm.Dispose();
        w.Close();
        Directory.Delete(tmp, true);
    }
}
