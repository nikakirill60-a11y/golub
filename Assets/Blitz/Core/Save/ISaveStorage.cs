using System.Collections.Generic;

namespace BlitzLegacy.Core.Save
{
    /// <summary>
    /// Filesystem seam. Unity supplies a persistentDataPath implementation;
    /// tests supply an in-memory one, which is why Core needs no engine types.
    /// </summary>
    public interface ISaveStorage
    {
        bool Exists(string relativePath);

        string ReadAllText(string relativePath);

        void WriteAllText(string relativePath, string contents);

        void Delete(string relativePath);

        IEnumerable<string> ListFiles(string relativeDirectory, string extension);
    }
}
