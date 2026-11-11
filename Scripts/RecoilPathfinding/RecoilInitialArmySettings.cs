using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [Serializable]
        private sealed class InitialArmyBlock
        {
            public int X, Y;
            public int Type = -1;
            public int Count = 5;
        }

        [Serializable]
        private sealed class InitialArmyLayout
        {
            public bool Enabled;
            public List<InitialArmyBlock> Blocks = new();
        }

        private InitialArmyLayout _initialArmy;
        private bool _initialArmySettingsOpen, _initialArmyPickingType;
        private int _initialArmySelected = -1;
        private Vector2 _initialArmyGridScroll, _initialArmyPropertyScroll;
        private float _initialArmyZoom = 1f;
        private string _initialArmyCountInput = "5", _initialArmyStatus = "";
        // Font Awesome Free Solid, in Blue Units By Type order.
        private static readonly string[] InitialArmyIconNames =
        {
            "bullseye", "khanda", "shield-halved", "chess-knight",
            "bomb", "book-open", "paw", "shield"
        };
        private Texture2D[] _initialArmyIcons;

        private Texture2D InitialArmyIcon(int type)
        {
            if (type < 0 || type >= InitialArmyIconNames.Length) return null;
            if (_initialArmyIcons == null)
            {
                _initialArmyIcons = new Texture2D[InitialArmyIconNames.Length];
                for (var i = 0; i < _initialArmyIcons.Length; i++)
                    _initialArmyIcons[i] = Resources.Load<Texture2D>("InitialArmyIcons/" + InitialArmyIconNames[i]);
            }
            return _initialArmyIcons[type];
        }

        private GUIContent InitialArmyUnitContent(int type) =>
            new GUIContent("  " + CharacterTypeLabels[type], InitialArmyIcon(type));
        private string InitialArmyPath => Path.Combine(Application.streamingAssetsPath, "recoil_initial_army.csv");

        private static string WriteInitialArmyCsv(InitialArmyLayout layout)
        {
            var csv = new System.Text.StringBuilder();
            csv.AppendLine("# enabled=" + (layout.Enabled ? "1" : "0"));
            csv.AppendLine("Type,X,Y,Count");
            foreach (var block in layout.Blocks)
                if (block.Type >= 0)
                    csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3}", block.Type, Mathf.Abs(block.X), block.Y, block.Count));
            return csv.ToString();
        }

        private static InitialArmyLayout ReadInitialArmyCsv(string text)
        {
            var layout = new InitialArmyLayout { Enabled = true };
            var cells = new HashSet<Vector2Int>();
            var header = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0) continue;
                if (line.StartsWith("# enabled="))
                {
                    var value = line.Substring(10);
                    if (value != "0" && value != "1") throw new InvalidDataException("enabled phải là 0 hoặc 1.");
                    layout.Enabled = value == "1";
                    continue;
                }
                if (line.StartsWith("#")) continue;
                if (!header)
                {
                    if (line != "Type,X,Y,Count") throw new InvalidDataException("CSV cần cột Type,X,Y,Count.");
                    header = true;
                    continue;
                }
                var parts = line.Split(',');
                if (parts.Length != 4) throw new InvalidDataException("Dòng CSV cần đủ 4 cột: " + line);
                var numbers = new int[4];
                for (var i = 0; i < 4; i++)
                    if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out numbers[i]))
                        throw new InvalidDataException("Giá trị CSV không hợp lệ: " + line);
                var block = new InitialArmyBlock { Type = numbers[0], X = numbers[1], Y = numbers[2], Count = numbers[3] };
                if (block.Type < 0 || block.Type >= CharacterTypeCount || block.X < 0 || block.X > 6 ||
                    block.Y < 0 || block.Y > 12 || block.Count < 1 || block.Count > 5000 ||
                    !cells.Add(new Vector2Int(block.X, block.Y)))
                    throw new InvalidDataException("Loại quân, điểm lưới hoặc số lượng không hợp lệ/trùng: " + line);
                layout.Blocks.Add(block);
            }
            if (!header) throw new InvalidDataException("Thiếu tiêu đề CSV đội hình.");
            return layout;
        }
        private static InitialArmyLayout CreateDefaultInitialArmyLayout()
        {
            var layout = new InitialArmyLayout { Enabled = true };
            // Store one half; noncentral blocks are reflected across the longitudinal axis.
            void Add(int type, int x, int y) => layout.Blocks.Add(
                new InitialArmyBlock { Type = type, X = x, Y = y, Count = 5 });
            Add(6, 0, 0); // Elephants at the center of the front line.
            Add(2, 2, 0); // Spearmen protect the center.
            Add(3, 4, 0); // Cavalry on the outer front flanks.
            Add(1, 1, 1); // Militia and infantry behind the front.
            Add(7, 3, 1);
            foreach (var x in new[] { 0, 2, 4 }) Add(0, x, 2); // Archer line.
            foreach (var x in new[] { 0, 2, 4 }) Add(5, x, 3); // Mage line.
            foreach (var x in new[] { 0, 2 }) Add(4, x, 4); // Siege at the rear.
            return layout;
        }
        private void EnsureInitialArmyLayout()
        {
            if (_initialArmy != null) return;
            _initialArmy = CreateDefaultInitialArmyLayout();
            try
            {
                if (File.Exists(InitialArmyPath))
                    _initialArmy = ReadInitialArmyCsv(File.ReadAllText(InitialArmyPath));
                _initialArmy.Blocks ??= new List<InitialArmyBlock>();
                _initialArmy.Blocks.RemoveAll(b => b == null || b.X < -6 || b.X > 6 || b.Y < 0 || b.Y > 12 ||
                    b.Type < 0 || b.Type >= CharacterTypeCount || b.Count < 1 || b.Count > 5000);
                if (_initialArmy.Blocks.Count == 0)
                {
                    var enabled = _initialArmy.Enabled;
                    _initialArmy = CreateDefaultInitialArmyLayout();
                    _initialArmy.Enabled = enabled;
                }
                var cells = new HashSet<Vector2Int>();
                var duplicates = _initialArmy.Blocks.RemoveAll(b =>
                {
                    b.X = Mathf.Abs(b.X);
                    return !cells.Add(new Vector2Int(b.X, b.Y));
                });
                if (duplicates > 0)
                    _initialArmyStatus = "Đã gộp điểm trái/phải từ cấu hình cũ; giữ cụm đầu tiên ở mỗi cặp.";
            }
            catch (Exception e) { _initialArmyStatus = "Không đọc được đội hình: " + e.Message; }
        }

        private int CustomInitialTypeCount(int type)
        {
            EnsureInitialArmyLayout();
            if (!_initialArmy.Enabled) return -1;
            var count = 0;
            foreach (var block in _initialArmy.Blocks)
                if (block.Type == type) count += block.Count * (block.X == 0 ? 1 : 2);
            return count;
        }

        private void CancelUnassignedInitialArmyBlocks()
        {
            if (_initialArmy == null) return;
            var selected = _initialArmySelected >= 0 && _initialArmySelected < _initialArmy.Blocks.Count
                ? _initialArmy.Blocks[_initialArmySelected] : null;
            _initialArmy.Blocks.RemoveAll(b => b.Type < 0);
            _initialArmySelected = selected != null ? _initialArmy.Blocks.IndexOf(selected) : -1;
            if (_initialArmySelected < 0) _initialArmyPickingType = false;
        }
        private void SelectInitialArmyBlock(int index)
        {
            var selected = _initialArmy.Blocks[index];
            _initialArmy.Blocks.RemoveAll(b => b.Type < 0 && b != selected);
            _initialArmySelected = _initialArmy.Blocks.IndexOf(selected);
            index = _initialArmySelected;
            _initialArmyCountInput = _initialArmy.Blocks[index].Count.ToString();
            _initialArmyPickingType = false;
            GUI.FocusControl(null);
        }

        private void DrawInitialArmySettings()
        {
            EnsureInitialArmyLayout();
            var left = SetupNavigationWidth + 28f;
            var area = new Rect(left, 60f, Screen.width - left - 20f, Screen.height - 80f);
            GUI.Box(area, GUIContent.none);
            DrawSettingsTitle(new Rect(area.x + 14, area.y + 6, area.width - 28, 30), "QUÂN ĐẦU GAME");
            _initialArmy.Enabled = GUI.Toggle(new Rect(area.x + 12, area.y + 42, 250, 28),
                _initialArmy.Enabled, "Dùng đội hình tùy chỉnh");
            GUI.Label(new Rect(area.x + 12, area.y + 74, area.width - 24, 26),
                "Vị trí tương đối • Tự cân đối theo số quân • Đối xứng trái/phải");
            var propertyWidth = Mathf.Min(270f, area.width * .37f);
            var gridView = new Rect(area.x + 12, area.y + 136, area.width - propertyWidth - 36,
                Mathf.Max(80, area.height - 204));
            GUI.Label(new Rect(gridView.x, area.y + 105, 70, 24), "Thu phóng");
            _initialArmyZoom = GUI.HorizontalSlider(new Rect(gridView.x + 82, area.y + 114,
                Mathf.Max(40, gridView.width - 90), 18), _initialArmyZoom, .65f, 1.8f);
            var cell = Mathf.Min(44f, (gridView.width - 18f) / 14f, (gridView.height - 18f) / 14f) * _initialArmyZoom;
            var content = new Rect(0, 0, 14 * cell, 14 * cell);
            _initialArmyGridScroll = GUI.BeginScrollView(gridView, _initialArmyGridScroll, content);
            var mouse = Event.current.mousePosition;
            EnsureHealthFillRoundTexture();
            var centerColor = GUI.color;
            GUI.color = new Color(1f, .8f, .3f, .7f);
            GUI.DrawTexture(new Rect(6.5f * cell, .5f * cell, 1f, 12f * cell), Texture2D.whiteTexture);
            GUI.color = centerColor;
            for (var row = 0; row <= 12; row++)
            for (var col = 0; col <= 12; col++)
            {
                var node = new Vector2((col + .5f) * cell, (row + .5f) * cell);
                var old = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, .48f);
                if (col < 12) GUI.DrawTexture(new Rect(node.x, node.y, cell, 1), Texture2D.whiteTexture);
                if (row < 12) GUI.DrawTexture(new Rect(node.x, node.y, 1, cell), Texture2D.whiteTexture);
                var index = _initialArmy.Blocks.FindIndex(b => Mathf.Abs(b.X) == Mathf.Abs(col - 6) && b.Y == row);
                var hit = new Rect(node.x - cell * .45f, node.y - cell * .45f, cell * .9f, cell * .9f);
                var radius = Mathf.Min(index >= 0 ? 7f : 3f, cell * .2f);
                var teamColor = new Color(1f, .65f, .08f);
                GUI.color = index >= 0
                    ? (_initialArmy.Blocks[index].Type < 0 ? Color.yellow : teamColor) : new Color(1f, 1f, 1f, .85f);
                var icon = index >= 0 ? InitialArmyIcon(_initialArmy.Blocks[index].Type) : null;
                if (icon != null)
                {
                    var size = Mathf.Min(28f * _initialArmyZoom, cell * .7f);
                    GUI.DrawTexture(new Rect(node.x - size * .5f, node.y - size * .5f, size, size),
                        icon, ScaleMode.ScaleToFit, true);
                }
                else
                    GUI.DrawTexture(new Rect(node.x - radius, node.y - radius, radius * 2, radius * 2), _healthFillRoundTexture);
                GUI.color = old;
                if (index >= 0 && _initialArmySelected == index)
                    GUI.Box(new Rect(node.x - cell * .42f, node.y - cell * .42f, cell * .84f, cell * .84f), GUIContent.none);
                if (GUI.Button(hit, GUIContent.none, GUIStyle.none))
                {
                    if (index < 0)
                    {
                        _initialArmy.Blocks.Add(new InitialArmyBlock { X = Mathf.Abs(col - 6), Y = row });

                        index = _initialArmy.Blocks.Count - 1;
                    }
                    SelectInitialArmyBlock(index);
                }
            }
            GUI.EndScrollView();


            var property = new Rect(gridView.xMax + 12, gridView.y, propertyWidth, gridView.height);
            GUI.Box(property, GUIContent.none);
            GUILayout.BeginArea(new Rect(property.x + 10, property.y + 8, property.width - 20, property.height - 16));
            _initialArmyPropertyScroll = GUILayout.BeginScrollView(_initialArmyPropertyScroll);
            GUILayout.Label("Thuộc tính", SettingsAttributeLabelStyle);
            if (_initialArmySelected < 0 || _initialArmySelected >= _initialArmy.Blocks.Count)
                GUILayout.Label("Click một điểm trên lưới để thêm quân.");
            else
            {
                var block = _initialArmy.Blocks[_initialArmySelected];
                if (SettingsGUILayoutButton(block.Type < 0 ? new GUIContent("+ Thêm loại quân") : InitialArmyUnitContent(block.Type),
                    GUILayout.Height(36)))
                    _initialArmyPickingType = !_initialArmyPickingType;
                if (_initialArmyPickingType)
                    for (var type = 0; type < CharacterTypeCount; type++)
                        if (SettingsGUILayoutButton(InitialArmyUnitContent(type), GUILayout.Height(30)))
                        { block.Type = type; _initialArmy.Enabled = true; _initialArmyPickingType = false; }
                if (block.Type >= 0)
                {
                    var unitIcon = InitialArmyIcon(block.Type);
                    if (unitIcon != null)
                        GUILayout.Label(unitIcon, GUILayout.Height(64), GUILayout.Width(64));
                    GUILayout.Space(8);
                    GUILayout.Label(block.X == 0 ? "Số quân đầu game" : "Số quân mỗi cụm (2 cụm đối xứng)");
                    _initialArmyCountInput = GUILayout.TextField(_initialArmyCountInput, 5);
                    if (int.TryParse(_initialArmyCountInput, out var count) && count >= 1 && count <= 5000)
                        block.Count = count;
                    else GUILayout.Label("Nhập số từ 1 đến 5000.");
                }
                GUILayout.Space(12);
                if (SettingsGUILayoutButton(new GUIContent("Xóa điểm này"), GUILayout.Height(30)))
                { _initialArmy.Blocks.RemoveAt(_initialArmySelected); _initialArmySelected = -1; }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            if (SettingsButton(new Rect(area.x + 12, area.yMax - 57, 140, 34),
                new GUIContent("Lưu đội hình"), GUI.skin.button))
            {
                try
                {
                    if (_initialArmySelected >= 0 && _initialArmySelected < _initialArmy.Blocks.Count &&
                        _initialArmy.Blocks[_initialArmySelected].Type >= 0 &&
                        (!int.TryParse(_initialArmyCountInput, out var value) || value < 1 || value > 5000))
                        throw new InvalidDataException("Số quân phải từ 1 đến 5000.");
                    if (_initialArmy.Enabled && !_initialArmy.Blocks.Exists(b => b.Type >= 0))
                        throw new InvalidDataException("Hãy thêm ít nhất một loại quân.");
                    var total = 0;
                    foreach (var block in _initialArmy.Blocks)
                        if (block.Type >= 0) total += block.Count * (block.X == 0 ? 1 : 2);
                    if (_initialArmy.Enabled && total > 10000)
                        throw new InvalidDataException("Tối đa 10000 quân đầu game mỗi bên.");
                    Directory.CreateDirectory(Application.streamingAssetsPath);
                    CancelUnassignedInitialArmyBlocks();
                    File.WriteAllText(InitialArmyPath, WriteInitialArmyCsv(_initialArmy));
                    _initialArmyStatus = "Đã lưu. Áp dụng cho trận tiếp theo.";
                }
                catch (Exception e) { _initialArmyStatus = e.Message; }
            }
            if (SettingsButton(new Rect(area.x + 164, area.yMax - 57, 150, 34),
                new GUIContent("Đội hình mặc định"), GUI.skin.button))
            {
                _initialArmy = CreateDefaultInitialArmyLayout();
                _initialArmySelected = -1;
                _initialArmyPickingType = false;
                _initialArmyCountInput = "5";
                _initialArmyStatus = "Đã đặt mặc định. Nhấn Lưu đội hình để giữ lại.";
                GUI.FocusControl(null);
            }
            GUI.Label(new Rect(area.x + 326, area.yMax - 57, Mathf.Max(40, area.width - 338), 50), _initialArmyStatus);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                !property.Contains(Event.current.mousePosition))
                CancelUnassignedInitialArmyBlocks();
        }


        private bool TryBuildCustomInitialArmy(out string error, int[] runtimeCounts = null)
        {
            EnsureInitialArmyLayout();
            error = null;
            if (!_initialArmy.Enabled && runtimeCounts == null) return true;
            if (runtimeCounts == null)
            {
                runtimeCounts = new int[CharacterTypeCount];
                for (var type = 0; type < CharacterTypeCount; type++) runtimeCounts[type] = PreparedTypeCount(true, type);
            }
            for (var type = 0; type < runtimeCounts.Length; type++)
                runtimeCounts[type] = Mathf.Min(runtimeCounts[type], MaximumStagedPerTypePerTeam);
            _preparedBluePositions.Clear();
            _preparedRedPositions.Clear();
            var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            var axis = BattleAxis();
            var lateral = Vector3.Cross(Vector3.up, axis);
            var blocks = new List<RecoilInitialArmyPlanner.Block>();
            foreach (var block in _initialArmy.Blocks)
            {
                if (block.Type < 0) continue;
                var radius = .5f;
                for (var team = 0; team < 2; team++)
                {
                    var template = FindTemplateForType(team == 0, block.Type);
                    if (template != null && template.TryGetComponent<RecoilNavMeshPathAgent>(out var agent))
                        radius = Mathf.Max(radius, agent.MovementSettings.BodyRadius);
                }
                blocks.Add(new RecoilInitialArmyPlanner.Block
                {
                    Type = block.Type, X = Mathf.Abs(block.X), Y = block.Y,
                    Count = block.Count, Radius = radius
                });
            }
            if (runtimeCounts != null) blocks = BuildWaitingArmyBlocks(blocks, runtimeCounts);
            if (blocks.Count == 0) { error = "Hãy thêm loại quân vào đội hình."; return false; }
            var plans = new List<List<RecoilInitialArmyPlanner.Slot>>();
            foreach (var widthFactor in new[] { 1f, .75f, .5f, .25f, 1.25f, 1.5f })
            foreach (var aspect in new[] { 2f, 1f, 4f, .5f })
                if (RecoilInitialArmyPlanner.TryBuild(blocks,
                    Mathf.Max(8f, preparedFormationHalfWidth * widthFactor), aspect, out var plan))
                    plans.Add(plan);

            // Try narrower/deeper layouts and move the complete formation back together.
            // Individual units are never clamped, which would break symmetry or cause overlaps.
            for (var front = 8f; front <= 248f; front += 8f)
            foreach (var plan in plans)
            {
                _preparedBluePositions.Clear();
                _preparedRedPositions.Clear();
                _waitingPositionTypes.Clear();
                var fits = true;
                foreach (var slot in plan)
                {
                    var depth = front + slot.Depth;
                    var blue = middle + lateral * slot.Cross - axis * depth;
                    var red = middle + lateral * slot.Cross + axis * depth;
                    if (!TryProjectInitialArmyPoint(blue, true, slot.Radius, out var bp) ||
                        !TryProjectInitialArmyPoint(red, false, slot.Radius, out var rp))
                    { fits = false; break; }
                    _waitingPositionTypes.Add(slot.Type);
                    _preparedBluePositions.Add(bp);
                    _preparedRedPositions.Add(rp);
                }
                if (!fits) continue;
                _preparedPositionsBuilt = true;
                return true;
            }
            _preparedBluePositions.Clear();
            _preparedRedPositions.Clear();
            _preparedPositionsBuilt = false;
            error = "Không đủ vùng đi được cho số quân này sau khi tự cân đối đội hình. Hãy giảm tổng quân.";
            return false;
        }
        private bool TryProjectInitialArmyPoint(Vector3 point, bool blue, float radius, out Vector3 projected)
        {
            projected = default;
            if (point.x < SpawnMinX + radius || point.x > SpawnMaxX - radius ||
                point.z < SpawnMinZ + radius || point.z > SpawnMaxZ - radius ||
                !TryGetGroundHeight(point, out var height)) return false;
            point.y = height;
            if (!pathService.TrySamplePosition(point, 2f, GetSpawnAreaMask(blue), out projected)) return false;
            var horizontal = new Vector2(projected.x - point.x, projected.z - point.z);
            return horizontal.sqrMagnitude <= .0001f;
        }
    }
}
