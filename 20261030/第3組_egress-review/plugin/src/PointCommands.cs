using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Windows.Forms;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Form = System.Windows.Forms.Form;
using ComboBox = System.Windows.Forms.ComboBox;

namespace Group3.EgressExporter
{
    internal sealed class ElementKindFilter : ISelectionFilter
    {
        readonly bool stairs;
        internal ElementKindFilter(bool stairs) { this.stairs = stairs; }
        public bool AllowElement(Element e) => e.DesignOption == null && (stairs ? e is Stairs :
            e is FamilyInstance && e.Category?.Id.Value == (long)BuiltInCategory.OST_Doors);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
    internal static class PointInteraction
    {
        internal static void Scope(UIDocument uiDoc, out Level level, out Phase phase)
        {
            if (uiDoc == null || uiDoc.Document.IsFamilyDocument) throw new ArgumentException("請先開啟專案的樓層平面圖。");
            var view = uiDoc.ActiveView;
            level = view.GenLevel;
            phase = uiDoc.Document.GetElement(view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId) as Phase;
            if (!(view is ViewPlan) || view.ViewType != ViewType.FloorPlan || level == null || phase == null)
                throw new ArgumentException("請先切到要設定的樓板平面圖；定位會使用該視圖的樓層與階段。");
        }
        internal static Element Select(UIDocument uiDoc, bool stairs, bool useSelection = true)
        {
            var filter = new ElementKindFilter(stairs);
            var selected = uiDoc.Selection.GetElementIds().Select(uiDoc.Document.GetElement).Where(filter.AllowElement).ToList();
            if (useSelection && selected.Count == 1) return selected[0];
            var reference = uiDoc.Selection.PickObject(ObjectType.Element, filter, stairs ?
                "請選取整座樓梯（可按 Tab 切換選取；Esc 取消）" : "請選取要設定位置的門（Esc 取消）");
            return uiDoc.Document.GetElement(reference.ElementId);
        }
        internal static void CheckElement(Element element, Level level, Phase phase)
        {
            if (!RevitPositions.Exists(element, phase)) throw new ArgumentException("元件不在主模型或本視圖階段。");
            if (element is Stairs stair)
            {
                if (level.ProjectElevation < stair.BaseElevation - 0.01 || level.ProjectElevation > stair.TopElevation + 0.01)
                    throw new ArgumentException("樓梯沒有跨越本樓層，請切換正確樓層。");
            }
            else if (element is FamilyInstance door && door.LevelId != level.Id &&
                door.get_FromRoom(phase)?.LevelId != level.Id && door.get_ToRoom(phase)?.LevelId != level.Id)
                throw new ArgumentException("門不屬於本樓層，也沒有本樓層的相鄰房間。");
        }
        internal static XYZ Pick(UIDocument uiDoc, Level level, string prompt)
        {
            // A horizontal temporary work plane makes XY independent of any old, tilted work plane.
            // Roll back the entire group on success, cancellation and failure; never leave model changes.
            using (var group = new TransactionGroup(uiDoc.Document, "暫時定位工作平面"))
            {
                if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("無法開始定位工作平面。");
                try
                {
                    using (var transaction = new Transaction(uiDoc.Document, "暫時定位工作平面"))
                    {
                        transaction.Start();
                        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.ProjectElevation));
                        uiDoc.ActiveView.SketchPlane = SketchPlane.Create(uiDoc.Document, plane);
                        if (transaction.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("無法設定定位工作平面。");
                    }
                    var point = uiDoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints |
                        ObjectSnapTypes.Intersections | ObjectSnapTypes.Nearest, prompt + "（Esc 取消）");
                    return new XYZ(point.X, point.Y, level.ProjectElevation);
                }
                finally
                {
                    if (group.GetStatus() == TransactionStatus.Started && group.RollBack() != TransactionStatus.RolledBack)
                        throw new InvalidOperationException("定位工作平面未成功還原，未儲存定位設定。");
                }
            }
        }
        internal static PickedPointSetting Capture(Element element, XYZ point, string source) => new PickedPointSetting {
            point_m = RevitPositions.Metres(point), geometry_fingerprint = RevitPositions.Fingerprint(element), source = source };
        internal static string Coordinates(XYZ point)
        {
            var p = RevitPositions.Metres(point);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "X {0:F3} m，Y {1:F3} m，Z {2:F3} m", p[0], p[1], p[2]);
        }
        internal static bool Confirm(string title, string detail)
        {
            var dialog = new TaskDialog(title) { MainInstruction = "儲存這個定位設定？", MainContent = detail,
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel };
            return dialog.Show() == TaskDialogResult.Ok;
        }
    }
    [Transaction(TransactionMode.Manual)]
    public sealed class SetDoorPositionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = command.Application.ActiveUIDocument;
                PointInteraction.Scope(uiDoc, out var level, out var phase);
                var door = (FamilyInstance)PointInteraction.Select(uiDoc, false);
                PointInteraction.CheckElement(door, level, phase);
                var settings = LocalSettings.Load(uiDoc.Document, level, phase);
                XYZ candidate = null; string candidateSource = null;
                try { candidate = RevitPositions.DoorCandidate(door, level, out candidateSource); } catch (ArgumentException) { }
                if (candidate != null) candidate = new XYZ(candidate.X, candidate.Y, level.ProjectElevation);
                var dialog = new TaskDialog("設定門位置") { MainInstruction = level.Name + "／" + phase.Name + "：門 " + door.Id.Value,
                    MainContent = "請核對實際可通行門口的位置。包圍框中心只作候選，可能偏離門口。\n" +
                        (candidate == null ? "無可用候選位置。" : "候選：" + PointInteraction.Coordinates(candidate)),
                    CommonButtons = TaskDialogCommonButtons.Cancel, DefaultButton = TaskDialogResult.Cancel };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "在平面圖點選門口位置");
                if (candidate != null) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "已核對，採用候選位置");
                if (settings.doors.Any(d => d.unique_id == door.UniqueId))
                    dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "清除此門的本機定位設定");
                var choice = dialog.Show();
                if (choice != TaskDialogResult.CommandLink1 && choice != TaskDialogResult.CommandLink2 &&
                    choice != TaskDialogResult.CommandLink3) return Result.Cancelled;
                if (choice == TaskDialogResult.CommandLink3)
                {
                    settings.doors.RemoveAll(d => d.unique_id == door.UniqueId);
                    LocalSettings.Save(uiDoc.Document, level, phase, settings);
                    TaskDialog.Show("設定門位置", "已清除設定；下次匯出會重新自動擷取或列為待確認。");
                    return Result.Succeeded;
                }
                var point = choice == TaskDialogResult.CommandLink2 ? candidate :
                    PointInteraction.Pick(uiDoc, level, "請點選此門實際可通行門口的位置");
                var saved = PointInteraction.Capture(door, point, choice == TaskDialogResult.CommandLink2 ?
                    "user_confirmed:" + candidateSource : "user_picked_plan_point");
                RevitPositions.Validate(door, level, saved, false);
                if (!PointInteraction.Confirm("設定門位置", level.Name + "／" + phase.Name + "\n門 " + door.Id.Value +
                    "\n" + PointInteraction.Coordinates(point) + "\n此操作只確認位置，不會設定淨寬或房門關聯。")) return Result.Cancelled;
                PointRules.SetDoor(settings, new DoorSetting { unique_id = door.UniqueId, picked_point = saved });
                var path = LocalSettings.Save(uiDoc.Document, level, phase, settings);
                TaskDialog.Show("設定門位置", "已儲存。下次匯出相同樓層／階段時，設定欄留空即可自動套用。\n" + path);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("門定位未完成", ex.Message); return Result.Failed; }
        }
    }
    [Transaction(TransactionMode.Manual)]
    public sealed class SetStairEndpointCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData command, ref string message, ElementSet elements)
        {
            try
            {
                var uiDoc = command.Application.ActiveUIDocument;
                PointInteraction.Scope(uiDoc, out var level, out var phase);
                var stair = (Stairs)PointInteraction.Select(uiDoc, true);
                PointInteraction.CheckElement(stair, level, phase);
                var settings = LocalSettings.Load(uiDoc.Document, level, phase);
                string endpointType; bool? direct;
                using (var form = new StairPointForm(level.Name, phase.Name, stair.Id.Value.ToString()))
                {
                    if (form.ShowDialog(new WindowHandle(command.Application.MainWindowHandle)) != DialogResult.OK) return Result.Cancelled;
                    endpointType = form.EndpointType; direct = form.IsDirect;
                }
                FamilyInstance door = null;
                if (endpointType == "fire_door")
                {
                    door = (FamilyInstance)PointInteraction.Select(uiDoc, false, false);
                    PointInteraction.CheckElement(door, level, phase);
                }
                var point = PointInteraction.Pick(uiDoc, level, endpointType == "fire_door" ?
                    "請點選剛才指定防火門的通行位置" : "請點選本樓層要作為終點的第一階位置");
                var saved = PointInteraction.Capture(stair, point, "user_picked_plan_point");
                RevitPositions.Validate(stair, level, saved, true);
                if (door != null)
                {
                    var doorPoint = PointInteraction.Capture(door, point, "user_picked_plan_point");
                    RevitPositions.Validate(door, level, doorPoint, false);
                    saved.reference_geometry_fingerprint = doorPoint.geometry_fingerprint;
                }
                if (!PointInteraction.Confirm("設定樓梯終點", level.Name + "／" + phase.Name + "\n樓梯 " + stair.Id.Value + "\n" +
                    (endpointType == "fire_door" ? "防火門 " + door.Id.Value : "第一階") + "\n" + PointInteraction.Coordinates(point) +
                    "\n直通樓梯資格：" + (direct.HasValue ? (direct.Value ? "使用者確認是" : "使用者確認否") : "待確認") +
                    "\n終點類型與資格需依 C 組員的規則確認；外掛不自行判定。")) return Result.Cancelled;
                PointRules.SetStair(settings, new StairSetting { unique_id = stair.UniqueId, is_direct_stair = direct,
                    endpoint_type = endpointType, endpoint_unique_id = door?.UniqueId, picked_point = saved });
                var path = LocalSettings.Save(uiDoc.Document, level, phase, settings);
                TaskDialog.Show("設定樓梯終點", "已儲存。下次匯出相同樓層／階段時，設定欄留空即可自動套用。\n" + path);
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; TaskDialog.Show("樓梯終點未完成", ex.Message); return Result.Failed; }
        }
    }
    internal sealed class StairPointForm : Form
    {
        readonly ComboBox type = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly ComboBox direct = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        internal string EndpointType => type.SelectedIndex == 1 ? "first_step" : "fire_door";
        internal bool? IsDirect => direct.SelectedIndex == 0 ? (bool?)null : direct.SelectedIndex == 1;
        internal StairPointForm(string level, string phase, string id)
        {
            Text = "設定樓梯終點"; Width = 600; Height = 300; StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 6 };
            Controls.Add(layout);
            layout.Controls.Add(new Label { AutoSize = true, Text = level + "／" + phase + "：樓梯 " + id + "\n請依檢討規則選擇終點；不知道的資格保留待確認。" });
            type.Items.AddRange(new object[] { "請選擇終點類型", "第一階：在平面圖點選位置", "防火門：選門，再點選通行位置" }); type.SelectedIndex = 0;
            direct.Items.AddRange(new object[] { "直通樓梯資格：待確認", "已確認是直通樓梯", "已確認不是直通樓梯" }); direct.SelectedIndex = 0;
            layout.Controls.Add(type); layout.Controls.Add(direct);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var next = new Button { Text = "下一步：點選終點", AutoSize = true };
            next.Click += (s, e) => { if (type.SelectedIndex == 0) { MessageBox.Show(this, "請先選擇終點類型。"); return; } DialogResult = DialogResult.OK; };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(next); buttons.Controls.Add(cancel); layout.Controls.Add(buttons); CancelButton = cancel;
        }
    }
}
