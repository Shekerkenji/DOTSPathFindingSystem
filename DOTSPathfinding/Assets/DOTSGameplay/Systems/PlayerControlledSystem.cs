using Unity.Entities;
using Shek.ECSNavigation;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Watches for player-commanded units finishing their move (StoppedMoving event)
    /// and hands control back to the AI by disabling the PlayerControlled tag.
    ///
    /// Flow:
    ///   RTSSystem right-click  → enables PlayerControlled, sets AIState=Moving, clears CurrentTarget
    ///   Unit walks to destination → navigation fires StoppedMoving
    ///   This system            → disables PlayerControlled, resets AIState=Idle
    ///   Next frame             → ThreatScanSystem and AIDecisionSystem resume normally
    ///
    /// Runs after MovementEventSystem (which writes StoppedMoving).
    /// Does NOT declare [UpdateBefore(AIDecisionSystem)] — that would create a
    /// circular dependency through the navigation system chain.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MovementEventSystem))]
    public partial struct PlayerControlledSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (aiState, entity) in
                SystemAPI.Query<RefRW<AIState>>()
                    .WithAll<PlayerControlled>()
                    .WithDisabled<DeadTag>()
                    .WithEntityAccess())
            {
                // Only act on the frame StoppedMoving fires
                if (!SystemAPI.IsComponentEnabled<StoppedMoving>(entity)) continue;

                // Re-enable AI — ThreatScanSystem will assign targets next scan interval
                state.EntityManager.SetComponentEnabled<PlayerControlled>(entity, false);

                // Reset state to Idle so the unit doesn't resume a stale Attacking state.
                // Without this, AIState stays frozen at Attacking (set before the player
                // command), causing the attack animation to play on arrival and
                // MovementAnimationSystem to be skipped during the entire player-move.
                aiState.ValueRW.LastState = aiState.ValueRO.State;
                aiState.ValueRW.State = UnitState.Idle;
                aiState.ValueRW.StateTimer = 0f;
            }
        }
    }
}