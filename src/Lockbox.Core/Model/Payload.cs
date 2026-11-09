using System.Text.Json;
using System.Text.Json.Serialization;
using Lockbox.Core.Crypto;

namespace Lockbox.Core.Model;

/// <summary>Everything inside the encrypted vault blob.</summary>
internal sealed class VaultPayload
{
    public int Version { get; set; } = 1;

    /// <summary>Stable identity of this vault (survives renames/moves). Empty in vaults made before v0.4.</summary>
    public Guid VaultId { get; set; }

    /// <summary>Goes up by one on every save. Lets the app notice when an older copy replaces a newer one.</summary>
    public long Revision { get; set; }
    public DateTimeOffset LastSavedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<Entry> Entries { get; set; } = [];
    public SealedRecovery? Recovery { get; set; }
    public VaultSettings Settings { get; set; } = new();

    /// <summary>Attachment bytes by attachment id. Encrypted along with everything else in the vault.</summary>
    public Dictionary<Guid, byte[]> Attachments { get; set; } = [];
}

/// <summary>The backup-codes section, encrypted a second time under its own passphrase.</summary>
internal sealed class SealedRecovery
{
    public KdfParameters Kdf { get; set; } = KdfParameters.Default;
    public byte[] Salt { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
}

internal sealed class RecoveryPayload
{
    public Dictionary<Guid, string> Codes { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VaultPayload))]
[JsonSerializable(typeof(RecoveryPayload))]
internal sealed partial class PayloadJson : JsonSerializerContext;
