using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Writes Unity console output (errors, warnings, logs) to CoBuddyConsoleLog.txt at project root.
    /// CoBuddy can read this file when "console" is requested in readFiles, or the user can @ it.
    /// </summary>
    [InitializeOnLoad]
    public static class ConsoleLogExporter
    {
        private const int MaxEntries = 100;
        private static readonly List<LogEntry> Entries = new List<LogEntry>();
        private static readonly object Lock = new object();
        private static string _logFilePath;

        private struct LogEntry
        {
            public string message;
            public string stackTrace;
            public string logType;
            public string timestamp;
        }

        static ConsoleLogExporter()
        {
            _logFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "CoBuddyConsoleLog.txt");
            Application.logMessageReceived += OnLogMessageReceived;
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(stackTrace))
                return;

            var entry = new LogEntry
            {
                message = message ?? "",
                stackTrace = stackTrace ?? "",
                logType = type.ToString(),
                timestamp = DateTime.Now.ToString("HH:mm:ss.fff")
            };

            lock (Lock)
            {
                Entries.Add(entry);
                while (Entries.Count > MaxEntries)
                    Entries.RemoveAt(0);
                FlushToFileUnsafe();
            }
        }

        private static void FlushToFileUnsafe()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# Unity Console Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("# CoBuddy auto-generated. Use readFiles: [\"console\"] or @CoBuddyConsoleLog.txt");
                sb.AppendLine();
                foreach (var e in Entries)
                {
                    sb.AppendLine($"[{e.timestamp}] [{e.logType}] {e.message}");
                    if (!string.IsNullOrEmpty(e.stackTrace))
                        sb.AppendLine(e.stackTrace);
                }
                File.WriteAllText(_logFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CoBuddy] ConsoleLogExporter: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the current log file path. Used by BridgeServer when serving console content.
        /// </summary>
        public static string GetLogFilePath() => _logFilePath;

        /// <summary>
        /// Returns the full console log content as a string.
        /// </summary>
        public static string GetLogContent()
        {
            lock (Lock)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var e in Entries)
                {
                    sb.AppendLine($"[{e.timestamp}] [{e.logType}] {e.message}");
                    if (!string.IsNullOrEmpty(e.stackTrace))
                        sb.AppendLine(e.stackTrace);
                }
                return sb.ToString();
            }
        }
    }
}
