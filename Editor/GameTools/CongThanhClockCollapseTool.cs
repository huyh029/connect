using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    /// <summary>
    /// Edits the clock's closed state: the TIME button (where it sits, its size) and the small clock face that sits on
    /// the button's round icon until it is clicked. Edits only live in this window until "Lưu", which writes them to
    /// the clock prefab the lobby uses and to the test-scene clock that prefab is rebuilt from.
    /// </summary>
    public sealed class CongThanhClockCollapseTool : EditorWindow
    {
        private const string DialButtonLabelField = "collapseButtonLabel";
        private const string DialIconCenterField = "collapseIconCenter";
        private const string DialIconRadiusField = "collapseIconRadius";
        private const string DialGrowDurationField = "growDuration";
        private const float DefaultIconRadius = .165f;
        private const float DefaultGrowDuration = .45f;
        private static readonly string[] DefaultRevealLabels = { "Vòng giờ", "Vòng phút", "Bánh răng ngoài", "Chỉ báo" };
        private static readonly string[] ViewNames = { "Khung: nút TIME", "Khung: toàn bộ" };

        private enum View { Button, Whole }
        private enum DragMode { None, Icon, Radius, Button }

        // Working copy: nothing in a scene or prefab changes until "Lưu".
        private RecoilClockImageLayer[] _layers;
        private int _buttonIndex = -1;
        private int _faceIndex = -1;
        private string _buttonLabel = CongThanhClockImageTool.TimeButtonLabel;
        private Vector2 _iconCenter = CongThanhClockImageTool.TimeButtonIcon;
        private float _iconRadius = DefaultIconRadius;
        private float _growDuration = DefaultGrowDuration;
        private readonly HashSet<string> _revealLabels = new();
        private string _sourceName;
        private bool _buttonWasMissing;
        private bool _dirty;
        private string _status;

        private View _view;
        private float _open; // 0 = closed onto the button, 1 = fully open
        private bool _playing;
        private float _playFrom, _playTo, _playDuration;
        private double _playStart;
        private DragMode _drag;
        private Vector2 _dragOffset;
        private Rect _frozenBounds;
        private Vector2 _scroll;

        /// <summary>A layer as drawn with the clock open: center, aspect-fitted size and rotation in clock space.</summary>
        private readonly struct Placed
        {
            public readonly Vector2 Center;
            public readonly Vector2 Size;
            public readonly float Rotation;

            public Placed(Vector2 center, Vector2 size, float rotation)
            {
                Center = center;
                Size = size;
                Rotation = rotation;
            }

            public bool Contains(Vector2 point)
            {
                var local = RecoilClockImagePreview.Rotate(point - Center, -Rotation);
                return Mathf.Abs(local.x) <= Size.x * .5f && Mathf.Abs(local.y) <= Size.y * .5f;
            }

            public IEnumerable<Vector2> Corners()
            {
                var half = Size * .5f;
                yield return Center + RecoilClockImagePreview.Rotate(new Vector2(-half.x, -half.y), Rotation);
                yield return Center + RecoilClockImagePreview.Rotate(new Vector2(half.x, -half.y), Rotation);
                yield return Center + RecoilClockImagePreview.Rotate(new Vector2(half.x, half.y), Rotation);
                yield return Center + RecoilClockImagePreview.Rotate(new Vector2(-half.x, half.y), Rotation);
            }
        }

        [MenuItem("Game Tools/Công Thành/Clock/Chỉnh mặt thu nhỏ + nút TIME")]
        internal static void Open()
        {
            var window = GetWindow<CongThanhClockCollapseTool>("Mặt thu nhỏ + nút TIME");
            window.minSize = new Vector2(420f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            Load();
            EditorApplication.update += Tick;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            if (_dirty) return; // keep unsaved edits
            Load();
            Repaint();
        }

        private void Tick()
        {
            if (!_playing) return;
            var t = (float)((EditorApplication.timeSinceStartup - _playStart) / Mathf.Max(.01f, _playDuration));
            _open = Mathf.Lerp(_playFrom, _playTo, Mathf.Clamp01(t));
            if (t >= 1f) _playing = false;
            Repaint();
        }

        // ---------------------------------------------------------------- GUI

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Lúc đóng, cả đồng hồ thu nhỏ vào icon tròn trên nút TIME; bấm nút hoặc mặt đồng hồ thì nở ra.\n" +
                "• Kéo trong vòng xanh để dời mặt nhỏ trên nút; kéo ô vàng (hoặc lăn chuột) để đổi cỡ.\n" +
                "• Chọn \"Khung: toàn bộ\" để kéo cả nút so với đồng hồ lúc mở.\n" +
                "Chỉ xem trong cửa sổ này; bấm Lưu mới ghi vào đồng hồ.",
                MessageType.Info);

            if (_layers == null || _faceIndex < 0 || _buttonIndex < 0)
            {
                EditorGUILayout.HelpBox(_layers == null
                        ? "Không tìm thấy đồng hồ: mở scene test hoặc new.unity, hoặc tạo prefab bằng tool \"Chỉnh hình đồng hồ\"."
                        : _faceIndex < 0
                            ? "Đồng hồ không có lớp \"" + RecoilClockImagePreview.FaceLayerLabel + "\"."
                            : "Không tạo được lớp nút \"" + _buttonLabel + "\" (thiếu timeBtn.png?).",
                    MessageType.Warning);
                if (GUILayout.Button("Tải lại")) Load();
                DrawStatus();
                return;
            }

            EditorGUILayout.LabelField("Đang xem", _sourceName + (_dirty ? "   (chưa lưu)" : string.Empty),
                EditorStyles.miniBoldLabel);
            if (_buttonWasMissing)
                EditorGUILayout.HelpBox("Đồng hồ này chưa có lớp \"" + _buttonLabel + "\": Lưu sẽ thêm vào (nằm dưới cùng).",
                    MessageType.None);

            _view = (View)GUILayout.Toolbar((int)_view, ViewNames);
            DrawPreview();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var open = EditorGUILayout.Slider(new GUIContent("Xem thử đóng → mở"), _open, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    _open = open;
                    _playing = false;
                }
                if (GUILayout.Button(_open < .5f ? "Mở thử" : "Đóng thử", GUILayout.Width(72f)))
                {
                    _playFrom = _open;
                    _playTo = _open < .5f ? 1f : 0f;
                    _playDuration = _growDuration * Mathf.Abs(_playTo - _playFrom);
                    _playStart = EditorApplication.timeSinceStartup;
                    _playing = true;
                }
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                DrawFields();
                _scroll = scroll.scrollPosition;
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Lưu (prefab sảnh + scene test)", GUILayout.Height(30f)))
                    // Opens / saves scenes and prefabs, so run it after this GUI pass rather than in the middle of it.
                    EditorApplication.delayCall += () => RunSafely(Save);
                if (GUILayout.Button("Tải lại", GUILayout.Width(80f), GUILayout.Height(30f)))
                    Load();
            }
            DrawStatus();
        }

        private void DrawFields()
        {
            var center = Center;
            var layer = _layers[_buttonIndex];
            var button = Place(_buttonIndex, center);

            EditorGUILayout.LabelField("Nút TIME", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var offset = EditorGUILayout.Vector2Field(
                new GUIContent("Tâm nút", "Tâm hình nút so với tâm mặt đồng hồ lúc mở (đơn vị của đồng hồ, y hướng lên)."),
                button.Center - center);
            var scale = EditorGUILayout.Slider(new GUIContent("Scale nút", "Phóng to / thu nhỏ quanh tâm của nút."),
                layer.scale, .05f, 3f);
            var rotation = EditorGUILayout.FloatField(new GUIContent("Xoay nút", "Xoay quanh tâm của nút (độ)."),
                layer.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                layer.scale = Mathf.Max(.05f, scale);
                layer.rotation = rotation;
                SetButtonCenter(center + offset); // scale / turn the button around its own center
                _dirty = true;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Mặt đồng hồ lúc thu nhỏ", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var iconCenter = EditorGUILayout.Vector2Field(
                new GUIContent("Tâm trên nút", "Vị trí tâm mặt nhỏ trên hình nút, theo tỉ lệ 0–1 tính từ góc trên-trái."),
                _iconCenter);
            var iconRadius = EditorGUILayout.Slider(
                new GUIContent("Bán kính", "Bán kính mặt nhỏ, theo tỉ lệ chiều cao hình nút."), _iconRadius, .01f, 1f);
            var growDuration = EditorGUILayout.Slider(
                new GUIContent("Thời gian nở (giây)", "Mặt đồng hồ nở ra từ nút / thu về nút trong chừng này giây."),
                _growDuration, .05f, 2f);
            if (EditorGUI.EndChangeCheck())
            {
                _iconCenter = new Vector2(Mathf.Clamp01(iconCenter.x), Mathf.Clamp01(iconCenter.y));
                _iconRadius = Mathf.Clamp(iconRadius, .01f, 1f);
                _growDuration = Mathf.Max(.05f, growDuration);
                _dirty = true;
            }

            button = Place(_buttonIndex, center);
            EditorGUILayout.LabelField(" ",
                $"Thu nhỏ còn {CollapsedScale(button) * 100f:0.#}% · đường kính {2f * IconRadius(button):0.#}",
                EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Đặt mặt nhỏ vào tâm đồng hồ",
                        "Dời cả nút sao cho mặt nhỏ nằm đúng tâm mặt đồng hồ lúc mở: đồng hồ nở ra tại chỗ.")))
                {
                    SetButtonCenter(button.Center + (center - IconPoint(button)));
                    _dirty = true;
                }
                if (GUILayout.Button(new GUIContent("Về mặc định", "Icon tròn của timeBtn.png, nút không xoay, scale 1.")))
                {
                    _iconCenter = CongThanhClockImageTool.TimeButtonIcon;
                    _iconRadius = DefaultIconRadius;
                    _growDuration = DefaultGrowDuration;
                    layer.scale = 1f;
                    layer.rotation = 0f;
                    button = Place(_buttonIndex, center);
                    SetButtonCenter(button.Center + (center - IconPoint(button)));
                    _dirty = true;
                }
            }
        }

        private void DrawPreview()
        {
            var rect = GUILayoutUtility.GetRect(10f, Mathf.Clamp(position.width * .6f, 220f, 420f),
                GUILayout.ExpandWidth(true));
            var controlId = GUIUtility.GetControlID(0x54494D45, FocusType.Passive, rect);

            var center = Center;
            var button = Place(_buttonIndex, center);
            var iconPoint = IconPoint(button);
            var iconRadius = IconRadius(button);
            var bounds = _drag != DragMode.None ? _frozenBounds : ViewBounds(center, button, iconPoint, iconRadius);
            var k = Mathf.Max(1e-4f, Mathf.Min((rect.width - 24f) / bounds.width, (rect.height - 24f) / bounds.height));
            Vector2 ToGui(Vector2 p) => rect.center + new Vector2(p.x - bounds.center.x, bounds.center.y - p.y) * k;
            Vector2 ToClock(Vector2 g) => bounds.center + new Vector2(g.x - rect.center.x, rect.center.y - g.y) / k;

            var iconGui = ToGui(iconPoint);
            var radiusGui = iconRadius * k;
            var handle = iconGui + new Vector2(radiusGui, -radiusGui) * .7071f;
            var current = Event.current;

            switch (current.GetTypeForControl(controlId))
            {
                case EventType.Repaint:
                    DrawClock(rect, center, button, iconPoint, ToGui);
                    DrawOverlay(rect, center, button, iconGui, radiusGui, handle, ToGui, k);
                    break;

                case EventType.MouseDown:
                    if (current.button != 0 || !rect.Contains(current.mousePosition)) break;
                    _drag = DragMode.None;
                    var down = ToClock(current.mousePosition);
                    if ((current.mousePosition - handle).magnitude <= 9f)
                    {
                        _drag = DragMode.Radius;
                    }
                    else if ((current.mousePosition - iconGui).magnitude <= Mathf.Max(radiusGui, 8f))
                    {
                        _drag = DragMode.Icon;
                        _dragOffset = down - iconPoint;
                    }
                    else if (_view == View.Whole && button.Contains(down))
                    {
                        _drag = DragMode.Button;
                        _dragOffset = down - button.Center;
                    }
                    if (_drag == DragMode.None) break;
                    _frozenBounds = bounds; // the view must not refit under the mouse while dragging
                    _playing = false;
                    GUIUtility.hotControl = controlId;
                    current.Use();
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != controlId || _drag == DragMode.None) break;
                    var point = ToClock(current.mousePosition);
                    if (_drag == DragMode.Radius)
                    {
                        _iconRadius = Mathf.Clamp((point - iconPoint).magnitude / Mathf.Max(button.Size.y, .001f), .01f, 1f);
                    }
                    else if (_drag == DragMode.Icon)
                    {
                        var local = RecoilClockImagePreview.Rotate(point - _dragOffset - button.Center, -button.Rotation);
                        _iconCenter = new Vector2(Mathf.Clamp01(.5f + local.x / Mathf.Max(button.Size.x, .001f)),
                            Mathf.Clamp01(.5f - local.y / Mathf.Max(button.Size.y, .001f)));
                    }
                    else
                    {
                        SetButtonCenter(point - _dragOffset);
                    }
                    _dirty = true;
                    current.Use();
                    Repaint();
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl != controlId) break;
                    GUIUtility.hotControl = 0;
                    _drag = DragMode.None;
                    current.Use();
                    Repaint();
                    break;

                case EventType.ScrollWheel:
                    if (!rect.Contains(current.mousePosition)) break;
                    _iconRadius = Mathf.Clamp(_iconRadius * (1f - current.delta.y * .02f), .01f, 1f);
                    _dirty = true;
                    current.Use();
                    Repaint();
                    break;
            }
        }

        /// <summary>
        /// The clock at the preview's open progress, as RecoilClockDial shows it: everything but the button scaled
        /// about the face center and slid from the icon to the face's own place; the button stays and fades out;
        /// the layers hidden behind the face only show once fully open.
        /// </summary>
        private void DrawClock(Rect rect, Vector2 center, Placed button, Vector2 iconPoint, Func<Vector2, Vector2> toGui)
        {
            EditorGUI.DrawRect(rect, new Color(.11f, .12f, .14f));
            var eased = _open * _open * (3f - 2f * _open);
            var scale = Mathf.Lerp(CollapsedScale(button), 1f, eased);
            var faceAt = Vector2.Lerp(iconPoint, center, eased);
            var k = (toGui(Vector2.right) - toGui(Vector2.zero)).x;

            GUI.BeginClip(rect);
            foreach (var i in RecoilClockImagePreview.GetDrawOrder(_layers))
            {
                if (!IsDrawn(i)) continue;
                var placed = Place(i, center);
                if (i == _buttonIndex)
                {
                    var alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_open / .6f));
                    if (alpha > .001f)
                        DrawSprite(_layers[i].sprite, toGui(placed.Center) - rect.position, placed.Size * k,
                            placed.Rotation, alpha);
                    continue;
                }
                if (_open < 1f && IsBehindFaceWhileClosed(i)) continue;
                DrawSprite(_layers[i].sprite, toGui(faceAt + (placed.Center - center) * scale) - rect.position,
                    placed.Size * (scale * k), placed.Rotation, 1f);
            }
            GUI.EndClip();
        }

        private void DrawOverlay(Rect rect, Vector2 center, Placed button, Vector2 iconGui, float radiusGui, Vector2 handle,
            Func<Vector2, Vector2> toGui, float k)
        {
            var previous = Handles.color;
            Handles.color = new Color(1f, 1f, 1f, _view == View.Whole ? .45f : .25f);
            var corners = button.Corners().Select(p => (Vector3)toGui(p)).ToList();
            corners.Add(corners[0]);
            Handles.DrawAAPolyLine(1.5f, corners.ToArray());

            Handles.color = Color.cyan;
            var circle = new Vector3[49];
            for (var i = 0; i < circle.Length; i++)
            {
                var angle = i * Mathf.PI * 2f / (circle.Length - 1);
                circle[i] = iconGui + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radiusGui;
            }
            Handles.DrawAAPolyLine(2f, circle);
            DrawCross(iconGui, 6f);
            if (_view == View.Whole)
            {
                Handles.color = new Color(1f, .85f, .3f, .8f);
                DrawCross(toGui(center), 9f); // face center when open
            }
            Handles.color = previous;

            var color = GUI.color;
            GUI.color = Color.yellow;
            GUI.DrawTexture(new Rect(handle.x - 5f, handle.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = color;

            GUI.Label(new Rect(rect.x + 6f, rect.yMax - 18f, rect.width - 12f, 16f),
                $"Thu nhỏ {CollapsedScale(button) * 100f:0.#}%   ·   1 px = {1f / k:0.##} đơn vị",
                EditorStyles.whiteMiniLabel);
        }

        private static void DrawCross(Vector2 point, float size)
        {
            Handles.DrawAAPolyLine(2f, point + new Vector2(-size, 0f), point + new Vector2(size, 0f));
            Handles.DrawAAPolyLine(2f, point + new Vector2(0f, -size), point + new Vector2(0f, size));
        }

        private static void DrawSprite(Sprite sprite, Vector2 center, Vector2 size, float rotation, float alpha)
        {
            var texture = sprite.texture;
            if (texture == null || size.x <= .01f || size.y <= .01f) return;
            var source = sprite.textureRect;
            var matrix = GUI.matrix;
            var color = GUI.color;
            // GUI space is y-down, so a counter-clockwise clock rotation turns the other way here.
            GUIUtility.RotateAroundPivot(-rotation, center);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTextureWithTexCoords(new Rect(center - size * .5f, size), texture,
                new Rect(source.x / texture.width, source.y / texture.height,
                    source.width / texture.width, source.height / texture.height), true);
            GUI.matrix = matrix;
            GUI.color = color;
        }

        /// <summary>Clock-space area the preview fits: the button and its icon, or the whole open clock with the button.</summary>
        private Rect ViewBounds(Vector2 center, Placed button, Vector2 iconPoint, float iconRadius)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            void Add(Vector2 p)
            {
                min = Vector2.Min(min, p);
                max = Vector2.Max(max, p);
            }

            foreach (var corner in button.Corners()) Add(corner);
            Add(iconPoint - Vector2.one * iconRadius * 1.3f);
            Add(iconPoint + Vector2.one * iconRadius * 1.3f);
            if (_view == View.Whole)
                for (var i = 0; i < _layers.Length; i++)
                    if (IsDrawn(i))
                        foreach (var corner in Place(i, center).Corners())
                            Add(corner);
            var size = Vector2.Max(max - min, Vector2.one * 10f);
            return new Rect((min + max) * .5f - size * .5f, size);
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        // ---------------------------------------------------------------- geometry (mirrors RecoilClockDial)

        private Vector2 Center => RecoilClockImagePreview.GetRotationCenter(_layers);

        private bool IsDrawn(int index)
        {
            var layer = _layers[index];
            if (layer == null || !layer.visible || layer.sprite == null) return false;
            var parent = RecoilClockImagePreview.FindParentIndex(_layers, index);
            return parent < 0 || _layers[parent].visible;
        }

        /// <summary>Layers RecoilClockDial tucks behind the face while closed (and the numbers riding them).</summary>
        private bool IsBehindFaceWhileClosed(int index)
        {
            var parent = RecoilClockImagePreview.FindParentIndex(_layers, index);
            return _revealLabels.Contains(_layers[parent >= 0 ? parent : index].label);
        }

        private Placed Place(int index, Vector2 center)
        {
            var layer = _layers[index];
            var parentIndex = RecoilClockImagePreview.FindParentIndex(_layers, index);
            if (parentIndex < 0)
                return new Placed(
                    center + RecoilClockImagePreview.Rotate((layer.position - center) * layer.scale, layer.rotation),
                    Fit(layer.sprite, layer.size * layer.scale), layer.rotation);
            var parent = _layers[parentIndex];
            return new Placed(
                RecoilClockImagePreview.GetAttachedCenter(parent, layer.position, parent.rotation, center),
                Fit(layer.sprite, layer.size * (layer.scale * parent.scale)),
                layer.rotation + (layer.keepUpright ? 0f : parent.rotation));
        }

        /// <summary>Size the sprite is drawn at inside its layer box (Image.preserveAspect).</summary>
        private static Vector2 Fit(Sprite sprite, Vector2 box)
        {
            if (sprite == null || sprite.rect.height <= 0f || box.y <= 0f) return box;
            var aspect = sprite.rect.width / sprite.rect.height;
            return box.x / box.y > aspect ? new Vector2(box.y * aspect, box.y) : new Vector2(box.x, box.x / aspect);
        }

        private Vector2 IconPoint(Placed button) =>
            button.Center + RecoilClockImagePreview.Rotate(
                new Vector2((_iconCenter.x - .5f) * button.Size.x, (.5f - _iconCenter.y) * button.Size.y), button.Rotation);

        private float IconRadius(Placed button) => _iconRadius * button.Size.y;

        private float FaceRadius()
        {
            var face = _layers[_faceIndex];
            var size = Fit(face.sprite, face.size * face.scale);
            return .45f * Mathf.Min(size.x, size.y);
        }

        private float CollapsedScale(Placed button) =>
            Mathf.Clamp(IconRadius(button) / Mathf.Max(FaceRadius(), .001f), .02f, 1f);

        /// <summary>Stores the button position that draws its center at <paramref name="target"/> (clock space).</summary>
        private void SetButtonCenter(Vector2 target)
        {
            var layer = _layers[_buttonIndex];
            var center = Center;
            layer.position = center + RecoilClockImagePreview.Rotate(target - center, -layer.rotation) /
                Mathf.Max(layer.scale, .0001f);
        }

        // ---------------------------------------------------------------- load / save

        private void Load()
        {
            _status = null;
            _dirty = false;
            _drag = DragMode.None;
            _playing = false;
            _layers = null;
            _buttonIndex = _faceIndex = -1;

            RecoilClockImagePreview source = null;
            if (!EditorApplication.isPlaying)
                source = CongThanhClockImageTool.FindPreview(SceneManager.GetActiveScene());
            if (source != null)
            {
                _sourceName = "scene " + source.gameObject.scene.name +
                              (PrefabUtility.IsPartOfPrefabInstance(source) ? " (prefab đồng hồ)" : string.Empty);
            }
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CongThanhClockImageTool.ClockPrefabPath);
                source = prefab != null ? prefab.GetComponent<RecoilClockImagePreview>() : null;
                _sourceName = "prefab " + Path.GetFileName(CongThanhClockImageTool.ClockPrefabPath);
            }
            if (source == null || source.Layers == null || source.Layers.Length == 0) return;
            _layers = source.Layers.Where(layer => layer != null).Select(Clone).ToArray();

            _buttonLabel = CongThanhClockImageTool.TimeButtonLabel;
            _iconCenter = CongThanhClockImageTool.TimeButtonIcon;
            _iconRadius = DefaultIconRadius;
            _growDuration = DefaultGrowDuration;
            _revealLabels.Clear();
            var dial = source.GetComponent<RecoilClockDial>();
            if (dial != null)
            {
                var serialized = new SerializedObject(dial);
                var label = serialized.FindProperty(DialButtonLabelField);
                if (label != null && !string.IsNullOrEmpty(label.stringValue)) _buttonLabel = label.stringValue;
                var iconCenter = serialized.FindProperty(DialIconCenterField);
                if (iconCenter != null) _iconCenter = iconCenter.vector2Value;
                var iconRadius = serialized.FindProperty(DialIconRadiusField);
                if (iconRadius != null) _iconRadius = iconRadius.floatValue;
                var growDuration = serialized.FindProperty(DialGrowDurationField);
                if (growDuration != null) _growDuration = growDuration.floatValue;
                var reveal = serialized.FindProperty("revealLayers");
                if (reveal != null)
                    for (var i = 0; i < reveal.arraySize; i++)
                        _revealLabels.Add(reveal.GetArrayElementAtIndex(i).FindPropertyRelative("label").stringValue);
            }
            else
            {
                _revealLabels.UnionWith(DefaultRevealLabels);
            }

            _faceIndex = FindTopLevel(_layers, RecoilClockImagePreview.FaceLayerLabel);
            _buttonIndex = FindTopLevel(_layers, _buttonLabel);
            _buttonWasMissing = _buttonIndex < 0;
            if (_buttonWasMissing && _buttonLabel == CongThanhClockImageTool.TimeButtonLabel)
            {
                // Same default the image tool adds: under everything, its icon on the face center.
                var button = CongThanhClockImageTool.LoadDefaultLayers()
                    .FirstOrDefault(layer => layer.label == CongThanhClockImageTool.TimeButtonLabel);
                if (button != null && button.sprite != null)
                {
                    button.position += Center;
                    button.order = int.MinValue;
                    _layers = _layers.Append(button).ToArray();
                    _buttonIndex = _layers.Length - 1;
                }
            }
            if (_buttonIndex >= 0 && !_layers[_buttonIndex].visible)
                _layers[_buttonIndex].visible = true; // saving turns it back on: the dial needs the image
        }

        private void Save()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Thoát Play Mode trước.");
            if (_layers == null || _buttonIndex < 0) return;

            var notes = new List<string> { SaveToTestScene(), SaveToPrefab() };
            var reverted = RevertInstanceOverrides();
            if (reverted > 0)
                notes.Add("Bỏ " + reverted + " giá trị bị ghi đè riêng trên đồng hồ trong scene đang mở để nhận giá trị mới.");

            var view = _view;
            var open = _open;
            Load();
            _view = view;
            _open = open;
            _status = string.Join("\n", notes.Where(note => !string.IsNullOrEmpty(note)));
            Repaint();
        }

        /// <summary>The test-scene clock: the image tool rebuilds the lobby prefab from it, so it must agree.</summary>
        private string SaveToTestScene()
        {
            var path = CongThanhClockImageTool.TestScenePath;
            if (!File.Exists(path)) return "Không có " + path + ": chỉ lưu prefab.";
            var scene = SceneManager.GetSceneByPath(path);
            var openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                var preview = CongThanhClockImageTool.FindPreview(scene);
                if (preview == null) return "Scene test chưa có đồng hồ: chỉ lưu prefab.";
                var wasDirty = scene.isDirty;
                WriteTo(preview, true);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!openedHere && wasDirty)
                    return "Đã ghi vào scene test (scene đang có thay đổi khác chưa lưu: nhớ Ctrl+S).";
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException("Không lưu được " + path);
                return "Đã lưu vào scene test.";
            }
            finally
            {
                if (openedHere && scene.IsValid())
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private string SaveToPrefab()
        {
            var path = CongThanhClockImageTool.ClockPrefabPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                return "Chưa có prefab " + path + ": bấm \"Thêm / cập nhật đồng hồ ở sảnh\" trong tool Chỉnh hình đồng hồ.";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var preview = root.GetComponentInChildren<RecoilClockImagePreview>(true);
                if (preview == null)
                    throw new InvalidOperationException("Prefab " + path + " không có RecoilClockImagePreview.");
                WriteTo(preview, false);
                PrefabUtility.SaveAsPrefabAsset(root, path, out var saved);
                if (!saved)
                    throw new InvalidOperationException("Không lưu được prefab " + path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            return "Đã lưu prefab " + Path.GetFileName(path) + ": đồng hồ ở sảnh (new.unity) dùng prefab này.";
        }

        /// <summary>Writes the button layer and the dial's collapse settings into <paramref name="preview"/>, then rebuilds its images.</summary>
        private void WriteTo(RecoilClockImagePreview preview, bool withUndo)
        {
            var edited = _layers[_buttonIndex];
            var serialized = new SerializedObject(preview);
            var layers = serialized.FindProperty("layers");
            var index = FindTopLevel(layers, _buttonLabel);
            if (index < 0)
            {
                if (_buttonLabel != CongThanhClockImageTool.TimeButtonLabel)
                    throw new InvalidOperationException("Đồng hồ không có lớp \"" + _buttonLabel + "\".");
                CongThanhClockImageTool.AddMissingDefaultLayers(preview);
                serialized.Update();
                layers = serialized.FindProperty("layers");
                index = FindTopLevel(layers, _buttonLabel);
                if (index < 0)
                    throw new InvalidOperationException("Không thêm được lớp \"" + _buttonLabel + "\".");
            }

            var layer = layers.GetArrayElementAtIndex(index);
            // Kept relative to the face center, so it lands the same even if that clock's face sits elsewhere.
            var targetCenter = RecoilClockImagePreview.GetRotationCenter(preview.Layers);
            layer.FindPropertyRelative("position").vector2Value = targetCenter + (edited.position - Center);
            layer.FindPropertyRelative("scale").floatValue = edited.scale;
            layer.FindPropertyRelative("rotation").floatValue = edited.rotation;
            layer.FindPropertyRelative("visible").boolValue = true;
            var sprite = layer.FindPropertyRelative("sprite");
            if (sprite.objectReferenceValue == null) sprite.objectReferenceValue = edited.sprite;
            if (withUndo) serialized.ApplyModifiedProperties();
            else serialized.ApplyModifiedPropertiesWithoutUndo();

            var dial = preview.GetComponent<RecoilClockDial>();
            if (dial == null)
                dial = withUndo
                    ? Undo.AddComponent<RecoilClockDial>(preview.gameObject)
                    : preview.gameObject.AddComponent<RecoilClockDial>();
            var dialObject = new SerializedObject(dial);
            SetDialValue(dialObject, DialButtonLabelField, p => p.stringValue = _buttonLabel);
            SetDialValue(dialObject, DialIconCenterField, p => p.vector2Value = _iconCenter);
            SetDialValue(dialObject, DialIconRadiusField, p => p.floatValue = _iconRadius);
            SetDialValue(dialObject, DialGrowDurationField, p => p.floatValue = _growDuration);
            if (withUndo) dialObject.ApplyModifiedProperties();
            else dialObject.ApplyModifiedPropertiesWithoutUndo();
            CongThanhClockImageTool.EnsureDialSound(dial);

            preview.RebuildImages();
        }

        private static void SetDialValue(SerializedObject dial, string field, Action<SerializedProperty> set)
        {
            var property = dial.FindProperty(field);
            if (property == null)
                throw new InvalidOperationException(
                    "RecoilClockDial chưa có ô \"" + field + "\": chờ Unity biên dịch xong rồi bấm Lưu lại.");
            set(property);
        }

        /// <summary>
        /// Clock prefab instances in open scenes that override these values would hide the saved ones (the lobby
        /// clock normally overrides only its placement): drop those overrides.
        /// </summary>
        private int RevertInstanceOverrides()
        {
            var prefabGuid = AssetDatabase.AssetPathToGUID(CongThanhClockImageTool.ClockPrefabPath);
            var count = 0;
            for (var s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                var sceneCount = 0;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var preview in root.GetComponentsInChildren<RecoilClockImagePreview>(true))
                {
                    if (!PrefabUtility.IsPartOfPrefabInstance(preview)) continue;
                    var asset = PrefabUtility.GetCorrespondingObjectFromSource(preview);
                    if (asset == null || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset)) != prefabGuid)
                        continue;

                    var dial = preview.GetComponent<RecoilClockDial>();
                    if (dial != null)
                    {
                        var dialObject = new SerializedObject(dial);
                        foreach (var field in new[] { DialButtonLabelField, DialIconCenterField, DialIconRadiusField, DialGrowDurationField })
                            sceneCount += Revert(dialObject.FindProperty(field));
                    }
                    var previewObject = new SerializedObject(preview);
                    var layers = previewObject.FindProperty("layers");
                    var index = FindTopLevel(layers, _buttonLabel);
                    if (index < 0) continue;
                    var layer = layers.GetArrayElementAtIndex(index);
                    foreach (var field in new[] { "position", "scale", "rotation", "visible" })
                        sceneCount += Revert(layer.FindPropertyRelative(field));
                }
                if (sceneCount > 0) EditorSceneManager.MarkSceneDirty(scene);
                count += sceneCount;
            }
            return count;
        }

        private static int Revert(SerializedProperty property)
        {
            if (property == null || !property.prefabOverride) return 0;
            PrefabUtility.RevertPropertyOverride(property, InteractionMode.AutomatedAction);
            return 1;
        }

        private static int FindTopLevel(RecoilClockImageLayer[] layers, string label) =>
            Array.FindIndex(layers, layer => layer != null && layer.label == label && string.IsNullOrEmpty(layer.attachTo));

        private static int FindTopLevel(SerializedProperty layers, string label)
        {
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                if (layer.FindPropertyRelative("label").stringValue == label &&
                    string.IsNullOrEmpty(layer.FindPropertyRelative("attachTo").stringValue))
                    return i;
            }
            return -1;
        }

        private static RecoilClockImageLayer Clone(RecoilClockImageLayer layer) => new()
        {
            label = layer.label,
            sprite = layer.sprite,
            visible = layer.visible,
            order = layer.order,
            size = layer.size,
            scale = layer.scale,
            position = layer.position,
            rotation = layer.rotation,
            attachTo = layer.attachTo,
            keepUpright = layer.keepUpright,
            pivot = layer.pivot
        };

        private void RunSafely(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _status = exception.Message;
                Debug.LogException(exception);
            }
            Repaint();
        }
    }
}
