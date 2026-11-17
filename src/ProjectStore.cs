using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace CombustorBlocks
{
    public static class ProjectStore
    {
        private static string _root;

        public static string Root
        {
            get
            {
                if (_root == null)
                {
                    // Keep everything next to the game, like the reference PNG mod:
                    // <game>/UserData/CombustorBlocks
                    try
                    {
                        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                        var dir = Path.GetDirectoryName(exe);
                        _root = Path.Combine(dir, "UserData", "CombustorBlocks");
                    }
                    catch
                    {
                        _root = Path.Combine(Application.persistentDataPath, "CombustorBlocks");
                    }
                    try { Directory.CreateDirectory(_root); } catch { }
                }
                return _root;
            }
        }

        public static string AutosavePath => Path.Combine(Root, "autosave.json");

        public static string Save(BlockProject project, string fileName)
        {
            try
            {
                Directory.CreateDirectory(Root);
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "programa";
                if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) fileName += ".json";
                var path = Path.Combine(Root, fileName);
                var json = JsonConvert.SerializeObject(project, Formatting.Indented);
                File.WriteAllText(path, json);
                return path;
            }
            catch (Exception e)
            {
                MelonLogger.Error("Save: " + e);
                return null;
            }
        }

        public static BlockProject Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var p = JsonConvert.DeserializeObject<BlockProject>(json);
                p?.Fix();
                return p;
            }
            catch (Exception e)
            {
                MelonLogger.Error("Load: " + e);
                return null;
            }
        }

        public static List<string> ListFiles()
        {
            try
            {
                if (!Directory.Exists(Root)) return new List<string>();
                return Directory.GetFiles(Root, "*.json")
                    .Select(Path.GetFileName)
                    .OrderByDescending(f => File.GetLastWriteTime(Path.Combine(Root, f)))
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void Delete(string fileName)
        {
            try
            {
                var path = Path.Combine(Root, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("Delete: " + e.Message);
            }
        }

        public static void WriteCode(string code)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(Path.Combine(Root, "programa.gs"), code ?? "");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("WriteCode: " + e.Message);
            }
        }
    }
}
