namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// Valheim's <c>string.GetStableHashCode()</c> (assembly_utils): a djb2 variant that consumes two UTF-16 code
    /// units per step. The game hashes a world's seed name with it to get the integer seed, and a location prefab's
    /// name to get the hash a world save stores for each location.
    /// </summary>
    public static class ValheimHash
    {
        public static int GetStableHashCode(string text)
        {
            unchecked
            {
                var num = 5381;
                var num2 = num;
                for (var i = 0; i < text.Length && text[i] != '\0'; i += 2)
                {
                    num = ((num << 5) + num) ^ text[i];
                    if (i == text.Length - 1 || text[i + 1] == '\0') break;
                    num2 = ((num2 << 5) + num2) ^ text[i + 1];
                }
                return num + num2 * 1566083941;
            }
        }

        /// <summary>The integer seed the game derives from a seed name. The empty name is hard-coded to 0.</summary>
        public static int SeedFromName(string seedName)
            => seedName.Length == 0 ? 0 : GetStableHashCode(seedName);
    }
}
