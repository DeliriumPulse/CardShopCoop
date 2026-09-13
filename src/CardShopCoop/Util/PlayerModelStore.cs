using System;
using System.IO;
using BepInEx;
using CardShopCoop.Net.Messages;
using Newtonsoft.Json;

namespace CardShopCoop.Util
{
    /// <summary>Private co-op appearance storage. This file is deliberately under the
    /// BepInEx config directory and never participates in the game's save pipeline.</summary>
    public static class PlayerModelStore
    {
        private static readonly string FilePath = Path.Combine(Paths.ConfigPath, "CardShopCoopPlayerModel.json");

        public static bool TryLoad(out PlayerModelEntry model)
        {
            model = null;
            if (!File.Exists(FilePath))
                return false;
            try
            {
                model = JsonConvert.DeserializeObject<PlayerModelEntry>(File.ReadAllText(FilePath));
                if (model == null || model.ModelIndex < 0)
                    throw new InvalidDataException("missing or invalid model fields");
                return true;
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("local player model could not be loaded; using the game appearance: " + e.Message);
                model = null;
                return false;
            }
        }

        public static void Save(PlayerModelEntry model)
        {
            if (model == null)
                return;
            string temp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Paths.ConfigPath);
                File.WriteAllText(temp, JsonConvert.SerializeObject(model, Formatting.Indented));
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, null);
                else
                    File.Move(temp, FilePath);
            }
            catch (Exception e)
            {
                CoopPlugin.Log.LogWarning("local player model could not be saved: " + e.Message);
                try
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                catch (System.Exception caught) { Swallow.Log(caught); }
            }
        }
    }
}
