using System;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    // Grid coordinates express ordering and symmetry, never fixed world distances.
    internal static class RecoilInitialArmyPlanner
    {
        internal struct Block
        {
            public int Type, X, Y, Count;
            public float Radius;
        }

        internal struct Slot
        {
            public int Type;
            public float Cross, Depth, Radius;
        }

        internal static List<Block> Resize(List<Block> source, int[] counts, List<Block> fallback, float[] radii)
        {
            var result = new List<Block>();
            var rear = 0;
            foreach (var block in source) rear = Math.Max(rear, block.Y + 1);
            for (var type = 0; type < counts.Length; type++)
            {
                if (counts[type] <= 0) continue;
                var pattern = source.FindAll(b => b.Type == type);
                var radius = radii[type];
                if (pattern.Count == 0)
                    foreach (var block in fallback)
                        if (block.Type == type) pattern.Add(new Block
                        { Type = type, X = block.X, Y = block.Y, Count = block.Count, Radius = radius });
                var weight = 0;
                foreach (var block in pattern) weight += block.Count * (block.X == 0 ? 1 : 2);
                var remaining = counts[type];
                var center = -1;
                foreach (var original in pattern)
                {
                    var block = original;
                    block.Radius = radius;
                    block.Count = counts[type] * original.Count / Math.Max(1, weight);
                    if (block.Count == 0) continue;
                    remaining -= block.Count * (block.X == 0 ? 1 : 2);
                    if (block.X == 0) center = result.Count;
                    result.Add(block);
                }
                if (remaining <= 0) continue;
                if (center >= 0)
                {
                    var block = result[center]; block.Count += remaining; result[center] = block;
                }
                else
                {
                    // Odd reinforcements occupy the center, retaining exact left/right symmetry.
                    var y = pattern[0].Y;
                    if (source.Exists(b => b.X == 0 && b.Y == y && b.Type != type) ||
                        result.Exists(b => b.X == 0 && b.Y == y)) y = rear++;
                    result.Add(new Block
                    { Type = type, X = 0, Y = y, Count = remaining, Radius = radius });
                }
            }
            var occupied = new HashSet<(int, int)>();
            for (var i = 0; i < result.Count; i++)
            {
                var block = result[i];
                while (!occupied.Add((block.X, block.Y))) block.Y = rear++;
                result[i] = block;
            }            return result;
        }

        internal static bool TryBuild(List<Block> blocks, float halfWidth, float aspect, out List<Slot> slots)
        {
            slots = new List<Slot>();
            if (halfWidth <= 0 || aspect <= 0) return false;
            var rows = new SortedDictionary<int, List<Block>>();
            foreach (var block in blocks)
            {
                if (block.Count <= 0 || block.Radius <= 0 || block.X < 0) return false;
                if (!rows.TryGetValue(block.Y, out var row)) rows.Add(block.Y, row = new List<Block>());
                row.Add(block);
            }
            var front = 0f;
            var previousY = -1;
            foreach (var pair in rows)
            {
                var row = pair.Value;
                row.Sort((a, b) => a.X.CompareTo(b.X));
                var weight = 0.0;
                foreach (var block in row) weight += Math.Sqrt(block.Count) * (block.X == 0 ? 1 : 2);
                var edge = 0f;
                var depth = 0f;
                var previousX = 0;
                if (previousY >= 0) front += 1f + Math.Min(3, Math.Max(0, pair.Key - previousY - 1)) * .5f;
                foreach (var block in row)
                {
                    var pitch = block.Radius * 2f + .15f;
                    var budget = (float)(2 * halfWidth * Math.Sqrt(block.Count) / weight) - 2f;
                    var maxColumns = Math.Max(1, (int)Math.Floor((budget - 2 * block.Radius) / pitch) + 1);
                    // Ranged lines stay wide when space permits; large armies gain depth rather than overlap.
                    var preferred = block.Type == 0 || block.Type == 5
                        ? block.Count : (int)Math.Ceiling(Math.Sqrt(block.Count * aspect));
                    var columns = Math.Max(1, Math.Min(block.Count, Math.Min(maxColumns, preferred)));
                    var width = (columns - 1) * pitch + block.Radius * 2;
                    var lines = (block.Count + columns - 1) / columns;
                    var blockDepth = (lines - 1) * pitch + block.Radius * 2;
                    float center;
                    if (block.X == 0)
                    {
                        center = 0;
                        edge = width * .5f;
                    }
                    else
                    {
                        var gap = 1f + Math.Min(3, Math.Max(0, block.X - previousX - 1)) * .5f;
                        center = edge + gap + width * .5f;
                        edge = center + width * .5f;
                    }
                    if (edge > halfWidth) return false;
                    depth = Math.Max(depth, blockDepth);
                    for (var mirror = 0; mirror < (block.X == 0 ? 1 : 2); mirror++)
                    for (var i = 0; i < block.Count; i++)
                    {
                        var lineCount = Math.Min(columns, block.Count - (i / columns) * columns);
                        var cross = center + (i % columns - (lineCount - 1) * .5f) * pitch;
                        slots.Add(new Slot
                        {
                            Type = block.Type, Radius = block.Radius,
                            Cross = mirror == 0 ? cross : -cross,
                            Depth = front + block.Radius + i / columns * pitch
                        });
                    }
                    previousX = block.X;
                }
                front += depth;
                previousY = pair.Key;
            }
            // The spawn queues consume all soldiers of each type together.
            slots.Sort((a, b) =>
            {
                var order = a.Type.CompareTo(b.Type);
                if (order == 0) order = a.Depth.CompareTo(b.Depth);
                if (order == 0) order = Math.Abs(a.Cross).CompareTo(Math.Abs(b.Cross));
                return order != 0 ? order : a.Cross.CompareTo(b.Cross);
            });
            return slots.Count > 0;
        }
    }
}
