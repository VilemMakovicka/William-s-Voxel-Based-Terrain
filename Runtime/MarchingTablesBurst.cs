using Unity.Mathematics;

namespace vilemmakovicka.wvbterrain {
    public static class MarchingTablesBurst {
        // Flattened Paul Bourke / Sebastian Lague tri-table (256 * 16)
        // Converted to NativeArray at runtime
        public static readonly int[] FlatTriTable = new int[256 * 16];
        public static readonly int[] FlatEdgeTable = new int[256];

        public static readonly int2[] EdgeCorners = new int2[12]
        {
        new int2(0, 1), new int2(1, 2), new int2(2, 3), new int2(3, 0),
        new int2(4, 5), new int2(5, 6), new int2(6, 7), new int2(7, 4),
        new int2(0, 4), new int2(1, 5), new int2(2, 6), new int2(3, 7)
        };

        static MarchingTablesBurst() {
            // Copy your validated 256x16 TriTable and EdgeTable into flat arrays
            for (int c = 0; c < 256; c++) {
                FlatEdgeTable[c] = MarchingTable.EdgeTable[c];
                for (int i = 0; i < 16; i++) {
                    FlatTriTable[c * 16 + i] = MarchingTable.TriTable[c, i];
                }
            }
        }
    } 
}