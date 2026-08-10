using System.Collections.Generic;
using GTA.Math;

namespace GTAZ.Data
{
    public class PlayerData
    {
        public Vector3 Position { get; set; }
        public float Heading { get; set; }
        public float Health { get; set; } = 100f;
        public float Armor { get; set; } = 0f;
        public List<int> OwnedTerritoryHashes { get; set; } = [];
    }
}
