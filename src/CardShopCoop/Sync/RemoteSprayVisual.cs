using UnityEngine;

namespace CardShopCoop.Sync
{
    [DefaultExecutionOrder(-90)]
    public sealed class RemoteSprayVisual : MonoBehaviour
    {
        private Transform _upper, _lower, _hand;
        private Quaternion _upperRotation, _lowerRotation;
        private bool _poseApplied, _spraying;
        private float _blend;
        private Item _can;
        private Quaternion _aim;
        private ParticleSystem _mist;
        private AudioSource _sound, _sourceSound;
        private static readonly System.Reflection.FieldInfo Muted =
            HarmonyLib.AccessTools.Field(typeof(SoundManager), "m_IsMute");

        public void Initialize(Animator animator)
        {
            if (animator != null && animator.isHuman)
            {
                _upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                _lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                _hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            }
            var player = FindObjectOfType<InteractionPlayerController>();
            if (player?.m_DeodorantSprayVFX != null)
            {
                _mist = Instantiate(player.m_DeodorantSprayVFX, transform);
                _mist.name = "Coop remote deodorant mist";
                _mist.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _mist.gameObject.SetActive(true);
            }
            var manager = FindObjectOfType<SoundManager>();
            _sourceSound = manager != null ? manager.m_SprayLoop : null;
            if (_sourceSound != null)
            {
                _sound = gameObject.AddComponent<AudioSource>();
                _sound.playOnAwake = false;
                _sound.loop = true;
                _sound.clip = _sourceSound.clip;
                _sound.outputAudioMixerGroup = _sourceSound.outputAudioMixerGroup;
                _sound.spatialBlend = 1f;
                _sound.minDistance = 1f;
                _sound.maxDistance = 8f;
                _sound.rolloffMode = AudioRolloffMode.Linear;
            }
        }

        public void SetState(Item can, bool spraying, Quaternion aim)
        {
            _can = can;
            _spraying = can != null && spraying;
            _aim = aim;
        }

        private void Restore()
        {
            if (!_poseApplied)
                return;
            if (_upper != null)
                _upper.localRotation = _upperRotation;
            if (_lower != null)
                _lower.localRotation = _lowerRotation;
            _poseApplied = false;
        }

        private void Update() => Restore();

        private void OnDisable()
        {
            Restore();
            _spraying = false;
            _can = null;
            _blend = 0f;
            if (_mist != null)
                _mist.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (_sound != null)
                _sound.Stop();
        }

        private void LateUpdate()
        {
            _blend = Mathf.MoveTowards(_blend, _can != null ? 1f : 0f, Time.deltaTime * 8f);
            Vector3 forward = _aim * Vector3.forward;
            if (_blend > 0f && _upper != null && _lower != null && _hand != null)
            {
                _upperRotation = _upper.localRotation;
                _lowerRotation = _lower.localRotation;
                _upper.rotation = Quaternion.Slerp(_upper.rotation,
                    Quaternion.FromToRotation(_lower.position - _upper.position, forward - Vector3.up * 0.5f) * _upper.rotation, _blend);
                _lower.rotation = Quaternion.Slerp(_lower.rotation,
                    Quaternion.FromToRotation(_hand.position - _lower.position, forward) * _lower.rotation, _blend);
                _poseApplied = true;
            }
            if (_can != null)
            {
                _can.transform.position = _hand != null ? _hand.position : transform.position + Vector3.up * 1.2f + forward * 0.4f;
                _can.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
                if (_mist != null)
                {
                    _mist.transform.position = _can.transform.position + Vector3.up * 0.08f + forward * 0.04f;
                    _mist.transform.rotation = _aim;
                }
            }
            if (_mist != null)
            {
                if (_spraying && !_mist.isEmitting)
                    _mist.Play(true);
                if (!_spraying && _mist.isEmitting)
                    _mist.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (_sound != null)
            {
                _sound.volume = RemoteAudioRules.Volume(SoundManager.SFXVolume, Muted?.GetValue(null) is true);
                if (_spraying && !_sound.isPlaying)
                    _sound.Play();
                if (!_spraying && _sound.isPlaying)
                    _sound.Stop();
            }
        }
    }
}
