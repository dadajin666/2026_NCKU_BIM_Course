using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Group3.EgressExporter
{
    public sealed class Extractor
    {
        readonly Document doc;
        readonly Level level;
        readonly Phase phase;
        readonly ExportSettings settings;
        readonly Snapshot result = new Snapshot();
        public Extractor(Document doc, Level level, Phase phase, ExportSettings settings)
        { this.doc = doc; this.level = level; this.phase = phase; this.settings = settings; }
        static string Id(Element e) => e.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static double M(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
        static double[] XY(XYZ p) => new[] { M(p.X), M(p.Y) };
        void Warn(Element e, string code, string message, string severity = "warning") =>
            result.warnings.Add(new WarningData { element_id = e == null ? null : Id(e), code = code, message = message, severity = severity });
        IEnumerable<T> All<T>() where T : Element => new FilteredElementCollector(doc)
            .OfClass(typeof(T)).WhereElementIsNotElementType().Cast<T>().OrderBy(e => e.Id.Value);
        bool MainModel(Element e) => e.DesignOption == null;
        bool Exists(Element e)
        {
            var status = e.GetPhaseStatus(phase.Id);
            return status == ElementOnPhaseStatus.Existing || status == ElementOnPhaseStatus.New
                || status == ElementOnPhaseStatus.None;
        }
        void Safe(Element e, Action action)
        {
            try { action(); }
            catch (Exception ex) { Warn(e, "ELEMENT_EXTRACTION_FAILED", e.Name + ": " + ex.Message, "error"); }
        }
        public Snapshot Run()
        {
            ValidateSettings();
            var version = Document.GetDocumentVersion(doc);
            result.project = new ProjectData {
                model_name = doc.Title, model_guid = doc.ProjectInformation.UniqueId,
                revit_version = doc.Application.VersionNumber, phase = phase.Name, phase_id = Id(phase),
                design_option = "主模型（不含設計選項）", level_id = Id(level),
                level = new LevelData { name = level.Name, elevation_m = M(level.ProjectElevation) },
                extracted_at = DateTimeOffset.Now.ToString("o"), has_unsaved_changes = doc.IsModified,
                model_version = version.VersionGUID + "/" + version.NumberOfSaves
            };
            foreach (var r in All<Room>().Where(r => r.LevelId == level.Id &&
                r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phase.Id && MainModel(r)))
                Safe(r, () => ReadRoom(r));
            foreach (var door in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().OrderBy(e => e.Id.Value)
                .Where(e => MainModel(e) && Exists(e))) Safe(door, () => ReadDoor(door));
            foreach (var stair in All<Stairs>().Where(e => MainModel(e) && Exists(e)))
                Safe(stair, () => ReadStair(stair));
            foreach (var stair in result.stairs.Where(s => s.entry_point == null).ToList()) {
                result.stairs.Remove(stair); result.unresolved_stairs.Add(stair);
            }
            foreach (var r in result.rooms.Where(r => r.door_ids.Count == 0))
                Warn(doc.GetElement(r.unique_id), "ROOM_NO_DOOR", "沒有找到與此房間關聯的門。", "error");
            foreach (var multi in All<MultistoryStairs>())
                Warn(multi, "MULTISTORY_STAIRS_REVIEW", "多層樓梯的重複實例尚未完整展開；請逐樓層核對樓梯數量與終點。", "error");
            foreach (var legacy in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType().Where(e => !(e is Stairs)))
                Warn(legacy, "STAIR_UNSUPPORTED_TYPE", "非元件式樓梯尚未支援；保留元件編號供人工修正。", "error");
            foreach (var corridor in settings.corridors) ReadCorridor(corridor);
            ReadColumns();
            foreach (var link in All<RevitLinkInstance>())
                Warn(link, "LINK_NOT_EXTRACTED", "本版只擷取主文件；連結模型未納入，請先確認房間、門、障礙物來源。", "error");
            if (All<DesignOption>().Any()) Warn(null, "DESIGN_OPTIONS_EXCLUDED", "只擷取主模型；設計選項中的元件未納入。", "error");
            if (!result.rooms.Any()) Warn(null, "NO_ROOMS", "所選樓層／階段找不到已放置房間。未放置房間沒有樓層，另列於清單。", "error");
            foreach (var r in All<Room>().Where(r => r.Location == null && MainModel(r) &&
                r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phase.Id))
                Warn(r, "ROOM_NOT_PLACED", "房間未放置，無法歸屬所選樓層。", "error");
            if (!result.corridors.Any()) Warn(null, "NO_CORRIDOR_CENTERLINE", "未設定走廊中心線；可在設定檔指定曲線，或先確認房間的 is_circulation。");
            Warn(null, "OBSTACLE_SCOPE", "柱子以保守包圍框擷取；設備、家具、矮牆等未完整納入。快照需覆核，不能直接作為法規核定結果。");
            result.warnings = result.warnings.OrderBy(w => w.element_id, StringComparer.Ordinal)
                .ThenBy(w => w.code, StringComparer.Ordinal).ThenBy(w => w.message, StringComparer.Ordinal).ToList();
            return result;
        }
        void ValidateSettings()
        {
            if (settings == null) throw new ArgumentException("設定檔不能為 null。");
            settings.rooms = settings.rooms ?? new List<RoomSetting>();
            settings.stairs = settings.stairs ?? new List<StairSetting>();
            settings.corridors = settings.corridors ?? new List<CorridorSetting>();
            bool hasMappings = settings.rooms.Any() || settings.stairs.Any() || settings.corridors.Any();
            if (hasMappings && (settings.model_guid != doc.ProjectInformation.UniqueId ||
                settings.level_id != Id(level) || settings.phase_id != Id(phase)))
                throw new ArgumentException("設定檔的模型、樓層或階段不符。請先匯出本樓層的設定範本。");
            var refs = settings.rooms.Select(x => x?.unique_id).Concat(settings.stairs.Select(x => x?.unique_id))
                .Concat(settings.corridors.Select(x => x?.room_unique_id)).ToList();
            if (refs.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("設定項目必須有 unique_id。");
            if (settings.rooms.GroupBy(x => x.unique_id).Any(x => x.Count() > 1) ||
                settings.stairs.GroupBy(x => x.unique_id).Any(x => x.Count() > 1) ||
                settings.corridors.GroupBy(x => x.room_unique_id).Any(x => x.Count() > 1))
                throw new ArgumentException("設定檔有重複的元件項目。");
            foreach (var uid in refs) if (doc.GetElement(uid) == null)
                throw new ArgumentException("設定元件不存在，請更新設定檔：" + uid);
        }
        List<double[]> Points(IEnumerable<Curve> curves, bool closed)
        {
            var parts = curves.Select(c => c.Tessellate().Select(XY).ToList()).ToList();
            var chain = Geometry.Chain(parts);
            if (closed && Geometry.Distance(chain[0], chain[chain.Count - 1]) > Geometry.Tolerance)
                throw new ArgumentException("輪廓未閉合，不能自動補線。");
            return closed ? Geometry.Ring(chain) : chain;
        }
        void ReadRoom(Room r)
        {
            var mapping = settings.rooms.SingleOrDefault(x => x.unique_id == r.UniqueId);
            var data = new RoomData { element_id = Id(r), unique_id = r.UniqueId, number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name, level = level.Name,
                area_m2 = r.Area > 0 ? (double?)UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters) : null,
                is_habitable = mapping?.is_habitable, is_circulation = mapping?.is_circulation };
            result.rooms.Add(data); // Retain incomplete rooms in the repair list.
            using (var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish })
            {
                var loops = r.GetBoundarySegments(options);
                if (loops != null)
                    foreach (var loop in loops) {
                        var curves = loop.Select(s => s.GetCurve()).ToList();
                        if (curves.Any(c => !(c is Line))) Warn(r, "CURVE_TESSELLATED", "曲線邊界依 Revit Tessellate 轉成折線，需確認近似誤差。");
                        data.boundary_loops.Add(Points(curves, true));
                    }
            }
            // Existing B engine cannot represent multiple boundary rings safely. Preserve originals, block routing.
            if (data.boundary_loops.Count == 1 && data.boundary_loops[0].Count >= 3 && data.area_m2.HasValue)
            { data.boundary = data.boundary_loops[0]; data.boundary_closed = true; }
            else if (data.boundary_loops.Count > 1)
                Warn(r, "ROOM_MULTIPLE_LOOPS", "包含孔洞或多個區域。原始輪廓保存在 boundary_loops；目前引擎不支援，boundary 留空以阻止錯誤路徑。", "error");
            else Warn(r, "ROOM_NOT_ENCLOSED", "房間未封閉、面積為零或沒有有效邊界。", "error");
            if (!data.is_habitable.HasValue) Warn(r, "ROOM_USE_UNCONFIRMED", "是否居室尚未確認。");
            if (!data.is_circulation.HasValue) Warn(r, "ROOM_CIRCULATION_UNCONFIRMED", "是否通行空間尚未確認；不會以房間名稱推測。");
        }
        double? ClearWidth(Element e, out string source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(settings.clear_width_parameter)) return null;
            foreach (var target in new[] { e, doc.GetElement(e.GetTypeId()) }.Where(x => x != null))
            {
                var ps = target.GetParameters(settings.clear_width_parameter);
                if (ps.Count > 1) { Warn(e, "WIDTH_PARAMETER_AMBIGUOUS", "同名淨寬參數不唯一，未採用。"); return null; }
                if (ps.Count == 0) continue;
                var p = ps[0];
                if (!p.HasValue || p.StorageType != StorageType.Double || p.Definition.GetDataType() != SpecTypeId.Length || p.AsDouble() <= 0)
                { Warn(e, "WIDTH_PARAMETER_INVALID", "淨寬參數必須是有值且大於零的長度參數。"); return null; }
                source = (target.Id == e.Id ? "instance:" : "type:") + settings.clear_width_parameter;
                return M(p.AsDouble());
            }
            return null;
        }
        void ReadDoor(FamilyInstance door)
        {
            var adjacent = new[] { door.get_FromRoom(phase), door.get_ToRoom(phase) }.Where(r => r != null)
                .Select(Id).Distinct().ToList();
            var rooms = result.rooms.Where(r => adjacent.Contains(r.element_id)).ToList();
            if (door.LevelId != level.Id && !rooms.Any()) return;
            if (!(door.Location is LocationPoint point)) { Warn(door, "DOOR_NO_POSITION", "門沒有可用定位點，未加入 doors。", "error"); return; }
            string source;
            var data = new DoorData { element_id = Id(door), unique_id = door.UniqueId, position = XY(point.Point),
                room_ids = rooms.Select(r => r.element_id).ToList(), clear_width_m = ClearWidth(door, out source), clear_width_source = source };
            result.doors.Add(data);
            foreach (var r in rooms) r.door_ids.Add(data.element_id);
            if (!data.clear_width_m.HasValue) Warn(door, "DOOR_NO_CLEAR_WIDTH", "缺少有效淨寬；不採用一般門寬或族群名稱代替。");
            if (rooms.Count < 2) Warn(door, "DOOR_ROOM_ASSOCIATION_INCOMPLETE", "只找到 " + rooms.Count + " 個本樓層相鄰房間；可能為室外門、未建模通道或房間計算點問題。");
            Warn(door, "DOOR_INSERTION_POINT", "position 使用族群插入點；需確認它位在實際可通行門口。");
        }
        void ReadStair(Stairs stair)
        {
            var box = stair.get_BoundingBox(null);
            if (box == null || level.ProjectElevation < stair.BaseElevation - 0.01 || level.ProjectElevation > stair.TopElevation + 0.01) return;
            var data = new StairData { element_id = Id(stair), unique_id = stair.UniqueId, name = stair.Name };
            result.stairs.Add(data);
            foreach (var id in stair.GetStairsRuns())
            {
                var run = (StairsRun)doc.GetElement(id);
                Safe(run, () => {
                    data.footprint_loops.Add(Points(run.GetFootprintBoundary(), true));
                    data.run_paths.Add(Points(run.GetStairsPath(), false));
                });
            }
            foreach (var id in stair.GetStairsLandings())
            {
                var landing = (StairsLanding)doc.GetElement(id);
                Safe(landing, () => data.footprint_loops.Add(Points(landing.GetFootprintBoundary(), true)));
            }
            if (data.footprint_loops.Count == 1) data.footprint = data.footprint_loops[0];
            else Warn(stair, "STAIR_COMPOSITE_FOOTPRINT", "實際梯段／平台分別保存在 footprint_loops；前端需逐圈繪製。未用包圍矩形或凸包替代樓梯。");
            var mapping = settings.stairs.SingleOrDefault(x => x.unique_id == stair.UniqueId);
            data.is_direct_stair = mapping?.is_direct_stair;
            if (mapping != null && !string.IsNullOrWhiteSpace(mapping.endpoint_unique_id))
            {
                var reference = doc.GetElement(mapping.endpoint_unique_id);
                if (reference == null || !MainModel(reference) || !Exists(reference))
                    throw new ArgumentException("樓梯終點參考不存在或不在主模型／所選階段。");
                XYZ point;
                if (mapping.endpoint_type == "fire_door" && reference is FamilyInstance door &&
                    door.Category?.Id.Value == (long)BuiltInCategory.OST_Doors && door.Location is LocationPoint lp)
                    point = lp.Point;
                else if (mapping.endpoint_type == "first_step" && reference is CurveElement curve &&
                    (mapping.curve_end == 0 || mapping.curve_end == 1))
                    point = curve.GeometryCurve.GetEndPoint(mapping.curve_end);
                else throw new ArgumentException("樓梯終點須為 fire_door 門或 first_step 曲線端點（curve_end 0/1）。");
                if (Math.Abs(M(point.Z - level.ProjectElevation)) > 0.5)
                    throw new ArgumentException("樓梯終點不在所選樓層高度附近（0.5 m 檢查容差）。");
                data.entry_point = XY(point); data.endpoint_type = mapping.endpoint_type;
                data.endpoint_source = "user_confirmed:" + reference.UniqueId;
            }
            if (data.entry_point == null) {
                data.is_direct_stair = data.is_direct_stair == false ? (bool?)false : null;
                Warn(stair, "STAIR_ENDPOINT_UNCONFIRMED", "尚未指定並確認本樓層第一階／防火門終點。保留 null，不會拿樓梯中心點代替。", "error");
            }
            if (!data.is_direct_stair.HasValue) Warn(stair, "STAIR_DIRECT_UNCONFIRMED", "直通樓梯資格尚未確認，不列入計算候選。");
        }
        void ReadCorridor(CorridorSetting setting)
        {
            var room = doc.GetElement(setting.room_unique_id) as Room;
            if (room == null) throw new ArgumentException("走廊 room_unique_id 必須指向房間。");
            Safe(room, () => {
                if (!result.rooms.Any(r => r.unique_id == room.UniqueId)) throw new ArgumentException("走廊房間不在所選樓層／階段。");
                var parts = new List<List<double[]>>();
                foreach (var uid in setting.curve_unique_ids ?? new List<string>())
                {
                    var curve = doc.GetElement(uid) as CurveElement;
                    if (curve == null || !MainModel(curve)) throw new ArgumentException("走廊中心線須指向主模型的 ModelCurve／DetailCurve。");
                    var points = curve.GeometryCurve.Tessellate();
                    if (points.Any(p => Math.Abs(M(p.Z - level.ProjectElevation)) > 0.5))
                        throw new ArgumentException("走廊中心線不在所選樓層高度附近。");
                    parts.Add(points.Select(XY).ToList());
                }
                string source;
                var data = new CorridorData { element_id = Id(room), unique_id = room.UniqueId, name = room.Name,
                    centerline = Geometry.Chain(parts), clear_width_m = ClearWidth(room, out source),
                    clear_width_source = source, rooms_both_sides = setting.rooms_both_sides };
                var roomData = result.rooms.Single(r => r.unique_id == room.UniqueId);
                if (!roomData.boundary_closed || !Geometry.ContainsPath(roomData.boundary, data.centerline))
                    throw new ArgumentException("走廊中心線離開房間輪廓，或房間邊界未能完整表示；未加入路網。");
                result.corridors.Add(data);
                if (!data.clear_width_m.HasValue) Warn(room, "CORRIDOR_NO_CLEAR_WIDTH", "走廊尚未設定有效淨寬。");
                if (!data.rooms_both_sides.HasValue) Warn(room, "CORRIDOR_SIDES_UNCONFIRMED", "走廊兩側是否均有居室尚未確認。");
                Warn(room, "CORRIDOR_PATH_REVIEW", "中心線來自使用者指定曲線，需覆核是否位於可行走區域且未穿牆。");
            });
        }
        void ReadColumns()
        {
            var filter = new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns });
            foreach (var column in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType()
                .OrderBy(e => e.Id.Value).Where(e => MainModel(e) && Exists(e)))
                Safe(column, () => {
                    var b = column.get_BoundingBox(null);
                    if (b == null) { Warn(column, "OBSTACLE_NO_GEOMETRY", "柱子無可用包圍框。", "error"); return; }
                    if (b.Max.Z <= level.ProjectElevation || b.Min.Z > level.ProjectElevation + 0.01) return;
                    var corners = new List<XYZ>();
                    foreach (var x in new[] { b.Min.X, b.Max.X }) foreach (var y in new[] { b.Min.Y, b.Max.Y })
                        foreach (var z in new[] { b.Min.Z, b.Max.Z }) corners.Add(b.Transform.OfPoint(new XYZ(x, y, z)));
                    double x0 = M(corners.Min(p => p.X)), x1 = M(corners.Max(p => p.X)), y0 = M(corners.Min(p => p.Y)), y1 = M(corners.Max(p => p.Y));
                    result.obstacles.Add(new ObstacleData { element_id = Id(column), unique_id = column.UniqueId,
                        category = column.Category.Name, geometry_source = "conservative_axis_aligned_bounding_box",
                        footprint = new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } } });
                    Warn(column, "OBSTACLE_CONSERVATIVE_BOX", "以包圍框保守近似柱子，可能封閉原本可行的通道，需覆核。");
                });
        }
        public ExportSettings Template() => new ExportSettings {
            model_guid = doc.ProjectInformation.UniqueId, level_id = Id(level), phase_id = Id(phase),
            clear_width_parameter = settings.clear_width_parameter,
            rooms = result.rooms.Select(r => new RoomSetting { unique_id = r.unique_id, is_habitable = r.is_habitable, is_circulation = r.is_circulation }).ToList(),
            stairs = result.stairs.Concat(result.unresolved_stairs).Select(s => settings.stairs.SingleOrDefault(x => x.unique_id == s.unique_id) ??
                new StairSetting { unique_id = s.unique_id }).ToList(), corridors = settings.corridors
        };
    }
}
