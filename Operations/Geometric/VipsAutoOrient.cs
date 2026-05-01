using System;

namespace CosmoImage.Operations.Geometric;

/// <summary>
/// Surgical edits to raw EXIF/TIFF byte streams. Walks IFD0 to find a
/// specific tag and rewrites its inline value, leaving everything else
/// untouched. Doesn't mutate input — returns a fresh array (or the original
/// reference if no edit is needed).
/// </summary>
internal static class ExifPatcher
{
    private const ushort TagOrientation = 0x0112;
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    /// <summary>
    /// Rewrite the Orientation tag (0x0112) in IFD0 to <paramref name="value"/>.
    /// Returns the original array unchanged if EXIF is malformed or the tag
    /// is missing.
    /// </summary>
    public static byte[] SetOrientation(byte[] exif, ushort value)
    {
        if (exif.Length < 8) return exif;

        // Endianness: "II" = little, "MM" = big.
        bool littleEndian;
        if (exif[0] == 0x49 && exif[1] == 0x49) littleEndian = true;
        else if (exif[0] == 0x4D && exif[1] == 0x4D) littleEndian = false;
        else return exif;

        ushort magic = ReadU16(exif, 2, littleEndian);
        if (magic != 0x002A) return exif;

        uint ifdOffset = ReadU32(exif, 4, littleEndian);
        if (ifdOffset + 2 > exif.Length) return exif;

        ushort numEntries = ReadU16(exif, (int)ifdOffset, littleEndian);
        int entriesStart = (int)ifdOffset + 2;
        if (entriesStart + numEntries * 12 > exif.Length) return exif;

        for (int i = 0; i < numEntries; i++)
        {
            int e = entriesStart + i * 12;
            ushort tag = ReadU16(exif, e, littleEndian);
            if (tag != TagOrientation) continue;

            ushort type = ReadU16(exif, e + 2, littleEndian);
            uint count = ReadU32(exif, e + 4, littleEndian);
            if (count != 1) return exif; // unexpected — leave alone

            // Patch a copy. Both SHORT and LONG fit inline in the 4-byte
            // value-or-offset field at e+8.
            var patched = (byte[])exif.Clone();
            if (type == TypeShort)
            {
                WriteU16(patched, e + 8, value, littleEndian);
                // Clear remaining 2 bytes of the value field for cleanliness.
                patched[e + 10] = 0;
                patched[e + 11] = 0;
            }
            else if (type == TypeLong)
            {
                WriteU32(patched, e + 8, value, littleEndian);
            }
            else
            {
                return exif;
            }
            return patched;
        }

        return exif;
    }

    private static ushort ReadU16(byte[] buf, int offset, bool le) =>
        le
            ? (ushort)(buf[offset] | (buf[offset + 1] << 8))
            : (ushort)((buf[offset] << 8) | buf[offset + 1]);

    private static uint ReadU32(byte[] buf, int offset, bool le) =>
        le
            ? (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24))
            : (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);

    private static void WriteU16(byte[] buf, int offset, ushort value, bool le)
    {
        if (le)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
        else
        {
            buf[offset] = (byte)((value >> 8) & 0xFF);
            buf[offset + 1] = (byte)(value & 0xFF);
        }
    }

    private static void WriteU32(byte[] buf, int offset, uint value, bool le)
    {
        if (le)
        {
            buf[offset + 0] = (byte)((value >> 0) & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
        else
        {
            buf[offset + 0] = (byte)((value >> 24) & 0xFF);
            buf[offset + 1] = (byte)((value >> 16) & 0xFF);
            buf[offset + 2] = (byte)((value >> 8) & 0xFF);
            buf[offset + 3] = (byte)((value >> 0) & 0xFF);
        }
    }
}
