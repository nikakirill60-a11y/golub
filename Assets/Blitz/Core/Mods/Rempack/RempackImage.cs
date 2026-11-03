namespace BlitzLegacy.Core.Mods.Rempack
{
    public enum RempackImageFormat
    {
        Unknown,
        Png,
        Jpeg,
    }

    /// <summary>
    /// Reads the size of a PNG or JPEG from its header without decoding it, so a
    /// package's icons and screenshots are checked before any decoder sees them.
    /// </summary>
    public static class RempackImage
    {
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool TryProbe(byte[] data, out RempackImageFormat format, out int width, out int height)
        {
            format = RempackImageFormat.Unknown;
            width = 0;
            height = 0;
            if (data == null)
            {
                return false;
            }

            if (IsPng(data))
            {
                // The first chunk must be IHDR: length 13, then width and height big-endian.
                if (data.Length < 33 || Be32(data, 8) != 13 || data[12] != 'I' || data[13] != 'H' || data[14] != 'D' || data[15] != 'R')
                {
                    return false;
                }

                var w = Be32(data, 16);
                var h = Be32(data, 20);
                if (w <= 0 || h <= 0)
                {
                    return false;
                }

                format = RempackImageFormat.Png;
                width = w;
                height = h;
                return true;
            }

            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
            {
                return ProbeJpeg(data, ref format, ref width, ref height);
            }

            return false;
        }

        public static bool IsPng(byte[] data)
        {
            if (data == null || data.Length < PngSignature.Length)
            {
                return false;
            }

            for (var i = 0; i < PngSignature.Length; i++)
            {
                if (data[i] != PngSignature[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ProbeJpeg(byte[] data, ref RempackImageFormat format, ref int width, ref int height)
        {
            var at = 2;
            while (at + 4 <= data.Length)
            {
                if (data[at] != 0xFF)
                {
                    return false;
                }

                var marker = data[at + 1];
                if (marker == 0xFF)
                {
                    at++;
                    continue;
                }

                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    at += 2;
                    continue;
                }

                if (marker == 0xD9 || marker == 0xDA)
                {
                    return false;
                }

                var length = (data[at + 2] << 8) | data[at + 3];
                if (length < 2 || at + 2 + length > data.Length)
                {
                    return false;
                }

                var frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (frame)
                {
                    if (length < 7)
                    {
                        return false;
                    }

                    var h = (data[at + 5] << 8) | data[at + 6];
                    var w = (data[at + 7] << 8) | data[at + 8];
                    if (w <= 0 || h <= 0)
                    {
                        return false;
                    }

                    format = RempackImageFormat.Jpeg;
                    width = w;
                    height = h;
                    return true;
                }

                at += 2 + length;
            }

            return false;
        }

        private static int Be32(byte[] data, int at)
        {
            var value = ((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];
            return value > int.MaxValue ? -1 : (int)value;
        }
    }
}
