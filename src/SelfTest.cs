using System;
using System.IO;
using System.Linq;
using Il2CppGasSim;
using MelonLoader;
using UnityEngine;

namespace CombustorBlocks
{
    /// <summary>
    /// Headless smoke test. Create .../CombustorBlocks/selftest.flag to run it.
    /// It never touches input, so it is safe to use while the game is in the background.
    /// </summary>
    public static class SelfTest
    {
        private static bool _ran;
        private static int _stage;
        private static float _checkAt = -1;
        private static string _log = "";

        public static void Tick()
        {
            if (_ran) return;
            string flag;
            try { flag = Path.Combine(ProjectStore.Root, "selftest.flag"); }
            catch { return; }
            if (!File.Exists(flag)) return;
            _ran = true;
            try { File.Delete(flag); } catch { }
            Begin();
        }

        public static void Update()
        {
            if (_checkAt <= 0 || Time.unscaledTime < _checkAt) return;
            _checkAt = -1;
            try
            {
                if (_stage == 1)
                {
                    GameApi.Tick(true);
                    MelonLogger.Msg("[SELFTEST] after sandbox: sim=" + (GameApi.Sim != null)
                        + " rig=" + (GameApi.Rig != null)
                        + " parts=" + GameApi.Parts.Count
                        + " input=" + (GameApi.GameInput != null));
                    if (GameApi.Sim == null)
                    {
                        MelonLogger.Warning("[SELFTEST] no simulation; aborting runtime test");
                        return;
                    }
                    RunProgram();
                    _stage = 2;
                    _checkAt = Time.unscaledTime + 4f;
                }
                else if (_stage == 2)
                {
                    Report();
                    _stage = 3;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SELFTEST] stage " + _stage + ": " + e);
            }
        }

        private static void Begin()
        {
            try
            {
                GameApi.Tick(true);
                MelonLogger.Msg("[SELFTEST] begin sim=" + (GameApi.Sim != null)
                    + " window=" + (GameApi.Window != null)
                    + " input=" + (GameApi.GameInput != null)
                    + " rig=" + (GameApi.Rig != null)
                    + " parts=" + GameApi.Parts.Count
                    + " gases=" + GameApi.Gases.Count);

                if (GameApi.Sim == null)
                {
                    MelonLogger.Warning("[SELFTEST] no simulation yet; open Sandbox and create the flag again");
                    return;
                }
                RunProgram();
                _stage = 2;
                _checkAt = Time.unscaledTime + 4f;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[SELFTEST] begin: " + e);
            }
        }

        private static void RunProgram()
        {
            var project = BlockProject.NewProject();
            project.Name = "selftest";
            project.Variables.Add(new VariableDef { Name = "temperatura_objetivo", Value = "800" });
            project.Stacks.Clear();

            var start = new ScriptStack { X = 100, Y = 100 };
            start.Blocks.Add(new BlockNode { Type = "when_start" });
            var set = new BlockNode { Type = "var_set" };
            set.Fields["var"] = "temperatura_objetivo";
            set.Inputs["value"] = new BlockNode { Type = "num", Fields = { { "value", "900" } } };
            start.Blocks.Add(set);
            var hello = new BlockNode
            {
                Type = "print",
                Inputs = { { "a", new BlockNode { Type = "text", Fields = { { "value", "hola desde bloques" } } } } }
            };
            start.Blocks.Add(hello);
            project.Stacks.Add(start);

            var update = new ScriptStack { X = 460, Y = 100 };
            update.Blocks.Add(new BlockNode { Type = "when_update" });
            var ifb = new BlockNode { Type = "if" };
            ifb.Inputs["cond"] = new BlockNode
            {
                Type = "cmp_gt",
                Inputs =
                {
                    { "a", new BlockNode { Type = "var_get", Fields = { { "var", "temperatura_objetivo" } } } },
                    { "b", new BlockNode { Type = "num", Fields = { { "value", "850" } } } }
                }
            };
            ifb.Body.Add(new BlockNode
            {
                Type = "print",
                Inputs = { { "a", new BlockNode { Type = "text", Fields = { { "value", "temperatura alta" } } } } }
            });
            update.Blocks.Add(ifb);
            // Exercise wait(), forever and a nested loop.
            update.Blocks.Add(new BlockNode
            {
                Type = "wait",
                Inputs = { { "seconds", new BlockNode { Type = "num", Fields = { { "value", "0.25" } } } } }
            });
            project.Stacks.Add(update);

            var emit = new GsEmitter(project).Emit();
            MelonLogger.Msg("[SELFTEST] source:\n" + emit.Source);
            _log = emit.Source;

            string err;
            int line;
            bool valid = ScriptRunner.Validate(emit.Source, out err, out line);
            MelonLogger.Msg("[SELFTEST] compile=" + valid + " line=" + line + " err=" + err);
            if (!valid) return;

            bool ok = ScriptRunner.Run(emit.Source, out err, out line);
            MelonLogger.Msg("[SELFTEST] start=" + ok + " line=" + line + " err=" + err);
        }

        private static void Report()
        {
            var rig = GameApi.Rig;
            MelonLogger.Msg("[SELFTEST] DONE running=" + (rig?.Running ?? false)
                + " sleeping=" + (rig?.Sleeping ?? false)
                + " finished=" + (rig?.Finished ?? false)
                + " line=" + (rig?.ErrorLine ?? 0)
                + " err=" + (rig?.ErrorText ?? "")
                + " log=" + (rig?.Log?.Count ?? 0));
            if (rig?.Log != null)
            {
                for (int i = 0; i < rig.Log.Count && i < 12; i++)
                    MelonLogger.Msg("[SELFTEST] out: " + rig.Log[i]);
            }
        }
    }
}
