using BlitzLegacy.Core.Audio;
using UnityEditor;
using UnityEngine;

namespace BlitzLegacy.EditorTools
{
    /// <summary>
    /// Godot ran its mixer at 22050 Hz (project.godot audio/driver/mix_rate),
    /// which resampled the login stems down from their native 48 kHz. The port
    /// keeps the source rate instead, so nothing is thrown away before output.
    /// </summary>
    public static class AudioProjectSettings
    {
        public const int SampleRate = AudioOutput.SampleRate;

        /// <summary>Runtime requests this value from the actual output device.</summary>
        public const int DspBufferSize = AudioOutput.DspBufferSize;

        public static void Apply()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Blitz] AudioManager.asset could not be loaded");
                return;
            }

            // m_DSPBufferSize is the current editor device's resolved value and
            // Unity rewrites it on shutdown. Android is configured at runtime
            // through AudioSettings.Reset instead of falsifying the Linux value.
            var settings = new SerializedObject(assets[0]);
            SetInt(settings, "m_SampleRate", SampleRate);
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            Debug.Log(
                "[Blitz] audio project: " + SampleRate +
                " Hz; runtime DSP request " + DspBufferSize);
        }

        private static void SetInt(SerializedObject target, string property, int value)
        {
            var found = target.FindProperty(property);
            if (found == null)
            {
                Debug.LogWarning("[Blitz] audio property not found: " + property);
                return;
            }

            found.intValue = value;
        }
    }
}
