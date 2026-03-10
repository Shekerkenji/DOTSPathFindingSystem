using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// ?????????????????????????????????????????????????????????????????????????????
//  BoxSelectAction.cs  —  namespace PlayerActions
//
//  Mobile double-tap + drag ? box (marquee) multi-unit selection.
//
//  GESTURE FLOW
//  ????????????
//  Tap 1 down  ? record time & position
//  Tap 1 up    ? finger lifted
//  Tap 2 down  ? if within DoubleTapWindow seconds AND DoubleTapRadius pixels
//                of tap 1 ? enter PENDING_BOX state
//  Tap 2 held + moved beyond DragThreshold ? enter DRAGGING state
//  Tap 2 up while DRAGGING ? commit box selection
//  Tap 2 up without enough drag ? treat as normal double-tap (no box)
//
//  While DRAGGING:
//    • input.ClickConsumedBySelection = true every frame
//      ? prevents MoveCommandSystem and camera pan from firing
//    • BoxSelectSingleton.IsDragging = true
//      ? BoxSelectOverlay MonoBehaviour draws the rectangle
//
//  Shift held on release ? additive selection, else replace.
//
//  NOTES
//  ?????
//  • Uses Input.touches for mobile; falls back to mouse for Editor testing.
//  • Single-tap-drag camera panning is handled by your camera system reading
//    Input.touches directly — it should skip panning when
//    BoxSelectSingleton.IsDragging == true.
// ?????????????????????????????????????????????????????????????????????????????

namespace PlayerActions
{
    // ?? Drag state singleton ??????????????????????????????????????????????????

    /// <summary>
    /// Tracks double-tap + drag box selection state.
    /// Read by BoxSelectOverlay (MonoBehaviour) to draw the rect.
    /// Read by your camera pan system to suppress panning while dragging.
    /// </summary>
    public class BoxSelectSingleton : IComponentData
    {
        /// <summary>True while the player is performing a double-tap drag.</summary>
        public bool IsDragging;

        /// <summary>Screen position where the second tap began.</summary>
        public Vector2 DragStart;

        /// <summary>Current finger/mouse screen position during drag.</summary>
        public Vector2 DragCurrent;

        // ?? Internal double-tap detection state ???????????????????????????????

        internal float FirstTapTime = -999f;
        internal Vector2 FirstTapPos = Vector2.zero;
        internal bool WaitingSecondTap = false;
        internal bool PendingBox = false;   // second tap down, not yet dragged

        /// <summary>
        /// Screen-space Rect of the drag box in GUI coords (y=0 top-left).
        /// </summary>
        public Rect ScreenRect
        {
            get
            {
                float sh = Screen.height;
                Vector2 a = new Vector2(DragStart.x, sh - DragStart.y);
                Vector2 b = new Vector2(DragCurrent.x, sh - DragCurrent.y);
                return new Rect(
                    Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                    Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            }
        }
    }

    // ?? Box Select System ?????????????????????????????????????????????????????

    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(PlayerActionSystemGroup))]
    [UpdateAfter(typeof(MoveCommandSystem))]
    public partial struct BoxSelectSystem : ISystem
    {
        // ?? Tuning constants ??????????????????????????????????????????????????

        /// <summary>Max seconds between tap-up and tap-down to count as double tap.</summary>
        private const float DoubleTapWindow = 0.35f;

        /// <summary>Max pixels between first and second tap positions.</summary>
        private const float DoubleTapRadius = 60f;

        /// <summary>Pixels finger must move from second-tap origin to start box.</summary>
        private const float DragThreshold = 12f;

        // ?? Lifecycle ?????????????????????????????????????????????????????????

        public void OnCreate(ref SystemState state)
        {
            if (!SystemAPI.ManagedAPI.HasSingleton<BoxSelectSingleton>())
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.SetName(e, "BoxSelectSingleton");
                state.EntityManager.AddComponentObject(e, new BoxSelectSingleton());
            }
        }

        public void OnDestroy(ref SystemState state) { }

        // ?? Update ????????????????????????????????????????????????????????????

        // Not Burst — reads managed singletons, Camera, Input
        public void OnUpdate(ref SystemState state)
        {
            var input = SystemAPI.ManagedAPI.GetSingleton<PlayerInputSingleton>();
            var sel = SystemAPI.ManagedAPI.GetSingleton<SelectionSingleton>();
            var box = SystemAPI.ManagedAPI.GetSingleton<BoxSelectSingleton>();

            // ?? Resolve touch or mouse input ??????????????????????????????????
            bool fingerDown = false;
            bool fingerHeld = false;
            bool fingerUp = false;
            Vector2 fingerPos = Vector2.zero;

#if UNITY_EDITOR || UNITY_STANDALONE
            // Editor / PC: use mouse button 0
            fingerDown = Input.GetMouseButtonDown(0);
            fingerHeld = Input.GetMouseButton(0);
            fingerUp = Input.GetMouseButtonUp(0);
            fingerPos = Input.mousePosition;
#else
            // Mobile: use first touch
            if (Input.touchCount > 0)
            {
                Touch t   = Input.GetTouch(0);
                fingerPos = t.position;
                fingerDown = t.phase == TouchPhase.Began;
                fingerHeld = t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary;
                fingerUp   = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            }
#endif

            float now = Time.time;

            // ?? State machine ?????????????????????????????????????????????????

            if (fingerDown)
            {
                if (box.WaitingSecondTap &&
                    (now - box.FirstTapTime) <= DoubleTapWindow &&
                    Vector2.Distance(fingerPos, box.FirstTapPos) <= DoubleTapRadius)
                {
                    // ?? Second tap detected ???????????????????????????????????
                    box.PendingBox = true;
                    box.WaitingSecondTap = false;
                    box.DragStart = fingerPos;
                    box.DragCurrent = fingerPos;
                    box.IsDragging = false;
                }
                else
                {
                    // ?? First tap (or missed double-tap window) ???????????????
                    box.FirstTapTime = now;
                    box.FirstTapPos = fingerPos;
                    box.WaitingSecondTap = true;
                    box.PendingBox = false;
                    box.IsDragging = false;
                }
            }

            if (fingerHeld)
            {
                box.DragCurrent = fingerPos;

                if (box.PendingBox)
                {
                    float moved = Vector2.Distance(box.DragStart, fingerPos);
                    if (moved >= DragThreshold)
                    {
                        // Crossed threshold — start drawing the box
                        box.IsDragging = true;
                        box.PendingBox = false;
                    }
                }

                if (box.IsDragging)
                {
                    // Suppress single-tap selection and camera pan this frame
                    input.ClickConsumedBySelection = true;
                }
            }

            if (fingerUp)
            {
                if (box.IsDragging)
                {
                    // Commit the box selection
                    input.ClickConsumedBySelection = true;
                    box.IsDragging = false;
                    box.PendingBox = false;
                    CommitBoxSelection(ref state, sel, input, box);
                }
                else if (box.PendingBox)
                {
                    // Second tap released without enough drag ? normal tap, no box
                    box.PendingBox = false;
                }
            }

            // Expire double-tap window
            if (box.WaitingSecondTap && (now - box.FirstTapTime) > DoubleTapWindow)
                box.WaitingSecondTap = false;
        }

        // ?? Commit: select all units whose world pos projects inside rect ??????

        private void CommitBoxSelection(
            ref SystemState state,
            SelectionSingleton sel,
            PlayerInputSingleton input,
            BoxSelectSingleton box)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // Screen-space rect in screen coords (y=0 bottom-left)
            Vector2 a = box.DragStart;
            Vector2 b = box.DragCurrent;
            float xMin = Mathf.Min(a.x, b.x);
            float xMax = Mathf.Max(a.x, b.x);
            float yMin = Mathf.Min(a.y, b.y);
            float yMax = Mathf.Max(a.y, b.y);

            if (!input.ShiftHeld)
                ClearSelection(ref state, sel);

            // Only box-select individual units — groups selected via banner click
            foreach (var (transform, selTag, entity) in
                SystemAPI.Query<RefRO<LocalTransform>, RefRO<SelectableTag>>()
                         .WithEntityAccess())
            {
                if (selTag.ValueRO.Kind != SelectableKind.Unit) continue;

                Vector3 sp = cam.WorldToScreenPoint(transform.ValueRO.Position);
                if (sp.z < 0f) continue; // behind camera

                if (sp.x >= xMin && sp.x <= xMax &&
                    sp.y >= yMin && sp.y <= yMax)
                {
                    SelectEntity(ref state, sel, entity);
                }
            }
        }

        // ?? Helpers ???????????????????????????????????????????????????????????

        private void SelectEntity(ref SystemState state, SelectionSingleton sel, Entity e)
        {
            if (sel.Contains(e)) return;
            SystemAPI.SetComponentEnabled<Selected>(e, true);
            sel.Add(e);
            SetFeedback(ref state, e, true);
        }

        private void ClearSelection(ref SystemState state, SelectionSingleton sel)
        {
            for (int i = 0; i < sel.SelectedEntities.Length; i++)
            {
                Entity e = sel.SelectedEntities[i];
                if (!SystemAPI.Exists(e)) continue;
                SystemAPI.SetComponentEnabled<Selected>(e, false);
                SetFeedback(ref state, e, false);
            }
            sel.Clear();
        }

        private void SetFeedback(ref SystemState state, Entity e, bool active)
        {
            if (!SystemAPI.HasComponent<Selected>(e)) return;
            var comp = SystemAPI.GetComponent<Selected>(e);
            Entity fe = comp.FeedbackEntity;
            if (fe != Entity.Null && SystemAPI.HasComponent<SelectionFeedbackActive>(fe))
                SystemAPI.SetComponentEnabled<SelectionFeedbackActive>(fe, active);
        }
    }
}