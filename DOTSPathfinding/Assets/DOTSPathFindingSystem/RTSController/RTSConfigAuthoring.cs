using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Shek.ECSNavigation
{
    /// <summary>
    /// Place on ONE empty GameObject in your test scene to configure and enable the RTS system.
    /// Creates the RTSConfig singleton entity at bake time.
    ///
    /// SETUP:
    ///   1. Create an empty GameObject, name it "RTS Controller".
    ///   2. Add RTSConfigAuthoring to it.
    ///   3. Set Ground Layer to your walkable ground physics layer.
    ///   4. Add SelectedAuthoring to every unit prefab/GameObject alongside UnitAuthoring.
    ///   5. Make sure your Camera is tagged "MainCamera" (RTSSystem uses Camera.main).
    ///
    /// CONTROLS (handled entirely in RTSSystem, no MonoBehaviour):
    ///   WASD / Arrow keys  — pan camera
    ///   Q / E              — rotate camera left / right
    ///   Scroll wheel       — zoom in / out
    ///   Left click         — select unit under cursor
    ///   Shift + click      — add / remove from selection
    ///   Left drag          — box-select multiple units
    ///   Right click        — move selected units to clicked ground point
    ///   Escape             — deselect all
    /// </summary>
    [AddComponentMenu("Navigation/RTS/RTS Config")]
    public class RTSConfigAuthoring : MonoBehaviour
    {
        [Header("Camera")]
        public float panSpeed = 20f;
        public float rotateSpeed = 60f;
        public float zoomSpeed = 10f;
        public float startHeight = 20f;
        public float minHeight = 5f;
        public float maxHeight = 60f;
        [Tooltip("Fixed camera pitch (top-down angle).")]
        public float cameraTiltDeg = 50f;

        [Header("Selection")]
        [Tooltip("Screen-pixel radius within which a click counts as hitting a unit.")]
        public float clickPickRadius = 28f;

        [Header("Formation")]
        [Tooltip("Distance between units in the formation grid on move orders.")]
        public float formationSpacing = 2f;

        [Header("Physics")]
        [Tooltip("Ground layer for right-click move raycasts.")]
        public LayerMask groundLayer = ~0;
    }

    public class RTSConfigBaker : Baker<RTSConfigAuthoring>
    {
        public override void Bake(RTSConfigAuthoring a)
        {
            // TransformUsageFlags.None — this entity has no transform, it's a pure singleton
            var entity = GetEntity(TransformUsageFlags.None);

            AddComponent(entity, new RTSConfig
            {
                // Config
                PanSpeed = a.panSpeed,
                RotateSpeed = a.rotateSpeed,
                ZoomSpeed = a.zoomSpeed,
                MinHeight = a.minHeight,
                MaxHeight = a.maxHeight,
                CameraTiltDeg = a.cameraTiltDeg,
                ClickPickRadiusPx = a.clickPickRadius,
                FormationSpacing = a.formationSpacing,
                GroundLayerMask = a.groundLayer,

                // Runtime camera state — starts at origin, yaw 0, configured height
                PivotPosition = float3.zero,
                CurrentYaw = 0f,
                CurrentHeight = a.startHeight,

                // Everything else zeroed / false by default
            });
        }
    }
}