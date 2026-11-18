using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlexManager.Core.Settings;

/// <summary>
/// A token or API key. On disk it is DPAPI-encrypted for the current Windows user and written as
/// <c>"dpapi:&lt;base64&gt;"</c>; a plain string is still read (so an imported or hand-written value works) and
/// re-encrypted the next time settings are saved.
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret
{
    private const string Prefix = "dpapi:";

    public Secret()
    {
    }

    public Secret(string? value)
    {
        Value = value ?? "";
    }

    public string Value { get; set; } = "";

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    /// <summary>What the UI shows instead of the value: the last four characters, or nothing when unset.</summary>
    public string Hint => Value.Length <= 4 ? (HasValue ? "****" : "") : "****" + Value[^4..];

    public static implicit operator string(Secret? secret) => secret?.Value ?? "";

    public override string ToString() => Value;

    internal static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or CryptographicException)
        {
            // No DPAPI (not Windows, or a profile that cannot protect): keep the value usable rather than losing it.
            return value;
        }
    }

    internal static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return "";
        }

        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or PlatformNotSupportedException)
        {
            // Settings copied from another user or machine cannot be decrypted; treat them as unset.
            return "";
        }
    }
}

internal sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(Secret.Unprotect(reader.TokenType == JsonTokenType.Null ? null : reader.GetString()));

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Secret.Protect(value.Value));
}
