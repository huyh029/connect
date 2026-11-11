using System;
using UnityEngine;
using UnityEngine.UI;

namespace RecoilPathfinding
{
    [Serializable]
    public sealed class RecoilClockImageLayer
    {
        public string label;
        public Sprite sprite;
        public bool visible = true;
        [Tooltip("Thứ tự trước/sau: 1 nằm dưới cùng, số lớn hơn nằm trên. Trùng số thì lớp đứng sau trong danh sách nằm trên.")]
        public int order;
        public Vector2 size = new(640f, 640f);
        public float scale = 1f;
        [Tooltip("Tâm của lớp hình khi scale = 1 và chưa xoay. Scale và xoay đều tính từ tâm mặt đồng hồ.")]
        public Vector2 position;
        [Tooltip("Xoay quanh tâm mặt đồng hồ.")]
        public float rotation;
        [Tooltip("Tên lớp vòng mà lớp này gắn vào (ví dụ số trên vòng giờ). Khi gắn: vị trí là độ lệch so với tâm vòng, " +
                 "đi theo scale và góc xoay của vòng, và được vẽ ngay trên vòng.")]
        public string attachTo;
        [Tooltip("Khi gắn vào vòng: luôn đứng thẳng, không xoay theo vòng.")]
        public bool keepUpright;

        // Legacy (layout version 0): position was the rotation pivot point and this was the
        // normalized pivot. Kept only so old scenes can be converted; no longer edited or used.
        [HideInInspector] public Vector2 pivot = new(.5f, .5f);
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RecoilClockImagePreview : MonoBehaviour
    {
        /// <summary>Every layer rotates around the center of the layer with this label.</summary>
        public const string FaceLayerLabel = "Mặt đồng hồ";
        /// <summary>Ring numbers are labelled "&lt;ring label&gt;&lt;separator&gt;&lt;number&gt;", e.g. "Vòng giờ · 3".</summary>
        public const string NumberLabelSeparator = " · ";
        private const int CurrentLayoutVersion = 1;

        [SerializeField] private RectTransform clockRoot;
        [SerializeField] private RecoilClockImageLayer[] layers = Array.Empty<RecoilClockImageLayer>();
        [SerializeField] private Vector2 rootPosition;
        [SerializeField] private float rootScale = 1f;
        // No initializer on purpose: scenes saved before this field existed load as 0 (legacy).
        [SerializeField, HideInInspector] private int layoutVersion;

        public RectTransform ClockRoot => clockRoot;
        public RecoilClockImageLayer[] Layers => layers;
        public Vector2 RootPosition => rootPosition;
        public float RootScale => rootScale;
        public bool NeedsLayoutUpgrade => layoutVersion < CurrentLayoutVersion;
        public Vector2 RotationCenter => GetRotationCenter(layers);

        public void Configure(RectTransform root, RecoilClockImageLayer[] clockLayers)
        {
            clockRoot = root;
            layers = clockLayers ?? Array.Empty<RecoilClockImageLayer>();
            layoutVersion = CurrentLayoutVersion;
            if (clockRoot != null)
            {
                rootPosition = clockRoot.anchoredPosition;
                rootScale = clockRoot.localScale.x;
            }
        }

        /// <summary>
        /// Converts legacy layers (position = own pivot point) to the current layout without moving anything on
        /// screen. Current layout: a layer is drawn centered at faceCenter + (position - faceCenter) * scale,
        /// then rotated around faceCenter, so both scale and rotation are measured from the clock face center.
        /// </summary>
        public bool UpgradeLayout()
        {
            if (!NeedsLayoutUpgrade)
                return false;

            var count = layers?.Length ?? 0;
            var renderedCenters = new Vector2[count];
            var faceCenter = Vector2.zero;
            for (var i = 0; i < count; i++)
            {
                var layer = layers[i];
                if (layer == null) continue;
                var size = layer.size * layer.scale;
                renderedCenters[i] = layer.position +
                                     Rotate(Vector2.Scale(new Vector2(.5f, .5f) - layer.pivot, size), layer.rotation);
                if (layer.label == FaceLayerLabel)
                    faceCenter = renderedCenters[i];
            }

            for (var i = 0; i < count; i++)
            {
                var layer = layers[i];
                if (layer == null) continue;
                var scale = layer.scale > 0f ? layer.scale : 1f;
                layer.position = faceCenter + Rotate(renderedCenters[i] - faceCenter, -layer.rotation) / scale;
                layer.pivot = new Vector2(.5f, .5f);
            }

            layoutVersion = CurrentLayoutVersion;
            return true;
        }

        public void ApplyRootLayout()
        {
            if (clockRoot == null)
                throw new InvalidOperationException("Clock preview root is not assigned.");

            clockRoot.anchorMin = clockRoot.anchorMax = new Vector2(.5f, .5f);
            clockRoot.pivot = new Vector2(.5f, .5f);
            clockRoot.anchoredPosition = rootPosition;
            clockRoot.sizeDelta = Vector2.one * 640f;
            clockRoot.localScale = Vector3.one * rootScale;
        }

        public void RebuildImages()
        {
            UpgradeLayout();
            ApplyRootLayout();

            for (var i = clockRoot.childCount - 1; i >= 0; i--)
            {
                var child = clockRoot.GetChild(i).gameObject;
                if (Application.isPlaying) Destroy(child);
                else DestroyImmediate(child);
            }

            var rotationCenter = RotationCenter;
            foreach (var index in GetDrawOrder(layers))
            {
                var layer = layers[index];
                if (layer == null || !layer.visible || layer.sprite == null)
                    continue;
                var parentIndex = FindParentIndex(layers, index);
                var parent = parentIndex >= 0 ? layers[parentIndex] : null;
                if (parent != null && !parent.visible)
                    continue;

                var imageObject = new GameObject(layer.label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                imageObject.transform.SetParent(clockRoot, false);
                var rect = (RectTransform)imageObject.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                if (parent == null)
                {
                    // Pivot = clock face center projected onto this layer, so both scale and rotation
                    // happen around the face center.
                    rect.pivot = GetRotationPivot(layer, rotationCenter);
                    rect.sizeDelta = layer.size * layer.scale;
                    rect.anchoredPosition = rotationCenter;
                    rect.localRotation = Quaternion.Euler(0f, 0f, layer.rotation);
                }
                else
                {
                    // Rides on its ring: placed by the ring's scale/rotation, turned only by its own rotation
                    // when it keeps upright.
                    rect.pivot = new Vector2(.5f, .5f);
                    rect.sizeDelta = layer.size * (layer.scale * parent.scale);
                    rect.anchoredPosition = GetAttachedCenter(parent, layer.position, parent.rotation, rotationCenter);
                    rect.localRotation = Quaternion.Euler(0f, 0f,
                        layer.rotation + (layer.keepUpright ? 0f : parent.rotation));
                }

                var image = imageObject.GetComponent<Image>();
                image.sprite = layer.sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
        }

        /// <summary>Center of the clock face layer; the clock root center if there is no face layer.</summary>
        public static Vector2 GetRotationCenter(RecoilClockImageLayer[] clockLayers)
        {
            if (clockLayers != null)
                foreach (var layer in clockLayers)
                    if (layer != null && layer.label == FaceLayerLabel)
                        return layer.position;
            return Vector2.zero;
        }

        /// <summary>
        /// Normalized pivot of <paramref name="layer"/> that lands on <paramref name="rotationCenter"/>.
        /// Independent of scale, because the layer scales around that same point.
        /// </summary>
        public static Vector2 GetRotationPivot(RecoilClockImageLayer layer, Vector2 rotationCenter)
        {
            var size = layer.size;
            return new Vector2(
                size.x > 0f ? .5f + (rotationCenter.x - layer.position.x) / size.x : .5f,
                size.y > 0f ? .5f + (rotationCenter.y - layer.position.y) / size.y : .5f);
        }

        /// <summary>Counter-clockwise rotation in clock space (y up), matching a RectTransform's z rotation.</summary>
        public static Vector2 Rotate(Vector2 vector, float degrees)
        {
            var radians = degrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
        }

        /// <summary>
        /// Center of a layer attached to <paramref name="parent"/>: <paramref name="offset"/> is measured from the
        /// ring's own center in the ring's unscaled units, then scaled and rotated around the clock face center.
        /// </summary>
        public static Vector2 GetAttachedCenter(RecoilClockImageLayer parent, Vector2 offset, float parentRotation,
            Vector2 rotationCenter) =>
            rotationCenter + Rotate((parent.position + offset - rotationCenter) * parent.scale, parentRotation);

        /// <summary>
        /// Index of the ring layer that layer <paramref name="index"/> is attached to, or -1.
        /// Only one level: a ring must not itself be attached.
        /// </summary>
        public static int FindParentIndex(RecoilClockImageLayer[] clockLayers, int index)
        {
            var label = clockLayers?[index]?.attachTo;
            if (string.IsNullOrEmpty(label)) return -1;
            for (var i = 0; i < clockLayers.Length; i++)
            {
                var candidate = clockLayers[i];
                if (i != index && candidate != null && candidate.label == label && string.IsNullOrEmpty(candidate.attachTo))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Back-to-front draw order: top-level layers by <see cref="RecoilClockImageLayer.order"/> (ties kept in list
        /// order), each followed directly by the layers attached to it.
        /// </summary>
        public static int[] GetDrawOrder(RecoilClockImageLayer[] clockLayers)
        {
            var count = clockLayers?.Length ?? 0;
            var parents = new int[count];
            var topLevel = new System.Collections.Generic.List<int>(count);
            for (var i = 0; i < count; i++)
            {
                parents[i] = FindParentIndex(clockLayers, i);
                if (parents[i] < 0) topLevel.Add(i);
            }
            topLevel.Sort((a, b) =>
            {
                var compare = OrderOf(clockLayers[a]).CompareTo(OrderOf(clockLayers[b]));
                return compare != 0 ? compare : a.CompareTo(b);
            });
            var result = new int[count];
            var next = 0;
            foreach (var top in topLevel)
            {
                result[next++] = top;
                for (var i = 0; i < count; i++)
                    if (parents[i] == top) result[next++] = i;
            }
            return result;
        }

        private static int OrderOf(RecoilClockImageLayer layer) => layer?.order ?? 0;
    }
}
