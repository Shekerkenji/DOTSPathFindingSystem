using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;

// ─────────────────────────────────────────────────────────────────────────────
//  SelectionComponents.cs  (v2)
//
//  All data types for the selection system.
//  Rules:
//    - No structural add/remove — everything is IEnableableComponent toggled.
//    - Burst-compatible value types only on IComponentData structs.
// ─────────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────────
//  Selected
// ─────────────────────────────────────────────────────────────────────────────

public struct Selected : IComponentData, IEnableableComponent
{
    public Entity FeedbackEntity;
}

// ─────────────────────────────────────────────────────────────────────────────
//  SelectionFeedbackActive
// ─────────────────────────────────────────────────────────────────────────────

public struct SelectionFeedbackActive : IComponentData, IEnableableComponent { }

// ─────────────────────────────────────────────────────────────────────────────
//  SelectableKind
// ─────────────────────────────────────────────────────────────────────────────

public enum SelectableKind : byte
{
    Unit = 0,
    Group = 1,
    BigGroup = 2,
}

// ─────────────────────────────────────────────────────────────────────────────
//  SelectableTag
// ─────────────────────────────────────────────────────────────────────────────

public struct SelectableTag : IComponentData
{
    public SelectableKind Kind;
}

// ─────────────────────────────────────────────────────────────────────────────
//  SelectionSingleton  (managed class)
// ─────────────────────────────────────────────────────────────────────────────

public class SelectionSingleton : IComponentData
{
    public NativeList<Entity> SelectedEntities;

    public bool HasSelection => SelectedEntities.IsCreated && SelectedEntities.Length > 0;
    public Entity Primary => HasSelection ? SelectedEntities[0] : Entity.Null;

    public void Init()
    {
        SelectedEntities = new NativeList<Entity>(64, Allocator.Persistent);
    }

    public void Dispose()
    {
        if (SelectedEntities.IsCreated) SelectedEntities.Dispose();
    }

    public void Clear() => SelectedEntities.Clear();

    public void Add(Entity e) => SelectedEntities.Add(e);

    public bool Contains(Entity e)
    {
        for (int i = 0; i < SelectedEntities.Length; i++)
            if (SelectedEntities[i] == e) return true;
        return false;
    }

    public void Remove(Entity e)
    {
        for (int i = 0; i < SelectedEntities.Length; i++)
        {
            if (SelectedEntities[i] == e)
            {
                SelectedEntities.RemoveAtSwapBack(i);
                return;
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  PlayerInputSingleton  (managed class)
// ─────────────────────────────────────────────────────────────────────────────

public class PlayerInputSingleton : IComponentData
{
    // ── Mouse / touch ─────────────────────────────────────────────────────────

    /// <summary>Screen-space pointer position this frame.</summary>
    public UnityEngine.Vector2 MouseScreenPos;

    /// <summary>
    /// True on the frame any tap/click goes down.
    /// Used by UnitSelectionSystem for single-tap selection.
    /// </summary>
    public bool LeftClickThisFrame;

    /// <summary>
    /// True on the frame the player completes a long press (finger held
    /// >= LongPressDuration seconds without drifting more than LongPressMaxDrift px).
    /// Used by MoveCommandSystem to issue formation move orders.
    /// Fires only once per hold, resets when finger lifts.
    /// </summary>
    public bool LongPressThisFrame;

    /// <summary>Right mouse button pressed this frame (desktop only).</summary>
    public bool RightClickThisFrame;

    /// <summary>Shift key held this frame.</summary>
    public bool ShiftHeld;

    // ── Camera ray ────────────────────────────────────────────────────────────

    /// <summary>World-space ray origin (camera world position).</summary>
    public float3 RayOrigin;

    /// <summary>World-space ray direction (normalised).</summary>
    public float3 RayDirection;

    // ── Ground hit ────────────────────────────────────────────────────────────

    /// <summary>True when the camera ray intersected the y=0 ground plane.</summary>
    public bool HitGround;

    /// <summary>World-space point on the ground plane (valid when HitGround=true).</summary>
    public float3 GroundHitPoint;

    // ── Click arbitration ─────────────────────────────────────────────────────

    /// <summary>
    /// Set to true by UnitSelectionSystem when a tap hit a unit and was handled.
    /// MoveCommandSystem and BoxSelectSystem check this to avoid double-firing.
    /// Reset to false at the start of every frame by PlayerInputGatherSystem.
    /// </summary>
    public bool ClickConsumedBySelection;
}