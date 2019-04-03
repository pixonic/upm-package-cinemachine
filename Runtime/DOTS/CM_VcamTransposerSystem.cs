using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;
using System.Runtime.CompilerServices;
using UnityEngine;
using System;

namespace Cinemachine.ECS
{
    [Serializable]
    public struct CM_VcamTransposer : IComponentData
    {
        /// <summary>
        /// The coordinate space to use when interpreting the offset from the target
        /// </summary>
        public enum BindingMode
        {
            /// <summary>
            /// Camera will be bound to the Follow target using a frame of reference consisting
            /// of the target's local frame, with the tilt and roll zeroed out.
            /// </summary>
            LockToTargetWithWorldUp,
            /// <summary>
            /// Camera will be bound to the Follow target using a frame of reference consisting
            /// of the target's local frame, with the roll zeroed out.
            /// </summary>
            LockToTargetNoRoll,
            /// <summary>
            /// Camera will be bound to the Follow target using the target's local frame.
            /// </summary>
            LockToTarget,
            /// <summary>Camera will be bound to the Follow target using a world space offset.</summary>
            WorldSpace,
            /// <summary>Offsets will be calculated relative to the target, using Camera-local axes</summary>
            SimpleFollowWithWorldUp
        }
        /// <summary>The coordinate space to use when interpreting the offset from the target</summary>
        public BindingMode bindingMode;

        /// <summary>The distance which the transposer will attempt to maintain from the transposer subject</summary>
        public float3 followOffset;

        /// <summary>How aggressively the camera tries to maintain the offset in the 3 axes.
        /// Small numbers are more responsive, rapidly translating the camera to keep the target's
        /// offset.  Larger numbers give a more heavy slowly responding camera.
        /// Using different settings per axis can yield a wide range of camera behaviors</summary>
        public float3 damping;

        /// <summary>How aggressively the camera tries to track the target's rotation.
        /// Small numbers are more responsive.  Larger numbers give a more heavy slowly responding camera.</summary>
        public float angularDamping;
    }

    [Serializable]
    public struct CM_VcamTransposerState : IComponentData
    {
        /// State information used for damping
        public float3 previousTargetPosition;
        public quaternion previousTargetRotation;
    }

    [ExecuteAlways]
    [UpdateAfter(typeof(CM_VcamPreBodySystem))]
    [UpdateBefore(typeof(CM_VcamPreAimSystem))]
    [UpdateInGroup(typeof(LateSimulationSystemGroup))]
    public class CM_VcamTransposerSystem : JobComponentSystem
    {
        ComponentGroup m_vcamGroup;
        ComponentGroup m_missingStateGroup;

        protected override void OnCreateManager()
        {
            m_vcamGroup = GetComponentGroup(
                ComponentType.ReadWrite<CM_VcamPositionState>(),
                ComponentType.ReadWrite<CM_VcamTransposerState>(),
                ComponentType.ReadOnly<CM_VcamTransposer>(),
                ComponentType.ReadOnly<CM_VcamFollowTarget>(),
                ComponentType.ReadOnly<CM_VcamChannel>());

            m_missingStateGroup = GetComponentGroup(
                ComponentType.Exclude<CM_VcamTransposerState>(),
                ComponentType.ReadOnly<CM_VcamTransposer>());
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            // Add any missing transposer state components
            if (m_missingStateGroup.CalculateLength() > 0)
                EntityManager.AddComponent(m_missingStateGroup,
                    ComponentType.ReadWrite<CM_VcamTransposerState>());

            var targetSystem = World.GetOrCreateSystem<CM_TargetSystem>();
            var targetLookup = targetSystem.GetTargetLookupForJobs(ref inputDeps);
            if (!targetLookup.IsCreated)
                return inputDeps; // no targets yet

            var channelSystem = World.GetOrCreateSystem<CM_ChannelSystem>();
            JobHandle vcamDeps = channelSystem.InvokePerVcamChannel(
                m_vcamGroup, inputDeps, new TransposerJobLaunch { targetLookup = targetLookup });

            return targetSystem.RegisterTargetLookupReadJobs(vcamDeps);
        }

        struct TransposerJobLaunch : CM_ChannelSystem.VcamGroupCallback
        {
            public NativeHashMap<Entity, CM_TargetSystem.TargetInfo> targetLookup;
            public JobHandle Invoke(
                ComponentGroup filteredGroup, Entity channelEntity,
                CM_Channel c, CM_ChannelState state, JobHandle inputDeps)
            {
                var job = new TrackTargetJob
                {
                    deltaTime = state.deltaTime,
                    targetLookup = targetLookup
                };
                return job.ScheduleGroup(filteredGroup, inputDeps);
            }
        }

        [BurstCompile]
        struct TrackTargetJob : IJobForEach<
            CM_VcamPositionState, CM_VcamTransposerState,
            CM_VcamTransposer, CM_VcamFollowTarget>
        {
            public float deltaTime;
            [ReadOnly] public NativeHashMap<Entity, CM_TargetSystem.TargetInfo> targetLookup;

            public void Execute(
                ref CM_VcamPositionState posState,
                ref CM_VcamTransposerState transposerState,
                [ReadOnly] ref CM_VcamTransposer transposer,
                [ReadOnly] ref CM_VcamFollowTarget follow)
            {
                if (!targetLookup.TryGetValue(follow.target, out CM_TargetSystem.TargetInfo targetInfo))
                    return;

                float dt = math.select(-1, deltaTime, posState.previousFrameDataIsValid);

                var targetPos = targetInfo.position;
                var targetRot = GetRotationForBindingMode(
                        targetInfo.rotation, transposer.bindingMode,
                        targetPos - posState.raw);

                var prevPos = transposerState.previousTargetPosition + targetInfo.warpDelta;
                targetRot = ApplyRotationDamping(
                    dt, 0,
                    math.select(0, transposer.angularDamping, dt >= 0),
                    transposerState.previousTargetRotation, targetRot);
                targetPos = ApplyPositionDamping(
                    dt, 0,
                    math.select(float3.zero, transposer.damping, dt >= 0),
                    prevPos, targetPos, targetRot);

                transposerState.previousTargetPosition = targetPos;
                transposerState.previousTargetRotation = targetRot;

                posState.raw = targetPos + math.mul(targetRot, transposer.followOffset);
                posState.up = math.mul(targetRot, math.up());
            }
        }

        /// <summary>Applies damping to target rotation</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static quaternion ApplyRotationDamping(
            float deltaTime, float fixedDelta, float angularDamping,
            quaternion previousTargetRotation, quaternion currentTargetRotation)
        {
            float t = MathHelpers.Damp(1, angularDamping, deltaTime, fixedDelta);
            return math.slerp(previousTargetRotation, currentTargetRotation, t);
        }

        /// <summary>Applies damping to target position</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float3 ApplyPositionDamping(
            float deltaTime, float fixedDelta, float3 damping,
            float3 previousTargetPosition, float3 currentTargetPosition,
            quaternion currentTargetRotation)
        {
            var worldOffset = currentTargetPosition - previousTargetPosition;
            float3 localOffset = math.mul(math.inverse(currentTargetRotation), worldOffset);
            localOffset = MathHelpers.Damp(localOffset, damping, deltaTime, fixedDelta);
            worldOffset = math.mul(currentTargetRotation, localOffset);
            return previousTargetPosition + worldOffset;
        }

        /// <summary>Applies binding mode:
        /// Returns the axes for applying target offset and damping</summary>
        public static quaternion GetRotationForBindingMode(
            quaternion targetRotation,
            CM_VcamTransposer.BindingMode bindingMode,
            float3 directionCameraToTarget) // not normalized
        {
            // GML todo: optimize!  Can we get rid of the switch?
            switch (bindingMode)
            {
                case CM_VcamTransposer.BindingMode.LockToTargetWithWorldUp:
                    return MathHelpers.Uppify(targetRotation, math.up());
                case CM_VcamTransposer.BindingMode.LockToTargetNoRoll:
                    return quaternion.LookRotationSafe(math.forward(targetRotation), math.up());
                case CM_VcamTransposer.BindingMode.LockToTarget:
                    return targetRotation;
                case CM_VcamTransposer.BindingMode.SimpleFollowWithWorldUp:
                {
                    directionCameraToTarget.y = 0;
                    float len = math.length(directionCameraToTarget);
                    return math.select(
                        quaternion.LookRotation(directionCameraToTarget / len, math.up()).value,
                        targetRotation.value, len < MathHelpers.Epsilon);
                }
                default:
                    return quaternion.identity;
            }
        }
    }
}
