// Lockbox release tool.
//
//   dotnet run --project tools/Lockbox.Release -- keygen            (once: create your signing key)
//   dotnet run --project tools/Lockbox.Release -- release 0.6.0     (every release)
//
// "release" checks your work is committed and pushed, runs the tests, builds the Windows app, packages
// it with Velopack (installer + delta updates), signs the package with your offline key, uploads it to
// GitHub Releases as a draft tagged at your exact commit, attaches the signature and release notes,
// then publishes. Installed copies of Lockbox pick it up on their next start.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lockbox.Core;
using Lockbox.Core.Crypto;
using Lockbox.Core.Tools;
using Lockbox.Core.Updates;

return await Cli.RunAsync(args);

internal static partial class Cli
{
    private static string Root = "";
    private static string TrustFile => Path.Combine(Root, "src", "Lockbox.App", "Services", "UpdateTrust.cs");
    private static string DefaultKeyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "lockbox-release" + UpdateSigning.KeyFileExtension);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            Root = FindRepoRoot();
            var opts = Options.Parse(args);
            return opts.Command switch
            {
                "keygen" => Keygen(opts),
                "release" => await ReleaseAsync(opts),
                _ => Usage(),
            };
        }
        catch (ToolException ex)
        {
            Error(ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            Lockbox release tool

              keygen [--out <file>] [--force]
                  Create your update-signing key (once). The private key is saved encrypted with a
                  passphrase (default: ~/lockbox-release.lbxsign) and the public key is written into
                  src/Lockbox.App/Services/UpdateTrust.cs. Commit that file; never commit the key.

              release <version> [--notes <file.md>] [--key <file>] [--token <github-token>]
                                [--dry-run] [--skip-tests] [--allow-dirty]
                  Test, build, package, sign and publish a release, e.g. `release 0.6.0`.
                  Release notes come from release-notes/<version>.md unless --notes says otherwise.
                  They show in the app's update banner and on the GitHub release page.
                  The version must be higher than every existing release, and your work must be
                  committed and pushed (--allow-dirty skips that check, for testing only).
                  --dry-run does everything except publish to GitHub (output in ./Releases).
                  The token needs "Contents: read and write" on the repo. It can also come from
                  LOCKBOX_GITHUB_TOKEN, GITHUB_TOKEN or GH_TOKEN, or `gh auth token`.
            """);
        return 2;
    }

    // ───────────────────────── keygen ─────────────────────────

    private static int Keygen(Options o)
    {
        string outPath = Path.GetFullPath(o.Get("out") ?? DefaultKeyPath);
        string currentKey = ReadTrustedPublicKey();

        if (currentKey.Length > 0 && !o.Has("force"))
            throw new ToolException(
                "This repo already trusts a signing key. Installed copies only accept updates signed with THAT key, so a new one\n" +
                "would cut them off from updates. If you really mean to replace it (e.g. the old key was lost), re-run with --force,\n" +
                "and have everyone reinstall from the new Setup.exe.");
        if (File.Exists(outPath)) throw new ToolException($"{outPath} already exists. Choose another --out path.");
        if (outPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("Don't keep the signing key inside the repo folder. Pick a path outside it (or a USB stick).");

        Info("Choose a passphrase for your signing key. You'll type it for every release.");
        string pass = ReadSecret("Passphrase: ");
        if (PasswordStrength.ValidateMasterPassword(pass) is { } err) throw new ToolException(err);
        if (ReadSecret("Again: ") != pass) throw new ToolException("The passphrases don't match.");

        Info("Tuning key protection for this computer…");
        var kdf = Kdf.Calibrate(TimeSpan.FromSeconds(1));
        var (priv, pub) = UpdateSigning.GenerateKeyPair();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, UpdateSigning.ProtectPrivateKey(priv, pass, kdf));
        }
        finally { CryptographicOperations.ZeroMemory(priv); }

        WriteTrustedPublicKey(pub);
        Ok($"Signing key saved (encrypted) to {outPath}");
        Ok($"Public key written to {Path.GetRelativePath(Root, TrustFile)}");
        Console.WriteLine("""

            Next:
              1. Back up the key file somewhere safe (e.g. your USB stick). Lose it and you can't ship updates
                 that existing installs will accept.
              2. Commit UpdateTrust.cs (it only contains the PUBLIC key):
                   git add src/Lockbox.App/Services/UpdateTrust.cs && git commit -m "Add update signing key"
              3. Release: dotnet run --project tools/Lockbox.Release -- release 0.6.0
            """);
        return 0;
    }

    // ───────────────────────── release ─────────────────────────

    private static async Task<int> ReleaseAsync(Options o)
    {
        string version = o.Positional.ElementAtOrDefault(0) ?? throw new ToolException("Which version? e.g. `release 0.6.0`");
        if (!VersionRx().IsMatch(version)) throw new ToolException("Use a version like 0.6.0 (major.minor.patch).");
        bool dryRun = o.Has("dry-run");
        string tag = "v" + version;
        string repoUrl = ReadRepoUrl();
        string trusted = ReadTrustedPublicKey();
        if (trusted.Length == 0) throw new ToolException("No signing key yet. Run `keygen` first, then commit UpdateTrust.cs.");

        string keyPath = Path.GetFullPath(o.Get("key") ?? DefaultKeyPath);
        if (!File.Exists(keyPath)) throw new ToolException($"Signing key not found at {keyPath}. Use --key <file>.");

        string? token = dryRun ? null : (o.Get("token") ?? FindToken());
        if (!dryRun && string.IsNullOrEmpty(token))
            throw new ToolException("A GitHub token is needed to publish. Pass --token, set LOCKBOX_GITHUB_TOKEN, or log in with `gh auth login`.");

        string? notes = FindReleaseNotes(o, version);

        // Catch the "oops" cases before a long build: uncommitted or unpushed work (the release would be
        // tagged at a commit that doesn't contain it), and versions that installed copies would ignore.
        Step("Checking the repo");
        CheckVelopackVersions();
        string commit = CheckGitState(allowDirty: o.Has("allow-dirty"), strictRemote: !dryRun);
        CheckVersionIsNew(version, tag, required: !dryRun);
        Ok($"Releasing {tag} from commit {commit[..Math.Min(10, commit.Length)]}" + (notes != null ? $" with notes from {Path.GetRelativePath(Root, notes)}" : ""));

        // Unlock the key first so a typo doesn't waste a whole build.
        byte[] priv;
        try { priv = UpdateSigning.UnprotectPrivateKey(File.ReadAllText(keyPath), ReadSecret("Signing key passphrase: ")); }
        catch (WrongCredentialsException) { throw new ToolException("Wrong passphrase for the signing key."); }
        catch (VaultFormatException ex) { throw new ToolException($"{keyPath}: {ex.Message}"); }
        try
        {
            if (UpdateSigning.PublicKeyOf(priv) != trusted)
                throw new ToolException("This signing key doesn't match the public key in UpdateTrust.cs. Installed copies would reject the release.");

            string publishDir = Path.Combine(Root, "publish", "release", "win-x64");
            string releasesDir = Path.Combine(Root, "Releases");
            Directory.CreateDirectory(releasesDir);

            if (!o.Has("skip-tests"))
            {
                Step("Running tests");
                Run("dotnet", ["test", Path.Combine(Root, "Lockbox.slnx"), "-c", "Release", "--nologo"]);
            }

            Step($"Building Lockbox {version} for Windows");
            if (Directory.Exists(publishDir)) Directory.Delete(publishDir, true);
            Run("dotnet", ["publish", Path.Combine(Root, "src", "Lockbox.App"), "-c", "Release", "-r", "win-x64", "--self-contained",
                $"-p:Version={version}", "-p:DebugType=none", "-o", publishDir, "--nologo"]);

            Run("dotnet", ["tool", "restore"]);   // the pinned vpk from .config/dotnet-tools.json

            if (!dryRun)
            {
                Step("Fetching the previous release (for small delta updates)");
                TryRun("dotnet", ["vpk", "download", "github", "--repoUrl", repoUrl, "--token", token!, "-o", releasesDir, "--channel", "win"]);
            }

            Step("Packaging with Velopack");
            var pack = new List<string> { "vpk" };
            if (!OperatingSystem.IsWindows()) pack.Add("[win]"); // cross-compile directive
            pack.AddRange(["pack", "-u", "Lockbox", "-v", version, "-r", "win-x64", "-p", publishDir, "-e", "Lockbox.exe", "-o", releasesDir,
                "-c", "win", "--packTitle", "Lockbox", "--packAuthors", "Tyler Perkins",
                "-i", Path.Combine(Root, "src", "Lockbox.App", "Assets", "lockbox.ico")]);
            if (notes != null) pack.AddRange(["--releaseNotes", notes]);
            Run("dotnet", pack);

            Step("Signing");
            string package = Path.Combine(releasesDir, $"Lockbox-{version}-full.nupkg");
            if (!File.Exists(package)) throw new ToolException($"Velopack didn't produce {Path.GetFileName(package)}.");
            byte[] manifest = UpdateSigning.SerializeManifest(UpdateSigning.CreateManifest(version, package));
            string sig = UpdateSigning.Sign(manifest, priv);
            // Prove it verifies exactly the way the app will check it.
            var verified = UpdateSigning.VerifyManifest(manifest, sig, trusted);
            UpdateSigning.VerifyPackage(package, verified, version);
            string manifestPath = Path.Combine(releasesDir, UpdateManifest.ManifestFileName);
            string sigPath = Path.Combine(releasesDir, UpdateManifest.SignatureFileName);
            File.WriteAllBytes(manifestPath, manifest);
            File.WriteAllText(sigPath, sig);
            Ok($"Signed {Path.GetFileName(package)} (SHA-256 {verified.Sha256[..16]}…)");

            if (dryRun)
            {
                Ok($"Dry run complete. Everything is in {releasesDir}");
                Console.WriteLine("  Setup for friends: " + Path.Combine(releasesDir, "Lockbox-win-Setup.exe"));
                return 0;
            }

            using var github = new GitHub(repoUrl, token!);
            await github.DeleteStaleDraftAsync(tag);

            Step($"Uploading to GitHub as a draft ({tag})");
            Run("dotnet", ["vpk", "upload", "github", "-o", releasesDir, "--repoUrl", repoUrl, "--token", token!, "--channel", "win",
                "--tag", tag, "--releaseName", $"Lockbox {version}", "--targetCommitish", commit]);

            Step("Attaching the signature and release notes, then publishing");
            await github.PublishWithSignatureAsync(tag, manifestPath, sigPath, ReleaseBody(notes));
            Ok($"Lockbox {version} is live: {repoUrl}/releases/tag/{tag}");
            Console.WriteLine("  Installed copies will offer the update the next time they start.");
            Console.WriteLine("  Run `git fetch --tags` to get the new tag locally.");
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(priv); }
    }

    private static string? FindReleaseNotes(Options o, string version)
    {
        if (o.Get("notes") is { } given)
        {
            string path = Path.GetFullPath(given);
            return File.Exists(path) ? path : throw new ToolException($"Release notes file not found: {path}");
        }
        string standard = Path.Combine(Root, "release-notes", version + ".md");
        if (File.Exists(standard)) return standard;
        Warn($"No release notes found (looked for release-notes/{version}.md). The update banner will show no notes.");
        return null;
    }

    /// <summary>The GitHub release description: your notes, plus a short "how do I get this" footer for people landing on the page.</summary>
    private static string ReleaseBody(string? notesPath)
    {
        string notes = notesPath != null ? File.ReadAllText(notesPath).Trim() : "";
        const string footer = """
            ---

            **New to Lockbox?** Download **Lockbox-win-Setup.exe** from the assets below and run it.

            **Already using it?** Lockbox offers this update by itself the next time it starts.
            """;
        return (notes.Length > 0 ? notes + "\n\n" : "") + footer;
    }

    /// <summary>
    /// Returns the commit to release. Refuses uncommitted changes and commits that GitHub doesn't have,
    /// because the release is tagged at this exact commit.
    /// </summary>
    private static string CheckGitState(bool allowDirty, bool strictRemote)
    {
        string commit = Git("rev-parse", "HEAD") ?? throw new ToolException("This folder isn't a git repository (or git isn't installed).");
        if (allowDirty)
        {
            Warn("--allow-dirty: skipping the committed-and-pushed checks.");
            return commit;
        }

        string status = Git("status", "--porcelain") ?? "";
        if (status.Length > 0)
            throw new ToolException("You have uncommitted changes. Commit (and push) them first so the release matches the code on GitHub:\n" +
                                    string.Join('\n', status.Split('\n').Take(10).Select(l => "    " + l)));

        string branch = Git("rev-parse", "--abbrev-ref", "HEAD") ?? "HEAD";
        if (branch == "HEAD") throw new ToolException("You're not on a branch (detached HEAD). Check out main first.");

        if (Git("fetch", "--quiet", "origin") == null)
        {
            if (strictRemote) throw new ToolException("Couldn't reach GitHub with `git fetch`. Check your connection and try again.");
            Warn("Couldn't run `git fetch`; skipping the pushed check for this dry run.");
            return commit;
        }

        string? upstream = Git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (upstream == null) throw new ToolException($"Branch '{branch}' isn't on GitHub yet. Push it first: git push -u origin {branch}");
        if (int.TryParse(Git("rev-list", "--count", "@{u}..HEAD"), out int ahead) && ahead > 0)
            throw new ToolException($"You have {ahead} commit(s) that aren't on GitHub yet. Run `git push` first.");
        if (int.TryParse(Git("rev-list", "--count", "HEAD..@{u}"), out int behind) && behind > 0)
            throw new ToolException($"GitHub has {behind} newer commit(s) on {upstream}. Run `git pull` first.");
        if (branch != "main") Warn($"You're releasing from '{branch}', not main.");
        return commit;
    }

    /// <summary>Installed copies only move forward, so a release must be newer than every existing tag.</summary>
    private static void CheckVersionIsNew(string version, string tag, bool required)
    {
        string? remote = Git("ls-remote", "--tags", "--refs", "origin");
        if (remote == null)
        {
            if (required) throw new ToolException("Couldn't list the existing release tags on GitHub (`git ls-remote`).");
            Warn("Couldn't list existing tags; skipping the version check for this dry run.");
            return;
        }
        var existing = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t').Last().Trim().Replace("refs/tags/", ""))
            .ToList();
        if (existing.Contains(tag)) throw new ToolException($"{tag} already exists on GitHub. Pick the next version number.");
        Version? newest = existing.Where(t => t.StartsWith('v') && VersionRx().IsMatch(t[1..]))
            .Select(t => Version.Parse(t[1..]))
            .DefaultIfEmpty()
            .Max();
        if (newest != null && Version.Parse(version) <= newest)
            throw new ToolException($"{tag} isn't newer than the latest release (v{newest}). Installed copies only update to higher versions.");
    }

    /// <summary>
    /// The packager (vpk, pinned in .config/dotnet-tools.json) must match the Velopack library inside the app
    /// (Directory.Packages.props), or installed copies may fail to apply the update.
    /// </summary>
    private static void CheckVelopackVersions()
    {
        string props = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        string tools = File.ReadAllText(Path.Combine(Root, ".config", "dotnet-tools.json"));
        string? lib = VelopackLibRx().Match(props) is { Success: true } a ? a.Groups[1].Value : null;
        string? cli = null;
        using (var doc = JsonDocument.Parse(tools))
            if (doc.RootElement.GetProperty("tools").TryGetProperty("vpk", out var vpk)) cli = vpk.GetProperty("version").GetString();
        if (lib == null || cli == null) throw new ToolException("Couldn't find the Velopack versions in Directory.Packages.props and .config/dotnet-tools.json.");
        if (lib != cli)
            throw new ToolException($"The app uses Velopack {lib} but the packager (vpk) is {cli}. Set both to the same version " +
                                    "(Directory.Packages.props and .config/dotnet-tools.json), commit, and try again.");
    }

    /// <summary>Runs git in the repo and returns trimmed stdout, or null if it failed.</summary>
    private static string? Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _ = stderr.Result;
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Lockbox.slnx"))) return d.FullName;
        throw new ToolException("Run this from inside the Lockbox repo.");
    }

    private static string ReadTrustedPublicKey() => TrustKeyRx().Match(File.ReadAllText(TrustFile)) is { Success: true } m ? m.Groups[1].Value : "";

    private static string ReadRepoUrl() =>
        RepoUrlRx().Match(File.ReadAllText(TrustFile)) is { Success: true } m ? m.Groups[1].Value : throw new ToolException("RepoUrl not found in UpdateTrust.cs.");

    private static void WriteTrustedPublicKey(string pub)
    {
        string text = File.ReadAllText(TrustFile);
        string updated = TrustKeyRx().Replace(text, $"public const string PublicKey = \"{pub}\";", 1);
        if (updated == text && !text.Contains(pub)) throw new ToolException("Couldn't find the PublicKey line in UpdateTrust.cs.");
        File.WriteAllText(TrustFile, updated);
    }

    private static string? FindToken()
    {
        foreach (var v in new[] { "LOCKBOX_GITHUB_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } t) return t;
        try
        {
            var psi = new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            string t = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 && t.Length > 0 ? t : null;
        }
        catch { return null; }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }

    private static void Run(string exe, IEnumerable<string> args)
    {
        if (TryRun(exe, args) != 0) throw new ToolException($"`{exe} {Redact(args)}` failed.");
    }

    private static int TryRun(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Root };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi) ?? throw new ToolException($"Couldn't start {exe}.");
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolException($"Couldn't run `{exe}`. Is it installed and on your PATH?");
        }
    }

    private static string Redact(IEnumerable<string> args)
    {
        var list = args.ToList();
        int i = list.IndexOf("--token");
        if (i >= 0 && i + 1 < list.Count) list[i + 1] = "***";
        return string.Join(' ', list);
    }

    private static void Step(string s) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n▶ {s}"); Console.ResetColor(); }
    private static void Info(string s) => Console.WriteLine(s);
    private static void Warn(string s) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"! {s}"); Console.ResetColor(); }
    private static void Ok(string s) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"✔ {s}"); Console.ResetColor(); }
    private static void Error(string s) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"✖ {s}"); Console.ResetColor(); }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")] private static partial Regex VersionRx();
    [GeneratedRegex(@"<PackageVersion Include=""Velopack"" Version=""([^""]+)""")] private static partial Regex VelopackLibRx();
    [GeneratedRegex(@"public const string PublicKey = ""([^""]*)"";")] private static partial Regex TrustKeyRx();
    [GeneratedRegex(@"public const string RepoUrl = ""([^""]+)"";")] private static partial Regex RepoUrlRx();
}

internal sealed class ToolException(string message) : Exception(message);

/// <summary>The few GitHub REST calls the release needs beyond what `vpk upload` does.</summary>
internal sealed class GitHub : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _api;

    public GitHub(string repoUrl, string token)
    {
        var parts = new Uri(repoUrl).AbsolutePath.Trim('/').Split('/');
        _api = $"https://api.github.com/repos/{parts[0]}/{parts[1].Replace(".git", "")}";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Lockbox-Release-Tool");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonElement?> FindReleaseAsync(string tag)
    {
        var list = await _http.GetStringAsync($"{_api}/releases?per_page=100");
        using var doc = JsonDocument.Parse(list);
        foreach (var r in doc.RootElement.EnumerateArray())
            if (r.GetProperty("tag_name").GetString() == tag) return r.Clone();
        return null;
    }

    /// <summary>A failed earlier attempt can leave an unpublished draft behind. Nobody else can see drafts, so clear it out.</summary>
    public async Task DeleteStaleDraftAsync(string tag)
    {
        if (await FindReleaseAsync(tag) is not { } r) return;
        if (!r.GetProperty("draft").GetBoolean()) throw new ToolException($"A published release for {tag} already exists on GitHub.");
        var del = await _http.DeleteAsync($"{_api}/releases/{r.GetProperty("id").GetInt64()}");
        if (!del.IsSuccessStatusCode)
            throw new ToolException($"Couldn't remove the unfinished draft of {tag} ({(int)del.StatusCode}). Delete it on GitHub and retry.");
        Console.WriteLine($"  Removed an unfinished draft of {tag} left by an earlier attempt.");
    }

    /// <summary>Uploads manifest + signature to the draft, sets the description, then publishes (so clients never see it unsigned).</summary>
    public async Task PublishWithSignatureAsync(string tag, string manifestPath, string sigPath, string body)
    {
        var release = await FindReleaseAsync(tag) ?? throw new ToolException($"Couldn't find the draft release {tag} on GitHub.");
        long id = release.GetProperty("id").GetInt64();
        string uploadUrl = release.GetProperty("upload_url").GetString()!.Split('{')[0];

        foreach (var file in new[] { manifestPath, sigPath })
        {
            string name = Path.GetFileName(file);
            foreach (var asset in release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name))
                await _http.DeleteAsync($"{_api}/releases/assets/{asset.GetProperty("id").GetInt64()}");
            using var content = new ByteArrayContent(await File.ReadAllBytesAsync(file));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var up = await _http.PostAsync($"{uploadUrl}?name={Uri.EscapeDataString(name)}", content);
            if (!up.IsSuccessStatusCode) throw new ToolException($"Uploading {name} failed: {(int)up.StatusCode} {await up.Content.ReadAsStringAsync()}");
        }

        string json = JsonSerializer.Serialize(new Dictionary<string, object> { ["draft"] = false, ["make_latest"] = "true", ["body"] = body });
        using var patch = new StringContent(json, Encoding.UTF8, "application/json");
        var pub = await _http.PatchAsync($"{_api}/releases/{id}", patch);
        if (!pub.IsSuccessStatusCode) throw new ToolException($"Publishing failed: {(int)pub.StatusCode} {await pub.Content.ReadAsStringAsync()}");
    }
}


internal sealed class Options
{
    public string Command { get; private init; } = "";
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(string[] args)
    {
        var o = new Options { Command = args.FirstOrDefault()?.ToLowerInvariant() ?? "" };
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string name = args[i][2..];
                bool isFlag = name is "dry-run" or "force" or "skip-tests" or "allow-dirty";
                o._named[name] = isFlag || i + 1 >= args.Length ? null : args[++i];
            }
            else o.Positional.Add(args[i]);
        }
        return o;
    }

    public bool Has(string name) => _named.ContainsKey(name);
    public string? Get(string name) => _named.TryGetValue(name, out var v) ? v : null;
}
