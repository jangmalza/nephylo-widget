using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace UsageWidget
{
    public partial class WidgetWindow
    {
        internal const double CompactWidgetHeight = 44;
        private HwndSource widgetGeometrySource;
        private DispatcherOperation widgetGeometryRefresh;
        private bool widgetGeometryMoving, widgetGeometryApplying, widgetGeometryDisposed;
        private bool widgetGeometryRefreshRequested;
        private bool widgetGeometryVerticalResize, widgetGeometryHorizontalResize;
        private double widgetGeometryInitialHeight, widgetGeometryInitialWidth;

        private void InitializeWidgetGeometry()
        {
            ApplyWidgetGeometry();
            // The final monitor DPI is available only after the HWND has been created.
            SourceInitialized += WidgetGeometrySourceInitialized;
            LocationChanged += WidgetGeometryLocationChanged;
            Closed += WidgetGeometryClosed;
            Dispatcher.ShutdownStarted += WidgetGeometryClosed;
            SystemEvents.DisplaySettingsChanged += WidgetGeometryDisplaySettingsChanged;
        }

        private void WidgetGeometrySourceInitialized(object sender, EventArgs args)
        {
            if (widgetGeometryDisposed) return;
            widgetGeometrySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (widgetGeometrySource != null) {
                widgetGeometrySource.AddHook(WidgetGeometryMessage);
                widgetGeometrySource.Disposed += WidgetGeometryClosed;
            }
            QueueWidgetGeometryRefresh();
        }

        private IntPtr WidgetGeometryMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Keep WPF's own DPI handling intact. Reconcile only after it has
            // consumed the suggested native bounds and updated its DIP transform.
            if (message == 0x0231) { // WM_ENTERSIZEMOVE, including Window.DragMove.
                widgetGeometryMoving = true;
                widgetGeometryVerticalResize = false;
                widgetGeometryHorizontalResize = false;
                widgetGeometryInitialHeight = ActualHeight > 0 ? ActualHeight : Height;
                widgetGeometryInitialWidth = ActualWidth > 0 ? ActualWidth : Width;
                if (widgetGeometryRefresh != null) { widgetGeometryRefresh.Abort(); widgetGeometryRefresh = null; }
            } else if (message == 0x0214 && widgetGeometryMoving) { // WM_SIZING
                // The top, bottom and corner handles may alter height. A move or
                // width-only resize must keep automatic height fitting enabled.
                int edge = wParam.ToInt32();
                if (edge >= 3 && edge <= 8) widgetGeometryVerticalResize = true;
                if (edge == 1 || edge == 2 || edge == 4 || edge == 5 || edge == 7 || edge == 8)
                    widgetGeometryHorizontalResize = true;
            } else if (message == 0x0232) { // WM_EXITSIZEMOVE
                widgetGeometryMoving = false;
                double currentHeight = ActualHeight > 0 ? ActualHeight : Height;
                double currentWidth = ActualWidth > 0 ? ActualWidth : Width;
                if (settings.CompactView && widgetGeometryHorizontalResize && settings.CompactAutoWidth
                    && Math.Abs(currentWidth - widgetGeometryInitialWidth) > 1) {
                    // Only a completed width change is a manual-width preference.
                    // Moving between monitors may change DIP bounds without resizing.
                    CaptureWidgetGeometry();
                    SetCompactAutoWidth(false);
                }
                if (!settings.CompactView && widgetGeometryVerticalResize && settings.FitContentHeight
                    && Math.Abs(currentHeight - widgetGeometryInitialHeight) > 1) {
                    // A deliberate height change is the user's new layout choice,
                    // including after restart or a subsequent provider refresh.
                    CaptureWidgetGeometry();
                    SetFitContentHeight(false);
                }
                widgetGeometryVerticalResize = false;
                widgetGeometryHorizontalResize = false;
                // Save a finished move now; logoff can end the process before
                // the close path writes the window bounds.
                if (!demo) { CaptureWidgetGeometry(); TrySaveSettings(); }
                QueueWidgetGeometryRefresh();
                RequestFitHeight();
            } else if (message == 0x02E0 || message == 0x007E || message == 0x001A) {
                // WM_DPICHANGED, WM_DISPLAYCHANGE, WM_SETTINGCHANGE (taskbar/work area).
                QueueWidgetGeometryRefresh();
            }
            return IntPtr.Zero;
        }

        private void WidgetGeometryLocationChanged(object sender, EventArgs args)
        {
            QueueWidgetGeometryRefresh();
        }

        private void WidgetGeometryDisplaySettingsChanged(object sender, EventArgs args)
        {
            // DisplaySettingsChanged is raised on the SystemEvents thread.
            if (widgetGeometryDisposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(QueueWidgetGeometryRefresh)); }
            catch (InvalidOperationException) { /* A closing dispatcher needs no geometry update. */ }
        }

        private void QueueWidgetGeometryRefresh()
        {
            if (widgetGeometryDisposed || widgetGeometryApplying || exiting || Dispatcher.HasShutdownStarted) return;
            widgetGeometryRefreshRequested = true;
            if (widgetGeometryMoving || widgetGeometryRefresh != null) return;
            widgetGeometryRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(() => {
                widgetGeometryRefresh = null;
                if (widgetGeometryDisposed || exiting || widgetGeometryMoving || widgetGeometryApplying || !widgetGeometryRefreshRequested) return;
                widgetGeometryRefreshRequested = false;
                ClampCurrentWidgetGeometry();
            }));
        }

        private void QueueCompactAutoWidth()
        {
            if (settings.CompactView && settings.CompactAutoWidth)
                QueueWidgetGeometryRefresh();
        }

        private void WidgetGeometryClosed(object sender, EventArgs args)
        {
            if (widgetGeometryDisposed) return;
            widgetGeometryDisposed = true;
            SourceInitialized -= WidgetGeometrySourceInitialized;
            LocationChanged -= WidgetGeometryLocationChanged;
            Closed -= WidgetGeometryClosed;
            Dispatcher.ShutdownStarted -= WidgetGeometryClosed;
            SystemEvents.DisplaySettingsChanged -= WidgetGeometryDisplaySettingsChanged;
            if (widgetGeometryRefresh != null) { widgetGeometryRefresh.Abort(); widgetGeometryRefresh = null; }
            var source = widgetGeometrySource; widgetGeometrySource = null;
            if (source != null) {
                source.Disposed -= WidgetGeometryClosed;
                try { source.RemoveHook(WidgetGeometryMessage); }
                catch (InvalidOperationException) { /* The source may already be disposing. */ }
            }
        }

        private void ApplyWidgetGeometry()
        {
            double left = settings.CompactView && IsGeometryNumber(settings.CompactLeft) ? settings.CompactLeft : settings.Left;
            double top = settings.CompactView && IsGeometryNumber(settings.CompactTop) ? settings.CompactTop : settings.Top;
            Rect area = GetWidgetWorkArea(left, top);
            Rect bounds = ResolveWidgetBounds(settings, settings.CompactView, area);
            if (settings.CompactView && settings.CompactAutoWidth)
                bounds = ClampWidgetBounds(left, top, CalculateCompactAutoWidth(CompactPreferredWidth, area),
                    CompactWidgetHeight, true, area);
            SetWidgetBounds(bounds, area);
        }

        private void ClampCurrentWidgetGeometry()
        {
            if (exiting || widgetGeometryDisposed || widgetGeometryMoving) return;
            Rect area = GetWidgetWorkArea(double.NaN, double.NaN);
            ApplyWidgetWorkArea(area);
        }

        private void ApplyWidgetWorkArea(Rect area)
        {
            if (widgetGeometryDisposed || widgetGeometryMoving) return;
            // Reconcile the current window, not a saved mode's position. A monitor
            // change must never restore stale settings or alter the other mode.
            double width = settings.CompactView && settings.CompactAutoWidth
                ? CalculateCompactAutoWidth(CompactPreferredWidth, area)
                : ActualWidth > 0 ? ActualWidth : Width;
            Rect bounds = ClampWidgetBounds(Left, Top, width,
                ActualHeight > 0 ? ActualHeight : Height, settings.CompactView, area);
            SetWidgetBounds(bounds, area);
        }

        private void SetWidgetBounds(Rect bounds, Rect area)
        {
            widgetGeometryApplying = true;
            try {
                // Release the old mode's constraints before setting the new ones. In
                // particular, a compact MaxHeight of 44 must not coerce normal height.
                MinWidth = 0; MinHeight = 0;
                MaxWidth = Double.PositiveInfinity; MaxHeight = Double.PositiveInfinity;
                MinWidth = Math.Min(settings.CompactView ? 240 : 280, area.Width);
                MinHeight = Math.Min(settings.CompactView ? CompactWidgetHeight : WidgetSettings.MinimumHeight, area.Height);
                MaxWidth = settings.CompactView ? area.Width : Math.Min(1000, area.Width);
                MaxHeight = settings.CompactView ? CompactWidgetHeight : Math.Min(1400, area.Height);
                ResizeMode = ResizeMode.CanResize;
                Width = bounds.Width; Height = bounds.Height;
                Left = bounds.Left; Top = bounds.Top;
            } finally { widgetGeometryApplying = false; }
        }

        private void CaptureWidgetGeometry()
        {
            SaveWidgetGeometry(settings, settings.CompactView, Left, Top,
                ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        }

        internal static void CopyWidgetGeometry(WidgetSettings source, WidgetSettings target)
        {
            target.Left = source.Left; target.Top = source.Top;
            target.Width = source.Width; target.Height = source.Height;
            target.CompactLeft = source.CompactLeft; target.CompactTop = source.CompactTop;
            target.CompactWidth = source.CompactWidth;
            target.CompactAutoWidth = source.CompactAutoWidth;
            target.LayoutVersion = source.LayoutVersion;
        }

        internal static void SaveWidgetGeometry(WidgetSettings target, bool compact, double left, double top, double width, double height)
        {
            if (compact) {
                if (IsGeometryNumber(left)) target.CompactLeft = left;
                if (IsGeometryNumber(top)) target.CompactTop = top;
                if (IsGeometryNumber(width) && width >= 240) target.CompactWidth = width;
                return;
            }
            if (IsGeometryNumber(left)) target.Left = left;
            if (IsGeometryNumber(top)) target.Top = top;
            if (IsGeometryNumber(width) && width >= 280) target.Width = width;
            if (IsGeometryNumber(height) && height >= WidgetSettings.MinimumHeight) target.Height = height;
        }

        internal static Rect ResolveWidgetBounds(WidgetSettings value, bool compact, Rect area)
        {
            double left = compact && IsGeometryNumber(value.CompactLeft) ? value.CompactLeft : value.Left;
            double top = compact && IsGeometryNumber(value.CompactTop) ? value.CompactTop : value.Top;
            return ClampWidgetBounds(left, top, compact ? value.CompactWidth : value.Width,
                compact ? CompactWidgetHeight : value.Height, compact, area);
        }

        internal static double CalculateCompactAutoWidth(double preferredWidth, Rect area)
        {
            if (area.IsEmpty || !IsGeometryNumber(area.Width) || area.Width <= 0)
                area = new Rect(0, 0, 1024, 768);
            double minimum = Math.Min(240, area.Width);
            double maximum = Math.Min(area.Width, Math.Max(minimum, area.Width * 0.8));
            if (!IsGeometryNumber(preferredWidth)) preferredWidth = minimum;
            return Math.Max(minimum, Math.Min(Math.Ceiling(preferredWidth), maximum));
        }

        internal static Rect ClampWidgetBounds(double left, double top, double width, double height, bool compact, Rect area)
        {
            if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
                area = new Rect(0, 0, 1024, 768);
            double minimumWidth = Math.Min(compact ? 240 : 280, area.Width);
            double maximumWidth = compact ? area.Width : Math.Min(1000, area.Width);
            double minimumHeight = Math.Min(compact ? CompactWidgetHeight : WidgetSettings.MinimumHeight, area.Height);
            double maximumHeight = compact ? Math.Min(CompactWidgetHeight, area.Height) : Math.Min(1400, area.Height);
            width = IsGeometryNumber(width) ? width : compact ? 420 : 320;
            height = IsGeometryNumber(height) ? height : compact ? CompactWidgetHeight : WidgetSettings.DefaultHeight;
            width = Math.Max(minimumWidth, Math.Min(width, maximumWidth));
            height = Math.Max(minimumHeight, Math.Min(height, maximumHeight));
            left = IsGeometryNumber(left) ? left : area.Right - width - 24;
            top = IsGeometryNumber(top) ? top : area.Top + 40;
            left = Math.Max(area.Left, Math.Min(left, area.Right - width));
            top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
            return new Rect(left, top, width, height);
        }

        private Rect GetWidgetWorkArea(double preferredLeft, double preferredTop)
        {
            var source = PresentationSource.FromVisual(this);
            Matrix fromDevice;
            Matrix toDevice;
            if (source != null && source.CompositionTarget != null) {
                fromDevice = source.CompositionTarget.TransformFromDevice;
                toDevice = source.CompositionTarget.TransformToDevice;
            } else {
                // SystemParameters uses logical units; Forms.Screen uses device
                // pixels even before the WPF window obtains its composition target.
                var primary = Forms.Screen.PrimaryScreen.WorkingArea;
                var logical = SystemParameters.WorkArea;
                double scale = logical.Width > 0 ? primary.Width / logical.Width : 1;
                if (!IsGeometryNumber(scale) || scale <= 0) scale = 1;
                toDevice = new Matrix(scale, 0, 0, scale, 0, 0);
                fromDevice = new Matrix(1 / scale, 0, 0, 1 / scale, 0, 0);
            }
            Forms.Screen screen;
            if (IsGeometryNumber(preferredLeft) && IsGeometryNumber(preferredTop)) {
                Point point = toDevice.Transform(new Point(preferredLeft, preferredTop));
                screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y));
            } else {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                screen = handle != IntPtr.Zero ? Forms.Screen.FromHandle(handle) : Forms.Screen.PrimaryScreen;
            }
            var deviceArea = screen.WorkingArea;
            return new Rect(fromDevice.Transform(new Point(deviceArea.Left, deviceArea.Top)),
                fromDevice.Transform(new Point(deviceArea.Right, deviceArea.Bottom)));
        }

        private static bool IsGeometryNumber(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }
}
