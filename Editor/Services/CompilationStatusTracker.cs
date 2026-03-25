using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// Tracks Unity compilation status, domain reload lifecycle, and collects compiler errors.
    /// Pushes events through BridgeServer to the CoBuddy app.
    /// Modelled after the reference app's full lifecycle tracking:
    ///   CompilationStarted → CompilationFinished → BeforeAssemblyReload → DomainUnload → AfterAssemblyReload
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationStatusTracker
    {
        /// <summary>Domain reload lifecycle states, matching the reference app's pattern.</summary>
        public enum DomainReloadState
        {
            AfterAssemblyReload,
            DomainUnload,
            BeforeAssemblyReload,
            CompilationFinished,
            CompilationStarted,
        }

        private static bool _isCompiling;
        private static readonly List<CompilationErrorEntry> Errors = new List<CompilationErrorEntry>();
        private static readonly object Lock = new object();
        private static DomainReloadState _lastKnownState = DomainReloadState.AfterAssemblyReload;

        [Serializable]
        public class CompilationErrorEntry
        {
            public string file;
            public int line;
            public int column;
            public string message;
        }

        static CompilationStatusTracker()
        {
            // Unsubscribe first to avoid duplicate handlers after domain reload
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            // Assembly reload lifecycle events
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            AppDomain.CurrentDomain.DomainUnload -= OnDomainUnload;
            AppDomain.CurrentDomain.DomainUnload += OnDomainUnload;

            // On static constructor (runs after assembly reload), send AfterAssemblyReload
            // Use delayCall to ensure BridgeServer is initialized first
            EditorApplication.delayCall += () =>
            {
                SendDomainReloadState(DomainReloadState.AfterAssemblyReload);
            };
        }

        private static void OnCompilationStarted(object context)
        {
            lock (Lock)
            {
                _isCompiling = true;
                Errors.Clear();
            }
            BridgeServer.InvalidateIndexCache();
            BridgeServer.PushCompileStart();
            SendDomainReloadState(DomainReloadState.CompilationStarted);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null) return;

            lock (Lock)
            {
                foreach (var msg in messages)
                {
                    if (msg.type == CompilerMessageType.Error)
                    {
                        Errors.Add(new CompilationErrorEntry
                        {
                            file = msg.file ?? "",
                            line = msg.line,
                            column = msg.column,
                            message = msg.message ?? ""
                        });
                    }
                }
            }
        }

        private static void OnCompilationFinished(object context)
        {
            lock (Lock) { _isCompiling = false; }
            BridgeServer.InvalidateIndexCache();
            var errors = GetErrors();
            BridgeServer.PushCompileFinish(errors);
            SendDomainReloadState(DomainReloadState.CompilationFinished, errors);
        }

        private static void OnBeforeAssemblyReload()
        {
            SendDomainReloadState(DomainReloadState.BeforeAssemblyReload);
            // Pause the WS client cleanly before domain unloads
            BridgeServer.PauseWebSocket();
        }

        private static void OnDomainUnload(object sender, EventArgs e)
        {
            // Best-effort — connection may already be torn down
            SendDomainReloadState(DomainReloadState.DomainUnload);
        }

        private static void OnAfterAssemblyReload()
        {
            SendDomainReloadState(DomainReloadState.AfterAssemblyReload);
        }

        /// <summary>Push domain reload state change to the app via WS and SSE.</summary>
        private static void SendDomainReloadState(DomainReloadState state, CompilationErrorEntry[] errors = null)
        {
            _lastKnownState = state;

            // Build errors JSON array
            string errorsJson = "[]";
            if (errors != null && errors.Length > 0)
            {
                var sb = new System.Text.StringBuilder("[");
                int count = Math.Min(errors.Length, 50);
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append($"\"{BridgeServer.EscapeJson(errors[i].message ?? "")}\"");
                }
                sb.Append("]");
                errorsJson = sb.ToString();
            }

            bool isReloading = state != DomainReloadState.AfterAssemblyReload;
            string stateName = state.ToString();
            string payload = $"{{\"isReloading\":{(isReloading ? "true" : "false")},\"state\":\"{stateName}\",\"errors\":{errorsJson}}}";

            BridgeServer.PushEvent("domainReloadStateChanged", payload);
        }

        public static DomainReloadState LastKnownState => _lastKnownState;

        public static bool IsCompiling
        {
            get { lock (Lock) return _isCompiling; }
        }

        public static CompilationErrorEntry[] GetErrors()
        {
            lock (Lock) { return Errors.ToArray(); }
        }
    }
}
