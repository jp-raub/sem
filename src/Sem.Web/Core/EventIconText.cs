using System.ComponentModel.DataAnnotations;
using System.Text;
namespace Sem.Web.Core;

/// <summary>Optional plain text, never markup, a URI or an image source.</summary>
public static class EventIconText
{
    public const int MaxLength = 64;
    public static string Validate(string value)
    {
        if (value.Length > MaxLength || value.Any(char.IsControl))
            throw new ValidationException("イベントアイコンは制御文字を含まない64文字以内のテキスト・絵文字で指定してください。");
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { throw new ValidationException("イベントアイコンの文字を確認してください。"); }
        return value.Trim();
    }
}
