using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

// One brick per world trunk. Coarse probes precede fine probes in the shared SH buffers.
internal sealed class LrtProbeLayout
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Brick
    {
        public int Offset;
        public int Edge;
        public int Slot;
        public int WorldIndex;
    }

    public readonly Brick[] Bricks;
    public readonly int FineEdge;
    public readonly int CoarseEdge;
    public readonly int FineBricks;
    public readonly int CoarseBricks;
    public readonly Vector3Int FineTiles;
    public readonly Vector3Int CoarseTiles;

    public int CoarseProbeCount => CoarseBricks * CoarseEdge * CoarseEdge * CoarseEdge;
    public int FineProbeCount => FineBricks * FineEdge * FineEdge * FineEdge;
    public int ProbeCount => CoarseProbeCount + FineProbeCount;
    public int PublishCellCount => CoarseBricks * TileCells(CoarseEdge) + FineBricks * TileCells(FineEdge);

    public LrtProbeLayout(Vector3Int trunks, Vector3 origin, float trunkEdge, int fineEdge, int coarseEdge,
        List<Bounds> gameplayBounds, int textureLimit)
    {
        FineEdge = fineEdge;
        CoarseEdge = coarseEdge;
        Bricks = new Brick[trunks.x * trunks.y * trunks.z];
        var fine = new bool[Bricks.Length];
        var trunkSize = Vector3.one * trunkEdge;

        foreach (Bounds region in gameplayBounds)
        {
            Vector3Int first = Vector3Int.Max(Vector3Int.zero, Vector3Int.FloorToInt((region.min - origin) / trunkEdge));
            Vector3Int end = Vector3Int.Min(trunks, Vector3Int.CeilToInt((region.max - origin) / trunkEdge));
            for (int z = first.z; z < end.z; z++)
            for (int y = first.y; y < end.y; y++)
            for (int x = first.x; x < end.x; x++)
            {
                int index = x + trunks.x * (y + trunks.y * z);
                if (fine[index])
                    continue;

                Vector3 min = origin + (Vector3)new Vector3Int(x, y, z) * trunkEdge;
                Bounds brick = new Bounds(min + trunkSize * 0.5f, trunkSize);
                if (!brick.Intersects(region))
                    continue;
                fine[index] = true;
                FineBricks++;
            }
        }

        CoarseBricks = Bricks.Length - FineBricks;
        int coarseSlot = 0;
        int fineSlot = 0;
        for (int i = 0; i < Bricks.Length; i++)
        {
            int slot = fine[i] ? fineSlot++ : coarseSlot++;
            int edge = fine[i] ? FineEdge : CoarseEdge;
            int orderedIndex = slot + (fine[i] ? CoarseBricks : 0);
            Bricks[i].Offset = (fine[i] ? CoarseProbeCount : 0) + slot * edge * edge * edge;
            Bricks[i].Edge = edge;
            Bricks[i].Slot = slot;
            Bricks[orderedIndex].WorldIndex = i;
        }

        FineTiles = PackTiles(FineBricks, FineEdge + 2, textureLimit);
        CoarseTiles = PackTiles(CoarseBricks, CoarseEdge + 2, textureLimit);
    }

    private static int TileCells(int edge)
    {
        int padded = edge + 2;
        return padded * padded * padded;
    }

    private static Vector3Int PackTiles(int count, int tileEdge, int textureLimit)
    {
        if (count == 0)
            return Vector3Int.one;

        int maxXY = textureLimit / tileEdge;
        int maxZ = textureLimit / (4 * tileEdge);
        if (maxZ == 0 || (long)maxXY * maxXY * maxZ < count)
            return Vector3Int.zero;

        int z = Mathf.Min(maxZ, Mathf.CeilToInt(Mathf.Pow(count, 1f / 3f)));
        int x = Mathf.CeilToInt(Mathf.Sqrt(Mathf.CeilToInt((float)count / z)));
        int y = Mathf.CeilToInt((float)count / (x * z));
        z = Mathf.CeilToInt((float)count / (x * y));
        return new Vector3Int(x, y, z);
    }
}
