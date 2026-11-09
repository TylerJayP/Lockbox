// Renders every screen of Lockbox headlessly to PNG, with sample data, so the UI can be checked without a display.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Lockbox.App;
using Lockbox.App.ViewModels;
using Lockbox.App.Views;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Model;

string outDir = args.Length > 0 && args[0] != "smoke" ? args[0] : "shots";
if (args is not ["smoke", ..]) Directory.CreateDirectory(outDir);
Environment.SetEnvironmentVariable("LOCKBOX_SETTINGS_DIR", Path.Combine(Path.GetTempPath(), "lockbox-shots-config"));

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

if (args is ["smoke", ..])
{
    Environment.ExitCode = Smoke.RunAll() == 0 ? 0 : 1;
    return;
}

var tmp = Directory.CreateTempSubdirectory("lockbox-shots-").FullName;
var fast = new KdfParameters(KdfParameters.MinMemoryKiB, 1);

void Pump()
{
    for (int i = 0; i < 6; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

void Shot(Window w, string name)
{
    Pump();
    var frame = w.CaptureRenderedFrame();
    if (frame == null) { Console.WriteLine($"no frame for {name}"); return; }
    var path = Path.Combine(outDir, name + ".png");
    frame.Save(path);
    Console.WriteLine("saved " + path);
}

MainWindow NewWindow(out MainWindowViewModel vm)
{
    var w = new MainWindow { Width = 1140, Height = 760 };
    vm = (MainWindowViewModel)w.DataContext!;
    w.Show();
    return w;
}

(Vault vault, byte[] key) SampleVault(string name)
{
    var key = KeyFile.Generate();
    var v = Vault.Create(Path.Combine(tmp, name + ".lbx"), "orbit-velvet-cactus-mango-ripple-anchor", key, fast);
    v.AddEntry(new Entry { Title = "GitHub", Url = "https://github.com", Username = "TylerJayP", Password = "Vq7#pL2!xRz9@mK4", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp, Tags = ["dev"] });
    v.AddEntry(new Entry { Title = "Netflix", Url = "netflix.com", Username = "tyler@example.com", Password = "hunter2" });
    v.AddEntry(new Entry { Title = "Steam", Url = "store.steampowered.com", Username = "tylerjp", Password = "Mazda$peed3-2010!", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.AuthenticatorApp });
    v.AddEntry(new Entry { Title = "Epic Games", Username = "tyler@example.com", Password = "Mazda$peed3-2010!" });
    v.AddEntry(new Entry { Title = "UVU myUVU", Url = "https://my.uvu.edu", Username = "10912345", Password = "correct-Orbit-velvet-cactus7", TwoFactorEnabled = true, TwoFactorMethod = TwoFactorMethod.Sms });
    v.AddEntry(new Entry { Title = "Discord", Username = "tyler#0001", Password = "N8$wq!Lz0#Rt5vYp", TwoFactorEnabled = true });
    v.AddEntry(new Entry { Title = "Amazon", Url = "amazon.com", Username = "tyler@example.com", Password = "Zx9!kQ#2mN8@pL4w" });
    var gh = v.Entries.First(e => e.Title == "GitHub");
    foreach (var old in new[] { "Vq7#pL2!xRz9@mK4", "gh-2024-Summer!x" })
    {
        var c = v.Entries.First(e => e.Id == gh.Id).Clone();
        c.Password = old == "Vq7#pL2!xRz9@mK4" ? "gh-2024-Summer!x" : "Vq7#pL2!xRz9@mK4-2026";
        v.UpdateEntry(c);
    }
    v.AddEntry(new Entry { Type = ItemType.Card, Title = "Chase Freedom", Card = new CardData { CardholderName = "Tyler J Perkins", Number = "4111 1111 1111 1111", Expiry = "08/29", SecurityCode = "123", Pin = "4821" }, Tags = ["banking"] });
    v.AddEntry(new Entry { Type = ItemType.Card, Title = "Amex Gold", Reprompt = true, Card = new CardData { CardholderName = "Tyler J Perkins", Number = "378282246310005", Expiry = "02/28", SecurityCode = "1234" } });
    v.AddEntry(new Entry { Type = ItemType.WiFi, Title = "Home Wi-Fi", Password = "Tundra-Pickle-Saddle-77", Wifi = new WifiData { NetworkName = "Perkins 5G", Security = WifiSecurity.Wpa3 },
        Fields = [new CustomField("Router admin page", "192.168.1.1", false), new CustomField("Router admin password", "rtr-Admin-9921!", true)] });
    v.AddEntry(new Entry { Type = ItemType.SecureNote, Title = "Gym locker & safe", Notes = "Gym locker: 12-34-08 (left, right, left)\nHome safe is behind the closet panel.",
        Fields = [new CustomField("Safe combination", "48-17-92", true), new CustomField("Locker number", "214", false)] });
    var ghId = v.Entries.First(e => e.Title == "GitHub").Id;
    v.AddAttachment(ghId, "github-recovery-codes.txt", System.Text.Encoding.UTF8.GetBytes("recovery codes"));
    v.AddAttachment(ghId, "2fa-setup-qr.png", new byte[48213]);
    v.SetupRecovery("separate-recovery-phrase-lunar-otter", fast);
    v.SetBackupCodes(v.Entries.First(e => e.Title == "GitHub").Id, "a1b2c-d3e4f\ng5h6i-j7k8l\nm9n0o-p1q2r\ns3t4u-v5w6x");
    v.Save();
    v.LockRecovery();
    return (v, key);
}

// 1. Unlock screen
{
    var w = NewWindow(out var vm);
    vm.Navigate(new UnlockViewModel(vm, "Locked after 5 min without activity."));
    Shot(w, "01-unlock");
    w.Close();
}

// 2. Create vault
{
    var w = NewWindow(out var vm);
    var c = new CreateVaultViewModel(vm)
    {
        VaultPath = @"C:\Users\tyler\OneDrive\Documents\MyVault.lbx",
    };
    c.GeneratePassphraseCommand.Execute(null);
    c.EmergencySheetSaved = true;
    vm.Navigate(c);
    Shot(w, "02-create");
    w.Close();
}

// 3. Vault, editor on a reused-password entry
{
    var w = NewWindow(out var vm);
    var (v, key) = SampleVault("main");
    var vvm = new VaultViewModel(vm, v, key) { Status = "Saved at 11:58 PM." };
    vm.OnUnlocked(vvm);
    vvm.Selected = vvm.Items.First(i => i.Title == "Steam");
    Shot(w, "03-vault");
    vm.UpdateVersion = "0.5.1";
    vm.UpdateBannerVisible = true;
    Shot(w, "16-update-banner");
    vm.UpdateBusy = true;
    vm.UpdateProgress = 45;
    Shot(w, "16b-update-progress");
    vm.UpdateBusy = false;
    vm.UpdateBannerVisible = false;

    // 4. Generator open on a weak entry
    vvm.Selected = vvm.Items.First(i => i.Title == "Netflix");
    vvm.Editor!.ToggleGeneratorCommand.Execute(null);
    Shot(w, "04-generator");

    // 5. Backup codes unlocked
    v.UnlockRecovery("separate-recovery-phrase-lunar-otter");
    vvm.Selected = vvm.Items.First(i => i.Title == "Amazon");
    vvm.Selected = vvm.Items.First(i => i.Title == "GitHub");
    Shot(w, "05-codes");
    vvm.Editor!.HistoryOpen = true;
    Shot(w, "05b-history");
    w.Height = 1500;
    vvm.Editor!.HistoryOpen = false;
    Shot(w, "05c-login-full");
    vvm.Selected = vvm.Items.First(i => i.Title == "Chase Freedom");
    vvm.Editor!.RevealCard = true;
    Shot(w, "11-card");
    vvm.Selected = vvm.Items.First(i => i.Title == "Home Wi-Fi");
    vvm.Editor!.ToggleWifiQrCommand.Execute(null);
    Shot(w, "12-wifi");
    vvm.Selected = vvm.Items.First(i => i.Title == "Gym locker & safe");
    Shot(w, "13-note");
    w.Height = 760;
    vvm.Selected = vvm.Items.First(i => i.Title == "Amex Gold");
    Shot(w, "14-sealed");
    vvm.TypeFilter = VaultViewModel.TypeFilters.Single(t => t.Label == "Cards");
    Shot(w, "14b-filter-cards");
    vvm.TypeFilter = VaultViewModel.TypeFilters[0];
    var ck = new CheckupViewModel(vvm);
    vm.Navigate(ck);
    w.Height = 1100;
    Shot(w, "15-checkup");
    w.Height = 760;
    vm.Navigate(vvm);

    // 6. Dialog
    _ = vm.PromptSecretAsync("Unlock backup codes", "Enter your recovery passphrase. Codes lock again automatically after 3 minutes.",
        "Recovery passphrase", "Unlock", "That's not the recovery passphrase.");
    Shot(w, "06-dialog");
    vm.Dialog!.CancelCommand.Execute(null);

    // 7. Import review
    var imp = new ImportViewModel(vvm)
    {
        SourceText = """
            ===== GAMES =====
            Steam | tylerjp | Mazda$peed3-2010!
            Xbox, tylerjp@outlook.com, Xb0x-Pass

            Google
            user: tyler.perkins@gmail.com
            pass: G00gle$ecret!
            2FA: authenticator app
            Backup codes:
            1234 5678
            2345 6789

            Spotify
            tyler@example.com
            Sp0tify#1
            """,
    };
    vm.Navigate(imp);
    Shot(w, "07-import-text");
    imp.FindLoginsCommand.Execute(null);
    Shot(w, "08-import-review");

    // 9. Settings
    var st = new SettingsViewModel(vvm);
    vm.Navigate(st);
    Shot(w, "09-settings");
    w.Height = 2400;
    st.ExportStatus = "Exported 7 logins to lockbox-export-2026-11-09.csv. It isn't encrypted: delete it as soon as you've used it, and empty the Recycle Bin.";
    Shot(w, "09b-settings-full");
    w.Height = 760;

    vvm.Dispose();
    w.Close();
}

// 10. Empty vault
{
    var w = NewWindow(out var vm);
    var key = KeyFile.Generate();
    var v = Vault.Create(Path.Combine(tmp, "empty.lbx"), "orbit-velvet-cactus-mango-ripple-anchor", key, fast);
    var vvm = new VaultViewModel(vm, v, key) { Status = "Vault created. Add a login or import your old text file." };
    vm.OnUnlocked(vvm);
    Shot(w, "10-empty");
    vvm.Dispose();
    w.Close();
}

Directory.Delete(tmp, true);
