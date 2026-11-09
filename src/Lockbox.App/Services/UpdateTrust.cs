namespace Lockbox.App.Services;

/// <summary>
/// Where updates come from, and the only key allowed to sign them.
///
/// PublicKey is filled in by `dotnet run --project tools/Lockbox.Release -- keygen`.
/// While it's empty, this build never installs updates. The matching private key stays on the
/// developer's computer, encrypted with a passphrase; it is never committed or uploaded.
/// </summary>
public static class UpdateTrust
{
    public const string RepoUrl = "https://github.com/TylerJayP/Lockbox";

    // UPDATE-PUBLIC-KEY (written by the release tool; don't edit by hand)
    public const string PublicKey = "";
}
