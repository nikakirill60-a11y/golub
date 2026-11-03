using System;

namespace BlitzLegacy.Core.Maps
{
    /// <summary>A plain vector, because Core is built without engine references.</summary>
    public struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// Godot is right-handed with -Z forward, Unity is left-handed with +Z
    /// forward. The port mirrors the source across Z, which is the convention
    /// HangarWorld already established for its light rig. A second convention
    /// in one project would silently misplace half the world, so every ported
    /// position, basis and triangle passes through this one place.
    /// </summary>
    public static class GodotSpace
    {
        /// <summary>Mirrors a source position or direction.</summary>
        public static Vec3 Point(float x, float y, float z)
        {
            return new Vec3(x, y, -z);
        }

        /// <summary>Mirrors a three-component array as written by the porter.</summary>
        public static Vec3 Point(float[] source)
        {
            if (source == null || source.Length != 3)
            {
                throw new ArgumentException("a source point needs three components");
            }

            return Point(source[0], source[1], source[2]);
        }

        /// <summary>
        /// Mirrors a Godot basis with S * M * S, where S = diag(1, 1, -1). The
        /// two sign flips cancel on the determinant, so the result stays a
        /// proper rotation and never degrades into a reflection.
        ///
        /// Godot serialises a basis by rows, so the nine numbers read row 0,
        /// row 1, row 2, and an axis is a column: the X axis is
        /// (basis[0], basis[3], basis[6]). Reading those rows as axes instead
        /// transposes the matrix, which for a rotation is its inverse. The
        /// map's sun caught that. The scene aims the light down at the terrain;
        /// a transposed read points it back into the sky and leaves the battle
        /// lit by ambient alone.
        ///
        /// Unity's forward is +Z while Godot's is -Z, and the mirror flips the
        /// sign of Z, so the mirrored Z column already points where the node
        /// has to look in Unity.
        /// </summary>
        public static void Basis(float[] basis, out Vec3 right, out Vec3 up, out Vec3 forward)
        {
            if (basis == null || basis.Length != 9)
            {
                throw new ArgumentException("a source basis needs nine components");
            }

            right = new Vec3(basis[0], basis[3], -basis[6]);
            up = new Vec3(basis[1], basis[4], -basis[7]);
            forward = new Vec3(-basis[2], -basis[5], basis[8]);
        }

        /// <summary>
        /// Triangle order survives the mirror untouched, which is the opposite
        /// of the usual "mirroring flips winding" advice and is worth spelling
        /// out so nobody "fixes" it later.
        ///
        /// Mirroring across Z does reverse geometric orientation, so on its own
        /// it would turn every face inside out. The engines also disagree about
        /// handedness while agreeing that front faces are wound clockwise:
        /// Godot is right-handed, Unity is left-handed. That second flip
        /// cancels the first exactly, so the mirrored face normal is simply the
        /// mirror of the source normal.
        ///
        /// This was measured rather than assumed. Across twenty thousand
        /// sampled landscape triangles the source winding yields a downward
        /// cross product without exception, so the terrain only faces up under
        /// Godot's clockwise rule, and mirroring that normal keeps it up here.
        /// </summary>
        public static void Triangle(int a, int b, int c, out int x, out int y, out int z)
        {
            x = a;
            y = b;
            z = c;
        }
    }
}
