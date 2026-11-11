using System;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    /// <summary>
    /// Opening formation: the whole army of one side forms one wide rectangle, laid out like the formation config.
    /// <list type="bullet">
    /// <item>Every config row (Y) becomes a band across the full width, rows front to back as in the config.</item>
    /// <item>Inside a band, the config columns keep their places: centre block in the middle, X = 1, 2… blocks
    /// mirrored outwards on both sides. Each block gets a share of the width that matches its size.</item>
    /// <item>All blocks of a band have the same number of lines, so the band, and the whole army, is a rectangle.</item>
    /// <item>Spots that cannot hold a unit (castle, water, off the map) are skipped; lines go on further back, so the
    /// army spills around obstacles.</item>
    /// </list>
    /// </summary>
    internal static class RecoilInitialArmyPlanner
    {
        /// <summary>A block of the formation config: Count units per side of the centre line (X = 0) or per mirror.</summary>
        internal struct ConfigBlock
        {
            public int Type, X, Y, Count;
        }

        /// <summary>One block placed in a band: Column is signed (negative = left of centre).</summary>
        internal struct Segment
        {
            public int Type, Column, Count;
            public float Radius;
        }

        internal sealed class Row
        {
            public int Y;
            public readonly List<Segment> Segments = new();
        }

        internal struct Placement
        {
            public int Type;
            public float Cross, Depth;
        }

        /// <summary>Centre-to-centre spacing of two units of this size with <paramref name="clearance"/> between them.</summary>
        internal static float Pitch(float radius, float clearance) => radius * 2f + clearance;

        /// <summary>
        /// Splits <paramref name="counts"/> (units per type for one side) over the config's blocks in proportion to
        /// their sizes. Types missing from the config use the fallback layout's blocks, else go to a new rear row.
        /// </summary>
        internal static List<Row> BuildRows(IReadOnlyList<ConfigBlock> config, IReadOnlyList<ConfigBlock> fallback,
            int[] counts, float[] radii)
        {
            var rows = new SortedDictionary<int, Row>();
            var rear = 0;
            foreach (var block in config) rear = Math.Max(rear, block.Y + 1);
            for (var type = 0; type < counts.Length; type++)
            {
                var total = counts[type];
                if (total <= 0) continue;
                var blocks = new List<ConfigBlock>();
                foreach (var block in config) if (block.Type == type && block.Count > 0) blocks.Add(block);
                if (blocks.Count == 0)
                    foreach (var block in fallback) if (block.Type == type && block.Count > 0) blocks.Add(block);
                if (blocks.Count == 0) blocks.Add(new ConfigBlock { Type = type, X = 0, Y = rear++, Count = 1 });

                long weight = 0;
                foreach (var block in blocks) weight += (long)block.Count * (block.X == 0 ? 1 : 2);
                var perSide = new int[blocks.Count];
                var extraLeft = new int[blocks.Count];
                var used = 0;
                for (var i = 0; i < blocks.Count; i++)
                {
                    perSide[i] = (int)(blocks[i].Count * (long)total / weight);
                    used += perSide[i] * (blocks[i].X == 0 ? 1 : 2);
                }
                var leftover = total - used;
                var centre = blocks.FindIndex(b => b.X == 0);
                if (centre >= 0) perSide[centre] += leftover;
                else
                {
                    // No centre block: pairs keep the mirror; a last odd unit goes to one side only.
                    for (var i = 0; leftover >= 2; i = (i + 1) % blocks.Count, leftover -= 2) perSide[i]++;
                    if (leftover == 1) extraLeft[0] = 1;
                }
                for (var i = 0; i < blocks.Count; i++)
                {
                    var block = blocks[i];
                    if (!rows.TryGetValue(block.Y, out var row)) rows.Add(block.Y, row = new Row { Y = block.Y });
                    var radius = radii[type];
                    if (block.X == 0)
                    {
                        if (perSide[i] > 0) row.Segments.Add(new Segment { Type = type, Column = 0, Count = perSide[i], Radius = radius });
                        continue;
                    }
                    var x = Math.Abs(block.X);
                    if (perSide[i] + extraLeft[i] > 0)
                        row.Segments.Add(new Segment { Type = type, Column = -x, Count = perSide[i] + extraLeft[i], Radius = radius });
                    if (perSide[i] > 0)
                        row.Segments.Add(new Segment { Type = type, Column = x, Count = perSide[i], Radius = radius });
                }
            }
            var result = new List<Row>();
            foreach (var row in rows.Values)
            {
                if (row.Segments.Count == 0) continue;
                row.Segments.Sort((a, b) => a.Column != b.Column ? a.Column.CompareTo(b.Column) : a.Type.CompareTo(b.Type));
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Places the rows: <paramref name="width"/> wide, front line <paramref name="frontGap"/> behind the middle,
        /// about <paramref name="unitsPerLine"/> units per line. <paramref name="canPlace"/>(cross, depth, radius)
        /// says whether a spot can hold a unit. False when the army does not fit within <paramref name="maxDepth"/>.
        /// </summary>
        internal static bool Place(List<Row> rows, float width, float clearance, int unitsPerLine, float frontGap,
            float maxDepth, Func<float, float, float, bool> canPlace, List<Placement> output)
        {
            var depth = frontGap;
            var first = true;
            var free = new List<float>();
            foreach (var row in rows)
            {
                var segments = row.Segments;
                float rowRadius = 0f, rowPitch = 0f, weight = 0f;
                var total = 0;
                foreach (var segment in segments)
                {
                    rowRadius = Math.Max(rowRadius, segment.Radius);
                    rowPitch = Math.Max(rowPitch, Pitch(segment.Radius, clearance));
                    weight += segment.Count * Pitch(segment.Radius, clearance);
                    total += segment.Count;
                }
                if (total <= 0) continue;

                // Each block gets width in proportion to the room its units need, left to right.
                var left = new float[segments.Count];
                var size = new float[segments.Count];
                var edge = -width * .5f;
                for (var i = 0; i < segments.Count; i++)
                {
                    left[i] = edge;
                    size[i] = width * segments[i].Count * Pitch(segments[i].Radius, clearance) / weight;
                    edge += size[i];
                }
                // Same number of lines for every block of the row (so the row is a rectangle), enough for each to fit.
                var lines = Math.Max(1, (int)Math.Round(total / (double)Math.Max(1, unitsPerLine)));
                var columns = new int[segments.Count];
                for (var guard = 0; guard < 100000; guard++, lines++)
                {
                    var fits = true;
                    for (var i = 0; i < segments.Count && fits; i++)
                    {
                        var pitch = Pitch(segments[i].Radius, clearance);
                        var maxColumns = Math.Max(1, (int)Math.Floor((size[i] - segments[i].Radius * 2f) / pitch) + 1);
                        columns[i] = (segments[i].Count + lines - 1) / lines;
                        fits = columns[i] <= maxColumns;
                    }
                    if (fits) break;
                }

                if (!first) depth += clearance * 2f; // a wider gap between two rows of the config
                first = false;
                var line = depth + rowRadius;
                var remaining = new int[segments.Count];
                var left_ = 0;
                for (var i = 0; i < segments.Count; i++) left_ += remaining[i] = segments[i].Count;
                while (left_ > 0)
                {
                    if (line > maxDepth) return false;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        if (remaining[i] <= 0) continue;
                        var segment = segments[i];
                        var span = Math.Max(0f, size[i] - segment.Radius * 2f);
                        free.Clear();
                        for (var column = 0; column < columns[i]; column++)
                        {
                            var cross = columns[i] > 1
                                ? left[i] + segment.Radius + column * span / (columns[i] - 1)
                                : left[i] + size[i] * .5f;
                            if (canPlace(cross, line, segment.Radius)) free.Add(cross);
                        }
                        var take = Math.Min(remaining[i], free.Count);
                        for (var k = 0; k < take; k++)
                        {
                            // A last, shorter line is spread evenly over the block, not packed to one side.
                            var index = take == free.Count ? k
                                : take == 1 ? free.Count / 2
                                : (int)Math.Round(k * (free.Count - 1) / (double)(take - 1));
                            output.Add(new Placement { Type = segment.Type, Cross = free[index], Depth = line });
                        }
                        remaining[i] -= take;
                        left_ -= take;
                    }
                    line += rowPitch;
                }
                depth = line - rowPitch + rowRadius; // rear edge of the row's last line
            }
            return true;
        }
    }
}
