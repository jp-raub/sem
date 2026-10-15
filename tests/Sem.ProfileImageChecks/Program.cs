using System.ComponentModel.DataAnnotations;
using ImageMagick;
using Sem.Web.Core;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
async Task Reject(byte[] bytes, string mime, string name)
{
    try { await ProfileImageNormalizer.NormalizeAsync(bytes, mime); throw new Exception("FAIL accepted " + name); }
    catch (ValidationException) { checks++; Console.WriteLine("PASS " + name); }
}

// Make in-memory source fixtures before the service installs its stricter read-only coder policy.
byte[] Fixture(uint w, uint h, MagickFormat format, string? comment = null)
{
    using var img = new MagickImage(MagickColors.CornflowerBlue, w, h);
    if (comment is not null)
    {
        img.Comment = comment;
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.ImageDescription, comment);
        img.SetProfile(exif);
    }
    return img.ToByteArray(format);
}
var largePng = Fixture(1200, 600, MagickFormat.Png, "gps=must-not-survive");
var overPixelCapPng = Fixture(5000, 5000, MagickFormat.Png);
var smallJpeg = Fixture(80, 40, MagickFormat.Jpeg);
var smallWebp = Fixture(128, 64, MagickFormat.WebP);
using var frames = new MagickImageCollection();
frames.Add(new MagickImage(MagickColors.Red, 24, 12));
frames.Add(new MagickImage(MagickColors.Blue, 24, 12));
var animatedWebp = frames.ToByteArray(MagickFormat.WebP);

var png = await ProfileImageNormalizer.NormalizeAsync(largePng, "image/png");
Check(png.Width == 512 && png.Height == 256 && png.Png.Length <= ProfileImageNormalizer.MaxOutputBytes, "PNG scales down preserving aspect and output cap");
Check(png.Png.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}), "output is normalized PNG");
using (var source = new MagickImage(largePng, MagickFormat.Png)) Check(source.GetExifProfile() is not null, "source fixture carries EXIF metadata");
using (var stripped = new MagickImage(png.Png, MagickFormat.Png)) Check(string.IsNullOrEmpty(stripped.Comment) && stripped.GetExifProfile() is null, "source metadata and EXIF are stripped");
var jpeg = await ProfileImageNormalizer.NormalizeAsync(smallJpeg, "image/jpeg");
Check(jpeg.Width == 80 && jpeg.Height == 40, "small JPEG is not upscaled");
var webp = await ProfileImageNormalizer.NormalizeAsync(smallWebp, "image/webp");
Check(webp.Width == 128 && webp.Height == 64, "static WebP accepted");
await Reject(animatedWebp, "image/webp", "animated WebP rejected before frame extraction");
await Reject(overPixelCapPng, "image/png", "pixel cap rejects oversized dimensions before pixel decode");
await Reject(largePng, "image/jpeg", "MIME and magic mismatch rejected");
await Reject([.. "<svg xmlns='http://www.w3.org/2000/svg'/>"u8], "image/svg+xml", "SVG rejected");
await Reject([.. new byte[64]], "image/png", "malformed raster rejected");
await Reject([.. new byte[ProfileImageNormalizer.MaxInputBytes + 1]], "image/png", "input over 10 MiB rejected");
Console.WriteLine($"PASS total={checks}");
