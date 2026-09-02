using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kanal.Core.Relay;

/// <summary>
/// Ephemeral P-256 identity for one room. It authenticates host messages on a public
/// Realtime channel; it does not turn that channel into an authenticated or revocable room.
/// </summary>
public sealed class RelaySigningKey : IDisposable
{
    private readonly object _gate = new();
    private ECDsa? _key;

    private RelaySigningKey(ECDsa key)
    {
        _key = key;
        VerificationKey = Base64Url.Encode(key.ExportSubjectPublicKeyInfo());
    }

    public string VerificationKey { get; }

    public static RelaySigningKey Create() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public SignedRelayMessage Sign(RelayMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is SignedRelayMessage)
            throw new ArgumentException("A signed relay envelope cannot be nested.", nameof(message));

        var data = Encoding.UTF8.GetBytes(RelayJson.Serialize(message));
        byte[] signature;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            signature = _key.SignData(
                data,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        return new SignedRelayMessage(1, Base64Url.Encode(data), Base64Url.Encode(signature));
    }

    public static bool TryVerify(
        string verificationKey,
        SignedRelayMessage envelope,
        out RelayMessage? message)
    {
        message = null;
        if (envelope.Version != 1)
            return false;

        try
        {
            var publicKey = Base64Url.Decode(verificationKey);
            var data = Base64Url.Decode(envelope.Data);
            var signature = Base64Url.Decode(envelope.Signature);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var read);
            if (read != publicKey.Length || !verifier.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return false;

            message = RelayJson.Deserialize(Encoding.UTF8.GetString(data));
            return message is not null and not SignedRelayMessage;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        {
            message = null;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _key?.Dispose();
            _key = null;
        }
    }
}
