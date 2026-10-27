using System.Collections.Generic;
using System.Linq;
using SeedLab.WorldGen;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>One point of interest on the world map.</summary>
    /// <param name="X">World X (east).</param>
    /// <param name="Z">World Z (north).</param>
    public sealed record WorldMapMarker(string Name, WorldLocationKind Kind, float X, float Z)
    {
        /// <summary>For a <see cref="WorldLocationKind.Pin"/>: the icon the player picked.</summary>
        public WorldMapPinIcon PinIcon { get; init; }

        /// <summary>For a <see cref="WorldLocationKind.Pin"/>: crossed out on the in-game map.</summary>
        public bool IsChecked { get; init; }
    }

    /// <summary>The icon of a pin on the in-game map.</summary>
    public enum WorldMapPinIcon
    {
        /// <summary>The plain round pin, also used for any pin type this app does not know.</summary>
        Dot,
        Fire,
        Home,
        Hammer,
        Rune,

        /// <summary>A boss altar found with a Vegvisir.</summary>
        Boss,

        /// <summary>A place Hildir asked players to look for.</summary>
        Quest,
    }

    /// <summary>A biome swatch for the map legend.</summary>
    public sealed record WorldMapLegendEntry(string Name, byte R, byte G, byte B);

    public static class WorldMapMarkers
    {
        /// <summary>
        /// The markers for a world save: every stored location the <see cref="WorldLocationCatalog"/> knows, in save
        /// order. Locations of any other type are left out.
        /// </summary>
        public static IReadOnlyList<WorldMapMarker> FromSave(WorldMapSaveData save)
        {
            var markers = new List<WorldMapMarker>();
            foreach (var location in save.Locations)
            {
                if (WorldLocationCatalog.Find(location.PrefabHash) is { } type)
                {
                    markers.Add(new WorldMapMarker(type.DisplayName(), type.Kind, location.X, location.Z));
                }
            }
            return markers;
        }

        /// <summary>The biomes in the legend, coloured as the map draws them.</summary>
        public static IReadOnlyList<WorldMapLegendEntry> Legend()
        {
            (Biome Biome, string Name)[] biomes =
            {
                (Biome.Meadows, Strings.Map_Biome_Meadows),
                (Biome.BlackForest, Strings.Map_Biome_BlackForest),
                (Biome.Swamp, Strings.Map_Biome_Swamp),
                (Biome.Mountain, Strings.Map_Biome_Mountain),
                (Biome.Plains, Strings.Map_Biome_Plains),
                (Biome.Mistlands, Strings.Map_Biome_Mistlands),
                (Biome.AshLands, Strings.Map_Biome_Ashlands),
                (Biome.DeepNorth, Strings.Map_Biome_DeepNorth),
                (Biome.Ocean, Strings.Map_Biome_Ocean),
            };

            return biomes.Select(b =>
            {
                var (r, g, bl) = b.Biome == Biome.Ocean ? WorldMapPalette.Water : WorldMapPalette.ForLegend(b.Biome);
                return new WorldMapLegendEntry(b.Name, (byte)r, (byte)g, (byte)bl);
            }).ToList();
        }
    }
}
