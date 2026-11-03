using System;
using System.Text;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>
    /// Cursor over the little-endian payloads Tools/port_godot_map.py bakes.
    /// Every read is bounds-checked, so a truncated or stale file fails with a
    /// sentence rather than an index error, which is the contract
    /// <see cref="TerrainHeightField"/> already sets for baked map data.
    /// </summary>
    internal sealed class BakedPayload
    {
        private readonly byte[] _bytes;
        private readonly string _label;
        private int _cursor;

        internal BakedPayload(byte[] bytes, string label, string magic)
        {
            if (bytes == null)
            {
                throw new ArgumentException(label + " payload is missing");
            }

            if (!BitConverter.IsLittleEndian)
            {
                throw new NotSupportedException(label + " payload is little-endian");
            }

            _bytes = bytes;
            _label = label;
            Require(magic.Length);
            for (var i = 0; i < magic.Length; i++)
            {
                if (bytes[i] != (byte)magic[i])
                {
                    throw new ArgumentException(label + " payload has the wrong magic");
                }
            }

            _cursor = magic.Length;
        }

        internal bool AtEnd
        {
            get { return _cursor == _bytes.Length; }
        }

        internal int ReadInt()
        {
            Require(4);
            var value = BitConverter.ToInt32(_bytes, _cursor);
            _cursor += 4;
            return value;
        }

        /// <summary>Length-prefixed UTF-8, as the porter writes entry names.</summary>
        internal string ReadName()
        {
            var length = ReadInt();
            if (length <= 0)
            {
                throw new ArgumentException(_label + " payload has an unnamed entry");
            }

            Require(length);
            var name = Encoding.UTF8.GetString(_bytes, _cursor, length);
            _cursor += length;
            return name;
        }

        internal float[] ReadFloats(int count)
        {
            var bytes = count * 4;
            Require(bytes);
            var values = new float[count];
            Buffer.BlockCopy(_bytes, _cursor, values, 0, bytes);
            _cursor += bytes;
            return values;
        }

        /// <summary>
        /// Indices are written as uint32. Every value a mesh this size can hold
        /// shares its bit pattern with int32, and anything larger arrives
        /// negative, where the caller's range check rejects it.
        /// </summary>
        internal int[] ReadIndices(int count)
        {
            var bytes = count * 4;
            Require(bytes);
            var values = new int[count];
            Buffer.BlockCopy(_bytes, _cursor, values, 0, bytes);
            _cursor += bytes;
            return values;
        }

        internal void ExpectEnd()
        {
            if (!AtEnd)
            {
                throw new ArgumentException(_label + " payload has trailing bytes");
            }
        }

        private void Require(int bytes)
        {
            if (bytes < 0 || _cursor + bytes > _bytes.Length)
            {
                throw new ArgumentException(_label + " payload is truncated");
            }
        }
    }
}
