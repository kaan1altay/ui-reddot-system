using System;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// Durability for the "the player has already looked at this" flags.
    /// </summary>
    /// <remarks>
    /// The whole seen set is one opaque string. Lua owns the format (a sorted,
    /// pipe-separated list of node paths) so that adding a badge never needs a change
    /// on this side, and so that nothing but strings crosses the Lua/C# boundary.
    /// </remarks>
    public interface ISeenPersistence
    {
        /// <summary>Returns the previously saved blob, or <c>null</c> when there is none.</summary>
        string Load();

        void Save(string blob);
    }

    /// <summary>Session-scoped store. Used by the tests and by the demo's "reset" button.</summary>
    public sealed class InMemorySeenPersistence : ISeenPersistence
    {
        public string Blob { get; private set; }

        /// <summary>How often Lua has written. Lets tests assert that saves are not chatty.</summary>
        public int SaveCount { get; private set; }

        public int LoadCount { get; private set; }

        public InMemorySeenPersistence(string initialBlob = null)
        {
            Blob = initialBlob;
        }

        public string Load()
        {
            LoadCount++;
            return Blob;
        }

        public void Save(string blob)
        {
            SaveCount++;
            Blob = blob;
        }

        public void Reset()
        {
            Blob = null;
            SaveCount = 0;
            LoadCount = 0;
        }
    }

    /// <summary>
    /// The shipping store. PlayerPrefs is the right size for this: the seen set is a
    /// few hundred bytes of purely cosmetic state that nobody needs to migrate.
    /// </summary>
    public sealed class PlayerPrefsSeenPersistence : ISeenPersistence
    {
        private readonly string _key;

        public PlayerPrefsSeenPersistence(string key = "reddot.seen")
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("PlayerPrefs key must not be empty.", nameof(key));
            }

            _key = key;
        }

        public string Load()
        {
            return PlayerPrefs.GetString(_key, null);
        }

        public void Save(string blob)
        {
            PlayerPrefs.SetString(_key, blob ?? string.Empty);
            PlayerPrefs.Save();
        }
    }
}
