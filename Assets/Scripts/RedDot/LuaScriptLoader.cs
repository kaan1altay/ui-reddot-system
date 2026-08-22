using System;
using System.Collections.Generic;
using System.IO;

namespace RedDot
{
    /// <summary>
    /// Resolves <c>require("reddot.manager")</c> to a file on disk, searching an ordered
    /// list of roots and returning the first hit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ordering is the point. Roots added as patches go to the front, so a
    /// downloaded patch folder containing <c>reddot/rules.lua</c> shadows the shipped
    /// copy without touching it. That is the same mechanism a live game uses to fix a
    /// badge on a Tuesday afternoon: ship a file, restart the Lua environment (or call
    /// <see cref="RedDotBridge.ReloadRulesFromModule"/>), done.
    /// </para>
    /// <para>
    /// Reading straight from the file system suits the Editor and the demo. A shipping
    /// build would swap the body of <see cref="Load"/> for an AssetBundle or
    /// StreamingAssets read; nothing above this class would notice, because the search
    /// order is the only contract.
    /// </para>
    /// </remarks>
    public sealed class LuaScriptLoader
    {
        private readonly List<string> _roots = new List<string>();

        private readonly Dictionary<string, string> _resolved =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<string> Roots => _roots;

        /// <summary>Where each module was actually loaded from. Handy in the patch demo.</summary>
        public IReadOnlyDictionary<string, string> ResolvedModules => _resolved;

        /// <summary>
        /// Adds a search root. <paramref name="asPatch"/> puts it in front of everything
        /// already registered, which is what makes it shadow the base scripts.
        /// </summary>
        public void AddRoot(string absolutePath, bool asPatch = false)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                throw new ArgumentException("Lua root must not be empty.", nameof(absolutePath));
            }

            var normalized = absolutePath.Replace('\\', '/').TrimEnd('/');

            _roots.Remove(normalized);
            if (asPatch)
            {
                _roots.Insert(0, normalized);
            }
            else
            {
                _roots.Add(normalized);
            }
        }

        public bool RemoveRoot(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return false;
            }

            return _roots.Remove(absolutePath.Replace('\\', '/').TrimEnd('/'));
        }

        /// <summary>
        /// The xLua custom loader. <paramref name="chunkName"/> arrives as the module name
        /// (<c>reddot.manager</c>) and is rewritten to the resolved file path, so Lua
        /// stack traces point at a real file.
        /// </summary>
        public byte[] Load(ref string chunkName)
        {
            if (string.IsNullOrEmpty(chunkName))
            {
                return null;
            }

            var relative = chunkName.Replace('.', '/') + ".lua";

            foreach (var root in _roots)
            {
                var candidate = root + "/" + relative;
                if (!File.Exists(candidate))
                {
                    continue;
                }

                _resolved[chunkName] = candidate;
                chunkName = candidate;
                return File.ReadAllBytes(candidate);
            }

            // Returning null lets xLua fall through to its remaining loaders and, if
            // they all decline, produce Lua's own "module not found" error listing the
            // paths it tried.
            return null;
        }

        /// <summary>True when the module resolves to a root registered as a patch.</summary>
        public bool TryGetSource(string moduleName, out string path)
        {
            return _resolved.TryGetValue(moduleName, out path);
        }

        public void ForgetResolved()
        {
            _resolved.Clear();
        }
    }
}
