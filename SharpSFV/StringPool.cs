using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpSFV
{
    /// <summary>
    /// A thread-safe string interning pool designed to reduce managed heap allocations.
    /// <para>
    /// <b>Problem Solved:</b> 
    /// When scanning deep directory structures (e.g., "C:\Data\Photos\2021\"), thousands of files 
    /// share identical directory strings. Standard string allocation creates a new object for every file, 
    /// wasting megabytes of RAM.
    /// </para>
    /// <para>
    /// <b>Solution:</b> 
    /// This pool stores unique strings. If a requested string exists, the existing reference is returned.
    /// Unlike <c>String.Intern</c>, this pool can be cleared to release memory.
    /// </para>
    /// </summary>
    public static class StringPool
    {
        // Dictionary acts as the store. Key=String, Value=String.
        // We store the string as the Value so we can return the reference to the caller.
        private static readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
        private static readonly object _lock = new();

        /// <summary>
        /// Gets an existing string instance for the provided span, or creates a new one if not found.
        /// <para>
        /// <b>Optimization:</b> 
        /// Uses <c>GetAlternateLookup&lt;ReadOnlySpan&lt;char&gt;&gt;</c>. 
        /// This allows us to query the Dictionary using a stack-allocated Span 
        /// <i>without</i> allocating a temporary string just to perform the lookup key comparison.
        /// A new string is allocated on the heap <i>only</i> if it does not already exist.
        /// </para>
        /// </summary>
        /// <param name="span">The character span to look up.</param>
        /// <returns>The pooled string reference.</returns>
        public static string GetOrAdd(ReadOnlySpan<char> span)
        {
            if (span.IsEmpty) return string.Empty;

            lock (_lock)
            {
                // .NET 9+ feature: Lookup dictionary entries using Span<char> to avoid allocation on hit.
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
        /// Used when the input is already a string but we want to discard duplicates to save memory long-term.
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

        /// <summary>
        /// Clears the pool. Should be called when the application state is reset (e.g., closing a file list)
        /// to allow the Garbage Collector to reclaim the pooled strings.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _pool.Clear();
            }
        }
    }
}