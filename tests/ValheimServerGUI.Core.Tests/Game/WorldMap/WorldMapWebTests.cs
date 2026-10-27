using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>Sharing the map on the web: the picture of the discovered area, its PNG, the page and the server.</summary>
    public class WorldMapWebTests
    {
        private static readonly WorldMapRenderer Renderer = new(ValheimHash.SeedFromName("j3QV2ftr3y"));

        // A table map that discovered a 600 m square around (1000, 500).
        private static WorldMapExploration Discovered()
        {
            const int size = 2048;
            var pixels = new byte[size * size];
            for (var x = 700; x <= 1300; x += 12)
            {
                for (var z = 200; z <= 800; z += 12)
                {
                    var col = (int)Math.Round(x / 12.0 + 1024);
                    var row = (int)Math.Round(z / 12.0 + 1024);
                    pixels[row * size + col] = 1;
                }
            }
            return WorldMapExploration.FromMinimap(size, pixels);
        }

        [Fact]
        public void Render_FramesTheDiscoveredAreaAndCoversTheRest()
        {
            var picture = WorldMapImage.Render(Renderer, Discovered(), maxPixels: 256);

            Assert.True(picture.Width <= 256 && picture.Height <= 256);
            Assert.InRange(picture.Width * picture.Region.MetersPerPixel, 600, 800);
            var (fx, fy) = picture.ToFraction(1000, 500);
            Assert.InRange(fx, 0.45, 0.55);
            Assert.InRange(fy, 0.45, 0.55);

            Assert.False(IsParchment(Pixel(picture, 1000, 500)));
            Assert.True(IsParchment(picture.Pixels[0])); // the corner, outside the discovered square
            Assert.All(picture.Pixels, p => Assert.Equal(0xFFu, p >> 24));
        }

        [Fact]
        public void Render_WithoutExploration_DrawsTheWholeWorld()
        {
            var picture = WorldMapImage.Render(Renderer, null, maxPixels: 64);

            Assert.Equal(64, picture.Width);
            Assert.Equal(WorldMapRenderer.MapRadius * 2, picture.Width * picture.Region.MetersPerPixel, 3);
            Assert.True(IsParchment(picture.Pixels[0])); // outside the world disc
            Assert.False(IsParchment(Pixel(picture, 0, 0)));
        }

        /// <summary>Close to the paper's colour: it is mottled, so not exactly <see cref="WorldMapImage.Parchment"/>.</summary>
        private static bool IsParchment(uint pixel)
        {
            const uint paper = WorldMapImage.Parchment;
            return pixel >> 24 == 0xFF && Enumerable.Range(0, 3).All(i =>
                Math.Abs((int)((pixel >> (8 * i)) & 0xFF) - (int)((paper >> (8 * i)) & 0xFF)) <= 24);
        }

        private static uint Pixel(WorldMapPicture picture, double x, double z)
        {
            var (fx, fy) = picture.ToFraction(x, z);
            return picture.Pixels[(int)(fy * picture.Height) * picture.Width + (int)(fx * picture.Width)];
        }

        [Fact]
        public void Png_HoldsThePixelsAsRgb()
        {
            var png = PngEncoder.Encode(2, 1, new uint[] { 0xFF102030, 0xFFA0B0C0 });

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
            Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
            Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

            var idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
            Assert.Equal("IDAT", Encoding.ASCII.GetString(png, 37, 4));
            using var zlib = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            zlib.CopyTo(raw);
            Assert.Equal(new byte[] { 0, 0x10, 0x20, 0x30, 0xA0, 0xB0, 0xC0 }, raw.ToArray());

            // IEND's CRC is fixed by the PNG specification.
            Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, png[^12..]);
        }

        [Fact]
        public void Page_EncodesTheWorldNameAndLeavesOutPlacesOffThePicture()
        {
            var picture = new WorldMapPicture(100, 100, new WorldMapRegion(0, 100, 1), new uint[100 * 100]);
            var markers = new[]
            {
                new WorldMapMarker("Eikthyr", WorldLocationKind.Boss, 50, 50),
                new WorldMapMarker("Far </script>", WorldLocationKind.Trader, 500, 50),
            };

            var page = WorldMapWebPage.Build("<b>Midgard</b>", picture, markers, new DateTime(2026, 10, 26, 15, 0, 0), "abc");

            Assert.Contains(WebUtility.HtmlEncode(string.Format(Strings.MapWeb_Title, "<b>Midgard</b>")), page);
            Assert.DoesNotContain("<b>Midgard</b>", page);
            Assert.Contains("\"n\":\"Eikthyr\",\"k\":\"boss\",\"x\":50,\"y\":50", page);
            Assert.DoesNotContain("Far", page);
            Assert.Contains("map.png?v=abc", page);
        }

        [Fact]
        public void Page_DrawsThePinsWithTheirIconAndCross()
        {
            var picture = new WorldMapPicture(100, 100, new WorldMapRegion(0, 100, 1), new uint[100 * 100]);
            var markers = new[]
            {
                new WorldMapMarker("Maison", WorldLocationKind.Pin, 25, 75) { PinIcon = WorldMapPinIcon.Home },
                new WorldMapMarker("Troll", WorldLocationKind.Pin, 10, 90) { PinIcon = WorldMapPinIcon.Dot, IsChecked = true },
            };

            var page = WorldMapWebPage.Build("Midgard", picture, markers, new DateTime(2026, 10, 27, 15, 0, 0), "abc");

            Assert.Contains("\"n\":\"Maison\",\"k\":\"pin\",\"x\":25,\"y\":25,\"i\":\"home\",\"c\":false", page);
            Assert.Contains("\"n\":\"Troll\",\"k\":\"pin\",\"x\":10,\"y\":10,\"i\":\"dot\",\"c\":true", page);
            Assert.Contains(WorldMapPinShapes.Path(WorldMapPinIcon.Home), page);
            Assert.Contains(WebUtility.HtmlEncode(Strings.Map_Layer_Pins), page);
        }

        [Fact]
        public void Server_ServesThePageThePictureAndTheVersion()
        {
            using var server = new WorldMapWebServer();
            Assert.Contains(WebUtility.HtmlEncode(Strings.MapWeb_NotReady), Body(server.Respond("GET / HTTP/1.1")));
            Assert.StartsWith("HTTP/1.1 404", Text(server.Respond("GET /map.png HTTP/1.1")));

            server.Publish(new WorldMapWebContent("<p>map</p>", new byte[] { 1, 2, 3 }, "v1"));

            Assert.Equal("<p>map</p>", Body(server.Respond("GET /?x=1 HTTP/1.1")));
            Assert.Equal("v1", Body(server.Respond("GET /version HTTP/1.1")));
            var png = server.Respond("GET /map.png?v=v1 HTTP/1.1");
            Assert.Contains("Content-Type: image/png", Text(png));
            Assert.Equal(new byte[] { 1, 2, 3 }, png[^3..]);
            Assert.Equal("", Body(server.Respond("HEAD / HTTP/1.1")));
            Assert.StartsWith("HTTP/1.1 404", Text(server.Respond("GET /../userprefs.json HTTP/1.1")));
            Assert.StartsWith("HTTP/1.1 405", Text(server.Respond("POST / HTTP/1.1")));
            Assert.StartsWith("HTTP/1.1 400", Text(server.Respond("nonsense")));
            Assert.StartsWith("HTTP/1.1 400", Text(server.Respond(null)));
        }

        [Fact]
        public async Task Server_AnswersOverTheNetwork()
        {
            using var server = new WorldMapWebServer();
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            server.Start(port);
            server.Publish(new WorldMapWebContent("<p>map</p>", new byte[] { 1 }, "v1"));
            using var client = new HttpClient();

            Assert.Equal("<p>map</p>", await client.GetStringAsync($"http://127.0.0.1:{port}/"));
            Assert.Equal(port, server.Port);

            server.Stop();
            Assert.False(server.IsRunning);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"http://127.0.0.1:{port}/"));
        }

        private static string Text(byte[] response) => Encoding.UTF8.GetString(response);

        private static string Body(byte[] response)
        {
            var text = Text(response);
            return text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        }
    }
}
