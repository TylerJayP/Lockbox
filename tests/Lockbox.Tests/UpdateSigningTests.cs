using System.Text;
using Lockbox.Core;
using Lockbox.Core.Updates;

namespace Lockbox.Tests;

public sealed class UpdateSigningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lockbox-update-").FullName;
    private readonly byte[] _priv;
    private readonly string _pub;

    public UpdateSigningTests() => (_priv, _pub) = UpdateSigning.GenerateKeyPair();

    public void Dispose() => Directory.Delete(_dir, true);

    private string Package(string version, string content = "package bytes")
    {
        var path = Path.Combine(_dir, $"Lockbox-{version}-full.nupkg");
        File.WriteAllText(path, content);
        return path;
    }

    private (byte[] Json, string Sig, string PackagePath) SignedRelease(string version = "0.5.0")
    {
        var pkg = Package(version);
        var json = UpdateSigning.SerializeManifest(UpdateSigning.CreateManifest(version, pkg));
        return (json, UpdateSigning.Sign(json, _priv), pkg);
    }

    [Fact]
    public void Genuine_release_verifies()
    {
        var (json, sig, pkg) = SignedRelease();
        var m = UpdateSigning.VerifyManifest(json, sig, _pub);
        Assert.Equal("0.5.0", m.Version);
        Assert.Equal("Lockbox-0.5.0-full.nupkg", m.PackageFile);
        UpdateSigning.VerifyPackage(pkg, m, "0.5.0"); // doesn't throw
    }

    [Fact]
    public void Edited_manifest_is_rejected()
    {
        var (json, sig, _) = SignedRelease();
        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(json).Replace("0.5.0", "0.6.0"));
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyManifest(edited, sig, _pub));
    }

    [Fact]
    public void Signature_from_another_key_is_rejected()
    {
        var (json, _, _) = SignedRelease();
        var (attackerKey, _) = UpdateSigning.GenerateKeyPair();
        var forged = UpdateSigning.Sign(json, attackerKey);
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyManifest(json, forged, _pub));
    }

    [Fact]
    public void Garbage_signatures_and_missing_key_are_rejected()
    {
        var (json, sig, _) = SignedRelease();
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyManifest(json, "not base64!!", _pub));
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyManifest(json, Convert.ToBase64String(new byte[64]), _pub));
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyManifest(json, sig, ""));
    }

    [Fact]
    public void Swapped_package_is_rejected()
    {
        var (json, sig, pkg) = SignedRelease();
        var m = UpdateSigning.VerifyManifest(json, sig, _pub);
        File.WriteAllText(pkg, "package bytez"); // same length, different content
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyPackage(pkg, m, "0.5.0"));
    }

    [Fact]
    public void Old_signed_manifest_cannot_vouch_for_a_different_version()
    {
        var (json, sig, _) = SignedRelease("0.4.0");
        var m = UpdateSigning.VerifyManifest(json, sig, _pub);
        var newer = Package("0.5.0");
        Assert.Throws<UpdateVerificationException>(() => UpdateSigning.VerifyPackage(newer, m, "0.5.0"));
    }

    [Fact]
    public void Private_key_file_round_trips_and_needs_the_passphrase()
    {
        string file = UpdateSigning.ProtectPrivateKey(_priv, "release passphrase here", VaultTests.Fast);
        using var doc = System.Text.Json.JsonDocument.Parse(file);
        Assert.Equal(_pub, doc.RootElement.GetProperty("PublicKey").GetString());
        Assert.DoesNotContain(Convert.ToBase64String(_priv), file);
        Assert.DoesNotContain(Convert.ToBase64String(_priv).Replace("+", "\\u002B"), file);

        Assert.Equal(_priv, UpdateSigning.UnprotectPrivateKey(file, "release passphrase here"));
        Assert.Throws<WrongCredentialsException>(() => UpdateSigning.UnprotectPrivateKey(file, "wrong"));
        Assert.Throws<VaultFormatException>(() => UpdateSigning.UnprotectPrivateKey("{\"Format\":\"something-else\"}", "x"));
    }

    [Fact]
    public void Public_key_is_derived_from_private_key()
    {
        Assert.Equal(_pub, UpdateSigning.PublicKeyOf(_priv));
    }
}
