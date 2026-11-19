using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CombustorBlocks.BlocksMod), "Combustor Blocks", "0.2.0", "Combustor Blocks")]
[assembly: MelonGame("DefaultCompany", "EngineSimulator")]

namespace CombustorBlocks
{
    public class BlocksMod : MelonMod
    {
        public static BlocksMod Instance;

        public override void OnInitializeMelon()
        {
            Instance = this;
            MelonLogger.Msg("Combustor Blocks listo. Pulsa F7 en Sandbox para abrir el editor.");

            try
            {
                Application.runInBackground = true;
                var _ = ProjectStore.Root;
                WebServer.Start();
                MelonLogger.Msg("Abre el editor en el navegador: " + WebServer.Url + " (F7)");
            }
            catch (Exception e)
            {
                MelonLogger.Error("No se pudo iniciar el editor web: " + e);
            }

        }

        private static float _statusAt;

        public override void OnUpdate()
        {
            try
            {
                Application.runInBackground = true;
                ApiBridge.Pump();
                if (Time.unscaledTime >= _statusAt)
                {
                    _statusAt = Time.unscaledTime + 0.4f;
                    ApiState.BuildStatus();
                }

                SelfTest.Tick();
                SelfTest.Update();

                try
                {
                    if (UnityEngine.InputSystem.Keyboard.current != null &&
                        UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame)
                    {
                        WebServer.OpenBrowser();
                    }
                }
                catch { }

                try
                {
                    var flag = System.IO.Path.Combine(ProjectStore.Root, "openweb.flag");
                    if (System.IO.File.Exists(flag))
                    {
                        System.IO.File.Delete(flag);
                        WebServer.OpenBrowser();
                    }
                }
                catch { }
            }
            catch (Exception e)
            {
                MelonLogger.Error("OnUpdate: " + e);
            }
        }
    }
}
