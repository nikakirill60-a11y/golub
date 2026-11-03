using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace BlitzLegacy.Core.Security
{
    /// <summary>
    /// Anti memory-editing (Cheat Engine / GameGuardian). A scan for the
    /// on-screen value only finds a plaintext decoy; the real value lives XOR-ed
    /// with a per-instance key. Editing or freezing the decoy is reported to
    /// <see cref="Detected"/> and has no effect on gameplay. This is a threshold for casual cheaters, not
    /// a guarantee; the authoritative checks live in the network layer.
    /// </summary>
    public static class AntiTamper
    {
        private static int _seed = Environment.TickCount;
        private static int _hits;

        /// <summary>Raised once per detected mismatch; argument names the value.</summary>
        public static event Action<string> Detected;

        public static int Hits { get { return _hits; } }

        internal static int NextKey()
        {
            // xorshift over a shared seed: cheap, never zero, differs per instance.
            int x = Interlocked.Add(ref _seed, unchecked((int)0x9E3779B9));
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            return x == 0 ? 0x5F3759DF : x;
        }

        internal static void Report(string what)
        {
            Interlocked.Increment(ref _hits);
            Detected?.Invoke(what);
        }

        /// <summary>Test hook.</summary>
        public static void ResetForTests() { _hits = 0; Detected = null; }
    }

    public struct ObscuredInt
    {
        private int _key, _value, _decoy;

        public ObscuredInt(int value) { _key = AntiTamper.NextKey(); _value = value ^ _key; _decoy = value; }

        public int Value
        {
            get { return Get("int"); }
            set { this = new ObscuredInt(value); }
        }

        /// <summary>The real value; <paramref name="what"/> names it in a tamper report.</summary>
        public int Get(string what)
        {
            if (_key == 0) { return _decoy; } // default(ObscuredInt) == 0
            int v = _value ^ _key;
            if (_decoy != v) { AntiTamper.Report(what); _decoy = v; }
            return v;
        }

        public static implicit operator ObscuredInt(int v) { return new ObscuredInt(v); }
        public static implicit operator int(ObscuredInt o) { return o.Value; }
        public override string ToString() { return Value.ToString(); }
    }

    public struct ObscuredFloat
    {
        private int _key, _value;
        private float _decoy;

        public ObscuredFloat(float value)
        {
            _key = AntiTamper.NextKey();
            _value = BitConverter.SingleToInt32Bits(value) ^ _key;
            _decoy = value;
        }

        public float Value
        {
            get { return Get("float"); }
            set { this = new ObscuredFloat(value); }
        }

        /// <summary>The real value; <paramref name="what"/> names it in a tamper report.</summary>
        public float Get(string what)
        {
            if (_key == 0) { return _decoy; }
            float v = BitConverter.Int32BitsToSingle(_value ^ _key);
            // bitwise compare so NaN does not count as tampering
            if (BitConverter.SingleToInt32Bits(_decoy) != BitConverter.SingleToInt32Bits(v))
            { AntiTamper.Report(what); _decoy = v; }
            return v;
        }

        public static implicit operator ObscuredFloat(float v) { return new ObscuredFloat(v); }
        public static implicit operator float(ObscuredFloat o) { return o.Value; }
        public override string ToString() { return Value.ToString(); }
    }

    /// <summary>
    /// A float list whose elements are <see cref="ObscuredFloat"/>: the
    /// autoloader's per-shell timers would otherwise sit in a plain float[]
    /// that a memory scanner finds next to the list header.
    /// </summary>
    public sealed class ObscuredFloatList : IReadOnlyList<float>
    {
        private readonly List<ObscuredFloat> _items = new List<ObscuredFloat>();
        private readonly string _what;

        public ObscuredFloatList(string what) { _what = what; }

        public int Count { get { return _items.Count; } }

        public float this[int index]
        {
            get
            {
                // List<T>'s indexer returns a copy, so the decoy re-sync has
                // to be written back to stop one edit from reporting forever.
                var item = _items[index];
                var v = item.Get(_what);
                _items[index] = item;
                return v;
            }
        }

        public void Add(float value) { _items.Add(value); }

        public void Clear() { _items.Clear(); }

        public IEnumerator<float> GetEnumerator()
        {
            for (var i = 0; i < _items.Count; i++) { yield return this[i]; }
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
}
