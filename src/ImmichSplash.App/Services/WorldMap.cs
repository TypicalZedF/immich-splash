using System.IO.Compression;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using ImmichSplash.Core;

namespace ImmichSplash.App.Services;

/// <summary>
/// A built-in world map from Natural Earth (public domain): land, lakes, major rivers, country
/// borders and state/province lines (US, Canada, Brazil, Australia), so the map always works without
/// any online map service. Shapes are in Web Mercator coordinates from 0 to 1, ready to be scaled.
/// </summary>
public static class WorldMap
{
    /// <summary>
    /// A layer cut into pieces with their bounds, so a zoomed-in map only draws the pieces on screen instead of
    /// every coastline in the world (drawing everything made each redraw slower the further you zoomed in).
    /// </summary>
    public sealed record Layer(IReadOnlyList<(Rect Bounds, Geometry Shape)> Pieces);

    sealed record Layers(Layer? Land, Layer? Lakes, Layer? Rivers, Layer? Borders, Layer? States);

    static readonly Lazy<Layers> Shapes = new(Load);

    public static Layer? Land => Shapes.Value.Land;
    public static Layer? Lakes => Shapes.Value.Lakes;
    public static Layer? Rivers => Shapes.Value.Rivers;
    public static Layer? Borders => Shapes.Value.Borders;
    public static Layer? States => Shapes.Value.States;

    /// <summary>Pieces are about this share of the world wide (lines are also split into runs this long).</summary>
    const int Grid = 24;

    public const string Attribution = "Natural Earth";

    static Layers Load()
    {
        var none = new Layers(null, null, null, null, null);
        try
        {
            using var asset = AssetLoader.Open(new Uri("avares://ImmichSplash/Assets/world-50m.bin.gz"));
            using var gzip = new GZipStream(asset, CompressionMode.Decompress);
            using var data = new MemoryStream();
            gzip.CopyTo(data);
            var reader = new Reader(data.ToArray());
            if (reader.Magic() != "IMWM2")
                return none;
            double sx = reader.Double(), sy = reader.Double(), tx = reader.Double(), ty = reader.Double();
            Point Project(long x, long y)
            {
                var lon = x * sx + tx;
                var lat = Math.Clamp(y * sy + ty, -85.05112878, 85.05112878);
                var sin = Math.Sin(lat * Math.PI / 180);
                return new Point((lon + 180) / 360, 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI));
            }
            // Layer order is fixed by the converter: land, lakes, rivers, borders, states.
            if (reader.Int32() != 5)
                return none;
            return new Layers(
                Read(reader, Project, closed: true),
                Read(reader, Project, closed: true),
                Read(reader, Project, closed: false),
                Read(reader, Project, closed: false),
                Read(reader, Project, closed: false));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load the built-in world map", ex);
            return none;
        }
    }

    static Layer Read(Reader reader, Func<long, long, Point> project, bool closed)
    {
        var figures = new List<Point[]>();
        var count = reader.Int32();
        for (var i = 0; i < count; i++)
        {
            var points = (int)reader.VarInt();
            var figure = new Point[points];
            long x = 0, y = 0;
            for (var p = 0; p < points; p++)
            {
                x += reader.VarInt();
                y += reader.VarInt();
                figure[p] = project(x, y);
            }
            if (points > 1)
            {
                if (closed)
                    figures.Add(figure);
                else
                    figures.AddRange(SplitLine(figure)); // long lines (borders, rivers) in short runs
            }
        }

        // Group figures by grid cell. A closed shape that sits inside a bigger one (a hole: lakes are holes in the
        // land) goes with the bigger one, so the even-odd fill still cuts it out.
        var bounds = figures.Select(BoundsOf).ToArray();
        var cellOf = new int[figures.Count];
        var order = Enumerable.Range(0, figures.Count).OrderByDescending(i => bounds[i].Width * bounds[i].Height).ToArray();
        var placed = new List<int>();
        foreach (var i in order)
        {
            var cell = -1;
            if (closed)
                foreach (var outer in placed)
                    if (bounds[outer].Contains(bounds[i]))
                    {
                        cell = cellOf[outer];
                        break;
                    }
            if (cell < 0)
            {
                var c = bounds[i].Center;
                cell = Math.Clamp((int)(c.Y * Grid), 0, Grid - 1) * Grid + Math.Clamp((int)(c.X * Grid), 0, Grid - 1);
            }
            cellOf[i] = cell;
            if (closed && bounds[i].Width * bounds[i].Height > 1e-6)
                placed.Add(i);
        }
        var pieces = new List<(Rect, Geometry)>();
        foreach (var group in Enumerable.Range(0, figures.Count).GroupBy(i => cellOf[i]))
        {
            var geometry = new StreamGeometry();
            Rect? all = null;
            using (var ctx = geometry.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                foreach (var i in group)
                {
                    var figure = figures[i];
                    ctx.BeginFigure(figure[0], isFilled: closed);
                    for (var p = 1; p < figure.Length; p++)
                        ctx.LineTo(figure[p]);
                    ctx.EndFigure(closed);
                    all = all is { } r ? r.Union(bounds[i]) : bounds[i];
                }
            }
            pieces.Add((all ?? default, geometry));
        }
        return new Layer(pieces);
    }

    static IEnumerable<Point[]> SplitLine(Point[] line)
    {
        const int Run = 64;
        if (line.Length <= Run)
        {
            yield return line;
            yield break;
        }
        for (var start = 0; start < line.Length - 1; start += Run - 1)
            yield return line[start..Math.Min(line.Length, start + Run)];
    }

    static Rect BoundsOf(Point[] points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    sealed class Reader(byte[] data)
    {
        int _pos;

        public string Magic()
        {
            var magic = System.Text.Encoding.ASCII.GetString(data, 0, 5);
            _pos = 5;
            return magic;
        }

        public double Double()
        {
            var value = BitConverter.ToDouble(data, _pos);
            _pos += 8;
            return value;
        }

        public int Int32()
        {
            var value = BitConverter.ToInt32(data, _pos);
            _pos += 4;
            return value;
        }

        /// <summary>Zigzag-encoded variable-length integer.</summary>
        public long VarInt()
        {
            ulong result = 0;
            var shift = 0;
            byte b;
            do
            {
                b = data[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return (long)(result >> 1) ^ -(long)(result & 1);
        }
    }
}
