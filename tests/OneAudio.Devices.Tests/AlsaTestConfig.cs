using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OneAudio.Devices.Tests;

/// <summary>
/// alsa-lib has no built-in way to capture from a file, so the tests define a PCM that does in a per-user configuration file. The library reads
/// the file once, when it first loads its configuration, so this runs when the test assembly loads: it points HOME at a scratch directory
/// that holds an <c>.asoundrc</c>, makes the library load its configuration, and puts HOME back.
/// </summary>
internal static partial class AlsaTestConfig
{
    public const string CaptureDevice = "oneaudio-test-capture";
    public const int CaptureFrames = 48_000;

    public static string? InputFile { get; private set; }

    public static bool Ready { get; private set; }

    [LibraryImport("libc.so.6", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport("libasound.so.2", EntryPoint = "snd_config_update")]
    private static partial int ConfigUpdate();

    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "oneaudio-alsa-config-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string input = Path.Combine(dir, "in.raw");
            var bytes = new byte[CaptureFrames * 8];
            for (int i = 0; i < CaptureFrames; i++)
            {
                float v = 0.25f * MathF.Sin(2f * MathF.PI * 440f * i / 48_000f);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), v);
                BitConverter.TryWriteBytes(bytes.AsSpan((i * 8) + 4), v);
            }

            File.WriteAllBytes(input, bytes);
            // ONEAUDIO_TEST_NO_DEFAULT_DEVICE=1 mimics a host without a sound card (a hosted CI runner): the default PCM points at a card
            // that does not exist and carries no description, so the tests have to cope with a missing default the way such a host does.
            string noDefault = Environment.GetEnvironmentVariable("ONEAUDIO_TEST_NO_DEFAULT_DEVICE") == "1"
                ? "pcm.!default { type plug slave.pcm \"hw:99\" }\nctl.!default { type hw card 99 }\n"
                : string.Empty;
            File.WriteAllText(
                Path.Combine(dir, ".asoundrc"),
                noDefault + $$"""
                pcm.{{CaptureDevice}} {
                  type file
                  slave.pcm "null"
                  file "/dev/null"
                  infile "{{input}}"
                  format raw
                }
                """);

            string? home = Environment.GetEnvironmentVariable("HOME");
            if (SetEnv("HOME", dir, 1) != 0)
            {
                return;
            }

            try
            {
                _ = ConfigUpdate();
            }
            finally
            {
                if (home is not null)
                {
                    _ = SetEnv("HOME", home, 1);
                }
            }

            InputFile = input;
            Ready = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            Ready = false;
        }
    }
}
