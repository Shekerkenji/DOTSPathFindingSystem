using Shek.ECSAnimation;
using Unity.Entities;
using UnityEngine;

namespace Shek.ECSGameplay
{
    /// <summary>
    /// Drives combat animations by watching AIState transitions each frame.
    ///
    /// Clip index map:
    ///   0 = Idle
    ///   1 = Walk/Run  (handled by MovementAnimationSystem)
    ///   2 = Attack
    ///   3 = Death
    ///
    /// Runs after DamageSystem so death state is already written
    /// before we touch the AnimationController.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(DamageSystem))]
    public partial struct CombatAnimationSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (controller, aiState) in
                SystemAPI.Query<
                    RefRW<AnimationController>,
                    RefRW<AIState>>())
            {
                UnitState current = aiState.ValueRO.State;
                UnitState previous = aiState.ValueRO.LastState;
                if (current == previous) continue; // no transition, nothing to do

                switch (current)
                {
                    case UnitState.Attacking:
                        Debug.Log("CombatAnimationSystem: Attacking");
                        AnimationControllerAPI.Play(ref controller.ValueRW, 2);
                        break;
                    case UnitState.Dead:
                        AnimationControllerAPI.Play(ref controller.ValueRW, 3);
                        break;
                    case UnitState.Idle:
                        AnimationControllerAPI.Play(ref controller.ValueRW, 0);
                        break;
                        // UnitState.Moving : MovementAnimationSystem owns this (clip 1)
                        // UnitState.Hit    : HitRecoveryAnimationSystem owns this
                }

                // Always consume the transition regardless of which state we landed in.
                // Unhandled states (Moving, Hit) must still update LastState, otherwise
                // a stale value causes the next Attacking entry to be missed or double-fired.
                aiState.ValueRW.LastState = current;
            }
        }
    }

    /// <summary>
    /// Resumes the correct animation after hit-stun ends.
    /// Must run after HitRecoverySystem so the state is already updated.
    ///
    /// KEY RULE: If the unit was already Attacking before the hit (PreHitState == Attacking),
    /// do NOT restart clip 2 — the looped animation is already queued and restarting it
    /// from frame 0 on every received hit is what caused the "re-triggers on every attack" bug.
    /// Only play clip 2 if the unit is freshly entering Attacking from a non-attack state.
    /// </summary>
    //[UpdateInGroup(typeof(SimulationSystemGroup))]
    //[UpdateAfter(typeof(HitRecoverySystem))]
    //public partial struct HitRecoveryAnimationSystem : ISystem
    //{
    //    public void OnUpdate(ref SystemState state)
    //    {
    //        foreach (var (controller, aiState) in
    //            SystemAPI.Query<
    //                RefRW<AnimationController>,
    //                RefRW<AIState>>()
    //                .WithDisabled<DeadTag>())
    //        {
    //            UnitState current = aiState.ValueRO.State;
    //            UnitState previous = aiState.ValueRO.LastState;

    //            // Only fire on the single frame Hit -> something-else transition
    //            if (previous != UnitState.Hit || current == UnitState.Hit) continue;

    //            switch (current)
    //            {
    //                case UnitState.Attacking:
    //                    // Only restart the attack clip if the unit wasn't already
    //                    // attacking before it was hit. If PreHitState == Attacking,
    //                    // the looped clip is already playing — restarting it here
    //                    // is exactly what caused the animation to re-trigger on
    //                    // every incoming hit during melee combat.
    //                    if (aiState.ValueRO.PreHitState != UnitState.Attacking)
    //                    {
    //                        Debug.Log("HitRecoveryAnimationSystem: Attacking (fresh entry)");
    //                        AnimationControllerAPI.Play(ref controller.ValueRW, 2);
    //                    }
    //                    break;
    //                default: // Idle, Moving, etc.
    //                    AnimationControllerAPI.Play(ref controller.ValueRW, 0);
    //                    break;
    //            }

    //            // Always update LastState after handling the Hit->X transition.
    //            aiState.ValueRW.LastState = current;
    //        }
    //    }
    //}
}