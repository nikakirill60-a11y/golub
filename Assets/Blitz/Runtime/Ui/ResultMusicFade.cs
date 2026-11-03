using UnityEngine;

namespace BlitzLegacy.Runtime.Ui
{
    public sealed class ResultMusicFade : MonoBehaviour
    {
        private AudioSource _source;
        private float _start, _duration, _elapsed;
        public void Begin(AudioSource source, float duration)
        { _source = source; _start = source.volume; _duration = duration; }
        private void Update()
        {
            if (_source == null) { Destroy(this); return; }
            _elapsed += Time.unscaledDeltaTime;
            _source.volume = Mathf.Lerp(_start, 0, _elapsed / _duration);
            if (_elapsed >= _duration) { _source.Stop(); Destroy(this); }
        }
    }
}
