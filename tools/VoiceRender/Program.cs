using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace VoiceRender
{
    /// <summary>
    /// Renders benchmark voice cues offline via WinRT SpeechSynthesis.
    /// Lists installed voices, picks en-* (Zira preferred), writes WAVs.
    /// Exit 0 = both files written.
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main()
        {
            var voices = SpeechSynthesizer.AllVoices.ToList();
            Console.WriteLine($"installed voices: {voices.Count}");
            foreach (var v in voices)
                Console.WriteLine($"  {v.Language} {v.Gender} \"{v.DisplayName}\"");

            var pick = voices.FirstOrDefault(v =>
                         v.Language.StartsWith("en-US", StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault(v =>
                         v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    ?? voices.FirstOrDefault();
            if (pick == null)
            {
                Console.WriteLine("FAIL: no speech voices installed. Add one via Settings > Time & language > Speech, then re-run.");
                return 1;
            }
            Console.WriteLine($"using: {pick.DisplayName}");

            string outDir = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "Assets", "Sounds"));
            Directory.CreateDirectory(outDir);

            using var synth = new SpeechSynthesizer { Voice = pick };
            int rc = 0;
            rc |= await RenderAsync(synth, "Recording started", Path.Combine(outDir, "voice_started.wav"));
            rc |= await RenderAsync(synth, "Recording stopped", Path.Combine(outDir, "voice_stopped.wav"));
            return rc;
        }

        private static async Task<int> RenderAsync(SpeechSynthesizer synth, string text, string path)
        {
            try
            {
                SpeechSynthesisStream stream = await synth.SynthesizeTextToStreamAsync(text);
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                byte[] bytes = new byte[stream.Size];
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
                // Stream content type is audio/wav: persist bytes verbatim.
                File.WriteAllBytes(path, bytes);
                Console.WriteLine($"wrote {path} ({bytes.Length} bytes, {stream.ContentType})");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL \"{text}\": {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
    }
}
