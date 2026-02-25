using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Pure ECS system — no MonoBehaviour.
    /// Runs in InitializationSystemGroup (before simulation) each frame.
    ///
    /// Four phases:
    ///   1. INPUT    — reads UnityEngine.Input once into RTSConfig singleton
    ///   2. CAMERA   — moves Camera.main based on config state
    ///   3. SELECTION— click / box select, SetComponentEnabled<Selected> only
    ///   4. MOVE     — right-click raycast, SetComponentEnabled<NavigationMoveCommand>
    ///
    /// ZERO STRUCTURAL CHANGES AT RUNTIME:
    ///   Selected           — baked disabled by SelectedBaker
    ///   NavigationMoveCommand — baked disabled by UnitBaker
    ///   This system only toggles enabled bits and writes data.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class RTSSystem : SystemBase
    {
        // All units that have Selected in their archetype (enabled OR disabled).
        // EntityQueryOptions.IgnoreComponentEnabledState lets the query match
        // regardless of the enabled bit — we check it manually per entity.
        private EntityQuery _selectableQuery;

        // Units with Selected currently ENABLED.
        // DOTS automatically filters by enabled bit when the component appears
        // in All[] WITHOUT IgnoreComponentEnabledState — no extra API call needed.
        private EntityQuery _selectedQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<RTSConfig>();

            // Match all archetypes that contain Selected — ignore enabled bit
            _selectableQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<LocalTransform>(),
                                                ComponentType.ReadWrite<Selected>() },
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            });

            // Match only entities whose Selected bit is currently ON.
            // NavigationMoveCommand is included via IgnoreComponentEnabledState so
            // the query matches regardless of its enabled bit — we still write to it
            // per-entity in Phase4_MoveOrders.
            _selectedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<LocalTransform>(),
                                                ComponentType.ReadWrite<Selected>(),
                                                ComponentType.ReadOnly<NavigationMoveCommand>() },
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            });
        }

        protected override void OnDestroy()
        {
            // EntityQuery is a struct — no IsCreated, no Dispose needed for
            // queries obtained via GetEntityQuery (system owns their lifetime).
        }

        protected override void OnUpdate()
        {
            ref RTSConfig cfg = ref SystemAPI.GetSingletonRW<RTSConfig>().ValueRW;

            Phase1_ReadInput(ref cfg);
            Phase2_Camera(ref cfg);
            Phase3_Selection(ref cfg);
            Phase4_MoveOrders(ref cfg);
        }

        // ── Phase 1: Input ─────────────────────────────────────────────────
        // Static so it cannot accidentally access system members or Time property.

        private static void Phase1_ReadInput(ref RTSConfig cfg)
        {
            cfg.PanInput = new float2(
                (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1f : 0f) -
                (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1f : 0f),
                (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1f : 0f) -
                (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1f : 0f));

            cfg.RotateInput = (Input.GetKey(KeyCode.E) ? 1f : 0f)
                             - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

            cfg.ZoomInput = Input.GetAxis("Mouse ScrollWheel");

            cfg.MousePositionPx = new float2(Input.mousePosition.x, Input.mousePosition.y);

            cfg.LeftDown = Input.GetMouseButtonDown(0);
            cfg.LeftHeld = Input.GetMouseButton(0);
            cfg.LeftUp = Input.GetMouseButtonUp(0);
            cfg.RightDown = Input.GetMouseButtonDown(1);
            cfg.ShiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            cfg.EscapeDown = Input.GetKeyDown(KeyCode.Escape);
        }

        // ── Phase 2: Camera ────────────────────────────────────────────────

        private static void Phase2_Camera(ref RTSConfig cfg)
        {
            // FIX: use UnityEngine.Time.deltaTime explicitly.
            // In a static method, bare 'Time' would resolve to Unity.Entities.SystemBase.Time
            // which is instance-only and obsolete — UnityEngine.Time.deltaTime is correct here.
            float dt = UnityEngine.Time.deltaTime;

            if (math.any(cfg.PanInput != float2.zero))
            {
                float yawRad = math.radians(cfg.CurrentYaw);
                float3 right = new float3(math.cos(yawRad), 0f, math.sin(yawRad));
                float3 forward = new float3(-math.sin(yawRad), 0f, math.cos(yawRad));
                cfg.PivotPosition +=
                    (right * cfg.PanInput.x + forward * cfg.PanInput.y) * cfg.PanSpeed * dt;
            }

            cfg.CurrentYaw += cfg.RotateInput * cfg.RotateSpeed * dt;
            cfg.CurrentHeight = math.clamp(
                cfg.CurrentHeight - cfg.ZoomInput * cfg.ZoomSpeed,
                cfg.MinHeight, cfg.MaxHeight);

            Camera cam = Camera.main;
            if (cam == null) return;

            float tiltRad = math.radians(cfg.CameraTiltDeg);
            float armLen = cfg.CurrentHeight / math.max(0.001f, math.sin(tiltRad));
            var rot = Quaternion.Euler(cfg.CameraTiltDeg, cfg.CurrentYaw, 0f);
            Vector3 offset = rot * new Vector3(0f, 0f, -armLen);

            cam.transform.SetPositionAndRotation(
                new Vector3(cfg.PivotPosition.x, 0f, cfg.PivotPosition.z) + offset,
                rot);
        }

        // ── Phase 3: Selection ─────────────────────────────────────────────

        private void Phase3_Selection(ref RTSConfig cfg)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            // Track drag start
            if (cfg.LeftDown)
            {
                cfg.DragStartPx = cfg.MousePositionPx;
                cfg.IsDragging = false;
            }

            // Promote to drag once cursor moves far enough
            if (cfg.LeftHeld && !cfg.IsDragging &&
                math.distance(cfg.MousePositionPx, cfg.DragStartPx) > 6f)
            {
                cfg.IsDragging = true;
            }

            // Commit on mouse-up
            if (cfg.LeftUp)
            {
                if (cfg.IsDragging)
                    ExecuteBoxSelect(ref cfg, cam);
                else
                    ExecuteClickSelect(ref cfg, cam);

                cfg.IsDragging = false;
            }

            if (cfg.EscapeDown)
                DeselectAll();
        }

        private void ExecuteClickSelect(ref RTSConfig cfg, Camera cam)
        {
            var entities = _selectableQuery.ToEntityArray(Allocator.Temp);
            var transforms = _selectableQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            Entity closest = Entity.Null;
            float bestDist = cfg.ClickPickRadiusPx;

            for (int i = 0; i < entities.Length; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(
                    new Vector3(transforms[i].Position.x,
                                transforms[i].Position.y,
                                transforms[i].Position.z));
                if (sp.z <= 0f) continue;

                float d = math.distance(new float2(sp.x, sp.y), cfg.MousePositionPx);
                if (d < bestDist) { bestDist = d; closest = entities[i]; }
            }

            entities.Dispose();
            transforms.Dispose();

            // Plain click without shift → clear first
            if (!cfg.ShiftHeld) DeselectAll();

            if (closest == Entity.Null) return;

            bool wasSelected = EntityManager.IsComponentEnabled<Selected>(closest);
            // Shift on already-selected → deselect; everything else → select
            EntityManager.SetComponentEnabled<Selected>(closest,
                cfg.ShiftHeld ? !wasSelected : true);
        }

        private void ExecuteBoxSelect(ref RTSConfig cfg, Camera cam)
        {
            if (!cfg.ShiftHeld) DeselectAll();

            float2 lo = math.min(cfg.DragStartPx, cfg.MousePositionPx);
            float2 hi = math.max(cfg.DragStartPx, cfg.MousePositionPx);

            var entities = _selectableQuery.ToEntityArray(Allocator.Temp);
            var transforms = _selectableQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                Vector3 sp = cam.WorldToScreenPoint(
                    new Vector3(transforms[i].Position.x,
                                transforms[i].Position.y,
                                transforms[i].Position.z));
                if (sp.z <= 0f) continue;

                float2 s = new float2(sp.x, sp.y);
                if (math.all(s >= lo) && math.all(s <= hi))
                    EntityManager.SetComponentEnabled<Selected>(entities[i], true);
            }

            entities.Dispose();
            transforms.Dispose();
        }

        private void DeselectAll()
        {
            var entities = _selectableQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
                EntityManager.SetComponentEnabled<Selected>(entities[i], false);
            entities.Dispose();
        }

        // ── Phase 4: Move Orders ───────────────────────────────────────────

        private void Phase4_MoveOrders(ref RTSConfig cfg)
        {
            if (!cfg.RightDown) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(
                new Vector3(cfg.MousePositionPx.x, cfg.MousePositionPx.y, 0f));

            if (!Physics.Raycast(ray, out RaycastHit hit, 2000f, cfg.GroundLayerMask)) return;

            float3 click = new float3(hit.point.x, hit.point.y, hit.point.z);

            // Query uses IgnoreComponentEnabledState so we must manually filter
            // to only entities whose Selected bit is ON.
            var allEntities = _selectedQuery.ToEntityArray(Allocator.Temp);
            var selectedList = new Unity.Collections.NativeList<Entity>(allEntities.Length, Allocator.Temp);
            for (int i = 0; i < allEntities.Length; i++)
                if (EntityManager.IsComponentEnabled<Selected>(allEntities[i]))
                    selectedList.Add(allEntities[i]);
            allEntities.Dispose();

            int count = selectedList.Length;
            if (count == 0) { selectedList.Dispose(); return; }

            int cols = (int)math.ceil(math.sqrt(count));
            int rows = (int)math.ceil((float)count / cols);
            float spacing = cfg.FormationSpacing;

            for (int i = 0; i < count; i++)
            {
                float ox = (i % cols - (cols - 1) * 0.5f) * spacing;
                float oz = (i / cols - (rows - 1) * 0.5f) * spacing;

                EntityManager.SetComponentEnabled<NavigationMoveCommand>(selectedList[i], true);
                EntityManager.SetComponentData(selectedList[i], new NavigationMoveCommand
                {
                    Destination = click + new float3(ox, 0f, oz),
                    Priority = 1
                });
            }

            Debug.Log($"[RTSSystem] Move → {hit.point:F1}  ({count} unit(s))");
            selectedList.Dispose();
        }
    }

    // ── Gizmo bridge ──────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns a hidden MonoBehaviour solely to receive OnDrawGizmos callbacks.
    /// The bridge queries ECS directly — no data stored in the MonoBehaviour.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(RTSSystem))]
    public partial class RTSGizmoSystem : SystemBase
    {
        private RTSGizmoBridge _bridge;

        protected override void OnCreate() => RequireForUpdate<RTSConfig>();
        protected override void OnUpdate() { }  // bridge handles gizmos

        protected override void OnStartRunning()
        {
            var go = new GameObject("[RTS Gizmos]") { hideFlags = HideFlags.DontSave };
            _bridge = go.AddComponent<RTSGizmoBridge>();
            _bridge.World = World;
        }

        protected override void OnStopRunning()
        {
            if (_bridge != null && _bridge.gameObject != null)
                Object.Destroy(_bridge.gameObject);
        }
    }

    public class RTSGizmoBridge : MonoBehaviour
    {
        public World World;

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (World == null || !World.IsCreated || !Application.isPlaying) return;

            var em = World.EntityManager;
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadOnly<LocalTransform>(),
                                                ComponentType.ReadWrite<Selected>() },
                Options = EntityQueryOptions.IgnoreComponentEnabledState
            });

            var entities = q.ToEntityArray(Allocator.Temp);
            var transforms = q.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                bool sel = em.IsComponentEnabled<Selected>(entities[i]);
                var pos = new Vector3(transforms[i].Position.x,
                                       transforms[i].Position.y,
                                       transforms[i].Position.z);

                if (sel)
                {
                    Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.95f);
                    DrawCircle(pos, 0.7f, 32);

                    if (em.HasComponent<AgentNavigation>(entities[i]))
                    {
                        var nav = em.GetComponentData<AgentNavigation>(entities[i]);
                        if (nav.HasDestination == 1)
                        {
                            var dest = new Vector3(nav.Destination.x,
                                                   nav.Destination.y,
                                                   nav.Destination.z);
                            Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.4f);
                            Gizmos.DrawLine(pos + Vector3.up * 0.1f, dest + Vector3.up * 0.1f);
                            Gizmos.color = new Color(0.15f, 1f, 0.35f, 0.9f);
                            Gizmos.DrawSphere(dest, 0.3f);
                        }
                    }
                }
                else
                {
                    Gizmos.color = new Color(1f, 1f, 1f, 0.18f);
                    DrawCircle(pos, 0.45f, 16);
                }
            }

            entities.Dispose();
            transforms.Dispose();
            q.Dispose();
#endif
        }

        private static void DrawCircle(Vector3 c, float r, int seg)
        {
            float step = Mathf.PI * 2f / seg;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * step, a1 = (i + 1) * step;
                Gizmos.DrawLine(
                    c + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * r,
                    c + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * r);
            }
        }
    }
}