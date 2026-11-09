using Avalonia.Threading;
using Lockbox.App.ViewModels;
using Lockbox.App.Views;
using Lockbox.Core;
using Lockbox.Core.Crypto;

/// <summary>Drives the real view-models end to end (headless) and checks the results on disk.</summary>
internal static class Smoke
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) _failures++;
    }

    private static void Wait(Func<bool> done, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for UI flow.");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Run(Task t) => Wait(() => t.IsCompleted);

    private static DialogViewModel? _lastDialog;

    private static void AnswerDialog(MainWindowViewModel vm, string buttonId, string? input = null)
    {
        Wait(() => vm.Dialog != null && vm.Dialog != _lastDialog);
        var d = vm.Dialog!;
        _lastDialog = d;
        if (input != null) { d.Input = input; d.ConfirmInput = input; }
        d.PressCommand.Execute(d.Buttons.First(b => b.Id == buttonId));
        Dispatcher.UIThread.RunJobs();
    }

    public static int RunAll()
    {
        var dir = Directory.CreateTempSubdirectory("lockbox-smoke-").FullName;
        string vaultPath = Path.Combine(dir, "Vault", "Mine.lbx");
        string keyPath = Path.Combine(dir, "Usb", "Mine.lbxkey");
        Directory.CreateDirectory(Path.GetDirectoryName(vaultPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        const string master = "quiver-lantern-oatmeal-spruce-gravel-meadow";
        const string recovery = "tundra-pickle-saddle-violin-ember-cobalt";

        var w = new MainWindow();
        w.Show();
        var vm = (MainWindowViewModel)w.DataContext!;

        // ── Create ──
        var create = new CreateVaultViewModel(vm) { VaultPath = vaultPath, KeyFilePath = keyPath };
        vm.Navigate(create);
        create.Password = "password123";
        create.ConfirmPassword = "password123";
        create.UnderstandsNoRecovery = true;
        Run(create.CreateCommand.ExecuteAsync(null));
        Check(create.Error != null && vm.ActiveVault == null, $"weak master password is refused ({create.Error})");

        create.Password = create.ConfirmPassword = master;
        Run(create.CreateCommand.ExecuteAsync(null));
        Check(vm.ActiveVault != null && create.Error == null, "vault created with strong passphrase" + (create.Error is { } e ? $" ({e})" : ""));
        Check(File.Exists(vaultPath) && File.Exists(keyPath), "vault and key file written");
        Check(KeyFile.Load(keyPath).Length == 32, "key file parses");
        var vault = vm.ActiveVault!;
        Check(vault.Vault.KdfParameters.MemoryKiB == 256 * 1024 && vault.Vault.KdfParameters.Passes >= 3, $"KDF calibrated: {vault.Vault.KdfParameters}");

        // ── Add an entry through the editor ──
        vault.NewEntryCommand.Execute(null);
        var ed = vault.Editor!;
        ed.Title = "GitHub";
        ed.Username = "TylerJayP";
        ed.Password = "gh-Sup3r-Secret-Pass!";
        ed.TwoFactorEnabled = true;
        Check(ed.IsDirty, "editor marks unsaved changes");
        Run(ed.SaveCommand.ExecuteAsync(null));
        Check(!ed.IsDirty && !vault.Vault.HasUnsavedChanges, "entry saved to disk");

        // ── Password history ──
        string firstPw = ed.Password;
        ed.Password = "gh-Rotated-Pass-2!";
        Run(ed.SaveCommand.ExecuteAsync(null));
        Check(ed.History.Count == 1 && ed.History[0].Password == firstPw, "changing the password moves the old one into history");
        Check(ed.History[0].Display != firstPw, "old passwords are masked until revealed");
        ed.ToggleHistoryRevealCommand.Execute(null);
        Check(ed.History[0].Display == firstPw, "reveal shows old passwords");
        ed.RestoreOldPasswordCommand.Execute(ed.History[0]);
        Check(ed.Password == firstPw && ed.IsDirty, "Use again puts the old password back (unsaved)");
        Run(ed.SaveCommand.ExecuteAsync(null));
        Check(ed.History.Count == 1 && ed.History[0].Password == "gh-Rotated-Pass-2!", "after restoring, the replaced password is in history, no duplicate");
        var clearHistory = ed.ClearHistoryCommand.ExecuteAsync(null);
        AnswerDialog(vm, "ok");
        Run(clearHistory);
        Check(!ed.HasHistory && vault.Vault.Entries.Single().PasswordHistory.Count == 0 && !vault.Vault.HasUnsavedChanges, "Clear history empties it and saves");

        // ── Backup codes: first use sets up the recovery passphrase ──
        var unlockCodes = ed.UnlockCodesCommand.ExecuteAsync(null);
        AnswerDialog(vm, "ok", recovery);
        Run(unlockCodes);
        Check(ed.CodesUnlocked, "recovery passphrase set up from the editor");
        ed.BackupCodes = "1111 2222\n3333 4444";
        Run(ed.SaveCommand.ExecuteAsync(null));
        Check(vault.Vault.Entries.Single().HasBackupCodes, "backup codes saved");

        // ── Import with codes ──
        var import = new ImportViewModel(vault)
        {
            SourceText = "Steam | tylerjp | St3amPass!\n\nGoogle\nuser: me@gmail.com\npass: G00gle$ecret\nBackup codes:\n9999 8888\n7777 6666\n\nGitHub\nuser: TylerJayP\npass: dup",
        };
        vm.Navigate(import);
        import.FindLoginsCommand.Execute(null);
        Check(import.Rows.Count == 3, $"import found 3 candidates ({import.Rows.Count})");
        Check(!import.Rows.Single(r => r.Title == "GitHub").Include, "duplicate of an existing login is unticked");
        var importTask = import.ImportCommand.ExecuteAsync(null);
        AnswerDialog(vm, "ok"); // "Imported N logins" (pasted text, so no file to delete)
        Run(importTask);
        Check(vm.CurrentPage == vault, "returned to the vault after import");
        Check(vault.Vault.Entries.Count == 3, $"vault now has 3 logins ({vault.Vault.Entries.Count})");
        var google = vault.Vault.Entries.Single(e => e.Title == "Google");
        Check(google.HasBackupCodes && vault.Vault.GetBackupCodes(google.Id) == "9999 8888\n7777 6666", "imported codes are sealed in the recovery section");

        // Keep an old copy of the file, then save once more so the copy is out of date.
        string snapshot = Path.Combine(dir, "old-copy.lbx");
        File.Copy(vaultPath, snapshot);
        long snapRevision = vault.Vault.Revision;
        vault.Vault.UpdateSettings(st0 => st0.ClipboardClearSeconds = 25);
        Run(vault.PersistAsync());
        Check(vault.Vault.Revision == snapRevision + 1 && vm.Settings.Known(vault.Vault.VaultId)?.Revision == snapRevision + 1,
            "each save bumps the revision and this PC remembers it");

        // ── Lock ──
        Run(vault.LockCommand.ExecuteAsync(null));
        Check(vm.ActiveVault == null && vm.CurrentPage is UnlockViewModel, "lock returns to unlock screen");
        Check(Assert.Throws<ObjectDisposedException>(() => vault.Vault.Save()), "locked vault object is unusable");

        // ── Unlock: wrong password, then right ──
        var unlock = (UnlockViewModel)vm.CurrentPage!;
        Check(unlock.VaultPath == vaultPath, "unlock screen remembers the vault path");
        unlock.KeyFilePath = keyPath;
        unlock.Password = master + "x";
        Run(unlock.UnlockCommand.ExecuteAsync(null));
        Check(unlock.Error != null && vm.ActiveVault == null, "wrong password refused");
        // Someone (or a sync app) puts the older copy back.
        File.Copy(snapshot, vaultPath, overwrite: true);
        unlock.Password = master;
        var rollback = unlock.UnlockCommand.ExecuteAsync(null);
        AnswerDialog(vm, "cancel");
        Run(rollback);
        Check(vm.ActiveVault == null && vm.CurrentPage == unlock, "rollback: older copy is flagged, and closing it keeps the vault locked");

        unlock.Password = master;
        var rollback2 = unlock.UnlockCommand.ExecuteAsync(null);
        AnswerDialog(vm, "ok");
        Run(rollback2);
        Check(vm.ActiveVault != null, "correct password + key file unlocks");
        Check(vm.ActiveVault!.Vault.Revision > snapRevision + 1, "opening an older copy on purpose makes it the newest revision");
        vault = vm.ActiveVault!;
        Check(vault.Vault.Entries.Count == 3 && !vault.Vault.IsRecoveryUnlocked, "data intact, backup codes sealed again after unlock");

        // ── Unlock with the typed emergency key instead of the file ──
        Run(vault.LockCommand.ExecuteAsync(null));
        unlock = (UnlockViewModel)vm.CurrentPage!;
        var secret = KeyFile.Load(keyPath);
        unlock.TypeKeyManually = true;
        unlock.ManualKey = KeyFile.FormatGroups(secret).ToLowerInvariant();
        unlock.ManualCheck = KeyFile.Checksum(secret);
        unlock.Password = master;
        Run(unlock.UnlockCommand.ExecuteAsync(null));
        Check(vm.ActiveVault != null, "typed emergency-sheet key unlocks");
        vault = vm.ActiveVault!;

        // ── Change master password ──
        var settings = new SettingsViewModel(vault);
        vm.Navigate(settings);
        settings.CurrentPassword = "not-it";
        settings.NewPassword = settings.ConfirmNewPassword = "harbor-velvet-tractor-plasma-orchid-willow";
        Run(settings.ChangeMasterPasswordCommand.ExecuteAsync(null));
        Check(settings.ChangeError != null, "change refused with wrong current password");
        settings.CurrentPassword = master;
        var change = settings.ChangeMasterPasswordCommand.ExecuteAsync(null);
        AnswerDialog(vm, "ok"); // delete old backups? yes
        Run(change);
        Check(settings.ChangeError == null, "master password changed" + (settings.ChangeError is { } ce ? $" ({ce})" : ""));
        Check(vault.Vault.ListBackups().Count == 0, "old backups deleted on request");
        Run(vault.LockCommand.ExecuteAsync(null));
        try { using var _ = Vault.Open(vaultPath, master, secret); Check(false, "old password no longer works"); }
        catch (WrongCredentialsException) { Check(true, "old password no longer works"); }
        using (var reopened = Vault.Open(vaultPath, "harbor-velvet-tractor-plasma-orchid-willow", secret))
            Check(reopened.Entries.Count == 3, "new password opens the vault with all logins");

        // ── Export (fake file picker) ──
        const string newMaster = "harbor-velvet-tractor-plasma-orchid-willow";
        string csvPath = Path.Combine(dir, "export.csv"), copyPath = Path.Combine(dir, "Usb", "Mine-copy.lbx");
        var fake = new FakePlatform();
        var fvm = new MainWindowViewModel(fake);
        var fv = new VaultViewModel(fvm, Vault.Open(vaultPath, newMaster, secret), secret.ToArray());
        fvm.OnUnlocked(fv);
        var st = new SettingsViewModel(fv);

        fake.NextSavePath = copyPath;
        Run(st.SaveEncryptedCopyCommand.ExecuteAsync(null));
        using (var copy = Vault.Open(copyPath, newMaster, secret))
            Check(copy.Entries.Count == 3, "encrypted copy opens with the same credentials");

        fake.NextSavePath = csvPath;
        var export = st.ExportCsvCommand.ExecuteAsync(null);
        AnswerDialog(fvm, "ok", "wrong-password");        // refused, asked again
        AnswerDialog(fvm, "ok", newMaster);               // verified
        AnswerDialog(fvm, "yes");                         // include backup codes
        AnswerDialog(fvm, "ok", recovery);                // unlock the codes section
        Run(export);
        Check(File.Exists(csvPath) && !st.ExportFailed, "CSV export written" + (st.ExportStatus is { } es && st.ExportFailed ? $" ({es})" : ""));
        var csv = File.ReadAllText(csvPath);
        Check(csv.StartsWith("folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp"), "CSV has the Bitwarden header");
        Check(csv.Contains("\"St3amPass!\"") && csv.Contains("9999 8888") && csv.Contains("2FA: Authenticator app"), "CSV contains passwords, codes and 2FA info");

        fake.NextSavePath = Path.Combine(dir, "export2.csv");
        var export2 = st.ExportCsvCommand.ExecuteAsync(null);
        AnswerDialog(fvm, "cancel");
        Run(export2);
        Check(!File.Exists(fake.NextSavePath), "cancelling the password prompt exports nothing");
        // ── Item types and custom fields ──
        fv.NewItemCommand.Execute("Card");
        var cardEd = fv.Editor!;
        Check(cardEd.IsCard && cardEd.Title == "New card", "New item → Payment card opens a card editor");
        cardEd.Title = "Chase Visa";
        cardEd.CardholderName = "Tyler Perkins";
        cardEd.CardNumber = "4111 1111 1111 1111";
        cardEd.CardExpiry = "08/29";
        cardEd.CardCode = "123";
        Check(cardEd.CardHint == "Visa", "card brand detected and checksum OK");
        cardEd.Reprompt = true;
        Run(cardEd.SaveCommand.ExecuteAsync(null));
        var savedCard = fv.Vault.Entries.Single(e => e.Type == Lockbox.Core.Model.ItemType.Card);
        Check(savedCard.Card!.Number == "4111 1111 1111 1111" && savedCard.Reprompt, "card saved with its details and protection flag");

        fv.NewItemCommand.Execute("WiFi");
        var wifiEd = fv.Editor!;
        wifiEd.NetworkName = "Perkins 5G";
        wifiEd.Password = "router-Pass-2026!";
        wifiEd.ToggleWifiQrCommand.Execute(null);
        Check(wifiEd.HasWifiQr, "Wi-Fi QR code generated");
        wifiEd.AddFieldCommand.Execute(null);
        wifiEd.Fields[0].Name = "Router admin";
        wifiEd.Fields[0].Value = "admin-pass";
        wifiEd.Fields[0].Hidden = true;
        Check(wifiEd.IsDirty, "custom field edits mark the item unsaved");
        Run(wifiEd.SaveCommand.ExecuteAsync(null));
        var savedWifi = fv.Vault.Entries.Single(e => e.Type == Lockbox.Core.Model.ItemType.WiFi);
        Check(savedWifi.Wifi!.NetworkName == "Perkins 5G" && savedWifi.Password == "router-Pass-2026!"
              && savedWifi.Fields.Single() == new Lockbox.Core.Model.CustomField("Router admin", "admin-pass", true), "Wi-Fi network and custom field saved");

        fv.TypeFilter = VaultViewModel.TypeFilters.Single(t => t.Type == Lockbox.Core.Model.ItemType.Card);
        Check(fv.Items.Count == 1 && fv.Items[0].Title == "Chase Visa", "type filter shows only cards");
        fv.ClearSearchCommand.Execute(null);
        Check(fv.Items.Count == fv.Vault.Entries.Count, "Esc / clear search resets filters");

        // ── Protected item ──
        fv.Selected = fv.Items.First(i => i.Title == "Google");
        fv.Selected = fv.Items.First(i => i.Title == "Chase Visa");
        Check(fv.Editor!.IsSealed, "protected item opens sealed");
        var copyCard = fv.CopySelectedPasswordCommand.ExecuteAsync(null);
        AnswerDialog(fvm, "ok", "nope");
        AnswerDialog(fvm, "ok", newMaster);
        Run(copyCard);
        Check(!fv.Editor!.IsSealed, "master password unseals it (wrong one asked again)");
        fv.Selected = fv.Items.First(i => i.Title == "Google");
        fv.Selected = fv.Items.First(i => i.Title == "Chase Visa");
        Check(fv.Editor!.IsSealed, "it seals again when you come back to it");

        // ── Attachments ──
        fv.Selected = fv.Items.First(i => i.Title == "Google");
        var gEd = fv.Editor!;
        string original = Path.Combine(dir, "google-recovery.txt");
        File.WriteAllText(original, "RECOVERY-KIT-CANARY 1234 5678");
        fake.NextOpenPath = original;
        var addAtt = gEd.AddAttachmentCommand.ExecuteAsync(null);
        AnswerDialog(fvm, "delete");
        Run(addAtt);
        Check(gEd.Attachments.Count == 1 && gEd.Attachments[0].FileName == "google-recovery.txt", "file attached");
        Check(!File.Exists(original), "original file overwritten and deleted on request");
        Check(!System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(vaultPath)).Contains("RECOVERY-KIT-CANARY"), "attachment is encrypted in the vault file");
        fake.NextSavePath = Path.Combine(dir, "restored.txt");
        Run(gEd.SaveAttachmentCommand.ExecuteAsync(gEd.Attachments[0]));
        Check(File.ReadAllText(fake.NextSavePath) == "RECOVERY-KIT-CANARY 1234 5678", "Save copy writes the original bytes back out");
        var rmAtt = gEd.RemoveAttachmentCommand.ExecuteAsync(gEd.Attachments[0]);
        AnswerDialog(fvm, "ok");
        Run(rmAtt);
        Check(gEd.Attachments.Count == 0 && fv.Vault.TotalAttachmentBytes == 0, "attachment removed");

        // ── Check-up ──
        fv.OpenCheckupCommand.Execute(null);
        var checkup = fvm.CurrentPage as CheckupViewModel;
        Check(checkup != null && checkup.Sections.Count > 0, $"check-up page lists issues ({checkup?.Headline})");
        var someItem = checkup!.Sections.SelectMany(sec => sec.Items).First();
        checkup.OpenCommand.Execute(someItem);
        Check(fvm.CurrentPage == fv && fv.Selected?.Id == someItem.Id, "Fix → jumps to that item");

        fv.Dispose();

        // ── Updates (no signing key in this build → never installs anything) ──
        var upd = new Lockbox.App.Services.UpdateService();
        var res = upd.CheckAsync().GetAwaiter().GetResult();
        Check(res.Status == Lockbox.App.Services.UpdateCheckStatus.NotConfigured || Lockbox.App.Services.UpdateService.IsConfigured,
            "without a signing key, the updater stays off");
        Check(Assert.Throws<InvalidOperationException>(() => upd.DownloadVerifyAndRestartAsync(_ => { }, () => { }).GetAwaiter().GetResult()),
            "installing without a checked update is refused");

        // ── Nothing readable on disk ──
        var raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(vaultPath));
        Check(!raw.Contains("TylerJayP") && !raw.Contains("9999") && !raw.Contains("St3amPass"), "vault file contains no plaintext");
        Check(Directory.GetFiles(Path.GetDirectoryName(vaultPath)!, "*.tmp").Length == 0, "no temp files left behind");

        w.Close();
        Directory.Delete(dir, true);
        Console.WriteLine(_failures == 0 ? "\nALL SMOKE CHECKS PASSED" : $"\n{_failures} SMOKE CHECK(S) FAILED");
        return _failures;
    }

    private sealed class FakePlatform : Lockbox.App.Services.IPlatform
    {
        public string? NextSavePath;
        public string? NextOpenPath;
        public bool SetCaptureProtection(bool enabled) => false;
        public Lockbox.App.Services.SecureClipboard Clipboard { get; } = new(() => null, () => 0);
        public Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult(NextOpenPath);
        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension, string filterName) => Task.FromResult(NextSavePath);
        public void OpenUrl(string url) { }
        public void OpenFolder(string path) { }
    }

    private static class Assert
    {
        public static bool Throws<T>(Action a) where T : Exception
        {
            try { a(); return false; } catch (T) { return true; }
        }
    }
}
