using System.Diagnostics;
using System.Text;

namespace Sentinel.Platform;

/// <summary>
/// Registers and checks the watchdog scheduled task.
///
/// <para>
/// Created from an XML definition rather than from <c>schtasks</c> switches, and that is not a
/// preference. <c>schtasks /create</c> always writes <c>DisallowStartIfOnBatteries=true</c> and
/// <c>StopIfGoingOnBatteries=true</c>, and it offers no switch to change either. On a laptop running
/// on its battery that means the task is created successfully, looks correct in every listing, and
/// then never runs once — which is exactly how this was found.
/// </para>
/// <para>
/// Arguments go through <see cref="ProcessStartInfo.ArgumentList"/> rather than a hand-built string:
/// the executable path and the config path both go into a value that already contains quotes, and
/// that is the kind of nesting manual escaping gets wrong on paths with spaces.
/// </para>
/// </summary>
public static class ScheduledTask
{
    public const string WatchdogTaskName = "SentinelWatchdog";

    /// <summary>The task runs this executable with this switch, once a minute, as SYSTEM.</summary>
    public const string WatchdogArgument = "--watchdog";

    public static bool Exists(string taskName)
    {
        using var process = Start("schtasks.exe", ["/query", "/tn", taskName]);
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }

    /// <summary>Creates or refreshes the watchdog task. Needs administrator rights.</summary>
    public static bool Ensure(string taskName, string executablePath, string arguments, out string? error)
    {
        error = null;

        var definition = Path.Combine(Path.GetTempPath(), $"{taskName}-{Guid.NewGuid():N}.xml");

        try
        {
            // schtasks reads this as UTF-16. Written as UTF-8 the localised description comes out
            // mangled, and on some builds the file is rejected outright.
            File.WriteAllText(definition, BuildXml(executablePath, arguments), Encoding.Unicode);

            using var process = Start("schtasks.exe", ["/create", "/tn", taskName, "/xml", definition, "/f"]);

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);

            if (process.HasExited && process.ExitCode == 0)
                return true;

            error = output.Trim();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try
            {
                File.Delete(definition);
            }
            catch
            {
                // A leftover temp file is not worth failing the registration over.
            }
        }
    }

    public static bool Delete(string taskName)
    {
        using var process = Start("schtasks.exe", ["/delete", "/tn", taskName, "/f"]);
        process.WaitForExit(10_000);
        return process.HasExited && process.ExitCode == 0;
    }

    /// <summary>
    /// The task definition. Public so a test can read the power settings straight out of it — the
    /// failure this guards against is invisible in every listing schtasks can produce.
    /// </summary>
    public static string BuildXml(string executablePath, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Sentinel 看门狗：每分钟核对服务配置，并在服务不在时把它拉起来。</Description>
          </RegistrationInfo>
          <Triggers>
            <TimeTrigger>
              <Repetition>
                <Interval>PT1M</Interval>
                <StopAtDurationEnd>false</StopAtDurationEnd>
              </Repetition>
              <StartBoundary>2020-01-01T00:00:00</StartBoundary>
              <Enabled>true</Enabled>
            </TimeTrigger>
            <BootTrigger>
              <Enabled>true</Enabled>
            </BootTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(executablePath)}</Command>
              <Arguments>{Escape(arguments)}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static Process Start(string file, string[] arguments)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info };
        process.Start();
        return process;
    }
}
