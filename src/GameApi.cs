using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppGasSim;
using Il2CppGasSim.Scripting;
using MelonLoader;
using UnityEngine;

namespace CombustorBlocks
{
    public sealed class PartInfo
    {
        public string Name;
        public PartKind Kind;
        public Il2CppSystem.Object Handle;

        public string Display => Name + "  ·  " + SpanishKind(Kind);

        public static string SpanishKind(PartKind k)
        {
            switch (k)
            {
                case PartKind.Valve: return "válvula";
                case PartKind.Injector: return "inyector";
                case PartKind.Igniter: return "encendedor";
                case PartKind.Hole: return "agujero negro";
                case PartKind.Fan: return "ventilador";
                case PartKind.Filter: return "filtro";
                case PartKind.Gate: return "compuerta";
                case PartKind.Piston: return "pistón";
                case PartKind.Sensor: return "sensor";
                default: return k.ToString();
            }
        }
    }

    public sealed class GasInfo
    {
        public string Name;
        public string Display;

        public override string ToString() => Display ?? Name;
    }

    /// <summary>
    /// Small, defensive bridge to the game's own objects. Everything that depends on
    /// internal names lives here so a game update only breaks one file.
    /// </summary>
    public static class GameApi
    {
        public static ScriptWindow Window { get; private set; }
        public static GasSimulation Sim { get; private set; }
        public static ScriptRig Rig { get; private set; }

        public static readonly List<PartInfo> Parts = new List<PartInfo>();
        public static readonly List<GasInfo> Gases = new List<GasInfo>();
        public static bool ShipAvailable;
        public static bool ShopAvailable;
        public static bool InjectorHeatFixed;

        private static float _nextScan;
        private static int _lastSignature = -1;
        private static string _lastError;
        private static float _simSeenAt = -1f;
        private static bool _gasesLoaded;
        private static bool _gasesTried;

        public static string LastError => _lastError;

        /// <summary>
        /// Generic FindObjectOfType is not always present in the stripped IL2CPP binary,
        /// so ask Unity for objects of a runtime type instead.
        /// </summary>
        public static T FindOne<T>() where T : UnityEngine.Object
        {
            try
            {
                var type = Il2CppInterop.Runtime.Il2CppType.Of<T>();
                var arr = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (arr == null || arr.Length == 0) return null;
                return arr[0].Cast<T>();
            }
            catch (Exception e)
            {
                _lastError = "FindOne<" + typeof(T).Name + ">: " + e.Message;
                return null;
            }
        }

        public static bool DisableScan = false;
        public static GasSimInput GameInput { get; private set; }

        public static void Tick(bool force = false)
        {
            if (DisableScan) return;
            if (!force && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.5f;
            try
            {
                if (Window == null) Window = FindOne<ScriptWindow>();
                if (Sim == null) Sim = FindOne<GasSimulation>();
                if (GameInput == null) GameInput = FindOne<GasSimInput>();
                Rig = Sim != null ? Sim.Script : null;

                if (Rig != null) RefreshParts(true);
                if (Sim != null)
                {
                    if (_simSeenAt < 0) _simSeenAt = force ? Time.unscaledTime - 10f : Time.unscaledTime;
                    if (!_gasesLoaded && !_gasesTried && Rig != null &&
                        Time.unscaledTime > _simSeenAt + 2.5f)
                        RefreshGases();
                }
                if (Rig != null)
                {
                    try { ShipAvailable = Rig.Flight != null; } catch { ShipAvailable = false; }
                    try { ShopAvailable = Rig.Shop != null; } catch { ShopAvailable = false; }
                    try { InjectorHeatFixed = Rig.InjectorHeatFixed; } catch { InjectorHeatFixed = false; }
                }
                else
                {
                    ShipAvailable = false;
                    ShopAvailable = false;
                }
                if (Sim == null)
                {
                    _simSeenAt = -1f;
                    _gasesTried = false;
                    _gasesLoaded = false;
                    Gases.Clear();
                }
                _lastError = null;
            }
            catch (Exception e)
            {
                _lastError = e.GetType().Name + ": " + e.Message;
                MelonLogger.Warning("GameApi scan: " + _lastError);
            }
        }

        public static void RefreshParts(bool force = false)
        {
            if (Rig == null) { Parts.Clear(); return; }
            int sig = 17;
            for (int k = 0; k < 9; k++) sig = sig * 31 + Rig.Count((PartKind)k);
            if (!force && sig == _lastSignature) return;
            _lastSignature = sig;

            Parts.Clear();
            for (int k = 0; k < 9; k++)
            {
                var kind = (PartKind)k;
                int n;
                try { n = Rig.Count(kind); } catch { continue; }
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var handle = Rig.PartAt(kind, i);
                        var name = Rig.NameOf(handle);
                        if (!string.IsNullOrEmpty(name))
                            Parts.Add(new PartInfo { Name = name, Kind = kind, Handle = handle });
                    }
                    catch (Exception e)
                    {
                        MelonLogger.Warning("PartAt " + kind + ": " + e.Message);
                    }
                }
            }
            Parts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        public static void RefreshGases()
        {
            if (_gasesTried && Gases.Count > 0) return;
            _gasesTried = true;
            try
            {
                var species = SimContent.Species;
                if (species == null) return;
                for (int i = 0; i < species.Length; i++)
                {
                    var s = species[i];
                    if (s == null) continue;
                    var name = s.name;
                    if (string.IsNullOrEmpty(name)) continue;
                    var display = s.DisplayName;
                    Gases.Add(new GasInfo { Name = name, Display = string.IsNullOrEmpty(display) ? name : display + "  (" + name + ")" });
                }
                _gasesLoaded = true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("RefreshGases: " + e.Message);
            }
        }

        public static void SetGameInput(bool enabled)
        {
            try
            {
                if (GameInput == null) GameInput = FindOne<GasSimInput>();
                if (GameInput != null) GameInput.enabled = enabled;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("SetGameInput: " + e.Message);
            }
        }

        public static PartInfo FindPart(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Parts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static PartKind KindOf(string name)
        {
            var p = FindPart(name);
            return p?.Kind ?? PartKind.Valve;
        }

        public static List<string> PartNames()
        {
            return Parts.Select(p => p.Name).ToList();
        }

        public static string[] SuggestionParts(PartKind? kind = null)
        {
            if (Parts.Count == 0) return Array.Empty<string>();
            return Parts.Where(p => kind == null || p.Kind == kind).Select(p => p.Name).ToArray();
        }
    }

    public static class ScriptRunner
    {
        public static string LastError = "";
        public static int LastErrorLine;
        public static bool LastRunOk;
        public static float LastRunAt;

        public static bool IsRunning
        {
            get
            {
                try { return GameApi.Rig != null && GameApi.Rig.Running; }
                catch { return false; }
            }
        }

        public static string StatusText()
        {
            var rig = GameApi.Rig;
            if (rig == null) return "Sin motor";
            try
            {
                if (!string.IsNullOrEmpty(rig.ErrorText)) return "Error línea " + rig.ErrorLine + ": " + rig.ErrorText;
                if (rig.Running) return rig.Sleeping ? "Esperando " + rig.SleepLeft.ToString("0.0") + " s" : "Ejecutando";
                if (rig.Finished) return "Terminado";
                return "Detenido";
            }
            catch { return "?"; }
        }

        public static bool Validate(string code, out string error, out int line)
        {
            error = null;
            line = 0;
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    error = "No hay código que ejecutar.";
                    return false;
                }
                GsCompiler.Compile(code);
                return true;
            }
            catch (Exception e)
            {
                error = CleanMessage(e);
                line = ExtractLine(error);
                return false;
            }
        }

        public static string CleanMessage(Exception e)
        {
            var msg = e.Message ?? e.ToString();
            // The interop layer sometimes wraps the real message.
            var idx = msg.IndexOf("ScriptFault", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) msg = msg.Substring(idx);
            return msg.Trim();
        }

        public static int ExtractLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(message, @"(?i)(?:line|línea|linea)\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
            return 0;
        }

        public static bool Run(string code, out string error, out int errorLine)
        {
            error = null;
            errorLine = 0;
            try
            {
                GameApi.Tick(true);
                var sim = GameApi.Sim;
                if (sim == null)
                {
                    error = "Abre un motor en Sandbox para poder ejecutar el programa.";
                    return false;
                }

                if (!Validate(code, out error, out errorLine)) return false;

                var rig = sim.Script;
                if (rig == null)
                {
                    rig = new ScriptRig();
                    sim._script = rig;
                }
                else
                {
                    try { if (rig.Running) rig.Stop(); } catch { }
                }

                // The game pauses when the window loses focus. Running from the
                // browser should still simulate, so clear that pause once.
                try { if (sim.paused) sim.paused = false; } catch { }

                rig.Source = code;
                bool ok = rig.Start(sim);
                GameApi.Tick(true);

                // Keep the game's own code window in sync so the player can read it.
                var win = GameApi.Window;
                if (win != null)
                {
                    try { win.SetSource(code); } catch { }
                }

                LastRunOk = ok && rig.Running;
                LastRunAt = Time.unscaledTime;
                if (!ok)
                {
                    error = "El juego no pudo iniciar el script";
                    var t = rig.ErrorText;
                    if (!string.IsNullOrEmpty(t))
                    {
                        error += " (línea " + rig.ErrorLine + "): " + t;
                        errorLine = rig.ErrorLine;
                    }
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = CleanMessage(e);
                errorLine = ExtractLine(error);
                LastRunOk = false;
                MelonLogger.Error("Run: " + e);
                return false;
            }
        }

        public static void Stop()
        {
            try
            {
                var rig = GameApi.Rig;
                if (rig != null && rig.Running) rig.Stop();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Stop: " + e.Message);
            }
        }
    }
}
