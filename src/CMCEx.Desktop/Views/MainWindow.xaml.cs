using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows.Resources;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using CMCEx.Core.Reentry;
using CMCEx.Infrastructure.Reentry;

namespace CMC // keep this namespace if your XAML is still x:Class="CMC.MainWindow"
{
    public partial class MainWindow : Window
    {
        // Keep window aspect ratio == design size (600x800 => 3:4)
        private const double DesignWidth = 695.0;
        private const double DesignHeight = 800.0;
        private static readonly double AspectRatio = DesignWidth / DesignHeight; // 0.75
        private bool _isAdjustingSize;
        private bool _isInResizeMove;
        private bool _ignoreNextSizeChanged;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly DispatcherTimer _pollTimer;
        private bool _readerRunning;
        private bool _jsonAccessible;
        private bool _powerIconOn;
        private byte[]? _clickWavBytes;
        // 3) Command sender (replaces StoredCmds)
        private readonly IReentryCommandSender _commandSender =
            new UdpReentryCommandSender(new ReentryOptions { Host = "127.0.0.1", Port = 8051 });

        public bool AreWeDarkMode { get; private set; }

        // Asset base (new structure)
        private const string ImgBase = "pack://application:,,,/Assets/Images/";
        private static readonly Uri UnlitDarkUri = new($"{ImgBase}lights/unlit/dark/unlit.png", UriKind.Absolute);
        private static readonly Uri UnlitBlankUri = new($"{ImgBase}lights/unlit/unlit.png", UriKind.Absolute);

        private static readonly Uri PowerOffUri = new($"{ImgBase}power-off.png", UriKind.Absolute);
        private static readonly Uri PowerOnUri  = new($"{ImgBase}power-on.png",  UriKind.Absolute);

        public MainWindow()
        {
            InitializeComponent();
            LoadClickSoundResource();

            AreWeDarkMode = false;

            MouseDown += Window_MouseDown;
            SizeChanged += Window_SizeChanged; // aspect-ratio enforcement (works even without XAML hook)

            // Improve resize smoothness: avoid constantly fighting the user's drag.
            // We only enforce the aspect ratio once the user finishes resizing.
            SourceInitialized += (_, _) =>
            {
                if (PresentationSource.FromVisual(this) is HwndSource src)
                    src.AddHook(WndProc);
            };

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _pollTimer.Tick += async (_, _) => await PollOnceAsync();

            // Ensure the power icon starts in a consistent state.
            UpdateJsonReaderPowerIcon();
        }

        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE  = 0x0232;

        private const int WM_NCHITTEST     = 0x0084;

        private const int HTCLIENT      = 1;
        private const int HTTOPLEFT     = 13;
        private const int HTTOPRIGHT    = 14;
        private const int HTBOTTOMLEFT  = 16;
        private const int HTBOTTOMRIGHT = 17;

        private const int ResizeBorder = 12; // px hit area for corner resize

                private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    _isInResizeMove = true;
                    break;

                case WM_EXITSIZEMOVE:
                    _isInResizeMove = false;
                    _ignoreNextSizeChanged = true; // prevent stale SizeChanged applying an older size
                    EnforceAspectRatioOnce();
                    break;

                case WM_NCHITTEST:
                    handled = true;
                    return HitTestNCA(lParam);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Corner-only resizing for borderless window: edges are disabled; only corners return HT* codes.
        /// </summary>
        private IntPtr HitTestNCA(IntPtr lParam)
        {
            // Screen coords packed into lParam
            int x = (short)((uint)lParam & 0xFFFF);
            int y = (short)(((uint)lParam >> 16) & 0xFFFF);

            Point p = PointFromScreen(new Point(x, y));

            bool left = p.X <= ResizeBorder;
            bool right = p.X >= ActualWidth - ResizeBorder;
            bool top = p.Y <= ResizeBorder;
            bool bottom = p.Y >= ActualHeight - ResizeBorder;

            if (top && left) return (IntPtr)HTTOPLEFT;
            if (top && right) return (IntPtr)HTTOPRIGHT;
            if (bottom && left) return (IntPtr)HTBOTTOMLEFT;
            if (bottom && right) return (IntPtr)HTBOTTOMRIGHT;

            // Edges disabled
            return (IntPtr)HTCLIENT;
        }

        private void EnforceAspectRatioOnce()
        {
            if (_isAdjustingSize) return;

            _isAdjustingSize = true;
            try
            {
                // Choose the adjustment that changes the window the least.
                double currentW = Width;
                double currentH = Height;

                if (currentW <= 0 || currentH <= 0) return;

                double targetHFromW = currentW / AspectRatio;
                double targetWFromH = currentH * AspectRatio;

                // Clamp to mins
                if (targetHFromW < MinHeight) targetHFromW = MinHeight;
                if (targetWFromH < MinWidth)  targetWFromH = MinWidth;

                double deltaH = Math.Abs(targetHFromW - currentH);
                double deltaW = Math.Abs(targetWFromH - currentW);

                if (deltaH <= deltaW)
                {
                    Height = targetHFromW;
                    // keep width consistent with new height if mins forced us
                    Width = Math.Max(MinWidth, Height * AspectRatio);
                }
                else
                {
                    Width = targetWFromH;
                    Height = Math.Max(MinHeight, Width / AspectRatio);
                }
            }
            finally
            {
                _isAdjustingSize = false;
            }
        }

        private void UpdateJsonReaderPowerIcon()
        {
            bool shouldBeOn = _readerRunning && _jsonAccessible;
            if (shouldBeOn == _powerIconOn && PowerJSONReader.Background is not null)
                return;

            _powerIconOn = shouldBeOn;
            var uri = shouldBeOn ? PowerOnUri : PowerOffUri;

            PowerJSONReader.Background = new ImageBrush(new BitmapImage(uri))
            {
                Stretch = Stretch.Uniform
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop polling to avoid any stray UI updates after close
            try { _pollTimer.Stop(); } catch { /* ignore */ }

            // Dispose UDP sender if applicable
            if (_commandSender is IDisposable d)
            {
                try { d.Dispose(); } catch { /* ignore */ }
            }

            base.OnClosed(e);
        }

        // Window drag movement
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            // If the cursor is on a resize corner, let Windows handle the resize.
            // (DragMove would otherwise "fight" the resize interaction.)
            var p = e.GetPosition(this);
            bool left = p.X <= ResizeBorder;
            bool right = p.X >= ActualWidth - ResizeBorder;
            bool top = p.Y <= ResizeBorder;
            bool bottom = p.Y >= ActualHeight - ResizeBorder;

            if ((top && left) || (top && right) || (bottom && left) || (bottom && right))
                return;

            DragMove();
        }

        // POWER button: start/stop polling
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            _readerRunning = !_readerRunning;

            if (!_readerRunning)
            {
                // When stopped, consider the JSON source inaccessible (icon goes off).
                _jsonAccessible = false;
            }

            if (_readerRunning)
                _pollTimer.Start();
            else
                _pollTimer.Stop();

            UpdateJsonReaderPowerIcon();


            // Refresh digits/annunciators visibility immediately.
            UpdateStoredValues();

            // Prime one read immediately so the icon can switch to ON as soon as
            // we confirm the json files are reachable.
            if (_readerRunning)
                _ = PollOnceAsync();
        }


        private void LoadClickSoundResource()
        {
            try
            {
                // click.wav is a WPF Resource (Build Action = Resource)
                var uri = new Uri("pack://application:,,,/Assets/Effects/click.wav", UriKind.Absolute);
                StreamResourceInfo? sri = Application.GetResourceStream(uri);
                if (sri is null)
                    return;

                using var ms = new MemoryStream();
                sri.Stream.CopyTo(ms);
                _clickWavBytes = ms.ToArray();
            }
            catch
            {
                // sound is optional; ignore failures
            }
        }

        private void PlayClick()
        {
            try
            {
                if (_clickWavBytes is null || _clickWavBytes.Length == 0)
                    return;

                // SoundPlayer requires the stream to stay alive during playback.
                // Create a new MemoryStream per play to keep it simple and reliable.
                using var ms = new MemoryStream(_clickWavBytes, writable: false);
                using var sp = new SoundPlayer(ms);
                sp.Play();
            }
            catch
            {
                // never allow audio issues to break UI
            }
        }

        private void HandleFunctionalButtonPress(object sender)
        {
            PlayClick();

            if (KeypressShadow is null || RootGrid is null)
                return;

            if (sender is not FrameworkElement fe)
                return;

            var transform = fe.TransformToAncestor(RootGrid);
            var pos = transform.Transform(new Point(0, 0));

            KeypressShadow.HorizontalAlignment = HorizontalAlignment.Left;
            KeypressShadow.VerticalAlignment = VerticalAlignment.Top;
            KeypressShadow.Margin = new Thickness(pos.X, pos.Y, 0, 0);
            KeypressShadow.Width = fe.ActualWidth;
            KeypressShadow.Height = fe.ActualHeight;
            KeypressShadow.Visibility = Visibility.Visible;
        }

        // Functional keypad buttons shadow overlay (keypress effect)
        private void FunctionalButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            HandleFunctionalButtonPress(sender);
        }

        private void FunctionalButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (KeypressShadow is not null)
                KeypressShadow.Visibility = Visibility.Collapsed;
        }

        private void FunctionalButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (KeypressShadow is not null)
                KeypressShadow.Visibility = Visibility.Collapsed;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void FunctionalButton_TouchDown(object sender, TouchEventArgs e)
        {
            HandleFunctionalButtonPress(sender);
            e.Handled = false;
        }

        private void FunctionalButton_TouchUp(object sender, TouchEventArgs e)
        {
            KeypressShadow.Visibility = Visibility.Collapsed;
            e.Handled = false;
        }

        private void FunctionalButton_TouchLeave(object sender, TouchEventArgs e)
        {
            KeypressShadow.Visibility = Visibility.Collapsed;
            e.Handled = false;
        }

        private async Task PollOnceAsync()
        {
            try
            {
                var values = await ReadAgcJsonAsync();
                if (values is null)
                {
                    if (_jsonAccessible)
                    {
                        _jsonAccessible = false;
                        Dispatcher.Invoke(UpdateJsonReaderPowerIcon);
                                            Dispatcher.Invoke(UpdateStoredValues);
                    }
                    return;
                }

                if (!_jsonAccessible)
                {
                    _jsonAccessible = true;
                    Dispatcher.Invoke(UpdateJsonReaderPowerIcon);
                }

                CopyToStorage(values);
                UpdateStoredValues();
            }
               catch (IOException)
            {
                // file may be temporarily locked; ignore this tick
            }
               catch (Exception)
            {
#if DEBUG
                Debug.WriteLine(ex);
#endif
            }
        }

        // Fixed aspect ratio resizing
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_ignoreNextSizeChanged)
            {
                _ignoreNextSizeChanged = false;
                return;
            }

            if (_isAdjustingSize) return;

            // While the user is dragging the resize border, don't continuously
            // rewrite Width/Height (it causes the "jumping back" feeling).
            // We'll enforce the aspect ratio once on WM_EXITSIZEMOVE.
            if (_isInResizeMove) return;

            // Ignore if not fully initialized
            if (e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;

            _isAdjustingSize = true;
            try
            {
                // Determine which dimension user likely changed more
                double dw = Math.Abs(e.NewSize.Width - e.PreviousSize.Width);
                double dh = Math.Abs(e.NewSize.Height - e.PreviousSize.Height);

                if (dw >= dh)
                {
                    // Width changed: derive height
                    double targetHeight = e.NewSize.Width / AspectRatio;

                    // Respect MinHeight
                    if (targetHeight < MinHeight)
                    {
                        targetHeight = MinHeight;
                        Width = targetHeight * AspectRatio;
                    }

                    Height = targetHeight;
                }
                else
                {
                    // Height changed: derive width
                    double targetWidth = e.NewSize.Height * AspectRatio;

                    // Respect MinWidth
                    if (targetWidth < MinWidth)
                    {
                        targetWidth = MinWidth;
                        Height = targetWidth / AspectRatio;
                    }

                    Width = targetWidth;
                }
            }
            finally
            {
                _isAdjustingSize = false;
            }
        }

        // JSON read
        private static async Task<CMCValues?> ReadAgcJsonAsync()
        {
            string localLow = Path.GetFullPath(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow"));

            string exportDir = Path.Combine(localLow, "Wilhelmsen Studios", "ReEntry", "Export", "Apollo");

            // ReEntry always writes the AGC export; it also includes IsInCM to tell us which craft is currently active.
            string agcFile = Path.Combine(exportDir, "outputAGC.json");

            // Some ReEntry builds/tools also write a dedicated LM/LGC export. If present, we will use it when IsInCM == false.
            string lgcFileA = Path.Combine(exportDir, "outputLGC.json");
            string lgcFileB = Path.Combine(exportDir, "outputLM.json");

            if (!File.Exists(agcFile))
                return null;

            // 1) Read the AGC file first (so we can inspect IsInCM).
            string agcJson = await File.ReadAllTextAsync(agcFile).ConfigureAwait(false);
            var agcValues = JsonSerializer.Deserialize<CMCValues>(agcJson);

            if (agcValues is null)
                return null;

            // 2) If we are in the LM, prefer a dedicated LM export file if one exists.
            // BUT only if it matches our CMCValues schema (ProgramD1/VerbD1/etc).
            if (!agcValues.IsInCM)
            {
                string? lmFile = File.Exists(lgcFileA) ? lgcFileA : (File.Exists(lgcFileB) ? lgcFileB : null);
                if (lmFile is not null)
                {
                    string lmJson = await File.ReadAllTextAsync(lmFile).ConfigureAwait(false);

                    // Heuristic: only accept LM json if it contains fields we actually render
                    // (otherwise it will deserialize to blanks).
                    if (lmJson.Contains("\"ProgramD1\"", StringComparison.OrdinalIgnoreCase) ||
                        lmJson.Contains("\"VerbD1\"", StringComparison.OrdinalIgnoreCase) ||
                        lmJson.Contains("\"Register1D1\"", StringComparison.OrdinalIgnoreCase))
                    {
                        var lmValues = JsonSerializer.Deserialize<CMCValues>(lmJson);
                        if (lmValues is not null)
                        {
                            lmValues.IsInCM = false; // ensure consistency
                            return lmValues;
                        }
                    }
                }
            }

            // Default: use outputAGC.json
            return agcValues;
        }

        private static string BlankToSpace(string? s)
        {
            return string.IsNullOrEmpty(s) ? " " : s;
        }

        private static void CopyToStorage(CMCValues v)
        {
            CMCStorage.IsInCM = v.IsInCM;
            CMCStorage.VerbD1 = v.VerbD1;
            CMCStorage.VerbD2 = v.VerbD2;
            CMCStorage.NounD1 = v.NounD1;
            CMCStorage.NounD2 = v.NounD2;

            CMCStorage.ProgramD1 = v.ProgramD1;
            CMCStorage.ProgramD2 = v.ProgramD2;

            CMCStorage.Register1D1 = BlankToSpace(v.Register1D1);
            CMCStorage.Register1D2 = BlankToSpace(v.Register1D2);
            CMCStorage.Register1D3 = BlankToSpace(v.Register1D3);
            CMCStorage.Register1D4 = BlankToSpace(v.Register1D4);
            CMCStorage.Register1D5 = BlankToSpace(v.Register1D5);
            CMCStorage.Register1Sign = BlankToSpace(v.Register1Sign);

            CMCStorage.Register2D1 = BlankToSpace(v.Register2D1);
            CMCStorage.Register2D2 = BlankToSpace(v.Register2D2);
            CMCStorage.Register2D3 = BlankToSpace(v.Register2D3);
            CMCStorage.Register2D4 = BlankToSpace(v.Register2D4);
            CMCStorage.Register2D5 = BlankToSpace(v.Register2D5);
            CMCStorage.Register2Sign = BlankToSpace(v.Register2Sign);

            CMCStorage.Register3D1 = BlankToSpace(v.Register3D1);
            CMCStorage.Register3D2 = BlankToSpace(v.Register3D2);
            CMCStorage.Register3D3 = BlankToSpace(v.Register3D3);
            CMCStorage.Register3D4 = BlankToSpace(v.Register3D4);
            CMCStorage.Register3D5 = BlankToSpace(v.Register3D5);
            CMCStorage.Register3Sign = BlankToSpace(v.Register3Sign);

            CMCStorage.IlluminateCompLight = v.IlluminateCompLight;
            CMCStorage.IlluminateTemp = v.IlluminateTemp;
            CMCStorage.IlluminateGimbalLock = v.IlluminateGimbalLock;
            CMCStorage.IlluminateProg = v.IlluminateProg;
            CMCStorage.IlluminateRestart = v.IlluminateRestart;
            CMCStorage.IlluminateTracker = v.IlluminateTracker;

            CMCStorage.IlluminateUplinkActy = v.IlluminateUplinkActy;
            CMCStorage.IlluminateNoAtt = v.IlluminateNoAtt;
            CMCStorage.IlluminateStby = v.IlluminateStby;
            CMCStorage.IlluminateKeyRel = v.IlluminateKeyRel;
            CMCStorage.IlluminateOprErr = v.IlluminateOprErr;

            if (!CMCStorage.IsInCM)
            {
                CMCStorage.IlluminateAlt = v.IlluminateAlt;
                CMCStorage.IlluminateVel = v.IlluminateVel;
            }

            CMCStorage.IsFlashing = v.IsFlashing;
            CMCStorage.HideVerb = v.HideVerb;
            CMCStorage.HideNoun = v.HideNoun;
            CMCStorage.BrightnessNumerics = v.BrightnessNumerics;
            CMCStorage.BrightnessIntegral = v.BrightnessIntegral;
        }

        private static double Clamp01(float v) => v < 0f ? 0d : (v > 1f ? 1d : v);

        
        private bool IsPowered => _readerRunning && _jsonAccessible;

        private void ApplyPowerGating(bool powered)
        {
            // Digits (Verb/Noun/Prog + Registers)
            var digitsVisibility = powered ? Visibility.Visible : Visibility.Hidden;
            Verb.Visibility = digitsVisibility;
            Noun.Visibility = digitsVisibility;
            Prog.Visibility = digitsVisibility;
            Register1.Visibility = digitsVisibility;
            Register2.Visibility = digitsVisibility;
            Register3.Visibility = digitsVisibility;

            // Annunciators / indicator images
            var annunciatorVisibility = powered ? Visibility.Visible : Visibility.Hidden;
            CompActy.Visibility = annunciatorVisibility;
            Temp.Visibility = annunciatorVisibility;
            Gimballock.Visibility = annunciatorVisibility;
            Program.Visibility = annunciatorVisibility;
            Restart.Visibility = annunciatorVisibility;
            Tracker.Visibility = annunciatorVisibility;
            UplinkActy.Visibility = annunciatorVisibility;
            NoAtt.Visibility = annunciatorVisibility;
            Stby.Visibility = annunciatorVisibility;
            KeyRel.Visibility = annunciatorVisibility;
            OprErr.Visibility = annunciatorVisibility;
            Alt.Visibility = annunciatorVisibility;
            Vel.Visibility = annunciatorVisibility;

            // "Unlit" placeholders (keep them in sync too)
            Unlit1.Visibility = annunciatorVisibility;
            Unlit2.Visibility = annunciatorVisibility;

            if (!powered)
            {
                // Clear digits to avoid showing stale values when powered off.
                Verb.Content = "";
                Noun.Content = "";
                Prog.Content = "";
                Register1.Content = "";
                Register2.Content = "";
                Register3.Content = "";
            }
        }

        // UI update
        public void UpdateStoredValues()
        {
            ApplyPowerGating(IsPowered);

            // When power is off, force backlights off and bail out.
            if (!IsPowered)
            {
                if (KeypadBacklightOverlay is not null)
                    KeypadBacklightOverlay.Opacity = 0.0;

                if (DisplayBacklightOverlay is not null)
                    DisplayBacklightOverlay.Opacity = 0.0;

                return;
            }

            LmOnlyPanel.Visibility = CMCStorage.IsInCM ? Visibility.Collapsed : Visibility.Visible;
            Verb.Content = CMCStorage.HideVerb ? "" : $"{CMCStorage.VerbD1}{CMCStorage.VerbD2}";
            Noun.Content = CMCStorage.HideNoun ? "" : $"{CMCStorage.NounD1}{CMCStorage.NounD2}";
            Prog.Content = $"{CMCStorage.ProgramD1}{CMCStorage.ProgramD2}";

            Register1.Content = $"{CMCStorage.Register1Sign}{CMCStorage.Register1D1}{CMCStorage.Register1D2}{CMCStorage.Register1D3}{CMCStorage.Register1D4}{CMCStorage.Register1D5}";
            Register2.Content = $"{CMCStorage.Register2Sign}{CMCStorage.Register2D1}{CMCStorage.Register2D2}{CMCStorage.Register2D3}{CMCStorage.Register2D4}{CMCStorage.Register2D5}";
            Register3.Content = $"{CMCStorage.Register3Sign}{CMCStorage.Register3D1}{CMCStorage.Register3D2}{CMCStorage.Register3D3}{CMCStorage.Register3D4}{CMCStorage.Register3D5}";

            // Map brightness → opacity:
            double numericOpacity   = 0;
            double integralOpacity  = 0;

            if (CMCStorage.IsInCM)
            {
                numericOpacity   = normalizeBrightness(CMCStorage.BrightnessNumerics, 0.2, 1.14117646);
                integralOpacity  = normalizeBrightness(CMCStorage.BrightnessIntegral, 0, 0.9411765);
            }
            else
            {
                numericOpacity   = normalizeBrightness(CMCStorage.BrightnessNumerics, 0.4, 1.4);
                integralOpacity  = normalizeBrightness(CMCStorage.BrightnessIntegral, 0.4, 1.4);
            }

            // Digit opacity (BrightnessNumerics)
            Verb.Opacity      = numericOpacity;
            Noun.Opacity      = numericOpacity;
            Prog.Opacity      = numericOpacity;
            Register1.Opacity = numericOpacity;
            Register2.Opacity = numericOpacity;
            Register3.Opacity = numericOpacity;

            // Backlight overlays
            if (KeypadBacklightOverlay is not null)
                KeypadBacklightOverlay.Opacity = integralOpacity;     // BrightnessIntegral

            if (DisplayBacklightOverlay is not null)
                DisplayBacklightOverlay.Opacity = numericOpacity;     // BrightnessNumerics

            // COMP ACTY
            CompActy.Visibility = CMCStorage.IlluminateCompLight ? Visibility.Visible : Visibility.Hidden;
            CompActy.Opacity = numericOpacity;

            // Left side annunciators
            SetAnnunciator(UplinkActy, CMCStorage.IlluminateUplinkActy > 0, "uplinkacty");
            SetAnnunciator(NoAtt,      CMCStorage.IlluminateNoAtt > 0, "noatt");
            SetAnnunciator(Stby,       CMCStorage.IlluminateStby > 0, "stby");
            SetAnnunciator(KeyRel,     CMCStorage.IlluminateKeyRel > 0, "keyrel");
            SetAnnunciator(OprErr,     CMCStorage.IlluminateOprErr > 0, "oprerr");

            // Right side annunciators
            SetAnnunciator(Temp,      CMCStorage.IlluminateTemp > 0,       "temp");
            SetAnnunciator(Gimballock,CMCStorage.IlluminateGimbalLock > 0, "gimballock");
            SetAnnunciator(Program,   CMCStorage.IlluminateProg > 0,       "prog");
            SetAnnunciator(Restart,   CMCStorage.IlluminateRestart > 0,    "restart");
            SetAnnunciator(Tracker,   CMCStorage.IlluminateTracker > 0,    "tracker");

            // LM-only bottom annunciators
            if (!CMCStorage.IsInCM)
            {
                SetAnnunciator(Alt, CMCStorage.IlluminateAlt > 0, "alt");
                SetAnnunciator(Vel, CMCStorage.IlluminateVel > 0, "vel");
            }
            else
            {
                var blank = AreWeDarkMode ? UnlitDarkUri : UnlitBlankUri;
                Alt.Source = new BitmapImage(blank);
                Vel.Source = new BitmapImage(blank);
            }

            // Still blank placeholders
            var blank2 = AreWeDarkMode ? UnlitDarkUri : UnlitBlankUri;
            Unlit1.Source = new BitmapImage(blank2);
            Unlit2.Source = new BitmapImage(blank2);
        }

        private static double normalizeBrightness(double v, double min, double max)
        {
            return (v - min) / (max - min);
        }

        private void SetAnnunciator(System.Windows.Controls.Image target, bool illuminated, string name)
        {
            if (illuminated && _readerRunning)
            {
                if (AreWeDarkMode)
                {
                    target.Source = new BitmapImage(new Uri($"{ImgBase}lights/lit/dark/{name}.png", UriKind.Absolute));
                }
                else
                {
                    target.Source = new BitmapImage(new Uri($"{ImgBase}lights/lit/{name}.png", UriKind.Absolute));
                }
                return;
            }

            if (AreWeDarkMode)
            {
                target.Source = new BitmapImage(new Uri($"{ImgBase}lights/unlit/dark/{name}.png", UriKind.Absolute));
            }
            else
            {
                target.Source = new BitmapImage(new Uri($"{ImgBase}lights/unlit/{name}.png", UriKind.Absolute));
            }
        }

        // Dark mode toggle
        private void DarkMode_Click(object sender, RoutedEventArgs e)
        {
            AreWeDarkMode = !AreWeDarkMode;

            var bg = AreWeDarkMode
                ? new Uri($"{ImgBase}agc-bg-dark.png", UriKind.Absolute)
                : new Uri($"{ImgBase}agc-bg.png", UriKind.Absolute);

            // Keypad backlight image: normal vs dark
            if (KeypadBacklightOverlay is not null)
            {
                var keypadUri = AreWeDarkMode
                    ? new Uri($"{ImgBase}keypad_bcklt-dark.png", UriKind.Absolute)
                    : new Uri($"{ImgBase}keypad_bcklt.png",      UriKind.Absolute);

                KeypadBacklightOverlay.Source = new BitmapImage(keypadUri);
            }

            MainBackgroundImage.Source = new BitmapImage(bg);

            // Force-refresh annunciators so they switch unlit appearance immediately
            UpdateStoredValues();
        }

        // Button handlers (now use IReentryCommandSender + AgcKey)
        private void Pro_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Pro, CMCStorage.IsInCM);
        private void Verb_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Verb, CMCStorage.IsInCM);
        private void KeyNoun_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Noun, CMCStorage.IsInCM);

        private void Key0_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D0, CMCStorage.IsInCM);
        private void Key1_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D1, CMCStorage.IsInCM);
        private void Key2_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D2, CMCStorage.IsInCM);
        private void Key3_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D3, CMCStorage.IsInCM);
        private void Key4_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D4, CMCStorage.IsInCM);
        private void Key5_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D5, CMCStorage.IsInCM);
        private void Key6_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D6, CMCStorage.IsInCM);
        private void Key7_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D7, CMCStorage.IsInCM);
        private void Key8_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D8, CMCStorage.IsInCM);
        private void Key9_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.D9, CMCStorage.IsInCM);

        private void KeyPluss_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Plus, CMCStorage.IsInCM);
        private void KeyMinus_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Minus, CMCStorage.IsInCM);

        private void KeyClear_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Clear, CMCStorage.IsInCM);
        private void KeyEntr_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Enter, CMCStorage.IsInCM);
        private void KeyKeyRel_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.KeyRel, CMCStorage.IsInCM);
        private void KeyRSET_Click(object sender, RoutedEventArgs e) => _ = _commandSender.SendKeyAsync(AgcKey.Reset, CMCStorage.IsInCM);
    }
}
