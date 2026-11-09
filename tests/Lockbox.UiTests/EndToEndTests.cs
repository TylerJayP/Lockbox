using Avalonia;
using Avalonia.Headless;
using Xunit;
using Xunit.Abstractions;

namespace Lockbox.UiTests;

/// <summary>
/// Runs the real Lockbox window headlessly through create, unlock, edit, import, export, settings,
/// recovery and more (see <see cref="Smoke"/>), then checks what ended up on disk.
/// </summary>
public sealed class EndToEndTests(ITestOutputHelper output)
{
    [Fact]
    public void Main_flows_work_end_to_end()
    {
        Environment.SetEnvironmentVariable("LOCKBOX_SETTINGS_DIR", Directory.CreateTempSubdirectory("lockbox-uitest-settings-").FullName);
        AppBuilder.Configure<Lockbox.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        int failures;
        try { failures = Smoke.RunAll(); }
        catch (Exception ex)
        {
            throw new Exception($"The run stopped after: {Smoke.Log.LastOrDefault() ?? "(no checks yet)"}\n\n" +
                                string.Join('\n', Smoke.Log.Where(l => l.StartsWith("FAIL"))), ex);
        }

        foreach (var line in Smoke.Log) output.WriteLine(line);
        Assert.True(failures == 0, $"{failures} of {Smoke.Log.Count} checks failed:\n" + string.Join('\n', Smoke.Log.Where(l => l.StartsWith("FAIL"))));
    }
}
