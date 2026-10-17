using System.ComponentModel.DataAnnotations;
using ImageMagick;
using ImageMagick.Configuration;

namespace Sem.Web.Core;

public sealed record ProfileImageResult(long RowVersion, int Width, int Height);

/// <summary>Strict raster-only profile image normalization. No input file is ever persisted.</summary>
public static class ProfileImageNormalizer
{
    public const int MaxInputBytes = 10 * 1024 * 1024;
    public const int MaxOutputBytes = 1024 * 1024;
    public const int MaxLongEdge = 512;
    public const int MaxPixels = 24_000_000;
    public const int MaxDimension = 12_000;
    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);
    public static bool TryAcquireUploadSlot() => UploadSlots.Wait(0);
    public static void ReleaseUploadSlot() => UploadSlots.Release();
    private static readonly HashSet<string> AllowedMime = new(StringComparer.OrdinalIgnoreCase)
        { "image/png", "image/jpeg", "image/webp" };

    static ProfileImageNormalizer()
    {
        // Restrict native coders and delegates globally; this path only reads PNG/JPEG/WebP and writes PNG.
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
<?xml version="1.0"?>
<policymap>
  <policy domain="delegate" rights="none" pattern="*" />
  <policy domain="module" rights="none" pattern="*" />
  <policy domain="module" rights="read|write" pattern="{JPEG,PNG,WEBP}" />
  <policy domain="coder" rights="none" pattern="*" />
  <policy domain="coder" rights="read" pattern="{JPEG,WEBP}" />
  <policy domain="coder" rights="read|write" pattern="PNG" />
  <policy domain="resource" name="memory" value="256MiB" />
  <policy domain="resource" name="map" value="256MiB" />
  <policy domain="resource" name="disk" value="0" />
  <policy domain="resource" name="width" value="12000" />
  <policy domain="resource" name="height" value="12000" />
  <policy domain="resource" name="area" value="24000000" />
  <policy domain="resource" name="list-length" value="2" />
  <policy domain="resource" name="thread" value="2" />
  <policy domain="resource" name="time" value="12" />
  <policy domain="resource" name="max-profile-size" value="2MiB" />
</policymap>
""";
        MagickNET.Initialize(configuration);
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.Disk = 0;
        ResourceLimits.Area = MaxPixels;
        ResourceLimits.Width = MaxDimension;
        ResourceLimits.Height = MaxDimension;
        ResourceLimits.ListLength = 2;
        ResourceLimits.Thread = 2;
        ResourceLimits.Time = 12;
        ResourceLimits.MaxProfileSize = 2UL * 1024 * 1024;
    }

    public static bool IsAllowedMime(string? mime) => mime is not null && AllowedMime.Contains(mime);

    public static async Task<(byte[] Png, int Width, int Height)> NormalizeAsync(
        byte[] input, string mime, CancellationToken ct = default)
        => await NormalizeRasterAsync(input, mime, MaxInputBytes, MaxLongEdge, ct);

    internal static async Task<(byte[] Png, int Width, int Height)> NormalizeRasterAsync(
        byte[] input, string mime, int maxInputBytes, int maxLongEdge, CancellationToken ct)
    {
        if (input.Length < 12 || input.Length > maxInputBytes) throw new ValidationException("画像サイズを確認してください。");
        if (!IsAllowedMime(mime)) throw new ValidationException("PNG、JPEG、WebP画像のみ利用できます。");
        mime = mime.ToLowerInvariant();
        if (!SignatureMatches(input, mime)) throw new ValidationException("PNG、JPEG、WebP画像のみ利用できます。");
        if (HasAnimation(input, mime)) throw new ValidationException("アニメーション画像は利用できません。");
        if (!await DecodeSlots.WaitAsync(0, ct)) throw new ProfileImageBusyException();
        try
        {
            var format = mime switch { "image/png" => MagickFormat.Png, "image/jpeg" => MagickFormat.Jpeg, "image/webp" => MagickFormat.WebP, _ => throw new ValidationException("画像形式を確認してください。") };
            using var headers = new MagickImageCollection();
            headers.Ping(input);
            if (headers.Count != 1) throw new ValidationException("アニメーション画像は利用できません。");
            var width = checked((long)headers[0].Width);
            var height = checked((long)headers[0].Height);
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
                throw new ValidationException("画像の縦横サイズが上限を超えています。");
            if (headers[0].Format != format) throw new ValidationException("画像の内容と形式が一致しません。");

            // Decode only after the single-frame, format, dimensions, and pixel limits pass.
            using var image = new MagickImage(input, format);
            if (image.Format != format) throw new ValidationException("画像の内容と形式が一致しません。");
            image.AutoOrient();
            if (Math.Max(image.Width, image.Height) > maxLongEdge)
                image.Resize(new MagickGeometry((uint)maxLongEdge, (uint)maxLongEdge) { IgnoreAspectRatio = false });
            image.Strip(); // EXIF/GPS, profiles, comments, and other source metadata are discarded.
            image.Format = MagickFormat.Png;
            var png = image.ToByteArray(MagickFormat.Png);
            if (png.Length is < 8 or > MaxOutputBytes) throw new ValidationException("変換後の画像サイズが上限を超えています。");
            return (png, checked((int)image.Width), checked((int)image.Height));
        }
        catch (MagickException)
        {
            throw new ValidationException("画像を読み取れません。対応形式の静止画像を指定してください。");
        }
        catch (OverflowException)
        {
            throw new ValidationException("画像の縦横サイズが上限を超えています。");
        }
        finally { DecodeSlots.Release(); }
    }

    private static bool HasAnimation(ReadOnlySpan<byte> bytes, string mime)
    {
        if (mime == "image/png")
        {
            var offset = 8;
            while (offset + 12 <= bytes.Length)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                var type = bytes.Slice(offset + 4, 4);
                if (type.SequenceEqual("acTL"u8)) return true;
                if (length > int.MaxValue || offset + 12L + length > bytes.Length) return false;
                if (type.SequenceEqual("IEND"u8)) return false;
                offset += checked(12 + (int)length);
            }
            return false;
        }
        if (mime == "image/webp")
        {
            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
                var type = bytes.Slice(offset, 4);
                if (type.SequenceEqual("ANIM"u8) || type.SequenceEqual("ANMF"u8)) return true;
                if (type.SequenceEqual("VP8X"u8) && size >= 10 && offset + 18 <= bytes.Length && (bytes[offset + 8] & 0x02) != 0) return true;
                if (size > int.MaxValue || offset + 8L + size > bytes.Length) return false;
                offset += checked(8 + (int)size + ((int)size & 1));
            }
        }
        return false;
    }

    private static bool SignatureMatches(ReadOnlySpan<byte> bytes, string mime) => mime switch
    {
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };
}

public sealed class ProfileImageBusyException : Exception { }
