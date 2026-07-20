using UnityEngine;

namespace RPG.Stats
{
    /// <summary>
    /// Diagnostic sink for the stat system's runtime managers.
    ///
    /// Every rejection, validation failure and misuse warning the runtime reports goes
    /// through here rather than calling Debug directly. By default it forwards to the
    /// Unity console, so nothing changes for a normal project.
    ///
    /// Assign <see cref="Handler"/> to route messages somewhere else — a game's own
    /// logging system, an in-game console, a mod-loader's per-plugin log, or a test
    /// that wants to assert on a diagnostic without the message reaching the console.
    /// Setting it to null restores the default.
    ///
    /// Editor-time authoring validation (StatDefinition.OnValidate) is deliberately NOT
    /// routed here: it logs with the asset as context so the console entry stays
    /// clickable, and it never runs at runtime.
    /// </summary>
    public static class StatLog
    {
        /// <summary>Receives a diagnostic. Type is Error, Warning or Log.</summary>
        public delegate void LogHandler(LogType type, string message);

        // Held as a field rather than referenced as a method group so identity comparisons
        // (HasCustomHandler) and re-assignment (Reset) work against one stable instance.
        private static readonly LogHandler Default = DefaultHandler;

        private static LogHandler _handler = Default;

        /// <summary>
        /// The active sink. Never null — assigning null restores the Unity console default.
        /// </summary>
        public static LogHandler Handler
        {
            get => _handler;
            set => _handler = value ?? Default;
        }

        /// <summary>True while a custom sink is installed.</summary>
        public static bool HasCustomHandler => _handler != Default;

        /// <summary>Restore the default Unity console sink.</summary>
        public static void Reset() => _handler = Default;

        public static void Error(string message)   => _handler(LogType.Error, message);
        public static void Warning(string message) => _handler(LogType.Warning, message);
        public static void Info(string message)    => _handler(LogType.Log, message);

        private static void DefaultHandler(LogType type, string message)
        {
            switch (type)
            {
                case LogType.Error:
                    Debug.LogError(message);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(message);
                    break;
                default:
                    Debug.Log(message);
                    break;
            }
        }

        /// <summary>
        /// Drop any custom sink on domain reload, matching the managers' static-state reset.
        /// Without this, a sink installed by a test (or a previous play session) would
        /// survive into the next one when domain reload is disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => _handler = Default;
    }
}
