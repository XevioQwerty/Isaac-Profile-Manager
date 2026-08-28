using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace IsaacProfileManager.Core.Services;

/// <summary>
/// Turns a <see cref="SharedProfile"/> into a paste-able string and back.
///
/// A code is deliberately self-contained: it carries the Workshop ids, the entry
/// names and the hashes, so the recipient can fetch the set *and* prove their
/// copy matches byte for byte. Nothing is hosted anywhere and nothing expires.
///
/// It cannot be short. A published file id needs about 34 bits, so 40 mods is
/// ~227 base64 characters before a single name or hash is added, and hashes are
/// incompressible hex. Roughly 100 characters buys 17 ids and nothing else.
/// A Steam collection id is the short alternative, and it is short precisely
/// because Steam stores the list instead — see <see cref="WorkshopCollectionService"/>.
/// </summary>
public static class ShareCodeService
{
    /// <summary>Envelope marker and version. Bump if the payload encoding changes.</summary>
    public const string Prefix = "IPM1-";

    /// <summary>
    /// Ceiling on the decompressed payload. A share code arrives from someone
    /// else, so a small string must not be allowed to expand without bound.
    /// </summary>
    private const int MaxDecompressedBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Encode(SharedProfile profile)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(profile, SerializerOptions);

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json, 0, json.Length);

        return Prefix + ToBase64Url(output.ToArray());
    }

    public static SharedProfile Decode(string code)
    {
        var trimmed = Clean(code);

        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new ShareCodeException($"That does not look like a share code — they start with {Prefix}.");

        byte[] compressed;
        try
        {
            compressed = FromBase64Url(trimmed[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new ShareCodeException("The code is damaged — it was probably cut short when it was copied.");
        }

        byte[] json;
        try
        {
            using var input = new MemoryStream(compressed);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            CopyBounded(deflate, output);
            json = output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw new ShareCodeException("The code is damaged — it was probably cut short when it was copied.");
        }

        SharedProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<SharedProfile>(json);
        }
        catch (JsonException ex)
        {
            throw new ShareCodeException($"The code decoded but is not a profile: {ex.Message}");
        }

        if (profile is null) throw new ShareCodeException("The code is empty.");

        if (profile.SchemaVersion != SharedProfile.CurrentSchemaVersion)
            throw new ShareCodeException(
                $"That code is version {profile.SchemaVersion}; this build understands " +
                $"{SharedProfile.CurrentSchemaVersion}. One of you needs to update.");

        return profile;
    }

    /// <summary>
    /// Codes get pasted out of chat clients, which wrap lines and add spaces.
    /// Stripping whitespace makes a wrapped paste work instead of failing with
    /// a corruption message that sends the user hunting for the wrong problem.
    /// </summary>
    private static string Clean(string code) =>
        new(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static void CopyBounded(Stream source, Stream destination)
    {
        var buffer = new byte[81920];
        var total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDecompressedBytes)
                throw new ShareCodeException("That code expands to an unreasonable size and was not read.");

            destination.Write(buffer, 0, read);
        }
    }

    // Base64url: '+' and '/' do not survive being pasted into URLs and some chat
    // clients, and '=' padding invites truncation on copy.
    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}

public sealed class ShareCodeException : Exception
{
    public ShareCodeException(string message) : base(message) { }
}
