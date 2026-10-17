using System.ComponentModel.DataAnnotations;
namespace Sem.Web.Core;

/// <summary>Stored external venue links are optional HTTP(S) navigation, never executable markup.</summary>
public static class EventLocationLink
{
    public static bool IsSafe(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 2048 &&
        !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\') &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    public static string Validate(string value)
    {
        // Reject control characters even at the edges before trimming ordinary surrounding spaces.
        if (value.Any(char.IsControl)) throw new ValidationException("会場の案内URLを確認してください。");
        var result = value.Trim();
        if (result.Length != 0 && !IsSafe(result)) throw new ValidationException("会場の案内URLはユーザー情報を含まないHTTP(S)で指定してください。");
        return result;
    }
}
