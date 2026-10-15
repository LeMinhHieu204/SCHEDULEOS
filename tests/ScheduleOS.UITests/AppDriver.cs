using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit.Abstractions;

namespace ScheduleOS.UITests;

/// <summary>
/// Drives the real ScheduleOS.exe through Windows UI Automation patterns only (Invoke, Value, SelectionItem,
/// Toggle). It never moves the mouse or sends keystrokes, so it does not interfere with other windows.
/// </summary>
public sealed class AppDriver : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly StreamWriter _log;
    private Application _app;
    private readonly UIA3Automation _automation = new();

    public AppDriver(ITestOutputHelper output, string dataDir, string captureDir, string logFile)
    {
        _output = output;
        DataDir = dataDir;
        CaptureDir = captureDir;
        Directory.CreateDirectory(captureDir);
        _log = new StreamWriter(logFile, append: true) { AutoFlush = true };
        _app = Launch();
        Window = WaitWindow();
    }

    public string DataDir { get; }
    public string CaptureDir { get; }
    public Window Window { get; private set; }
    public ConditionFactory Cf => _automation.ConditionFactory;

    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScheduleOS.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    public static string ExePath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SCHEDULEOS_EXE");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            foreach (var config in new[] { "Debug", "Release" })
            {
                var p = Path.Combine(RepoRoot, "src", "ScheduleOS.Desktop", "bin", config, "net10.0-windows", "ScheduleOS.exe");
                if (File.Exists(p)) return p;
            }

            throw new FileNotFoundException("Build src/ScheduleOS.Desktop first (ScheduleOS.exe not found).");
        }
    }

    private Application Launch()
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        psi.Environment["SCHEDULEOS_DATA_DIR"] = DataDir;
        psi.Environment["SCHEDULEOS_CAPTURE_DIR"] = CaptureDir;
        Step($"Launching {ExePath}");
        return Application.Launch(psi);
    }

    private Window WaitWindow()
    {
        var w = Retry.WhileNull(() => _app.GetMainWindow(_automation, TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500)).Result
                ?? throw new InvalidOperationException("Main window did not appear.");
        return w;
    }

    public void Step(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        _output.WriteLine(line);
        _log.WriteLine(line);
    }

    public AutomationElement Find(string automationId, int timeoutSeconds = 20)
    {
        var e = Retry.WhileNull(() => Window.FindFirstDescendant(Cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(250)).Result;
        return e ?? throw new InvalidOperationException($"Element '{automationId}' not found.");
    }

    public AutomationElement? TryFind(string automationId, int timeoutSeconds = 3) =>
        Retry.WhileNull(() => Window.FindFirstDescendant(Cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(250)).Result;

    public AutomationElement FindByName(string name, ControlType? type = null, int timeoutSeconds = 15)
    {
        ConditionBase condition = type is null ? Cf.ByName(name) : Cf.ByName(name).And(Cf.ByControlType(type.Value));
        return Retry.WhileNull(() => Window.FindFirstDescendant(condition), TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(250)).Result
               ?? throw new InvalidOperationException($"Element named '{name}' not found.");
    }

    public void Invoke(string automationId)
    {
        var e = Find(automationId);
        Retry.WhileFalse(() => e.IsEnabled, TimeSpan.FromSeconds(10));
        e.Patterns.Invoke.Pattern.Invoke();
        Step($"  invoke {automationId}");
    }

    public void Invoke(AutomationElement e) => e.Patterns.Invoke.Pattern.Invoke();

    public void SetText(string automationId, string text)
    {
        var e = Find(automationId);
        e.Patterns.Value.Pattern.SetValue(text);
        Step($"  set {automationId}");
    }

    public void Select(string automationId)
    {
        Find(automationId).Patterns.SelectionItem.Pattern.Select();
        Step($"  select {automationId}");
    }

    public void SelectComboIndex(string automationId, int index)
    {
        var combo = Find(automationId).AsComboBox();
        combo.Expand();
        Thread.Sleep(300);
        var items = Retry.WhileEmpty(() => combo.Items, TimeSpan.FromSeconds(10)).Result ?? [];
        items[Math.Min(index, items.Length - 1)].Patterns.SelectionItem.Pattern.Select();
        combo.Collapse();
        Step($"  combo {automationId} -> #{index}");
    }

    public void SelectComboByText(AutomationElement comboElement, string startsWith)
    {
        var combo = comboElement.AsComboBox();
        combo.Expand();
        var item = Retry.WhileNull(() => combo.Items.FirstOrDefault(i => (i.Name ?? "").StartsWith(startsWith, StringComparison.OrdinalIgnoreCase)
                                                                         || i.FindFirstDescendant(Cf.ByName(startsWith)) is not null),
            TimeSpan.FromSeconds(10)).Result ?? throw new InvalidOperationException($"Combo item '{startsWith}' not found.");
        item.Patterns.SelectionItem.Pattern.Select();
        combo.Collapse();
    }

    public void Toggle(AutomationElement e, bool on)
    {
        var t = e.Patterns.Toggle.Pattern;
        if ((t.ToggleState == ToggleState.On) != on) t.Toggle();
    }

    public string Text(string automationId, int timeoutSeconds = 20) => Find(automationId, timeoutSeconds).Name ?? "";

    public bool WaitUntil(Func<bool> condition, int timeoutSeconds = 20) =>
        Retry.WhileFalse(() =>
        {
            try
            {
                return condition();
            }
            catch (Exception)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(300)).Result;

    public void Navigate(string navAutomationId, string pageAutomationId)
    {
        Find(navAutomationId, 15).Patterns.SelectionItem.Pattern.Select();
        Find(pageAutomationId, 30);
        Step($"Navigated to {pageAutomationId}");
        Thread.Sleep(1200);
    }

    /// <summary>Asks the app to render its own window to a PNG (never captures the rest of the screen).</summary>
    public string Capture(string fileName, string? size = null)
    {
        var target = Path.Combine(CaptureDir, fileName);
        if (File.Exists(target)) File.Delete(target);
        File.WriteAllText(Path.Combine(CaptureDir, fileName + ".request"), size is null ? "capture" : "size=" + size);
        if (!WaitUntil(() => File.Exists(target), 25)) throw new TimeoutException("Capture timed out: " + fileName);
        Step($"  captured {fileName}");
        return target;
    }

    public void CloseDialogs()
    {
        for (var i = 0; i < 4; i++)
        {
            var close = TryFind("DialogClose", 1);
            if (close is null) return;
            close.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(400);
        }
    }

    public void CloseApp()
    {
        Step("Closing application");
        var pid = _app.ProcessId;
        try
        {
            Find("WindowClose", 5).Patterns.Invoke.Pattern.Invoke();
        }
        catch (Exception)
        {
            _app.Close();
        }

        WaitForExit(pid);
    }

    public static void WaitForExit(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(20000)) p.Kill();
        }
        catch (ArgumentException)
        {
        }
    }

    public void Relaunch()
    {
        _app.Dispose();
        _app = Launch();
        Window = WaitWindow();
    }

    /// <summary>Re-attaches after the application restarted itself (e.g. after a restore).</summary>
    public void AttachAfterRestart(int oldPid)
    {
        WaitForExit(oldPid);
        var newProcess = Retry.WhileNull(() => Process.GetProcessesByName("ScheduleOS").FirstOrDefault(p => p.Id != oldPid), TimeSpan.FromSeconds(40)).Result
                         ?? throw new InvalidOperationException("ScheduleOS did not restart.");
        _app.Dispose();
        _app = Application.Attach(newProcess.Id);
        Window = WaitWindow();
        Step($"Attached to restarted process {newProcess.Id}");
    }

    public int ProcessId => _app.ProcessId;

    public void Dispose()
    {
        try
        {
            if (!_app.HasExited) _app.Close();
            WaitForExit(_app.ProcessId);
        }
        catch (Exception)
        {
        }

        _app.Dispose();
        _automation.Dispose();
        _log.Dispose();
    }
}
