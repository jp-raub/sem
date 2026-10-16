using System.Runtime.InteropServices;
using System.Text.Json;
using Sem.Web.Runtime;

namespace Sem.Web.Data;

/// <summary>Host-only authorization, not an application user or an HTTP authorization shortcut.</summary>
internal static class EmergencyHostOperatorPolicy
{
    internal const string PolicyPath = "/etc/sem/emergency-recovery.json";
    internal static string RequireOperator()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Emergency maintenance requires Linux.");
        // Every ancestor is root-controlled. No command-line/environment policy override exists.
        for (var path = PolicyPath; path != "/"; path = Path.GetDirectoryName(path)!)
        {
            SemStorageRuntimeLease.RejectLinks(path);
            if (Statx(-100, path, 0x100, 0x0b, out var info) != 0)
                throw new InvalidOperationException("Trusted emergency policy is unavailable.");
            ValidateMetadata(info.Uid, info.Mode, path == PolicyPath);
        }
        using var file = new FileStream(PolicyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Statx(file.SafeFileHandle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x0b, out var opened) != 0)
            throw new InvalidOperationException("Policy ownership could not be verified.");
        ValidateMetadata(opened.Uid, opened.Mode, true);
        if (file.Length is < 1 or > 4096) throw new InvalidOperationException("Invalid emergency policy size.");
        using var memory = new MemoryStream(); file.CopyTo(memory);
        return Parse(memory.ToArray(), GetEffectiveUid());
    }
    internal static void ValidateMetadata(uint owner, ushort mode, bool regular)
    {
        if (owner != 0 || (mode & 0x12) != 0 || (mode & 0xf000) != (regular ? 0x8000 : 0x4000))
            throw new InvalidOperationException("Emergency policy and parents must be root-owned, non-writable by group/others, and not links.");
    }
    internal static string Parse(byte[] bytes, uint effectiveUid)
    {
        if (bytes.Length is < 1 or > 4096) throw new InvalidOperationException("Invalid emergency policy size.");
        using var doc = JsonDocument.Parse(bytes);
        var properties = doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToArray() : [];
        if (properties.Length != 1 || properties[0].Name != "allowedEffectiveUid" ||
            properties[0].Value.ValueKind != JsonValueKind.Number ||
            !properties[0].Value.TryGetUInt32(out var allowed) || allowed != effectiveUid)
            throw new InvalidOperationException("Current OS effective UID is not the configured emergency operator.");
        return "linux-euid:" + effectiveUid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    internal static void RequireRegularFile(string path)
    {
        if (Statx(-100, path, 0x100, 0x0b, out var info) != 0 || (info.Mode & 0xf000) != 0x8000)
            throw new InvalidOperationException("Only regular files are accepted for maintenance input.");
    }
    internal static void RequireProtectedDirectory(string directory)
    {
        var uid = GetEffectiveUid();
        var direct = true;
        for (var path = Path.GetFullPath(directory); ; path = Path.GetDirectoryName(path)!)
        {
            if (Statx(-100, path, 0x100, 0x0b, out var info) != 0 ||
                (info.Mode & 0xf000) != 0x4000 || (info.Uid != 0 && info.Uid != uid))
                throw new InvalidOperationException("Maintenance directories must be owned by root or the operator.");
            // A root-owned sticky ancestor such as /tmp cannot rename another owner's child.
            // The immediate destination parent itself must always be non-writable by other users.
            var trustedStickyAncestor = !direct && info.Uid == 0 && (info.Mode & 0x200) != 0;
            if ((info.Mode & 0x12) != 0 && !trustedStickyAncestor)
                throw new InvalidOperationException("Maintenance directories must not be writable by group/others.");
            direct = false;
            if (path == Path.GetPathRoot(path)) break;
        }
    }
    // statx has a fixed Linux ABI on x64 and arm64 (unlike struct stat).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxInfo
    {
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxInfo info);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
}
