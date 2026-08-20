using System;
using System.IO;

namespace BingWallpaper;

/// <summary>
/// Structural check of a downloaded picture: does the file really carry a
/// complete JPEG (or PNG), and how large is it?
///
/// The Windows Forms version handed the file to <c>System.Drawing.Image</c> for
/// this. System.Drawing is not part of modern .NET on Windows any more (it lives
/// in a separate NuGet package that is Windows only and GDI+ bound), and the WinUI
/// decoders are all asynchronous and want a WinRT stream - both are a lot of
/// machinery for one question. Reading the markers is a few dozen lines and
/// answers exactly what a download has to prove: the file starts like an image,
/// its dimensions are sane, and the end of image marker really is there, so a
/// connection that died halfway can never be mistaken for a valid cache entry.
/// </summary>
internal static class ImageProbe
{
    /// <summary>Nothing Bing serves is anywhere near this; it only bounds the read.</summary>
    private const long MaxBytes = 128L * 1024 * 1024;

    public static bool TryValidate(string path, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        byte[] bytes;
        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "file does not exist";
                return false;
            }

            if (info.Length == 0)
            {
                error = "file is empty";
                return false;
            }

            if (info.Length > MaxBytes)
            {
                error = "file is implausibly large (" + info.Length + " bytes)";
                return false;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (IsJpeg(bytes))
        {
            return TryValidateJpeg(bytes, out width, out height, out error);
        }

        if (IsPng(bytes))
        {
            return TryValidatePng(bytes, out width, out height, out error);
        }

        error = "unknown format, first bytes are " + Describe(bytes);
        return false;
    }

    private static bool IsJpeg(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsPng(byte[] bytes)
        => bytes.Length >= 8
           && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
           && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;

    /// <summary>
    /// Walks the marker segments up to the start of the compressed data, which is
    /// where the frame header with the real dimensions has to have appeared.
    /// </summary>
    private static bool TryValidateJpeg(byte[] bytes, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        int index = 2;
        while (index + 1 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                error = "malformed JPEG, expected a marker at offset " + index;
                return false;
            }

            // Any number of 0xFF bytes may pad the front of a marker.
            while (index < bytes.Length && bytes[index] == 0xFF)
            {
                index++;
            }

            if (index >= bytes.Length)
            {
                error = "truncated JPEG, file ends inside a marker";
                return false;
            }

            byte marker = bytes[index];
            index++;

            // Standalone markers: no length, no payload.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                continue;
            }

            if (index + 1 >= bytes.Length)
            {
                error = "truncated JPEG, file ends inside a segment header";
                return false;
            }

            int segmentLength = (bytes[index] << 8) | bytes[index + 1];
            if (segmentLength < 2 || index + segmentLength > bytes.Length)
            {
                error = "truncated JPEG, segment 0x" + marker.ToString("X2") + " runs past the end of the file";
                return false;
            }

            // SOF0..SOF15 carry the frame header. 0xC4 (DHT), 0xC8 (JPG) and 0xCC
            // (DAC) share the range but are something else entirely.
            bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                                  && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isStartOfFrame)
            {
                if (segmentLength < 7)
                {
                    error = "malformed JPEG frame header";
                    return false;
                }

                height = (bytes[index + 3] << 8) | bytes[index + 4];
                width = (bytes[index + 5] << 8) | bytes[index + 6];
            }

            // Start of scan: the entropy coded data follows, which is not a segment
            // and cannot be stepped over. Everything worth reading is behind us.
            if (marker == 0xDA)
            {
                break;
            }

            index += segmentLength;
        }

        if (width <= 0 || height <= 0)
        {
            error = "no JPEG frame header found";
            return false;
        }

        if (!EndsWithEndOfImage(bytes))
        {
            error = "truncated JPEG, the end of image marker is missing";
            return false;
        }

        return true;
    }

    /// <summary>
    /// FF D9 has to be the last thing in the file. Trailing padding bytes are
    /// tolerated - some encoders align the file - but nothing else is.
    /// </summary>
    private static bool EndsWithEndOfImage(byte[] bytes)
    {
        int end = bytes.Length - 1;
        while (end > 0 && bytes[end] == 0x00)
        {
            end--;
        }

        return end >= 1 && bytes[end] == 0xD9 && bytes[end - 1] == 0xFF;
    }

    /// <summary>
    /// PNG is not what Bing serves, but a cache directory is a place users drop
    /// files into, so the format the tray menu can hand to Windows is accepted too.
    /// </summary>
    private static bool TryValidatePng(byte[] bytes, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;

        // 8 byte signature, then the IHDR chunk: length, type, width, height.
        if (bytes.Length < 33 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
        {
            error = "malformed PNG, no IHDR chunk";
            return false;
        }

        width = ReadBigEndian(bytes, 16);
        height = ReadBigEndian(bytes, 20);
        if (width <= 0 || height <= 0)
        {
            error = "malformed PNG, implausible dimensions";
            return false;
        }

        // IEND is the last chunk: 4 length bytes, the type, 4 CRC bytes.
        int end = bytes.Length - 8;
        if (bytes[end] != 'I' || bytes[end + 1] != 'E' || bytes[end + 2] != 'N' || bytes[end + 3] != 'D')
        {
            error = "truncated PNG, the IEND chunk is missing";
            return false;
        }

        return true;
    }

    private static int ReadBigEndian(byte[] bytes, int offset)
        => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];

    private static string Describe(byte[] bytes)
    {
        int count = Math.Min(4, bytes.Length);
        char[] hex = new char[(count * 3) - 1];
        for (int i = 0; i < count; i++)
        {
            byte value = bytes[i];
            hex[i * 3] = ToHex(value >> 4);
            hex[(i * 3) + 1] = ToHex(value & 0x0F);
            if (i < count - 1)
            {
                hex[(i * 3) + 2] = ' ';
            }
        }

        return new string(hex);
    }

    private static char ToHex(int value) => (char)(value < 10 ? '0' + value : 'A' + (value - 10));
}
