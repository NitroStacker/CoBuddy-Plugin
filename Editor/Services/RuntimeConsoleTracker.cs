using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Tracks runtime console errors (LogType.Error, LogType.Exception) via Application.logMessageReceived.
    /// Used by CoBuddy to detect runtime errors after scripts are applied and Play mode is used.
    /// </summary>
    [InitializeOnLoad]
    public static class RuntimeConsoleTracker
    {
        private const int MaxEntries = 50;
        private static readonly List<RuntimeConsoleEntry> Entries = new List<RuntimeConsoleEntry>();
        private static readonly object Lock = new object();

        [Serializable]
        public class RuntimeConsoleEntry
        {
            public string message;
            public string stackTrace;
            public string logType;
        }

        static RuntimeConsoleTracker()
        {
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception)
                return;

            lock (Lock)
            {
                Entries.Add(new RuntimeConsoleEntry
                {
                    message = message ?? "",
                    stackTrace = stackTrace ?? "",
                    logType = type.ToString()
                });
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(0);
            }
            // Push to any connected SSE clients immediately
            BridgeServer.PushRuntimeError(message, stackTrace, type.ToString());
        }

        /// <summary>
        /// Returns recent runtime errors. Call Clear() to reset after fixes are applied.
        /// </summary>
        public static RuntimeConsoleEntry[] GetRecentErrors()
        {
            lock (Lock)
            {
                return Entries.ToArray();
            }
        }

        /// <summary>
        /// Clears the error buffer. Call after applying fixes or when starting a fresh verification.
        /// </summary>
        public static void Clear()
        {
            lock (Lock)
            {
                Entries.Clear();
            }
        }
    }
}
