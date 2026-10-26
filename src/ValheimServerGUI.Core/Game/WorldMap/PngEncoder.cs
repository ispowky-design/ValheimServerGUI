using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>Writes opaque pixels as a PNG image (8-bit RGB, no filtering), for the map shared on the web.</summary>
    public static class PngEncoder
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        /// <param name="pixels">BGRA, one <c>uint</c> per pixel, row-major; alpha is ignored.</param>
        public static byte[] Encode(int width, int height, uint[] pixels)
        {
            if (width <= 0 || height <= 0 || pixels.Length < width * height) throw new ArgumentException("The pixels do not match the size.", nameof(pixels));

            using var output = new MemoryStream();
            output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; // bit depth
            header[9] = 2; // truecolour
            WriteChunk(output, "IHDR", header);

            using (var data = new MemoryStream())
            {
                using (var zlib = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
                {
                    var row = new byte[1 + width * 3];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var p = pixels[y * width + x];
                            row[1 + x * 3] = (byte)(p >> 16);
                            row[2 + x * 3] = (byte)(p >> 8);
                            row[3 + x * 3] = (byte)p;
                        }
                        zlib.Write(row);
                    }
                }
                WriteChunk(output, "IDAT", data.ToArray());
            }

            WriteChunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            Span<byte> buffer = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
            output.Write(buffer);

            var typeBytes = Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes);
            output.Write(data);

            var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
            BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
            output.Write(buffer);
        }

        private static uint Crc(uint crc, byte[] bytes)
        {
            foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
