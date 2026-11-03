using UnityEngine;

namespace BlitzLegacy.Runtime.Hangar.Halloween
{
    /// <summary>
    /// A random state switcher of the Halloween hangar. The client's scenario
    /// draws Random(n), 0..n inclusive, when the entity is added and
    /// Entity.ActivateState shows that state; a draw past the last state shows
    /// none. The draw is repeated every time the hangar is built.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HalloweenHangarStates : MonoBehaviour
    {
        [SerializeField] private Transform[] _states = new Transform[0];
        [SerializeField] private int _randomN;

        /// <summary>The drawn state, -1 before the draw; equal to the state count when none shows.</summary>
        public int Drawn { get; private set; } = -1;

        public int StateCount => _states.Length;

        public int RandomN => _randomN;

        public void Configure(Transform[] states, int randomN)
        {
            _states = states;
            _randomN = randomN;
        }

        /// <summary>Random(n) of the scenario for a uniform value in [0, 1].</summary>
        public static int Draw(float value, int randomN)
        {
            int k = Mathf.FloorToInt(value * (randomN + 1));
            return Mathf.Clamp(k, 0, randomN);
        }

        /// <summary>Shows state <paramref name="k"/> and hides the others; k past the last state hides all.</summary>
        public void Show(int k)
        {
            Drawn = k;
            for (int i = 0; i < _states.Length; i++)
                if (_states[i] != null)
                    _states[i].gameObject.SetActive(i == k);
        }

        private void Start()
        {
            Show(Draw(Random.value, _randomN));
        }
    }
}
