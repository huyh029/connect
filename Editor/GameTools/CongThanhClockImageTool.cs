using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GameToolsEditor
{
    public sealed class CongThanhClockImageTool : EditorWindow
    {
        internal const string TestScenePath = "Assets/Scenes/test.unity";
        private const string ClockAssetFolder = "Assets/_Game/UI/Clock";
        private const float ClockSize = 640f;
        private enum DragMode { None, Position, Scale }
        // Negative = clockwise on screen, like a clock hand.
        private const float SpinDegreesPerSecond = -90f;

        private RecoilClockImagePreview _preview;
        private Vector2 _scroll;
        private int _selectedLayerIndex = -1;
        private DragMode _dragMode;
        private Vector2 _dragOffset;
        private int _dragControlId;
        private float _dragStartScale;
        private float _dragStartDistance;
        private string _status;
        // Layer index -> editor time its preview-only test spin started.
        private readonly Dictionary<int, double> _spinStart = new();
        // Ring layers whose attached numbers are listed.
        private readonly HashSet<int> _expandedLayers = new();

        // Numbers cut out of a ring image: the ring is swapped for its number-free version and each number
        // becomes an upright layer attached to the ring. Files are produced next to the ring image.
        private const string RingNumberSeparator = RecoilClockImagePreview.NumberLabelSeparator;
        private static readonly (string ringLabel, string originalFile, string cleanFile, string numbersFolder)[] RingNumberSets =
        {
            ("Vòng giờ", "giờ.png", "giờ không số.png", "Số giờ"),
            ("Vòng phút", "phút.png", "phút không số.png", "Số phút"),
        };

#pragma warning disable CS0649 // Filled by JsonUtility.
        [Serializable]
        private sealed class RingNumberManifest
        {
            public int[] imageSize;
            public RingNumberEntry[] numbers;
        }

        [Serializable]
        private sealed class RingNumberEntry
        {
            public string label;
            public string file;
            public float[] center; // source-image pixels, y down
            public int[] size;
        }
#pragma warning restore CS0649

        [Serializable]
        private sealed class ClockLayoutData
        {
            public Vector2 rootPosition;
            public float rootScale;
            /// <summary>Clock face center; every layer rotates around it.</summary>
            public Vector2 rotationCenter;
            public ClockLayerData[] layers;
        }

        [Serializable]
        private sealed class ClockLayerData
        {
            public string label;
            public string spriteAssetPath;
            public string spriteName;
            public bool visible;
            public int order;
            public Vector2 size;
            public float scale;
            /// <summary>Layer center before rotation; for an attached layer, its offset from the ring's center.</summary>
            public Vector2 position;
            /// <summary>Derived: normalized pivot that lands on <see cref="ClockLayoutData.rotationCenter"/> (0.5 for attached layers).</summary>
            public Vector2 pivot;
            public float rotation;
            public string attachTo;
            public bool keepUpright;
        }

        [MenuItem("Game Tools/Công Thành/Clock/Chỉnh hình đồng hồ")]
        private static void Open()
        {
            var window = GetWindow<CongThanhClockImageTool>("Chỉnh hình đồng hồ");
            window.minSize = new Vector2(420f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            _preview = FindPreview(SceneManager.GetActiveScene());
            EditorApplication.update += RepaintWhileSpinning;
        }

        private void OnDisable()
        {
            EditorApplication.update -= RepaintWhileSpinning;
        }

        private void OnGUI()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (_preview == null || _preview.gameObject.scene != activeScene)
            {
                _preview = FindPreview(activeScene);
                _spinStart.Clear();
                _expandedLayers.Clear();
            }

            EditorGUILayout.LabelField("Bố trí hình ảnh đồng hồ", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Chọn và kéo từng lớp để di chuyển; chỉnh scale và góc xoay riêng. Mọi lớp xoay quanh tâm mặt đồng hồ. " +
                "Tạo hoặc cập nhật bản xem thử trong Assets/Scenes/test.unity; không thay đổi scene trận chính.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Mở scene test và tạo/cập nhật đồng hồ", GUILayout.Height(32f)))
                    RunSafely(BuildTestPreview);
            }

            if (_preview == null || !_preview.gameObject.scene.IsValid())
            {
                EditorGUILayout.HelpBox("Mở scene test để chỉnh các lớp hình ảnh.", MessageType.Info);
                DrawStatus();
                return;
            }

            string notice = null;
            if (!EditorApplication.isPlayingOrWillChangePlaymode && _preview.NeedsLayoutUpgrade)
            {
                Undo.RecordObject(_preview, "Upgrade Clock Layout");
                _preview.UpgradeLayout();
                RunSafely(() => _preview.RebuildImages());
                EditorSceneManager.MarkSceneDirty(_preview.gameObject.scene);
                notice = "Đã chuyển sang xoay quanh tâm mặt đồng hồ, giữ nguyên hình đang hiển thị.";
            }

            var serialized = new SerializedObject(_preview);
            serialized.Update();
            var position = serialized.FindProperty("rootPosition");
            var rootScale = serialized.FindProperty("rootScale");
            var layers = serialized.FindProperty("layers");

            if (!EditorApplication.isPlayingOrWillChangePlaymode && !HasSequentialOrder(layers))
            {
                var topLevel = GetTopLevelOrder(layers);
                RenumberLayers(layers, topLevel);
                serialized.ApplyModifiedProperties();
                EditorSceneManager.MarkSceneDirty(_preview.gameObject.scene);
                var renumbered = "Đã đánh số thứ tự trước/sau 1.." + topLevel.Length + " theo thứ tự hiện tại của các lớp.";
                notice = notice == null ? renumbered : notice + "\n" + renumbered;
            }
            if (notice != null)
                _status = notice;
            foreach (var stale in _spinStart.Keys.Where(index => index >= layers.arraySize).ToList())
                _spinStart.Remove(stale);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(position, new GUIContent("Vị trí đồng hồ"));
            EditorGUILayout.Slider(rootScale, .1f, 3f, new GUIContent("Scale đồng hồ"));
            EditorGUILayout.HelpBox(
                "Bấm lớp để chọn; kéo ảnh để di chuyển, kéo nút góc vàng để scale. " +
                "Dấu + xanh = tâm lớp \"" + RecoilClockImagePreview.FaceLayerLabel +
                "\": mọi lớp đều xoay và scale quanh điểm này. Bấm \"Xoay\" ở từng lớp để xoay thử; " +
                "số gắn trên vòng chạy theo vòng nhưng vẫn đứng thẳng.",
                MessageType.None);
            DrawCompositePreview(layers, rootScale.floatValue, position.vector2Value);

            EditorGUILayout.LabelField(
                "Thứ tự trước/sau: 1 = nằm dưới cùng, số lớn hơn = nằm trên. Gõ số rồi Enter, các lớp khác tự dồn lại. " +
                "Số gắn trên vòng luôn nằm ngay trên vòng của nó (bấm + để xem).",
                EditorStyles.wordWrappedMiniLabel);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                foreach (var i in GetTopLevelOrder(layers))
                {
                    var layer = layers.GetArrayElementAtIndex(i);
                    var label = layer.FindPropertyRelative("label").stringValue;
                    var children = GetChildren(layers, i);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawOrderField(layers, i, null, GUILayout.Width(36f));
                        if (children.Count > 0)
                        {
                            var expanded = _expandedLayers.Contains(i);
                            if (UiOnlyToggle(expanded, new GUIContent(expanded ? "−" : "+", "Hiện/ẩn các số gắn trên vòng"),
                                    GUILayout.Width(22f)) != expanded)
                            {
                                if (expanded) _expandedLayers.Remove(i);
                                else _expandedLayers.Add(i);
                            }
                            label += "  (" + children.Count + " số)";
                        }
                        else
                        {
                            GUILayout.Space(26f);
                        }
                        DrawSelectToggle(i, label);
                        DrawSpinToggle(i);
                        DrawVisibleToggle(layer);
                    }

                    if (!_expandedLayers.Contains(i)) continue;
                    foreach (var child in children)
                    {
                        var childLayer = layers.GetArrayElementAtIndex(child);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(66f);
                            DrawSelectToggle(child, ChildDisplayName(childLayer, label));
                            DrawVisibleToggle(childLayer);
                        }
                    }
                }
                _scroll = scroll.scrollPosition;
            }

            if (_selectedLayerIndex >= 0 && _selectedLayerIndex < layers.arraySize)
            {
                var layer = layers.GetArrayElementAtIndex(_selectedLayerIndex);
                var parentIndex = FindParentIndex(layers, _selectedLayerIndex);
                EditorGUILayout.LabelField(layer.FindPropertyRelative("label").stringValue, EditorStyles.boldLabel);
                if (parentIndex < 0)
                {
                    DrawOrderField(layers, _selectedLayerIndex,
                        new GUIContent("Thứ tự trước/sau", "1 = nằm dưới cùng, số lớn hơn = nằm trên."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("sprite"), new GUIContent("Hình"));
                    EditorGUILayout.Slider(layer.FindPropertyRelative("scale"), .05f, 3f,
                        new GUIContent("Scale", "Scale tính từ tâm mặt đồng hồ."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("position"),
                        new GUIContent("Vị trí (tâm lớp)", "Tâm của lớp hình khi scale = 1 và chưa xoay."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("rotation"),
                        new GUIContent("Xoay", "Xoay quanh tâm mặt đồng hồ."));
                }
                else
                {
                    EditorGUILayout.LabelField("Gắn vào",
                        layers.GetArrayElementAtIndex(parentIndex).FindPropertyRelative("label").stringValue);
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("keepUpright"),
                        new GUIContent("Luôn đứng thẳng", "Không xoay theo vòng khi vòng xoay."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("sprite"), new GUIContent("Hình"));
                    EditorGUILayout.Slider(layer.FindPropertyRelative("scale"), .05f, 3f,
                        new GUIContent("Scale riêng", "Nhân thêm với scale của vòng; scale quanh tâm của số."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("position"),
                        new GUIContent("Vị trí trên vòng", "Độ lệch so với tâm vòng, tính khi vòng chưa scale/xoay."));
                    EditorGUILayout.PropertyField(layer.FindPropertyRelative("rotation"),
                        new GUIContent("Xoay riêng", "Góc nghiêng của riêng số này; 0 = đứng thẳng."));
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                RunSafely(() =>
                {
                    _preview.ApplyRootLayout();
                    _preview.RebuildImages();
                    EditorSceneManager.MarkSceneDirty(_preview.gameObject.scene);
                });
            }

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Làm mới hình trong scene test"))
                    RunSafely(() =>
                    {
                        _preview.RebuildImages();
                        EditorSceneManager.MarkSceneDirty(_preview.gameObject.scene);
                        _status = "Đã làm mới lớp hình trong scene test.";
                    });
                if (GUILayout.Button("Lưu thuộc tính ra JSON"))
                    RunSafely(SaveLayoutJson);
                if (GUILayout.Button("Chỉnh mặt thu nhỏ + nút TIME…"))
                    CongThanhClockCollapseTool.Open();
                if (GUILayout.Button("Thêm / cập nhật đồng hồ ở sảnh (Recoil battle demo)"))
                    // Switches scenes, so run it after this GUI pass rather than in the middle of it.
                    EditorApplication.delayCall += () => RunSafely(() => _status = AddClockToLobby());
            }
            DrawStatus();
        }

        private void DrawCompositePreview(SerializedProperty layers, float rootScale, Vector2 rootPosition)
        {
            const float previewSize = 300f;
            var preview = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(true));
            GUI.Box(preview, GUIContent.none);
            if (layers == null || rootScale <= 0f) return;

            _dragControlId = GUIUtility.GetControlID(0x434C4B, FocusType.Passive, preview);
            var previewScale = Mathf.Min((preview.width - 12f) / (ClockSize * rootScale),
                (preview.height - 12f) / (ClockSize * rootScale));
            var scale = previewScale * rootScale;
            var center = preview.center + rootPosition * new Vector2(previewScale, -previewScale);
            var rotationCenter = GetRotationCenter(layers);
            foreach (var i in GetDrawOrder(layers))
            {
                if (!IsDrawn(layers, i)) continue;
                var sprite = (Sprite)layers.GetArrayElementAtIndex(i).FindPropertyRelative("sprite").objectReferenceValue;
                var geometry = GetGeometry(layers, i, rotationCenter, center, scale);
                var oldMatrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(-geometry.Rotation, geometry.Pivot);
                // Match the scene's Image.preserveAspect: fit the sprite inside the layer rect, centered.
                GUI.DrawTextureWithTexCoords(FitRect(geometry.Rect, sprite.textureRect.width / sprite.textureRect.height),
                    sprite.texture,
                    new Rect(sprite.textureRect.x / sprite.texture.width,
                        sprite.textureRect.y / sprite.texture.height,
                        sprite.textureRect.width / sprite.texture.width,
                        sprite.textureRect.height / sprite.texture.height), true);
                GUI.matrix = oldMatrix;
            }

            var selected = IsDrawn(layers, _selectedLayerIndex) ? _selectedLayerIndex : -1;
            if (selected >= 0)
            {
                var geometry = GetGeometry(layers, selected, rotationCenter, center, scale);
                var previousColor = Handles.color;
                Handles.color = Color.yellow;
                Handles.DrawAAPolyLine(2f, geometry.Outline());
                Handles.color = previousColor;
                DrawHandle(geometry.ScaleHandle, Color.yellow);
            }
            DrawRotationCenter(ClockToPreview(rotationCenter, center, scale));

            var current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && preview.Contains(current.mousePosition))
            {
                _dragMode = DragMode.None;
                if (selected >= 0 && Vector2.Distance(current.mousePosition,
                        GetGeometry(layers, selected, rotationCenter, center, scale).ScaleHandle) <= 10f)
                {
                    // Grabbing a layer ends its (and its ring's) test spin so the edit works on real rotations.
                    StopSpin(layers, selected);
                    _dragMode = DragMode.Scale;
                    _dragStartScale = layers.GetArrayElementAtIndex(selected).FindPropertyRelative("scale").floatValue;
                    _dragStartDistance = Vector2.Distance(current.mousePosition,
                        GetScaleOrigin(layers, selected, rotationCenter, center, scale));
                    Undo.RecordObject(_preview, "Scale Clock Image Layer");
                }
                else
                {
                    _selectedLayerIndex = FindTopLayerAt(layers, current.mousePosition, rotationCenter, center, scale);
                    if (_selectedLayerIndex >= 0)
                    {
                        StopSpin(layers, _selectedLayerIndex);
                        _dragOffset = PreviewToClockPosition(current.mousePosition, center, scale) -
                                      GetRenderedCenter(layers, _selectedLayerIndex, rotationCenter);
                        _dragMode = DragMode.Position;
                        Undo.RecordObject(_preview, "Move Clock Image Layer");
                    }
                }

                if (_dragMode != DragMode.None)
                    GUIUtility.hotControl = _dragControlId;
                current.Use();
                Repaint();
            }
            else if (_dragMode != DragMode.None && GUIUtility.hotControl == _dragControlId &&
                     current.type == EventType.MouseDrag && current.button == 0)
            {
                if (_selectedLayerIndex >= 0 && _selectedLayerIndex < layers.arraySize)
                {
                    var layer = layers.GetArrayElementAtIndex(_selectedLayerIndex);
                    if (_dragMode == DragMode.Position)
                    {
                        var target = PreviewToClockPosition(current.mousePosition, center, scale) - _dragOffset;
                        layer.FindPropertyRelative("position").vector2Value =
                            RenderedCenterToPosition(layers, _selectedLayerIndex, rotationCenter, target);
                    }
                    else if (_dragMode == DragMode.Scale)
                    {
                        var distance = Vector2.Distance(current.mousePosition,
                            GetScaleOrigin(layers, _selectedLayerIndex, rotationCenter, center, scale));
                        if (_dragStartDistance > 0f)
                            layer.FindPropertyRelative("scale").floatValue =
                                Mathf.Max(.05f, _dragStartScale * distance / _dragStartDistance);
                    }
                    GUI.changed = true;
                    current.Use();
                }
            }
            else if (_dragMode != DragMode.None && GUIUtility.hotControl == _dragControlId &&
                     current.type == EventType.MouseUp && current.button == 0)
            {
                _dragMode = DragMode.None;
                GUIUtility.hotControl = 0;
                current.Use();
            }
        }

        /// <summary>A layer as drawn in the preview: unrotated rect turned by <see cref="Rotation"/> around <see cref="Pivot"/>.</summary>
        private readonly struct LayerGeometry
        {
            public readonly Rect Rect;
            public readonly Vector2 Pivot;
            public readonly float Rotation;

            public LayerGeometry(Rect rect, Vector2 pivot, float rotation)
            {
                Rect = rect;
                Pivot = pivot;
                Rotation = rotation;
            }

            public Vector2 Center => ToScreen(Rect.center);
            public Vector2 ScaleHandle => ToScreen(new Vector2(Rect.xMax, Rect.yMin));

            public bool Contains(Vector2 screenPoint) => Rect.Contains(FromScreen(screenPoint));

            public Vector3[] Outline() => new Vector3[]
            {
                ToScreen(new Vector2(Rect.xMin, Rect.yMin)), ToScreen(new Vector2(Rect.xMax, Rect.yMin)),
                ToScreen(new Vector2(Rect.xMax, Rect.yMax)), ToScreen(new Vector2(Rect.xMin, Rect.yMax)),
                ToScreen(new Vector2(Rect.xMin, Rect.yMin))
            };

            // GUI space is y-down, so a counter-clockwise turn on screen uses the transposed matrix.
            private Vector2 ToScreen(Vector2 point)
            {
                var radians = Rotation * Mathf.Deg2Rad;
                var cos = Mathf.Cos(radians);
                var sin = Mathf.Sin(radians);
                var delta = point - Pivot;
                return Pivot + new Vector2(delta.x * cos + delta.y * sin, -delta.x * sin + delta.y * cos);
            }

            private Vector2 FromScreen(Vector2 point)
            {
                var radians = Rotation * Mathf.Deg2Rad;
                var cos = Mathf.Cos(radians);
                var sin = Mathf.Sin(radians);
                var delta = point - Pivot;
                return Pivot + new Vector2(delta.x * cos - delta.y * sin, delta.x * sin + delta.y * cos);
            }
        }

        /// <summary>
        /// Preview geometry of layer <paramref name="index"/>. Top-level layers scale and turn around the clock face
        /// center; a layer attached to a ring is carried by the ring and turns around its own center.
        /// </summary>
        private LayerGeometry GetGeometry(SerializedProperty layers, int index, Vector2 rotationCenter, Vector2 center,
            float scale, bool withSpin = true)
        {
            var layer = layers.GetArrayElementAtIndex(index);
            var layerScale = layer.FindPropertyRelative("scale").floatValue;
            var position = layer.FindPropertyRelative("position").vector2Value;
            var rotation = layer.FindPropertyRelative("rotation").floatValue;
            var size = layer.FindPropertyRelative("size").vector2Value;
            var parentIndex = FindParentIndex(layers, index);
            if (parentIndex < 0)
            {
                var guiSize = size * (layerScale * scale);
                var layerCenter = ClockToPreview(rotationCenter + (position - rotationCenter) * layerScale, center, scale);
                return new LayerGeometry(new Rect(layerCenter - guiSize * .5f, guiSize),
                    ClockToPreview(rotationCenter, center, scale), rotation + (withSpin ? SpinAngle(index) : 0f));
            }

            var parent = layers.GetArrayElementAtIndex(parentIndex);
            var parentScale = parent.FindPropertyRelative("scale").floatValue;
            var parentRotation = parent.FindPropertyRelative("rotation").floatValue +
                                 (withSpin ? SpinAngle(parentIndex) : 0f);
            var attachedSize = size * (layerScale * parentScale * scale);
            var attachedCenter = ClockToPreview(GetAttachedCenter(parent, position, parentRotation, rotationCenter),
                center, scale);
            var keepUpright = layer.FindPropertyRelative("keepUpright").boolValue;
            return new LayerGeometry(new Rect(attachedCenter - attachedSize * .5f, attachedSize), attachedCenter,
                rotation + (keepUpright ? 0f : parentRotation));
        }

        /// <summary>Point the scale handle scales around: the face center, or a ring number's own center.</summary>
        private Vector2 GetScaleOrigin(SerializedProperty layers, int index, Vector2 rotationCenter, Vector2 center,
            float scale) =>
            FindParentIndex(layers, index) < 0
                ? ClockToPreview(rotationCenter, center, scale)
                : GetGeometry(layers, index, rotationCenter, center, scale, false).Center;

        /// <summary>Preview-only test spin angle for a layer (0 when not spinning); never written to the layer.</summary>
        private float SpinAngle(int layerIndex) =>
            _spinStart.TryGetValue(layerIndex, out var start)
                ? (float)((EditorApplication.timeSinceStartup - start) * SpinDegreesPerSecond % 360.0)
                : 0f;

        private void StopSpin(SerializedProperty layers, int index)
        {
            _spinStart.Remove(index);
            _spinStart.Remove(FindParentIndex(layers, index));
        }

        /// <summary>A toggle that only changes window state, so it must not count as an edit to the layer data.</summary>
        private static bool UiOnlyToggle(bool value, GUIContent content, params GUILayoutOption[] options)
        {
            var wasChanged = GUI.changed;
            var result = GUILayout.Toggle(value, content, EditorStyles.miniButton, options);
            GUI.changed = wasChanged;
            return result;
        }

        private void DrawSelectToggle(int layerIndex, string label)
        {
            if (UiOnlyToggle(_selectedLayerIndex == layerIndex, new GUIContent(label)))
                _selectedLayerIndex = layerIndex;
        }

        private static void DrawVisibleToggle(SerializedProperty layer)
        {
            var visible = layer.FindPropertyRelative("visible");
            EditorGUI.BeginChangeCheck();
            var showLayer = EditorGUILayout.Toggle(visible.boolValue, GUILayout.Width(20f));
            if (EditorGUI.EndChangeCheck())
                visible.boolValue = showLayer;
        }

        private void DrawSpinToggle(int layerIndex)
        {
            var spinning = _spinStart.ContainsKey(layerIndex);
            var spin = UiOnlyToggle(spinning,
                new GUIContent("Xoay", "Xoay thử trong khung xem trước để xem lớp có lệch tâm không. Không lưu vào dữ liệu."),
                GUILayout.Width(44f));
            if (spin == spinning) return;
            if (spin) _spinStart[layerIndex] = EditorApplication.timeSinceStartup;
            else _spinStart.Remove(layerIndex);
        }

        private static string ChildDisplayName(SerializedProperty child, string parentLabel)
        {
            var label = child.FindPropertyRelative("label").stringValue;
            var prefix = parentLabel.Split(new[] { "  (" }, StringSplitOptions.None)[0] + RingNumberSeparator;
            return label.StartsWith(prefix, StringComparison.Ordinal) ? "Số " + label.Substring(prefix.Length) : label;
        }

        private void RepaintWhileSpinning()
        {
            if (_spinStart.Count > 0)
                Repaint();
        }

        private static bool IsDrawn(SerializedProperty layers, int index)
        {
            if (index < 0 || index >= layers.arraySize) return false;
            var layer = layers.GetArrayElementAtIndex(index);
            if (!layer.FindPropertyRelative("visible").boolValue) return false;
            if (layer.FindPropertyRelative("sprite").objectReferenceValue as Sprite == null) return false;
            var parentIndex = FindParentIndex(layers, index);
            return parentIndex < 0 || layers.GetArrayElementAtIndex(parentIndex).FindPropertyRelative("visible").boolValue;
        }

        private static bool IsFaceLayer(SerializedProperty layer) =>
            layer.FindPropertyRelative("label").stringValue == RecoilClockImagePreview.FaceLayerLabel;

        private static Vector2 GetRotationCenter(SerializedProperty layers)
        {
            for (var i = 0; i < layers.arraySize; i++)
            {
                var layer = layers.GetArrayElementAtIndex(i);
                if (IsFaceLayer(layer))
                    return layer.FindPropertyRelative("position").vector2Value;
            }
            return Vector2.zero;
        }

        /// <summary>Mirror of <see cref="RecoilClockImagePreview.GetAttachedCenter"/> for serialized data.</summary>
        private static Vector2 GetAttachedCenter(SerializedProperty parent, Vector2 offset, float parentRotation,
            Vector2 rotationCenter) =>
            rotationCenter + RecoilClockImagePreview.Rotate(
                (parent.FindPropertyRelative("position").vector2Value + offset - rotationCenter) *
                parent.FindPropertyRelative("scale").floatValue, parentRotation);

        /// <summary>Layer center as placed in the scene (no test spin), in clock space.</summary>
        private static Vector2 GetRenderedCenter(SerializedProperty layers, int index, Vector2 rotationCenter)
        {
            var layer = layers.GetArrayElementAtIndex(index);
            var position = layer.FindPropertyRelative("position").vector2Value;
            var parentIndex = FindParentIndex(layers, index);
            if (parentIndex >= 0)
            {
                var parent = layers.GetArrayElementAtIndex(parentIndex);
                return GetAttachedCenter(parent, position, parent.FindPropertyRelative("rotation").floatValue,
                    rotationCenter);
            }
            return rotationCenter + RecoilClockImagePreview.Rotate(
                (position - rotationCenter) * layer.FindPropertyRelative("scale").floatValue,
                layer.FindPropertyRelative("rotation").floatValue);
        }

        /// <summary>Inverse of <see cref="GetRenderedCenter"/>: the stored position that draws the layer centered at <paramref name="renderedCenter"/>.</summary>
        private static Vector2 RenderedCenterToPosition(SerializedProperty layers, int index, Vector2 rotationCenter,
            Vector2 renderedCenter)
        {
            var layer = layers.GetArrayElementAtIndex(index);
            var parentIndex = FindParentIndex(layers, index);
            if (parentIndex >= 0)
            {
                var parent = layers.GetArrayElementAtIndex(parentIndex);
                var parentScale = Mathf.Max(.0001f, parent.FindPropertyRelative("scale").floatValue);
                return rotationCenter - parent.FindPropertyRelative("position").vector2Value +
                       RecoilClockImagePreview.Rotate(renderedCenter - rotationCenter,
                           -parent.FindPropertyRelative("rotation").floatValue) / parentScale;
            }
            // The face layer defines the center itself, so moving it is a plain translation.
            if (IsFaceLayer(layer)) return renderedCenter;
            var layerScale = Mathf.Max(.0001f, layer.FindPropertyRelative("scale").floatValue);
            return rotationCenter + RecoilClockImagePreview.Rotate(renderedCenter - rotationCenter,
                -layer.FindPropertyRelative("rotation").floatValue) / layerScale;
        }

        private static void DrawHandle(Vector2 point, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(point.x - 5f, point.y - 5f, 10f, 10f), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        private static void DrawRotationCenter(Vector2 point)
        {
            var previous = Handles.color;
            Handles.color = Color.cyan;
            Handles.DrawAAPolyLine(2f, point + new Vector2(-8f, 0f), point + new Vector2(8f, 0f));
            Handles.DrawAAPolyLine(2f, point + new Vector2(0f, -8f), point + new Vector2(0f, 8f));
            Handles.color = previous;
        }

        private static Rect FitRect(Rect rect, float aspect)
        {
            if (aspect <= 0f || rect.height <= 0f) return rect;
            var size = rect.width / rect.height > aspect
                ? new Vector2(rect.height * aspect, rect.height)
                : new Vector2(rect.width, rect.width / aspect);
            return new Rect(rect.center - size * .5f, size);
        }

        private static Vector2 ClockToPreview(Vector2 point, Vector2 center, float scale) =>
            center + new Vector2(point.x, -point.y) * scale;

        /// <summary>Mirror of <see cref="RecoilClockImagePreview.FindParentIndex"/> for serialized data.</summary>
        private static int FindParentIndex(SerializedProperty layers, int index)
        {
            if (index < 0 || index >= layers.arraySize) return -1;
            var label = layers.GetArrayElementAtIndex(index).FindPropertyRelative("attachTo").stringValue;
            if (string.IsNullOrEmpty(label)) return -1;
            for (var i = 0; i < layers.arraySize; i++)
            {
                if (i == index) continue;
                var candidate = layers.GetArrayElementAtIndex(i);
                if (candidate.FindPropertyRelative("label").stringValue == label &&
                    string.IsNullOrEmpty(candidate.FindPropertyRelative("attachTo").stringValue))
                    return i;
            }
            return -1;
        }

        private static List<int> GetChildren(SerializedProperty layers, int parentIndex) =>
            Enumerable.Range(0, layers.arraySize).Where(i => FindParentIndex(layers, i) == parentIndex).ToList();

        /// <summary>Layers that are not attached to a ring, back to front. Only these carry an order number.</summary>
        private static int[] GetTopLevelOrder(SerializedProperty layers)
        {
            var count = layers?.arraySize ?? 0;
            return Enumerable.Range(0, count)
                .Where(i => FindParentIndex(layers, i) < 0)
                .OrderBy(i => layers.GetArrayElementAtIndex(i).FindPropertyRelative("order").intValue)
                .ThenBy(i => i)
                .ToArray();
        }

        /// <summary>Full back-to-front draw order: each top-level layer followed by the layers attached to it.</summary>
        private static int[] GetDrawOrder(SerializedProperty layers)
        {
            var result = new List<int>(layers?.arraySize ?? 0);
            foreach (var top in GetTopLevelOrder(layers))
            {
                result.Add(top);
                result.AddRange(GetChildren(layers, top));
            }
            return result.ToArray();
        }

        private static bool HasSequentialOrder(SerializedProperty layers)
        {
            var topLevel = GetTopLevelOrder(layers);
            var used = new bool[topLevel.Length + 1];
            foreach (var i in topLevel)
            {
                var order = layers.GetArrayElementAtIndex(i).FindPropertyRelative("order").intValue;
                if (order < 1 || order > topLevel.Length || used[order]) return false;
                used[order] = true;
            }
            return true;
        }

        private static void RenumberLayers(SerializedProperty layers, IReadOnlyList<int> drawOrder)
        {
            for (var rank = 0; rank < drawOrder.Count; rank++)
                layers.GetArrayElementAtIndex(drawOrder[rank]).FindPropertyRelative("order").intValue = rank + 1;
        }

        private static void MoveLayerToOrder(SerializedProperty layers, int layerIndex, int newOrder)
        {
            var drawOrder = GetTopLevelOrder(layers).Where(i => i != layerIndex).ToList();
            drawOrder.Insert(Mathf.Clamp(newOrder - 1, 0, drawOrder.Count), layerIndex);
            RenumberLayers(layers, drawOrder);
        }

        private static void DrawOrderField(SerializedProperty layers, int layerIndex, GUIContent label,
            params GUILayoutOption[] options)
        {
            var order = layers.GetArrayElementAtIndex(layerIndex).FindPropertyRelative("order");
            var newOrder = label == null
                ? EditorGUILayout.DelayedIntField(order.intValue, options)
                : EditorGUILayout.DelayedIntField(label, order.intValue, options);
            if (newOrder != order.intValue)
                MoveLayerToOrder(layers, layerIndex, newOrder);
        }

        private int FindTopLayerAt(SerializedProperty layers, Vector2 point, Vector2 rotationCenter, Vector2 center,
            float scale)
        {
            var drawOrder = GetDrawOrder(layers);
            for (var rank = drawOrder.Length - 1; rank >= 0; rank--)
            {
                var i = drawOrder[rank];
                if (IsDrawn(layers, i) && GetGeometry(layers, i, rotationCenter, center, scale).Contains(point))
                    return i;
            }
            return -1;
        }

        private static Vector2 PreviewToClockPosition(Vector2 point, Vector2 center, float scale) =>
            new((point.x - center.x) / scale, (center.y - point.y) / scale);

        private void SaveLayoutJson()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Lưu thuộc tính đồng hồ",
                "clock-layout",
                "json",
                "Chọn vị trí lưu cấu hình đồng hồ dạng JSON.",
                "Assets/Data");
            if (string.IsNullOrEmpty(path))
                return;

            var layers = _preview.Layers ?? Array.Empty<RecoilClockImageLayer>();
            var rotationCenter = _preview.RotationCenter;
            var data = new ClockLayoutData
            {
                rootPosition = _preview.RootPosition,
                rootScale = _preview.RootScale,
                rotationCenter = rotationCenter,
                layers = new ClockLayerData[layers.Length]
            };
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                data.layers[i] = new ClockLayerData
                {
                    label = layer.label,
                    spriteAssetPath = layer.sprite != null ? AssetDatabase.GetAssetPath(layer.sprite) : string.Empty,
                    spriteName = layer.sprite != null ? layer.sprite.name : string.Empty,
                    visible = layer.visible,
                    order = layer.order,
                    size = layer.size,
                    scale = layer.scale,
                    position = layer.position,
                    pivot = RecoilClockImagePreview.FindParentIndex(layers, i) >= 0
                        ? new Vector2(.5f, .5f)
                        : RecoilClockImagePreview.GetRotationPivot(layer, rotationCenter),
                    rotation = layer.rotation,
                    attachTo = layer.attachTo,
                    keepUpright = layer.keepUpright
                };
            }

            File.WriteAllText(path, JsonUtility.ToJson(data, true));
            AssetDatabase.Refresh();
            _status = "Đã lưu thuộc tính đồng hồ: " + path;
        }

        private const string ClankSoundPath = "Assets/_Game/sound/clank1.wav";

        /// <summary>Gives the dial the clock's clank sound if it has none yet (it can still be swapped in the Inspector).</summary>
        internal static bool EnsureDialSound(RecoilClockDial dial)
        {
            if (dial == null) return false;
            var serialized = new SerializedObject(dial);
            var clip = serialized.FindProperty("clankSound");
            if (clip == null || clip.objectReferenceValue != null) return false;
            var sound = AssetDatabase.LoadAssetAtPath<AudioClip>(ClankSoundPath);
            if (sound == null) return false;
            clip.objectReferenceValue = sound;
            serialized.ApplyModifiedProperties();
            return true;
        }

        private const string LobbyScenePath = "Assets/Scenes/new.unity";
        internal const string ClockPrefabPath = ClockAssetFolder + "/Đồng hồ.prefab";
        // Default spot in the lobby: middle of the free area left of the title, between title and buttons.
        private static readonly Vector2 LobbyClockAnchor = new(.17f, .5f);
        private static readonly Vector2 LobbyClockOffset = new(0f, -20f);
        private const float LobbyClockScale = .55f;

        [MenuItem("Game Tools/Công Thành/Clock/Thêm đồng hồ vào sảnh (Recoil battle demo)")]
        private static void AddClockToLobbyMenu()
        {
            try
            {
                var message = AddClockToLobby();
                Debug.Log("[Đồng hồ ở sảnh] " + message);
                EditorUtility.DisplayDialog("Đồng hồ ở sảnh", message, "OK");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Đồng hồ ở sảnh", exception.Message, "OK");
            }
        }

        /// <summary>
        /// Saves the clock tuned in the test scene as a prefab, then puts it on the Recoil battle demo's home screen:
        /// a canvas with the lobby background (which OnGUI stops drawing while this canvas is up) and the clock on
        /// top, shown by the demo only while the lobby is. Running it again refreshes the prefab and keeps the
        /// clock where it was placed in the lobby.
        /// </summary>
        private static string AddClockToLobby()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Thoát Play Mode trước.");
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new OperationCanceledException();

            // 1. Prefab from the clock in the test scene: layout, cut-out numbers and dial settings included.
            var testScene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            var source = FindPreview(testScene);
            if (source == null || source.Layers == null || source.Layers.Length == 0)
                throw new InvalidOperationException(
                    "Chưa có đồng hồ trong scene test: mở tool \"Chỉnh hình đồng hồ\" và bấm \"Mở scene test và tạo/cập nhật đồng hồ\" trước.");
            if (source.GetComponent<RecoilClockDial>() == null)
            {
                source.gameObject.AddComponent<RecoilClockDial>();
                EditorSceneManager.MarkSceneDirty(testScene);
                EditorSceneManager.SaveScene(testScene);
            }
            // Bring the test-scene clock up to date first (new default layers such as the TIME button, cut-out
            // numbers, the clank sound), so saving it as the prefab never drops them from the lobby.
            var addedLayers = AddMissingDefaultLayers(source);
            var ringNotes = SetupRingNumbers(source);
            var soundAdded = EnsureDialSound(source.GetComponent<RecoilClockDial>());
            if (addedLayers.Count > 0 || ringNotes.Count > 0)
                source.RebuildImages();
            if (addedLayers.Count > 0 || ringNotes.Count > 0 || soundAdded)
            {
                EditorSceneManager.MarkSceneDirty(testScene);
                EditorSceneManager.SaveScene(testScene);
            }
            var prefab = PrefabUtility.SaveAsPrefabAsset(source.gameObject, ClockPrefabPath, out var saved);
            if (!saved || prefab == null)
                throw new InvalidOperationException("Không lưu được prefab: " + ClockPrefabPath);

            // 2. Lobby scene.
            var lobbyScene = EditorSceneManager.OpenScene(LobbyScenePath, OpenSceneMode.Single);
            RecoilAutoBattleDemo demo = null;
            foreach (var root in lobbyScene.GetRootGameObjects())
            {
                demo = root.GetComponentInChildren<RecoilAutoBattleDemo>(true);
                if (demo != null) break;
            }
            if (demo == null)
                throw new InvalidOperationException("Không tìm thấy RecoilAutoBattleDemo trong " + LobbyScenePath);
            var demoObject = new SerializedObject(demo);
            var canvasProperty = demoObject.FindProperty("lobbyClockCanvas");
            if (canvasProperty == null)
                throw new InvalidOperationException("RecoilAutoBattleDemo chưa có ô \"lobbyClockCanvas\": chờ Unity biên dịch xong rồi chạy lại.");

            var notes = new List<string> { "Đã lưu prefab " + ClockPrefabPath + " từ scene test." };
            var canvas = canvasProperty.objectReferenceValue as Canvas;
            if (canvas == null)
            {
                var canvasObject = new GameObject("Lobby Clock Canvas", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(canvasObject, lobbyScene);
                canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 0;
                // No CanvasScaler: the demo sets scaleFactor to the IMGUI lobby's own scale every frame.

                var background = demoObject.FindProperty("lobbyBackgroundImage")?.objectReferenceValue as Texture2D;
                var backgroundObject = new GameObject("Lobby Background", typeof(RectTransform), typeof(CanvasRenderer),
                    typeof(RawImage), typeof(AspectRatioFitter));
                backgroundObject.transform.SetParent(canvasObject.transform, false);
                var raw = backgroundObject.GetComponent<RawImage>();
                raw.raycastTarget = false;
                raw.texture = background;
                if (background == null) raw.color = new Color(.025f, .04f, .07f, .92f); // same fallback as OnGUI
                var fitter = backgroundObject.GetComponent<AspectRatioFitter>();
                fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; // fills and crops, like ScaleAndCrop
                fitter.aspectRatio = background != null && background.height > 0
                    ? (float)background.width / background.height
                    : 16f / 9f;

                Undo.RegisterCreatedObjectUndo(canvasObject, "Add lobby clock");
                canvasProperty.objectReferenceValue = canvas;
                demoObject.ApplyModifiedProperties();
                notes.Add("Đã tạo \"Lobby Clock Canvas\" (nền sảnh + đồng hồ) và gắn vào RecoilAutoBattleDemo.");
            }

            if (canvas.GetComponentInChildren<RecoilClockImagePreview>(true) == null)
            {
                var holder = new GameObject("Lobby Clock", typeof(RectTransform));
                holder.transform.SetParent(canvas.transform, false);
                var holderRect = (RectTransform)holder.transform;
                holderRect.anchorMin = holderRect.anchorMax = LobbyClockAnchor;
                holderRect.pivot = new Vector2(.5f, .5f);
                holderRect.sizeDelta = Vector2.zero;
                holderRect.anchoredPosition = LobbyClockOffset;
                holderRect.localScale = Vector3.one * LobbyClockScale;

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, lobbyScene);
                instance.transform.SetParent(holder.transform, false);
                var clock = instance.GetComponent<RecoilClockImagePreview>();
                var clockObject = new SerializedObject(clock);
                clockObject.FindProperty("rootPosition").vector2Value = Vector2.zero; // placed by the holder instead
                clockObject.FindProperty("rootScale").floatValue = 1f;
                clockObject.ApplyModifiedProperties();
                clock.ApplyRootLayout();
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                Undo.RegisterCreatedObjectUndo(holder, "Add lobby clock");
                notes.Add("Đã đặt đồng hồ bên trái sảnh. Muốn dời / đổi cỡ: chỉnh Anchors, Pos, Scale của \"Lobby Clock\".");
            }
            else
            {
                notes.Add("Đồng hồ ở sảnh đã có: cập nhật theo prefab mới, giữ nguyên chỗ đặt.");
            }

            EditorSceneManager.MarkSceneDirty(lobbyScene);
            if (!EditorSceneManager.SaveScene(lobbyScene))
                throw new InvalidOperationException("Không lưu được " + LobbyScenePath);
            Selection.activeGameObject = canvas.gameObject;
            notes.Add("Đã lưu " + LobbyScenePath + ". Nhấn Play: bấm mặt đồng hồ ở sảnh để mở vòng.");
            return string.Join("\n", notes);
        }

        private void BuildTestPreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Thoát Play Mode trước khi tạo preview.");

            var scene = SceneManager.GetActiveScene();
            if (scene.path != TestScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    throw new OperationCanceledException("Mở scene test đã bị hủy.");
                scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            }

            _preview = FindPreview(scene);
            if (_preview == null)
            {
                var canvasObject = new GameObject("Clock Image Test Canvas",
                    typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                SceneManager.MoveGameObjectToScene(canvasObject, scene);
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvasObject.GetComponent<GraphicRaycaster>().enabled = false;

                var scaler = canvasObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = .5f;

                var rootObject = new GameObject("Clock Image Preview",
                    typeof(RectTransform), typeof(RecoilClockImagePreview));
                rootObject.transform.SetParent(canvasObject.transform, false);
                var root = (RectTransform)rootObject.transform;
                root.anchorMin = root.anchorMax = new Vector2(.5f, .5f);
                root.pivot = new Vector2(.5f, .5f);
                root.sizeDelta = new Vector2(ClockSize, ClockSize);

                _preview = rootObject.GetComponent<RecoilClockImagePreview>();
                _preview.Configure(root, LoadDefaultLayers());
                Undo.RegisterCreatedObjectUndo(canvasObject, "Create clock image test");
            }
            else if (_preview.Layers == null || _preview.Layers.Length == 0)
            {
                _preview.Configure(_preview.ClockRoot, LoadDefaultLayers());
            }

            var added = new List<string>();
            if (_preview.Layers != null && _preview.Layers.Length > 0)
            {
                Undo.RecordObject(_preview, "Update Clock Layers");
                _preview.UpgradeLayout();
                added = AddMissingDefaultLayers(_preview);
            }
            var numberNotes = SetupRingNumbers(_preview);
            if (_preview.GetComponent<RecoilClockDial>() == null)
            {
                Undo.AddComponent<RecoilClockDial>(_preview.gameObject);
                numberNotes.Add("Đã gắn script RecoilClockDial: nhấn Play rồi bấm/kéo vòng giờ, vòng phút.");
            }
            if (EnsureDialSound(_preview.GetComponent<RecoilClockDial>()))
                numberNotes.Add("Đã gắn âm thanh " + ClankSoundPath + " (mở / đóng và bánh răng quay).");

            _preview.RebuildImages();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException("Không lưu được scene test.");
            SceneManager.SetActiveScene(scene);
            var notes = new List<string>();
            if (added.Count > 0) notes.Add("Đã thêm lớp mới: " + string.Join(", ", added) + ".");
            notes.AddRange(numberNotes);
            _status = notes.Count > 0
                ? string.Join("\n", notes) + "\nĐã lưu scene test."
                : "Đã tạo đồng hồ trong scene test. Nhấn Play để xem ở Game view.";
            _preview = FindPreview(scene);
            Selection.activeGameObject = _preview != null ? _preview.gameObject : null;
        }

        // TIME button: the round clock icon sits at (21.57%, 56.2%) of timeBtn.png (2097 x 750), from its top-left.
        internal const string TimeButtonLabel = "Nút Time";
        private static readonly Vector2 TimeButtonSize = new(640f, 640f * 750f / 2097f);
        internal static readonly Vector2 TimeButtonIcon = new(.2157f, .562f);

        internal static RecoilClockImageLayer[] LoadDefaultLayers()
        {
            var layers = new List<RecoilClockImageLayer>
            {
                // Under everything, placed so its round icon sits on the face center: closed, the clock shrinks onto
                // that icon (RecoilClockDial) and grows back out of it when opened.
                MakeLayer(TimeButtonLabel, "timeBtn.png", TimeButtonSize,
                    position: new Vector2((.5f - TimeButtonIcon.x) * TimeButtonSize.x, (TimeButtonIcon.y - .5f) * TimeButtonSize.y)),
                MakeLayer("Bánh răng ngoài", "bánh răng ngoài.png", new Vector2(ClockSize, ClockSize)),
                MakeLayer(RecoilClockImagePreview.FaceLayerLabel, "mặt đồng hồ.png", new Vector2(960f, ClockSize)),
                MakeLayer("Vòng giờ", "giờ.png", new Vector2(960f, ClockSize)),
                MakeLayer("Vòng phút", "phút.png", new Vector2(800f, ClockSize * 5f / 6f)),
                // Inner gears: centered on the axle, above every dial and below both hands.
                MakeLayer("Bánh răng trong lớn", "bánh răng ngoài.png", new Vector2(ClockSize, ClockSize), scale: .3f),
                MakeLayer("Bánh răng trong nhỏ", "bánh răng ngoài.png", new Vector2(ClockSize, ClockSize), scale: .2f),
                // Hands are offset so their axle (32% / 18% from the left edge) sits on the face center.
                MakeLayer("Kim giờ", "kim giờ.png", new Vector2(420f, 210f), position: new Vector2((.5f - .32f) * 420f, 0f)),
                MakeLayer("Kim phút", "kim phút.png", new Vector2(520f, 260f), position: new Vector2((.5f - .18f) * 520f, 0f)),
                MakeLayer("Chỉ báo", "index.png", new Vector2(480f, 320f), position: new Vector2(0f, 252f)),
                MakeLayer("Vô hạn thời gian", "vô hạn thời gian.png", new Vector2(560f, 373f), visible: false)
            };
            for (var i = 0; i < layers.Count; i++)
                layers[i].order = i + 1;
            return layers.ToArray();
        }

        /// <summary>
        /// Adds default layers the preview does not have yet (matched by label). Each one goes just below the
        /// next default layer that already exists, so it lands where the default stack places it.
        /// </summary>
        internal static List<string> AddMissingDefaultLayers(RecoilClockImagePreview preview)
        {
            var defaults = LoadDefaultLayers();
            var serialized = new SerializedObject(preview);
            var layers = serialized.FindProperty("layers");
            if (!HasSequentialOrder(layers))
                RenumberLayers(layers, GetTopLevelOrder(layers));

            var rotationCenter = GetRotationCenter(layers);
            var added = new List<string>();
            for (var d = 0; d < defaults.Length; d++)
            {
                var source = defaults[d];
                if (FindLayerIndex(layers, source.label) >= 0) continue;

                layers.arraySize++;
                var newIndex = layers.arraySize - 1;
                var layer = layers.GetArrayElementAtIndex(newIndex);
                layer.FindPropertyRelative("label").stringValue = source.label;
                layer.FindPropertyRelative("sprite").objectReferenceValue = source.sprite;
                layer.FindPropertyRelative("visible").boolValue = source.visible;
                layer.FindPropertyRelative("size").vector2Value = source.size;
                layer.FindPropertyRelative("scale").floatValue = source.scale;
                // Defaults assume the face sits at the origin; follow the face if it has been moved.
                layer.FindPropertyRelative("position").vector2Value = rotationCenter + source.position;
                layer.FindPropertyRelative("rotation").floatValue = source.rotation;
                layer.FindPropertyRelative("pivot").vector2Value = new Vector2(.5f, .5f);
                // arraySize++ copies the last element, which may be an attached ring number.
                layer.FindPropertyRelative("attachTo").stringValue = string.Empty;
                layer.FindPropertyRelative("keepUpright").boolValue = false;

                var above = -1;
                for (var next = d + 1; next < defaults.Length && above < 0; next++)
                    above = FindLayerIndex(layers, defaults[next].label);
                var targetOrder = above >= 0
                    ? layers.GetArrayElementAtIndex(above).FindPropertyRelative("order").intValue
                    : layers.arraySize;
                MoveLayerToOrder(layers, newIndex, targetOrder);
                added.Add(source.label);
            }

            serialized.ApplyModifiedProperties();
            return added;
        }

        /// <summary>
        /// For each ring whose numbers have been cut out: swap the ring to its number-free image and add the
        /// numbers as upright layers attached to it, placed exactly where they were on the original image.
        /// Rings showing some other image are left alone; existing number layers are kept as edited.
        /// </summary>
        private static List<string> SetupRingNumbers(RecoilClockImagePreview preview)
        {
            var notes = new List<string>();
            var serialized = new SerializedObject(preview);
            var layers = serialized.FindProperty("layers");
            foreach (var set in RingNumberSets)
            {
                var ringIndex = FindLayerIndex(layers, set.ringLabel);
                if (ringIndex < 0) continue;
                var manifestPath = ClockAssetFolder + "/" + set.numbersFolder + "/numbers.json";
                if (!File.Exists(manifestPath)) continue;

                var ring = layers.GetArrayElementAtIndex(ringIndex);
                var ringSprite = ring.FindPropertyRelative("sprite");
                var ringPath = ringSprite.objectReferenceValue != null
                    ? AssetDatabase.GetAssetPath(ringSprite.objectReferenceValue)
                    : string.Empty;
                var originalPath = ClockAssetFolder + "/" + set.originalFile;
                var cleanPath = ClockAssetFolder + "/" + set.cleanFile;
                if (ringPath != originalPath && ringPath != cleanPath)
                {
                    notes.Add("Bỏ qua tách số cho \"" + set.ringLabel + "\": lớp đang dùng hình khác (" + ringPath + ").");
                    continue;
                }
                if (ringPath == originalPath)
                    ringSprite.objectReferenceValue = EnsureSprite(cleanPath);

                var manifest = JsonUtility.FromJson<RingNumberManifest>(File.ReadAllText(manifestPath));
                var imageSize = new Vector2(manifest.imageSize[0], manifest.imageSize[1]);
                // The ring sprite (full texture) is drawn with preserveAspect, so it is fitted inside the layer size.
                var ringSize = ring.FindPropertyRelative("size").vector2Value;
                var fit = Mathf.Min(ringSize.x / imageSize.x, ringSize.y / imageSize.y);
                var addedCount = 0;
                foreach (var entry in manifest.numbers)
                {
                    var label = set.ringLabel + RingNumberSeparator + entry.label;
                    if (FindLayerIndex(layers, label) >= 0) continue;
                    layers.arraySize++;
                    var layer = layers.GetArrayElementAtIndex(layers.arraySize - 1);
                    layer.FindPropertyRelative("label").stringValue = label;
                    layer.FindPropertyRelative("sprite").objectReferenceValue =
                        EnsureSprite(ClockAssetFolder + "/" + set.numbersFolder + "/" + entry.file);
                    layer.FindPropertyRelative("visible").boolValue = true;
                    layer.FindPropertyRelative("order").intValue = 0;
                    layer.FindPropertyRelative("size").vector2Value = new Vector2(entry.size[0], entry.size[1]) * fit;
                    layer.FindPropertyRelative("scale").floatValue = 1f;
                    layer.FindPropertyRelative("position").vector2Value = new Vector2(
                        (entry.center[0] - imageSize.x * .5f) * fit,
                        (imageSize.y * .5f - entry.center[1]) * fit);
                    layer.FindPropertyRelative("rotation").floatValue = 0f;
                    layer.FindPropertyRelative("pivot").vector2Value = new Vector2(.5f, .5f);
                    layer.FindPropertyRelative("attachTo").stringValue = set.ringLabel;
                    layer.FindPropertyRelative("keepUpright").boolValue = true;
                    addedCount++;
                }
                if (ringPath == originalPath || addedCount > 0)
                    notes.Add("Đã tách " + manifest.numbers.Length + " số khỏi \"" + set.ringLabel +
                              "\": vòng dùng hình không số, số luôn đứng thẳng khi vòng xoay.");
            }
            serialized.ApplyModifiedProperties();
            return notes;
        }

        /// <summary>Loads a sprite, first switching the texture to a single UI sprite if it was imported otherwise.</summary>
        private static Sprite EnsureSprite(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter))
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                throw new InvalidOperationException("Không tìm thấy hình: " + path);
            if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
                throw new InvalidOperationException("Không tải được Sprite: " + path);
            return sprite;
        }

        private static int FindLayerIndex(SerializedProperty layers, string label)
        {
            for (var i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).FindPropertyRelative("label").stringValue == label)
                    return i;
            return -1;
        }

        private static RecoilClockImageLayer MakeLayer(string label, string fileName, Vector2 size,
            Vector2 position = default, bool visible = true, float scale = 1f)
        {
            var path = ClockAssetFolder + "/" + fileName;
            // Existing clock images are already sprites (keep their import as is); a new plain texture such as
            // timeBtn.png is switched to a UI sprite first.
            var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault() ?? EnsureSprite(path);
            if (sprite == null)
                throw new InvalidOperationException("Không tìm thấy Sprite: " + path);

            return new RecoilClockImageLayer
            {
                label = label,
                sprite = sprite,
                visible = visible,
                size = size,
                scale = scale,
                position = position
            };
        }

        internal static RecoilClockImagePreview FindPreview(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            foreach (var preview in Resources.FindObjectsOfTypeAll<RecoilClockImagePreview>())
                if (preview.gameObject.scene == scene && !EditorUtility.IsPersistent(preview))
                    return preview;
            return null;
        }

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
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }
    }
}
