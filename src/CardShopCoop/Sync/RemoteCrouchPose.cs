using UnityEngine;

namespace CardShopCoop.Sync
{
    // The customer avatar controller has no crouch clip. Add a bent-leg pose after
    // Animator evaluation, before CC.CopyPose copies the skeleton to clothes/hair.
    [DefaultExecutionOrder(-100)]
    public sealed class RemoteCrouchPose : MonoBehaviour
    {
        public bool Crouching;
        private Animator _anim;
        private Transform _hips;
        private Transform[] _bones;
        private Quaternion[] _rotations;
        private Vector3 _hipPosition;
        private float _legLength;
        private float _blend;
        private bool _applied;
        private static readonly HumanBodyBones[] Bones = {
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.Spine
        };
        private static readonly float[] Angles = { -40f, 80f, -40f, -40f, 80f, -40f, 12f };

        public void Initialize(Animator animator)
        {
            _anim = animator;
            if (_anim == null || !_anim.isHuman)
            {
                enabled = false;
                CoopPlugin.Log.LogWarning("Remote crouch pose unavailable: avatar has no humanoid rig");
                return;
            }
            _hips = _anim.GetBoneTransform(HumanBodyBones.Hips);
            _bones = new Transform[Bones.Length];
            _rotations = new Quaternion[Bones.Length];
            for (int i = 0; i < Bones.Length; i++)
                _bones[i] = _anim.GetBoneTransform(Bones[i]);
            if (_hips == null || _bones[0] == null || _bones[1] == null || _bones[2] == null)
            {
                enabled = false;
                return;
            }
            _legLength = Vector3.Distance(_bones[0].position, _bones[1].position)
                + Vector3.Distance(_bones[1].position, _bones[2].position);
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        private void Update()
        {
            Restore();
        }
        private void OnDisable()
        {
            Restore();
        }

        private void Restore()
        {
            if (!_applied)
                return;
            // Restore local rotations before the next animator evaluation. Inverse
            // world rotations would accumulate error as a walking avatar turns.
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null)
                    _bones[i].localRotation = _rotations[i];
            if (_hips != null)
                _hips.localPosition = _hipPosition;
            _applied = false;
        }

        private void LateUpdate()
        {
            if (_hips == null || _anim == null || !_anim.enabled)
                return;
            _blend = Mathf.MoveTowards(_blend, Crouching ? 1f : 0f, Time.deltaTime * 7f);
            if (_blend <= 0f)
                return;
            _hipPosition = _hips.localPosition;
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null)
                    _rotations[i] = _bones[i].localRotation;
            // Use the avatar's world right axis, independent of each rig's bone axes.
            for (int i = 0; i < _bones.Length; i++)
                if (_bones[i] != null)
                    _bones[i].rotation = Quaternion.AngleAxis(Angles[i] * _blend, transform.right) * _bones[i].rotation;
            float drop = _legLength * (1f - Mathf.Cos(40f * _blend * Mathf.Deg2Rad));
            _hips.position -= transform.up * drop;
            _applied = true;
        }
    }
}
