using System;
using System.Collections.Generic;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>The marker categories the world map can show or hide.</summary>
    public enum WorldLocationKind
    {
        Spawn,
        Boss,
        Trader,
        Dungeon,
        Camp,
    }

    /// <summary>A location type the map knows: its game prefab name, category and display name.</summary>
    public sealed record WorldLocationType(string PrefabName, WorldLocationKind Kind, Func<string> DisplayName);

    /// <summary>
    /// The location prefabs the world map draws, looked up by the stable hash a world save stores for each location
    /// instance. Every other location (ruins, runestones, villages, ...) is left off the map.
    /// </summary>
    public static class WorldLocationCatalog
    {
        private static readonly WorldLocationType[] Types =
        {
            new("StartTemple", WorldLocationKind.Spawn, () => Strings.MapLocation_StartTemple),

            new("Eikthyrnir", WorldLocationKind.Boss, () => Strings.MapLocation_Eikthyr),
            new("GDKing", WorldLocationKind.Boss, () => Strings.MapLocation_TheElder),
            new("Bonemass", WorldLocationKind.Boss, () => Strings.MapLocation_Bonemass),
            new("Dragonqueen", WorldLocationKind.Boss, () => Strings.MapLocation_Moder),
            new("GoblinKing", WorldLocationKind.Boss, () => Strings.MapLocation_Yagluth),
            new("Mistlands_DvergrBossEntrance1", WorldLocationKind.Boss, () => Strings.MapLocation_TheQueen),
            new("FaderLocation", WorldLocationKind.Boss, () => Strings.MapLocation_Fader),
            // The Deep North altar's prefab and its serialized name differ; a save may store either hash.
            new("DN_Bossroom", WorldLocationKind.Boss, () => Strings.MapLocation_DeepNorthBoss),
            new("bosslocation", WorldLocationKind.Boss, () => Strings.MapLocation_DeepNorthBoss),

            new("Vendor_BlackForest", WorldLocationKind.Trader, () => Strings.MapLocation_Haldor),
            new("Hildir_camp", WorldLocationKind.Trader, () => Strings.MapLocation_Hildir),
            new("BogWitch_Camp", WorldLocationKind.Trader, () => Strings.MapLocation_BogWitch),

            new("Crypt2", WorldLocationKind.Dungeon, () => Strings.MapLocation_BurialChambers),
            new("Crypt3", WorldLocationKind.Dungeon, () => Strings.MapLocation_BurialChambers),
            new("Crypt4", WorldLocationKind.Dungeon, () => Strings.MapLocation_BurialChambers),
            new("TrollCave02", WorldLocationKind.Dungeon, () => Strings.MapLocation_TrollCave),
            new("SunkenCrypt4", WorldLocationKind.Dungeon, () => Strings.MapLocation_SunkenCrypt),
            new("MountainCave02", WorldLocationKind.Dungeon, () => Strings.MapLocation_FrostCave),
            new("Mistlands_DvergrTownEntrance1", WorldLocationKind.Dungeon, () => Strings.MapLocation_InfestedMine),
            new("Mistlands_DvergrTownEntrance2", WorldLocationKind.Dungeon, () => Strings.MapLocation_InfestedMine),
            new("Hildir_crypt", WorldLocationKind.Dungeon, () => Strings.MapLocation_SmoulderingTombs),
            new("Hildir_cave", WorldLocationKind.Dungeon, () => Strings.MapLocation_HowlingCavern),
            new("Hildir_plainsfortress", WorldLocationKind.Dungeon, () => Strings.MapLocation_SealedTower),
            new("CharredFortress", WorldLocationKind.Dungeon, () => Strings.MapLocation_CharredFortress),
            new("MorgenHole1", WorldLocationKind.Dungeon, () => Strings.MapLocation_MorgenHole),
            new("MorgenHole2", WorldLocationKind.Dungeon, () => Strings.MapLocation_MorgenHole),
            new("MorgenHole3", WorldLocationKind.Dungeon, () => Strings.MapLocation_MorgenHole),
            new("TheHole01", WorldLocationKind.Dungeon, () => Strings.MapLocation_TheHole),
            new("thehole01", WorldLocationKind.Dungeon, () => Strings.MapLocation_TheHole),

            new("GoblinCamp2", WorldLocationKind.Camp, () => Strings.MapLocation_FulingVillage),
            new("GoblinCamp2_1", WorldLocationKind.Camp, () => Strings.MapLocation_FulingVillage),
            new("Greydwarf_camp1", WorldLocationKind.Camp, () => Strings.MapLocation_GreydwarfNest),
        };

        private static readonly Dictionary<int, WorldLocationType> ByHash = BuildIndex();

        public static IReadOnlyList<WorldLocationType> All => Types;

        /// <summary>The type a stored location hash belongs to, or null for a location the map does not draw.</summary>
        public static WorldLocationType? Find(int prefabHash)
            => ByHash.TryGetValue(prefabHash, out var type) ? type : null;

        private static Dictionary<int, WorldLocationType> BuildIndex()
        {
            var index = new Dictionary<int, WorldLocationType>();
            foreach (var type in Types) index[ValheimHash.GetStableHashCode(type.PrefabName)] = type;
            return index;
        }
    }
}
