using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpSFV
{
    /// <summary>
    /// OPTIMIZATION #4: String Pooling
    /// Reduces managed heap usage by deduplicating strings.
    /// Critical for datasets where thousands of files share the same folder structure.
    /// </summary>
    public static class StringPool
    {
        // Dictionary<string, string> acts as the store.
        // We store the string as both Key and Value so we can return the reference.
        private static readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
        private static readonly object _lock = new();

        /// <summary>
        /// Gets an existing string instance for the provided span, or creates a new one.
        /// Uses .NET 10 AlternateLookup to avoid allocating a key for the lookup.
        /// </summary>
        public static string GetOrAdd(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty) return string.Empty;

            lock (_lock)
            {
                // Lookup dictionary entries using Span<char>
                var lookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();

                if (lookup.TryGetValue(span, out string? existing))
                {
                    return existing;
                }

                // Allocation only happens if the string is unique
                string newString = new string(span);
                _pool.Add(newString, newString);
                return newString;
            }
        }

        /// <summary>
        /// Gets an existing string instance for the provided string.
        /// </summary>
        public static string GetOrAdd(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            lock (_lock)
            {
                if (_pool.TryGetValue(text, out string? existing))
                {
                    return existing;
                }

                _pool.Add(text, text);
                return text;
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _pool.Clear();
            }
        }
    }
}