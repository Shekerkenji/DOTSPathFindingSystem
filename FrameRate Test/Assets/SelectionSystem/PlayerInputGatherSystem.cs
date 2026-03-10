using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  PlayerInputGatherSystem.cs
//
//  Runs first in SimulationSystemGroup.
//  Reads UnityEngine.Input (main-thread only — not Burst compiled).
//  Writes into PlayerInputSingleton so all downstream systems never touch
//  UnityEngine.Input directly.
//
//  GESTURE MAP
//  ───────────
//  Single tap unit          → Select unit          (UnitSelectionSystem)
//  Single tap empty ground  → Deselect all         (UnitSelectionSystem)
//  Tap + drag               → Camera pan           (your camera system)
//  Double tap + drag        → Box select           (BoxSelectSystem)
//  Long press empty ground  → Move to position     (MoveCommandSystem)
//
//  LONG PRESS DETECTION
//  ────────────────────
//  Finger held >= LongPressDuration seconds without drifting more than
//  LongPressMaxDrift pixels → LongPressThisFrame = true for exactly one frame.
//  Resets when finger lifts. Fires only once per hold.
// ─────────────────────────────────────────────────────────────────────────────

[WorldSystemFilter(WorldSystemFilterFlags.Default)]
[UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
public partial struct PlayerInputGatherSystem : ISystem
{
    // ── Long-press tuning ─────────────────────────────────────────────────────

    /// <summary>Seconds the finger must be held to fire a long press.</summary>
    private const float LongPressDuration = 0.45f;

    /// <summary>Max pixels the finger may drift from its press origin.</summary>
    private const float LongPressMaxDrift = 15f;

    // ── Internal long-press state (struct fields — survive across frames) ─────

    private float _pressStartTime;
    private Vector2 _pressStartPos;
    private bool _pressActive;  // finger currently held down
    private bool _pressFired;   // long press already fired this hold

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void OnCreate(ref SystemState state)
    {
        _pressActive = false;
        _pressFired = false;

        if (!SystemAPI.ManagedAPI.HasSingleton<PlayerInputSingleton>())
        {
            var e = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(e, "PlayerInputSingleton");
            state.EntityManager.AddComponentObject(e, new PlayerInputSingleton());
        }

        if (!SystemAPI.ManagedAPI.HasSingleton<SelectionSingleton>())
        {
            var e = state.EntityManager.CreateEntity();
            state.EntityManager.SetName(e, "SelectionSingleton");
            var sel = new SelectionSingleton();
            sel.Init();
            state.EntityManager.AddComponentObject(e, sel);
        }
    }

    public void OnDestroy(ref SystemState state)
    {
        if (SystemAPI.ManagedAPI.HasSingleton<SelectionSingleton>())
            SystemAPI.ManagedAPI.GetSingleton<SelectionSingleton>().Dispose();
    }

    // NOT Burst — reads UnityEngine APIs
    public void OnUpdate(ref SystemState state)
    {
        var input = SystemAPI.ManagedAPI.GetSingleton<PlayerInputSingleton>();
        float now = Time.time;

        // ── Resolve touch or mouse ────────────────────────────────────────────
        bool fingerDown = false;
        bool fingerHeld = false;
        bool fingerUp = false;
        Vector2 fingerPos = Vector2.zero;

#if UNITY_EDITOR || UNITY_STANDALONE
        fingerDown = Input.GetMouseButtonDown(0);
        fingerHeld = Input.GetMouseButton(0);
        fingerUp = Input.GetMouseButtonUp(0);
        fingerPos = Input.mousePosition;
#else
        if (Input.touchCount > 0)
        {
            Touch t   = Input.GetTouch(0);
            fingerPos = t.position;
            fingerDown = t.phase == TouchPhase.Began;
            fingerHeld = t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary;
            fingerUp   = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
        }
#endif

        // ── Long-press state machine ──────────────────────────────────────────
        input.LongPressThisFrame = false; // reset every frame

        if (fingerDown)
        {
            _pressStartTime = now;
            _pressStartPos = fingerPos;
            _pressActive = true;
            _pressFired = false;
        }

        if (fingerHeld && _pressActive && !_pressFired)
        {
            float drift = Vector2.Distance(fingerPos, _pressStartPos);
            float held = now - _pressStartTime;

            if (drift > LongPressMaxDrift)
            {
                // Finger moved — became a pan/drag, cancel long press
                _pressFired = true;
            }
            else if (held >= LongPressDuration)
            {
                input.LongPressThisFrame = true;
                _pressFired = true; // fire only once per hold
            }
        }

        // Quick tap = finger up without long press having fired and without drifting.
        // We use finger-UP (not finger-down) so that a held finger never triggers
        // a tap — this prevents deselect firing before long press has a chance.
        bool quickTap = fingerUp && _pressActive && !_pressFired &&
                        Vector2.Distance(fingerPos, _pressStartPos) <= LongPressMaxDrift;

        if (fingerUp)
        {
            _pressActive = false;
            _pressFired = false;
        }

        // ── Standard input fields ─────────────────────────────────────────────
        input.LeftClickThisFrame = quickTap;   // tap: finger up quickly, no drift, no long press
        input.RightClickThisFrame = Input.GetMouseButtonDown(1);
        input.MouseScreenPos = Input.mousePosition;
        input.ShiftHeld = Input.GetKey(KeyCode.LeftShift) ||
                                    Input.GetKey(KeyCode.RightShift);

        // Reset per-frame derived state
        input.ClickConsumedBySelection = false;
        input.HitGround = false;
        input.RayOrigin = float3.zero;
        input.RayDirection = new float3(0, -1, 0);

        // ── Camera ray ────────────────────────────────────────────────────────
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector2 mp = input.MouseScreenPos;
        if (!math.isfinite(mp.x) || !math.isfinite(mp.y)) return;
        if (mp.x < 0f || mp.y < 0f || mp.x > cam.pixelWidth || mp.y > cam.pixelHeight) return;

        Ray ray = cam.ScreenPointToRay(new Vector3(mp.x, mp.y, 0f));
        input.RayOrigin = ray.origin;
        input.RayDirection = ray.direction;

        // ── Ground plane hit (y = 0, no physics) ─────────────────────────────
        if (math.abs(ray.direction.y) > 1e-5f)
        {
            float t = -ray.origin.y / ray.direction.y;
            if (t > 0f)
            {
                input.HitGround = true;
                input.GroundHitPoint = (float3)ray.origin + (float3)ray.direction * t;
            }
        }
    }
}