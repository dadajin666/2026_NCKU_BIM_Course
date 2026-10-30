using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;

namespace Group3.EgressExporter
{
    // Lowercase field names deliberately match the shared v0.3 contract.
    [DataContract] public class Snapshot
    {
        [DataMember] public string schema_version = "0.3";
        [DataMember] public ProjectData project;
        [DataMember] public List<RoomData> rooms = new List<RoomData>();
        [DataMember] public List<DoorData> doors = new List<DoorData>();
        [DataMember] public List<DoorData> unresolved_doors = new List<DoorData>();
        [DataMember] public List<StairData> stairs = new List<StairData>();
        [DataMember] public List<StairData> unresolved_stairs = new List<StairData>();
        [DataMember] public List<CorridorData> corridors = new List<CorridorData>();
        [DataMember] public List<ObstacleData> obstacles = new List<ObstacleData>();
        [DataMember] public List<WarningData> warnings = new List<WarningData>();
    }
    [DataContract] public class ProjectData
    {
        [DataMember] public string model_name, model_guid, revit_version, phase, design_option;
        [DataMember] public string unit = "m", coordinate = "Revit 專案座標（Internal Origin）";
        [DataMember] public string extracted_at, model_version, phase_id, level_id;
        [DataMember] public string settings_file;
        [DataMember] public bool has_unsaved_changes;
        [DataMember] public LevelData level;
        [DataMember] public string model_guid_source = "ProjectInformation.UniqueId (另存副本可能相同)";
        [DataMember] public string export_scope = "host_document_single_level_main_model";
    }
    [DataContract] public class LevelData
    {
        [DataMember] public string name;
        [DataMember] public double elevation_m;
    }
    [DataContract] public class RoomData
    {
        [DataMember] public string element_id, unique_id, number, name, level;
        [DataMember] public string category = "Rooms";
        [DataMember] public double? area_m2;
        [DataMember] public List<double[]> boundary = new List<double[]>();
        [DataMember] public List<List<double[]>> boundary_loops = new List<List<double[]>>();
        [DataMember] public bool boundary_closed;
        [DataMember] public List<string> door_ids = new List<string>();
        [DataMember] public bool? is_habitable, is_circulation;
    }
    [DataContract] public class DoorData
    {
        [DataMember] public string element_id, unique_id;
        [DataMember] public List<string> room_ids = new List<string>();
        [DataMember] public double[] position;
        [DataMember] public double[] position_candidate;
        [DataMember] public string position_source;
        [DataMember] public bool? position_confirmed;
        [DataMember] public double? clear_width_m;
        [DataMember] public string clear_width_source;
    }
    [DataContract] public class StairData
    {
        [DataMember] public string element_id, unique_id, name;
        [DataMember] public List<double[]> footprint = new List<double[]>();
        [DataMember] public List<List<double[]>> footprint_loops = new List<List<double[]>>();
        [DataMember] public List<List<double[]>> run_paths = new List<List<double[]>>();
        [DataMember] public double[] entry_point;
        [DataMember] public string endpoint_type, endpoint_source;
        [DataMember] public bool? is_direct_stair;
    }
    [DataContract] public class CorridorData
    {
        [DataMember] public string element_id, unique_id, name;
        [DataMember] public List<double[]> centerline = new List<double[]>();
        [DataMember] public double? clear_width_m;
        [DataMember] public string clear_width_source;
        [DataMember] public bool? rooms_both_sides;
    }
    [DataContract] public class ObstacleData
    {
        [DataMember] public string element_id, unique_id, category, geometry_source;
        [DataMember] public List<double[]> footprint = new List<double[]>();
    }
    [DataContract] public class WarningData
    {
        [DataMember] public string element_id, code, message;
        [DataMember] public string severity = "warning";
    }
    // Settings use UniqueId, not transient display names. Null means unconfirmed.
    [DataContract] public class ExportSettings
    {
        [DataMember] public string model_guid, level_id, phase_id;
        [DataMember] public string clear_width_parameter = "Egress_ClearWidth";
        [DataMember] public List<RoomSetting> rooms = new List<RoomSetting>();
        [DataMember] public List<DoorSetting> doors = new List<DoorSetting>();
        [DataMember] public List<StairSetting> stairs = new List<StairSetting>();
        [DataMember] public List<CorridorSetting> corridors = new List<CorridorSetting>();
    }
    [DataContract] public class RoomSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public bool? is_habitable, is_circulation;
    }
    [DataContract] public class StairSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public bool? is_direct_stair;
        // Explicit endpoint reference: a door insertion point or one end of a chosen curve.
        [DataMember] public string endpoint_unique_id, endpoint_type;
        [DataMember] public int curve_end;
        [DataMember] public PickedPointSetting picked_point;
    }
    [DataContract] public class PickedPointSetting
    {
        // Internal-Origin XYZ in metres. Fingerprint invalidates a point after geometry changes.
        [DataMember] public double[] point_m;
        [DataMember] public string geometry_fingerprint, reference_geometry_fingerprint, source;
    }
    [DataContract] public class DoorSetting
    {
        [DataMember] public string unique_id;
        [DataMember] public PickedPointSetting picked_point;
    }
    [DataContract] public class CorridorSetting
    {
        [DataMember] public string room_unique_id;
        [DataMember] public List<string> curve_unique_ids = new List<string>();
        [DataMember] public bool? rooms_both_sides;
    }
    public static class JsonFile
    {
        public static T Read<T>(string path)
        {
            using (var stream = File.OpenRead(path))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
        public static void Write<T>(string path, T value)
        {
            // Write and close completely before replacing a previous snapshot.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp))
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream,
                    new System.Text.UTF8Encoding(false), false, true, "  "))
                    new DataContractJsonSerializer(typeof(T)).WriteObject(writer, value);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
