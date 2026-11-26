using System.Text.RegularExpressions;

namespace TapRoom.Services;

public static partial class UrlNormaliser
{
    public static string Refang(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var url = value.Trim();

        // Protocol refanging, including hxxp[:]// and hxxps[:]// variants.
        url = HxxpsProtocol().Replace(url, "https");
        url = HxxpProtocol().Replace(url, "http");

        // Common defanged separators.
        url = DefangedColon().Replace(url, ":");
        url = DefangedDot().Replace(url, ".");

        return url;
    }

    public static bool LooksLikeUrl(string value)
    {
        var refanged = Refang(value);
        return refanged.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               refanged.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               refanged.Contains('.', StringComparison.Ordinal);
    }

    [GeneratedRegex("^hxxps(?=\\s*(?::|\\[:\\]|\\(:\\)|\\{:\\}))", RegexOptions.IgnoreCase)]
    private static partial Regex HxxpsProtocol();

    [GeneratedRegex("^hxxp(?=\\s*(?::|\\[:\\]|\\(:\\)|\\{:\\}))", RegexOptions.IgnoreCase)]
    private static partial Regex HxxpProtocol();

    [GeneratedRegex("\\s*(?:\\[:\\]|\\(:\\)|\\{:\\})\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DefangedColon();

    [GeneratedRegex("\\s*(?:\\[\\.\\]|\\(\\.\\)|\\{\\.\\})\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex DefangedDot();
}
