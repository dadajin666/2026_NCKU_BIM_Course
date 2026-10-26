using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Group3.EgressExporter;

static class Program
{
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }
    static double[] P(double x, double y) => new[] { x, y };
    static void Main()
    {
        var ring = Geometry.Ring(new[] { P(0,0), P(10,0), P(10,4), P(0,4), P(0,0) });
        Check(ring.Count == 4 && Geometry.Area(ring) == 40, "closed boundary removes duplicated endpoint without changing area");
        var path = Geometry.Chain(new List<List<double[]>> { new List<double[]> { P(0,0), P(1,0) }, new List<double[]> { P(2,0), P(1,0) } });
        Check(path.Count == 3 && path[2][0] == 2, "reversed corridor segments join in order");
        bool rejected = false;
        try { Geometry.Chain(new List<List<double[]>> { new List<double[]> { P(0,0), P(1,0) }, new List<double[]> { P(2,0), P(3,0) } }); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "disconnected corridor is rejected instead of bridged");
        var lShape = new List<double[]> { P(0,0), P(3,0), P(3,1), P(1,1), P(1,3), P(0,3) };
        Check(!Geometry.ContainsPath(lShape, new List<double[]> { P(0.5,2.5), P(2.5,0.5) }), "concave room rejects shortcut outside its boundary");
        Check(Geometry.ContainsPath(lShape, new List<double[]> { P(0.5,2.5), P(0.5,0.5), P(2.5,0.5) }), "corridor bend inside room is retained");
        var file = Path.Combine(Path.GetTempPath(), "group3-test-" + Guid.NewGuid() + ".json");
        try
        {
            var snapshot = new Snapshot { project = new ProjectData { model_name = "房間 <A> & 中文" } };
            snapshot.rooms.Add(new RoomData { element_id = "9007199254740993", boundary = ring });
            JsonFile.Write(file, snapshot);
            var bytes = File.ReadAllBytes(file);
            Check(!(bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191), "JSON has no UTF-8 BOM");
            var copy = JsonFile.Read<Snapshot>(file);
            Check(copy.rooms[0].element_id == "9007199254740993", "64-bit element ID remains a string");
            Check(copy.rooms[0].area_m2 == null && copy.rooms[0].is_habitable == null, "unknown fields survive as null");
            Check(copy.project.model_name == snapshot.project.model_name, "Chinese and special characters round-trip");
            JsonFile.Write(file, snapshot);
            Check(bytes.SequenceEqual(File.ReadAllBytes(file)), "same snapshot serializes deterministically and replaces atomically");
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
