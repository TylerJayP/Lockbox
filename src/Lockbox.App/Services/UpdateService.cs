using System.Reflection;
using Lockbox.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Lockbox.App.Services;

public enum UpdateCheckStatus { NotConfigured, NotInstalled, UpToDate, Available, Failed }

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string? Version = null, string? Notes = null, string? Error = null);

/// <summary>
/// Checks GitHub Releases for a newer Lockbox (via Velopack), and installs it only after verifying the
/// developer's Ed25519 signature over the exact package file. Nothing about the vault is ever sent:
/// the only request is "what's the latest release?" to GitHub.
/// </summary>
public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    public static bool IsConfigured => UpdateTrust.PublicKey.Length > 0;

    public static string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v == null ? "dev" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static IVelopackLocator? Locator => VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null;

    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (!IsConfigured) return new(UpdateCheckStatus.NotConfigured);
        try
        {
            var mgr = new UpdateManager(new GithubSource(UpdateTrust.RepoUrl, null, false), null, Locator);
            if (!mgr.IsInstalled) return new(UpdateCheckStatus.NotInstalled);

            var info = await mgr.CheckForUpdatesAsync();
            if (info == null || info.IsDowngrade) return new(UpdateCheckStatus.UpToDate);

            _manager = mgr;
            _pending = info;
            return new(UpdateCheckStatus.Available, info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown);
        }
        catch (Exception ex)
        {
            return new(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    /// <summary>Checks a downloaded package against the signed manifest; deletes it if it doesn't match.</summary>
    private static bool PackageVerifies(string package, UpdateManifest manifest, string version, out UpdateVerificationException? problem)
    {
        try
        {
            UpdateSigning.VerifyPackage(package, manifest, version);
            problem = null;
            return true;
        }
        catch (UpdateVerificationException ex)
        {
            // Never leave an unverified package where the installer could pick it up later.
            try { File.Delete(package); } catch { }
            problem = ex;
            return false;
        }
    }

    /// <summary>
    /// 1. Fetch and verify the signed manifest (refuse before downloading anything if it's not signed by our key).
    /// 2. Download the update with Velopack.
    /// 3. Verify the downloaded package is exactly the one the signature covers.
    /// 4. Run <paramref name="beforeRestart"/> (save + lock), then install and restart.
    /// Throws <see cref="UpdateVerificationException"/> if anything doesn't check out.
    /// </summary>
    public async Task DownloadVerifyAndRestartAsync(Action<int> progress, Action beforeRestart)
    {
        if (_manager == null || _pending == null) throw new InvalidOperationException("Check for updates first.");
        var target = _pending.TargetFullRelease;
        string version = target.Version.ToString();

        UpdateManifest manifest;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Lockbox/{CurrentVersion}");
            string baseUrl = $"{UpdateTrust.RepoUrl}/releases/download/v{version}/";
            byte[] manifestBytes;
            string signature;
            try
            {
                manifestBytes = await http.GetByteArrayAsync(baseUrl + UpdateManifest.ManifestFileName);
                signature = await http.GetStringAsync(baseUrl + UpdateManifest.SignatureFileName);
            }
            catch (HttpRequestException)
            {
                throw new UpdateVerificationException($"Version {version} has no signature file, so it can't be trusted.");
            }
            manifest = UpdateSigning.VerifyManifest(manifestBytes, signature, UpdateTrust.PublicKey);
        }

        string packagesDir = Locator?.PackagesDir ?? throw new UpdateVerificationException("Couldn't find where updates are stored.");
        string package = Path.Combine(packagesDir, target.FileName);

        var manager = _manager;
        var pending = _pending;
        await manager.DownloadUpdatesAsync(pending, progress);
        if (!PackageVerifies(package, manifest, version, out var problem) && pending.DeltasToTarget.Length > 0)
        {
            // A small delta update rebuilds the full package locally. If that rebuilt file doesn't match the
            // signed checksum, fetch the complete package instead and check again.
            manager = new UpdateManager(new GithubSource(UpdateTrust.RepoUrl, null, false),
                new UpdateOptions { MaximumDeltasBeforeFallback = 0 }, Locator);
            pending = await manager.CheckForUpdatesAsync() ?? throw new UpdateVerificationException("The update disappeared while downloading.");
            if (pending.TargetFullRelease.Version.ToString() != version) throw new UpdateVerificationException("The offered update changed while downloading. Try again.");
            await manager.DownloadUpdatesAsync(pending, progress);
            PackageVerifies(package, manifest, version, out problem);
        }
        if (problem != null) throw problem;

        beforeRestart();
        manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }
}
