using System.Buffers.Binary;

namespace ProjectMLib.Runtime;

/// <summary>
/// Minimal ELF64 reader for one question: which shared libraries does a .so declare as DT_NEEDED?
/// Used to detect GL-ES builds of libprojectM-4 (Arch ships one linked against libGLESv2) - feeding
/// a GLES-built projectM a desktop-GL context segfaults inside the driver during
/// <c>projectm_create</c>, so the probe must refuse it BEFORE any native call can crash.
/// </summary>
internal static class ElfNeededReader
{
    private const uint ShtDynamic = 6;
    private const long DtNeeded = 1;

    /// <summary>The DT_NEEDED dependency names of <paramref name="path"/>; empty on any parse problem
    /// (a malformed file must degrade to "no answer", never throw into the probe).</summary>
    public static IReadOnlyList<string> TryReadNeeded(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x40
                || bytes[0] != 0x7F || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F'
                || bytes[4] != 2) // EI_CLASS: 64-bit only (every platform we ship on)
            {
                return [];
            }

            var span = bytes.AsSpan();
            var shOff = BinaryPrimitives.ReadUInt64LittleEndian(span[0x28..]);
            var shEntSize = BinaryPrimitives.ReadUInt16LittleEndian(span[0x3A..]);
            var shNum = BinaryPrimitives.ReadUInt16LittleEndian(span[0x3C..]);
            if (shOff == 0 || shEntSize < 0x40 || shNum == 0)
                return [];

            for (var i = 0; i < shNum; i++)
            {
                var sh = span[(int)(shOff + (ulong)(i * shEntSize))..];
                if (BinaryPrimitives.ReadUInt32LittleEndian(sh[0x04..]) != ShtDynamic)
                    continue;

                var dynOffset = BinaryPrimitives.ReadUInt64LittleEndian(sh[0x18..]);
                var dynSize = BinaryPrimitives.ReadUInt64LittleEndian(sh[0x20..]);
                var strTabIndex = BinaryPrimitives.ReadUInt32LittleEndian(sh[0x28..]); // sh_link → .dynstr

                if (strTabIndex >= shNum)
                    return [];
                var strSh = span[(int)(shOff + strTabIndex * shEntSize)..];
                var strOffset = BinaryPrimitives.ReadUInt64LittleEndian(strSh[0x18..]);
                var strSize = BinaryPrimitives.ReadUInt64LittleEndian(strSh[0x20..]);
                if (strOffset + strSize > (ulong)bytes.Length || dynOffset + dynSize > (ulong)bytes.Length)
                    return [];
                var strTab = span.Slice((int)strOffset, (int)strSize);

                var needed = new List<string>();
                for (ulong entry = 0; entry + 16 <= dynSize; entry += 16)
                {
                    var dyn = span[(int)(dynOffset + entry)..];
                    var tag = BinaryPrimitives.ReadInt64LittleEndian(dyn);
                    if (tag == 0)
                        break; // DT_NULL ends the table
                    if (tag != DtNeeded)
                        continue;
                    var nameOffset = BinaryPrimitives.ReadUInt64LittleEndian(dyn[8..]);
                    if (nameOffset >= strSize)
                        continue;
                    var name = strTab[(int)nameOffset..];
                    var end = name.IndexOf((byte)0);
                    if (end > 0)
                        needed.Add(System.Text.Encoding.ASCII.GetString(name[..end]));
                }

                return needed;
            }

            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The on-disk path a loaded library resolved to, via /proc/self/maps (Linux only).</summary>
    public static string? TryFindLoadedLibraryPath(string nameFragment)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/maps"))
            {
                var pathIndex = line.IndexOf('/');
                if (pathIndex < 0)
                    continue;
                var path = line[pathIndex..];
                if (path.Contains(nameFragment, StringComparison.Ordinal))
                    return path;
            }
        }
        catch
        {
            // /proc unavailable (containers/hardening) - the caller treats "unknown" as "no veto"
        }

        return null;
    }
}
