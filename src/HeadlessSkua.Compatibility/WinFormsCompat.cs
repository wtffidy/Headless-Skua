// Headless implementations of the small Windows Forms surface used by shared Skua scripts.
// These controls intentionally do not create windows. They preserve script compilation and
// allow progress/status helpers to run without introducing a desktop UI dependency.

namespace System.Windows.Forms;

public delegate void MethodInvoker();
public delegate void PaintEventHandler(object? sender, System.Drawing.PaintEventArgs e);

public enum DockStyle { None, Top, Bottom, Left, Right, Fill }
public enum FormStartPosition { Manual, CenterScreen, WindowsDefaultLocation, WindowsDefaultBounds, CenterParent }
public enum FormBorderStyle { None, FixedSingle, Fixed3D, FixedDialog, Sizable, FixedToolWindow, SizableToolWindow }
public enum ProgressBarStyle { Blocks, Continuous, Marquee }
public enum DialogResult { None, OK, Cancel, Abort, Retry, Ignore, Yes, No }

public class ControlCollection : List<Control>;

public class Control : IDisposable
{
    public string Text { get; set; } = string.Empty;
    public System.Drawing.Size Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DockStyle Dock { get; set; }
    public System.Drawing.Color ForeColor { get; set; }
    public System.Drawing.Color BackColor { get; set; }
    public bool Visible { get; set; }
    public ControlCollection Controls { get; } = new();

    public event PaintEventHandler? Paint;
    public event EventHandler? Shown;

    public void Invoke(Delegate method) => method?.DynamicInvoke();
    public virtual void Dispose() { }

    protected void OnPaint(System.Drawing.PaintEventArgs e) => Paint?.Invoke(this, e);
    protected void OnShown(EventArgs e) => Shown?.Invoke(this, e);
}

public class Form : Control
{
    public FormStartPosition StartPosition { get; set; }
    public FormBorderStyle FormBorderStyle { get; set; }
    public bool MaximizeBox { get; set; }
    public bool MinimizeBox { get; set; }

    public void Close() { }
    public void Show() => OnShown(EventArgs.Empty);
    public DialogResult ShowDialog()
    {
        OnShown(EventArgs.Empty);
        return DialogResult.OK;
    }
}

public class ProgressBar : Control
{
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Value { get; set; }
    public ProgressBarStyle Style { get; set; }
}

public static class Application
{
    public static void Run(Form form) => form.Show();
    public static void DoEvents() { }
}
