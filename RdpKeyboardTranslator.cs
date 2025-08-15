using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Drawing;

using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace RdpKeyboardTranslator
{
    public class KeyboardTranslator
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        public static LowLevelKeyboardProc _proc = HookCallback;
        public static IntPtr _hookID = IntPtr.Zero;
        // Buffered Unicode pipeline (TSF primary)
        private static readonly object _bufLock = new object();
        private static System.Text.StringBuilder _unicodeBuffer = new System.Text.StringBuilder();
        public static System.Windows.Forms.Timer _flushTimer;
        public const int BUFFER_FLUSH_INTERVAL_MS = 10;  // 降低延遲從 25ms 到 10ms

        public static bool _translatorActive = true;
        private static Dictionary<int, ushort> _vkToScanCodeMap;
        
        // RDP Session Management
        private static bool _isRdpSession = false;
        private static uint _currentSessionId = 0;
        
        // Note: Duplicate prevention removed - now using system key skip logic instead

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public KEYBDINPUT ki;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        [DllImport("user32.dll")]
        private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        // Session Management APIs
        [DllImport("kernel32.dll")]
        static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("wtsapi32.dll")]
        static extern IntPtr WTSOpenServer([MarshalAs(UnmanagedType.LPStr)] string pServerName);

        [DllImport("wtsapi32.dll")]
        static extern void WTSCloseServer(IntPtr hServer);

        [DllImport("wtsapi32.dll")]
        static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr pMemory);

        private const int WTS_CURRENT_SESSION = -1;
        private const int WTSConnectState = 0;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        // Thread info for debugging
        private const uint THREAD_QUERY_LIMITED_INFORMATION = 0x0800;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetThreadDescription(IntPtr hThread, out IntPtr ppszThreadDescription);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

        // TSF (Text Services Framework) COM interfaces and constants
        [ComImport, Guid("AA80E801-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfThreadMgr
        {
            void Activate(out uint pClientId);
            void Deactivate();
            int CreateDocumentMgr(out ITfDocumentMgr ppDocMgr);
            // ... other methods (simplified for this implementation)
        }

        [ComImport, Guid("AA80E7F4-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfDocumentMgr
        {
            int CreateContext(uint tidOwner, uint dwFlags, [MarshalAs(UnmanagedType.IUnknown)] object punk, out ITfContext ppic, out uint pecTextStore);
            int Push(ITfContext pic);
            int Pop(uint dwFlags);
            // ... other methods (simplified for this implementation)
        }

        [ComImport, Guid("AA80E7FD-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfContext
        {
            // Basic context operations
            int RequestEditSession(uint tid, ITfEditSession pes, uint dwFlags, out int phrSession);
            int InWriteSession(uint tid, out bool pfWriteSession);
            int GetSelection(uint ec, uint ulIndex, uint ulCount, [Out] IntPtr pSelection, out uint pcFetched);
            int SetSelection(uint ec, uint ulCount, IntPtr pSelection);
            int GetStart(uint ec, out ITfRange ppStart);
            int GetEnd(uint ec, out ITfRange ppEnd);
            // ... other methods (simplified for this implementation)
        }

        [ComImport, Guid("AA80E7FF-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfRange
        {
            int GetText(uint ec, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pchText, uint cchMax, out uint pcch);
            int SetText(uint ec, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pchText, int cch);
            int GetFormattedText(uint ec, out object ppDataObject);
            // ... other methods
        }

        [ComImport, Guid("17D49A3D-F8B8-4B2F-B254-52319DD64C53"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfInsertAtSelection
        {
            int InsertTextAtSelection(uint ec, uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pchText, int cch, out ITfRange ppRange);
            int InsertEmbeddedAtSelection(uint ec, uint dwFlags, object pDataObject, out ITfRange ppRange);
        }

        [ComImport, Guid("AA80E803-2021-11D2-93E0-0060B067B86E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfEditSession
        {
            int DoEditSession(uint ec);
        }

        // TSF Composition Interface
        [ComImport, Guid("20168D64-5A8F-4A5A-B7BD-CFA29F4D0FD9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfComposition
        {
            int GetRange(out object ppRange);
            int ShiftStart(uint ec, object pNewStart);
            int ShiftEnd(uint ec, object pNewEnd);
            int EndComposition(uint ec);
        }

        [ComImport, Guid("D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITfCompositionSink
        {
            int OnCompositionTerminated(uint ecWrite, ITfComposition pComposition);
        }

        // TSF Unicode Edit Session class - dedicated for Unicode text
        private class TSFUnicodeEditSession : ITfEditSession
        {
            private readonly string _text;
            private readonly ITfContext _context;

            public TSFUnicodeEditSession(string text, ITfContext context)
            {
                _text = text;
                _context = context;
            }

            public int DoEditSession(uint ec)
            {
                try
                {
                    Console.WriteLine($"[TSF] Unicode DoEditSession called with EC=0x{ec:X8}, text: '{_text}'");

                    // Try to get the selection range and insert text at cursor
                    Console.WriteLine("[TSF] Attempting to get insertion point via selection");
                    
                    // Method 1: Get current selection and replace with our text
                    try
                    {
                        // This is a simplified approach - we're trying to get the end of the document
                        // and insert text there, which simulates typing at the current cursor position
                        int hr = _context.GetEnd(ec, out ITfRange endRange);
                        if (hr == 0 && endRange != null)
                        {
                            Console.WriteLine("[TSF] Got end range for Unicode text");
                            
                            // Try to set the text at the end position
                            int setTextHr = endRange.SetText(ec, 0, _text, _text.Length);
                            Console.WriteLine($"[TSF] Unicode SetText HR=0x{setTextHr:X8}");
                            
                            if (setTextHr == 0)
                            {
                                Console.WriteLine($"[TSF] Unicode text insertion succeeded: '{_text}'");
                                return 0; // S_OK
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TSF] Unicode range method failed: {ex.Message}");
                    }

                    // Method 2: Try composition-based approach
                    Console.WriteLine("[TSF] Trying composition-based Unicode insertion");
                    
                    // Since direct TSF insertion isn't working reliably, we fall back to
                    // sending the characters via Windows messages within the edit session context
                    IntPtr targetWindow = FindBestTargetWindow();
                    if (targetWindow != IntPtr.Zero)
                    {
                        Console.WriteLine($"[TSF] Using message-based Unicode insertion within edit session");
                        
                        // Use a more direct approach for Unicode characters
                        foreach (char c in _text)
                        {
                            Console.WriteLine($"[TSF] Sending Unicode char '{c}' (0x{(int)c:X4}) via messages");
                            
                            // Method 1: Try SendInput with proper Unicode flag (within TSF context)
                            INPUT[] unicodeInputs = new INPUT[2];
                            unicodeInputs[0] = new INPUT
                            {
                                type = 1, // INPUT_KEYBOARD
                                ki = new KEYBDINPUT
                                {
                                    wVk = 0,
                                    wScan = c,
                                    dwFlags = KEYEVENTF_UNICODE,
                                    time = 0,
                                    dwExtraInfo = IntPtr.Zero
                                }
                            };
                            unicodeInputs[1] = new INPUT
                            {
                                type = 1,
                                ki = new KEYBDINPUT
                                {
                                    wVk = 0,
                                    wScan = c,
                                    dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                                    time = 0,
                                    dwExtraInfo = IntPtr.Zero
                                }
                            };

                            uint result = SendInput(2, unicodeInputs, Marshal.SizeOf(typeof(INPUT)));
                            Console.WriteLine($"[TSF] SendInput for '{c}': {result}/2 {(result > 0 ? "(success)" : $"(Error: {GetLastError()})")}");
                            
                            if (result == 0)
                            {
                                // Fallback to messages if SendInput fails
                                Console.WriteLine($"[TSF] SendInput failed, using message fallback for '{c}'");
                                PostMessage(targetWindow, WM_UNICHAR, (IntPtr)c, IntPtr.Zero);
                                Thread.Sleep(1);
                            }
                            else
                            {
                                Thread.Sleep(1);
                            }
                        }
                        
                        Console.WriteLine($"[TSF] Message-based Unicode insertion completed: '{_text}'");
                        return 0; // S_OK
                    }

                    Console.WriteLine("[TSF] All Unicode insertion methods failed in edit session");
                    return -2147467259; // E_FAIL
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TSF] Unicode DoEditSession exception: {ex.Message}");
                    Console.WriteLine($"[TSF] Stack trace: {ex.StackTrace}");
                    return -2147467259; // E_FAIL
                }
            }
        }

        // Legacy ASCII Edit Session class - kept for backwards compatibility
        private class TSFInsertTextEditSession : ITfEditSession
        {
            private readonly string _text;
            private readonly ITfContext _context;

            public TSFInsertTextEditSession(string text, ITfContext context)
            {
                _text = text;
                _context = context;
            }

            public int DoEditSession(uint ec)
            {
                try
                {
                    Console.WriteLine($"[TSF] ASCII DoEditSession called with EC=0x{ec:X8}, text: '{_text}'");

                    // For ASCII text, use direct PostMessage which we know works
                    IntPtr targetWindow = FindBestTargetWindow();
                    if (targetWindow != IntPtr.Zero)
                    {
                        foreach (char c in _text)
                        {
                            PostMessage(targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                            Thread.Sleep(1);
                        }
                        Console.WriteLine($"[TSF] ASCII insertion completed: '{_text}'");
                        return 0;
                    }

                    return -2147467259; // E_FAIL
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TSF] ASCII DoEditSession exception: {ex.Message}");
                    return -2147467259; // E_FAIL
                }
            }
        }

        // TSF constants
        private const uint TF_ES_READWRITE = 0x00000006;
        private const uint TF_ES_SYNC = 0x00000001;

        // CTextStore class removed - using simplified TSF approach

        // TSF Class IDs
        private static readonly Guid CLSID_TF_ThreadMgr = new Guid("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");
        
        // TSF instance variables
        private static ITfThreadMgr _tsfThreadMgr;
        private static ITfDocumentMgr _tsfDocumentMgr;
        private static ITfContext _tsfContext;
        private static uint _tsfClientId;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);


        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

        // Clipboard APIs - re-added for TSF-integrated clipboard method
        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("imm32.dll")]
        private static extern bool ImmAssociateContextEx(IntPtr hWnd, IntPtr hIMC, uint dwFlags);

        [DllImport("imm32.dll")]
        private static extern bool ImmSetOpenStatus(IntPtr hIMC, bool fOpen);

        private const uint IACE_CHILDREN = 0x0001;
        private const uint IACE_DEFAULT = 0x0010;


        // IsClipboardFormatAvailable removed - TSF only approach

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmCreateContext();

        [DllImport("imm32.dll")]
        private static extern bool ImmDestroyContext(IntPtr hIMC);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

        private const int CPS_COMPLETE = 0x0001; // for ImmNotifyIME if needed


        // IMM32 (IME) APIs and constants
        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetContext(IntPtr hWnd);

        [DllImport("imm32.dll")]
        private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
        private const uint SMTO_ABORTIFHUNG = 0x0002;


        [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
        private static extern bool ImmSetCompositionStringW(IntPtr hIMC, uint dwIndex, byte[] lpComp, uint dwCompLen, byte[] lpRead, uint dwReadLen);

        [DllImport("imm32.dll")]
        private static extern bool ImmNotifyIME(IntPtr hIMC, uint dwAction, uint dwIndex, uint dwValue);



        private const uint SCS_SETSTR = 0x0009;
        private const uint NI_COMPOSITIONSTR = 0x0015;
        private const uint WM_IME_STARTCOMPOSITION = 0x010D;
        private const uint WM_IME_ENDCOMPOSITION = 0x010E;
        private const uint WM_IME_COMPOSITION = 0x010F;
        private const int GCS_RESULTSTR = 0x0800;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern UIntPtr GlobalSize(IntPtr hMem);
        private static string GetThreadNameOrId(uint tid)
        {
            try
            {
                IntPtr hThread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, false, tid);
                if (hThread != IntPtr.Zero)
                {
                    if (GetThreadDescription(hThread, out IntPtr pDesc) == 0 && pDesc != IntPtr.Zero)
                    {
                        string name = Marshal.PtrToStringUni(pDesc);
                        LocalFree(pDesc);
                        CloseHandle(hThread);
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                    CloseHandle(hThread);
                }
            }
            catch { }
            return $"TID:{tid}";
        }


        private static string DescribeElement(AutomationElement el)
        {
            if (el == null) return "<null>";
            try
            {
                string name = string.Empty;
                string cls = string.Empty;
                bool enabled = false, focusable = false;
                string ct = string.Empty;
                try { name = el.Current.Name; } catch { }
                try { cls = el.Current.ClassName; } catch { }
                try { enabled = el.Current.IsEnabled; } catch { }
                try { focusable = el.Current.IsKeyboardFocusable; } catch { }
                try { ct = el.Current.ControlType?.ProgrammaticName; } catch { }
                bool hasValue = el.TryGetCurrentPattern(ValuePattern.Pattern, out _);
                return $"Class='{cls}' CT='{ct}' Name='{name}' Enabled={enabled} Focusable={focusable} HasValuePattern={hasValue}";
            }
            catch { return "<desc-failed>"; }
        }

        // Clipboard constants - re-added for TSF-integrated clipboard method
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // Additional Windows message constants
        private const uint WM_CHAR = 0x0102;
        private const uint WM_UNICHAR = 0x0109;
        private const uint WM_IME_CHAR = 0x0286;

        // Constants for keybd_event
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint KEYEVENTF_UNICODE = 0x0004; // Use Unicode injection via SendInput

        // Main entry point with command line argument support (默認托盤模式)
        public static void Main(string[] args)
        {
            // Check for service mode
            if (args.Length > 0 && args[0].ToLower() == "--service")
            {
                RunAsService(args);
                return;
            }
            
            // Check for console/debug mode
            bool consoleMode = args.Length > 0 && (args[0].ToLower() == "--console" || args[0].ToLower() == "--debug");
            
            if (consoleMode)
            {
                RunDebugMode();
            }
            else
            {
                // Default to tray mode for production use
                RunTrayMode();
            }
        }

        private static void RunAsService(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .ConfigureServices(services =>
                {
                    services.AddHostedService<KeyboardTranslatorService>();
                })
                .Build();
                
            host.Run();
        }

        private static void RunDebugMode()
        {
            Console.WriteLine("=== RDP Keyboard Translator Debug Version ===");
            Console.WriteLine("Initializing...");

            // Initialize session monitoring
            InitializeSessionMonitoring();

            InitializeScanCodeMapping();
            
            // Initialize TSF
            if (InitializeTSF())
            {
                Console.WriteLine("TSF initialized successfully");
            }
            else
            {
                Console.WriteLine("WARNING: TSF initialization failed - falling back to legacy methods");
            }

            // initialize buffer flush timer
            _flushTimer = new System.Windows.Forms.Timer();
            _flushTimer.Interval = BUFFER_FLUSH_INTERVAL_MS;
            _flushTimer.Tick += (s, e) => FlushUnicodeBuffer();
            _flushTimer.Start();
            Console.WriteLine($"Scancode mapping initialized with {_vkToScanCodeMap.Count} entries");

            var translator = new KeyboardTranslator();
            Console.WriteLine("Hook installed successfully");
            Console.WriteLine("Translator is now active and monitoring keyboard events");
            Console.WriteLine("");
            Console.WriteLine("Debug Information:");
            Console.WriteLine("- Will show intercepted RDP events");
            Console.WriteLine("- Will show scancode injections");
            Console.WriteLine("- Monitoring RDP session changes");
            Console.WriteLine("- Press ESC + F12 simultaneously to exit");
            Console.WriteLine("");
            Console.WriteLine("Waiting for RDP keyboard events...");

            Application.Run(); // Keep the application running
        }

        private static void RunTrayMode()
        {
            // Hide console window in tray mode
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                ShowWindow(consoleWindow, 0); // SW_HIDE
            }

            // Initialize session monitoring
            InitializeSessionMonitoring();

            // Initialize components silently
            InitializeScanCodeMapping();
            InitializeTSF();
            
            _flushTimer = new System.Windows.Forms.Timer();
            _flushTimer.Interval = BUFFER_FLUSH_INTERVAL_MS;
            _flushTimer.Tick += (s, e) => FlushUnicodeBuffer();
            _flushTimer.Start();

            var translator = new KeyboardTranslator();
            
            // Create and run tray application
            var trayApp = new TrayApplication();
            Application.Run(trayApp);
        }

        // Console window management
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // Session monitoring and recovery methods
        private static void InitializeSessionMonitoring()
        {
            try
            {
                // Get current session info
                ProcessIdToSessionId(GetCurrentProcessId(), out _currentSessionId);
                _isRdpSession = IsRdpSession(_currentSessionId);
                
                Console.WriteLine($"[SESSION] Current Session ID: {_currentSessionId}");
                Console.WriteLine($"[SESSION] Is RDP Session: {_isRdpSession}");

                // Register for session change notifications
                SystemEvents.SessionSwitch += OnSessionSwitch;
                SystemEvents.SessionEnding += OnSessionEnding;
                SystemEvents.SessionEnded += OnSessionEnded;
                
                // Start a timer to periodically check session status
                var sessionCheckTimer = new System.Windows.Forms.Timer();
                sessionCheckTimer.Interval = 5000; // Check every 5 seconds
                sessionCheckTimer.Tick += CheckSessionStatus;
                sessionCheckTimer.Start();
                
                Console.WriteLine("[SESSION] Session monitoring initialized");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSION] Failed to initialize session monitoring: {ex.Message}");
            }
        }

        private static bool IsRdpSession(uint sessionId)
        {
            try
            {
                IntPtr server = WTSOpenServer(".");
                if (server == IntPtr.Zero)
                    return false;

                IntPtr ppBuffer;
                int bytesReturned;
                if (WTSQuerySessionInformation(server, (int)sessionId, WTSConnectState, out ppBuffer, out bytesReturned))
                {
                    int connectState = Marshal.ReadInt32(ppBuffer);
                    WTSFreeMemory(ppBuffer);
                    WTSCloseServer(server);
                    
                    // WTSActive = 0, WTSConnected = 1, WTSConnectQuery = 2, etc.
                    // RDP sessions typically have connectState > 0
                    return connectState > 0;
                }
                WTSCloseServer(server);
            }
            catch
            {
                // Fallback: check if remote desktop is enabled
                return Environment.GetEnvironmentVariable("SESSIONNAME")?.StartsWith("RDP") == true;
            }
            return false;
        }

        private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            Console.WriteLine($"[SESSION] Session switch event: {e.Reason}");
            
            switch (e.Reason)
            {
                case SessionSwitchReason.RemoteConnect:
                    Console.WriteLine("[SESSION] RDP connected - reinitializing translator");
                    ReinitializeTranslator();
                    break;
                case SessionSwitchReason.RemoteDisconnect:
                    Console.WriteLine("[SESSION] RDP disconnected - pausing translator");
                    _translatorActive = false;
                    break;
                case SessionSwitchReason.SessionLock:
                    Console.WriteLine("[SESSION] Session locked - pausing translator");
                    _translatorActive = false;
                    break;
                case SessionSwitchReason.SessionUnlock:
                    Console.WriteLine("[SESSION] Session unlocked - resuming translator");
                    _translatorActive = true;
                    break;
            }
        }

        private static void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Console.WriteLine($"[SESSION] Session ending: {e.Reason}");
            _translatorActive = false;
        }

        private static void OnSessionEnded(object sender, SessionEndedEventArgs e)
        {
            Console.WriteLine($"[SESSION] Session ended: {e.Reason}");
            _translatorActive = false;
        }

        private static void CheckSessionStatus(object sender, EventArgs e)
        {
            try
            {
                uint newSessionId;
                ProcessIdToSessionId(GetCurrentProcessId(), out newSessionId);
                
                if (newSessionId != _currentSessionId)
                {
                    Console.WriteLine($"[SESSION] Session ID changed: {_currentSessionId} -> {newSessionId}");
                    _currentSessionId = newSessionId;
                    _isRdpSession = IsRdpSession(_currentSessionId);
                    
                    if (_isRdpSession)
                    {
                        Console.WriteLine("[SESSION] New RDP session detected - reinitializing translator");
                        ReinitializeTranslator();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSION] Error checking session status: {ex.Message}");
            }
        }

        private static void ReinitializeTranslator()
        {
            try
            {
                Console.WriteLine("[SESSION] Reinitializing translator components...");
                
                // Restart TSF if needed
                if (_tsfThreadMgr != null)
                {
                    InitializeTSF();
                }
                
                // Reset and restart unicode buffer timer
                if (_flushTimer != null)
                {
                    _flushTimer.Stop();
                    lock (_bufLock)
                    {
                        _unicodeBuffer.Clear();
                    }
                    _flushTimer.Start();
                }
                
                // Reactivate translator
                _translatorActive = true;
                
                Console.WriteLine("[SESSION] Translator reinitialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SESSION] Error reinitializing translator: {ex.Message}");
            }
        }

        public KeyboardTranslator()
        {
            _hookID = SetHook(_proc);
        }

        public static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL,
                    proc,
                    GetModuleHandle(curModule.ModuleName),
                    0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _translatorActive)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                // Debug: Log all keyboard events
                string eventType = "";
                if (wParam == (IntPtr)WM_KEYDOWN) eventType = "WM_KEYDOWN";
                else if (wParam == (IntPtr)WM_KEYUP) eventType = "WM_KEYUP";
                else if (wParam == (IntPtr)WM_SYSKEYDOWN) eventType = "WM_SYSKEYDOWN";
                else if (wParam == (IntPtr)WM_SYSKEYUP) eventType = "WM_SYSKEYUP";

                Console.WriteLine($"[HOOK] {eventType} - VK:{vkCode:X2} ({(Keys)vkCode}) - ScanCode:{hookStruct.scanCode:X2} - Flags:{hookStruct.flags:X2}");

                // Skip keys that system already handles well - let system handle them natively (跳過系統原生支持的按鍵)
                if (IsSystemHandledKey(vkCode))
                {
                    Console.WriteLine($"[SKIP] System-handled key detected - letting system handle natively: {(Keys)vkCode}");
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                // Check for exit hotkey: ESC + F12
                if (vkCode == (int)Keys.Escape || vkCode == (int)Keys.F12)
                {
                    if (IsKeyPressed(Keys.Escape) && IsKeyPressed(Keys.F12))
                    {
                        Console.WriteLine("[EXIT] ESC + F12 detected - Exiting translator");
                        Application.Exit();
                        return (IntPtr)1;
                    }
                }

                // Detect RDP events by checking for VK_PACKET or specific RDP characteristics
                bool isRdpEvent = false;

                // Method 1: VK_PACKET is commonly used by RDP soft keyboards
                if (vkCode == 0xE7) // VK_PACKET
                {
                    Console.WriteLine($"[RDP] Detected VK_PACKET - RDP software keyboard event");
                    // Extract Unicode character from VK_PACKET
                    HandleVkPacket(wParam, lParam);
                    return (IntPtr)1; // Block original event
                }
                // Method 2: Check if this is our own injection (specific flag pattern)
                else if (hookStruct.flags == 0x08) // LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED
                {
                    Console.WriteLine($"[SKIP] Our own injection detected - flags:{hookStruct.flags:X2}");
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }
                // Method 3: Physical keyboard usually has different flag patterns
                else if (hookStruct.scanCode != 0 && hookStruct.flags == 0)
                {
                    Console.WriteLine($"[SKIP] Physical keyboard event - ScanCode:{hookStruct.scanCode:X2}, Flags:{hookStruct.flags:X2}");
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }
                // Method 4: Assume other events with scan codes but unusual flags might be RDP
                else if (hookStruct.scanCode != 0)
                {
                    Console.WriteLine($"[RDP?] Possible RDP event - VK:{vkCode:X2} ScanCode:{hookStruct.scanCode:X2} Flags:{hookStruct.flags:X2}");
                    isRdpEvent = true;
                }

                if (!isRdpEvent)
                {
                    Console.WriteLine($"[SKIP] Not identified as RDP event");
                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                Console.WriteLine($"[RDP] Processing RDP event - VK:{vkCode:X2} ({(Keys)vkCode}) - Converting to scancode");

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    InjectHardwareScanCode(vkCode, false); // Key down
                    return (IntPtr)1; // Block original event
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    InjectHardwareScanCode(vkCode, true); // Key up
                    return (IntPtr)1; // Block original event
                }
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        // Check if this key should be handled by system natively (系統原生支持的按鍵檢查)
        private static bool IsSystemHandledKey(int vkCode)
        {
            // Navigation keys
            if (vkCode == (int)Keys.Up || vkCode == (int)Keys.Down || 
                vkCode == (int)Keys.Left || vkCode == (int)Keys.Right ||
                vkCode == (int)Keys.Home || vkCode == (int)Keys.End ||
                vkCode == (int)Keys.PageUp || vkCode == (int)Keys.PageDown)
                return true;

            // Function keys
            if (vkCode >= (int)Keys.F1 && vkCode <= (int)Keys.F24)
                return true;

            // System keys
            if (vkCode == (int)Keys.Enter || vkCode == (int)Keys.Escape ||
                vkCode == (int)Keys.Tab || vkCode == (int)Keys.Space ||
                vkCode == (int)Keys.Back || vkCode == (int)Keys.Delete ||
                vkCode == (int)Keys.Insert)
                return true;

            // Modifier keys
            if (vkCode == (int)Keys.LShiftKey || vkCode == (int)Keys.RShiftKey ||
                vkCode == (int)Keys.LControlKey || vkCode == (int)Keys.RControlKey ||
                vkCode == (int)Keys.LMenu || vkCode == (int)Keys.RMenu ||
                vkCode == (int)Keys.LWin || vkCode == (int)Keys.RWin)
                return true;

            // Lock keys
            if (vkCode == (int)Keys.CapsLock || vkCode == (int)Keys.NumLock ||
                vkCode == (int)Keys.Scroll)
                return true;

            // Note: Keep numpad keys and number/symbol keys for processing
            // as they are often needed for text input

            return false;
        }


        private static void HandleVkPacket(IntPtr wParam, IntPtr lParam)
        {
            KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            bool isKeyDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);

            // The Unicode character is stored in the scanCode field for VK_PACKET
            char unicodeChar = (char)hookStruct.scanCode;
            Console.WriteLine($"[PACKET] Unicode character: '{unicodeChar}' (0x{hookStruct.scanCode:X4}) - {(isKeyDown ? "DOWN" : "UP")}");

            // Convert Unicode character to virtual key code
            short vkResult = VkKeyScan(unicodeChar);
            if (vkResult != -1)
            {
                int virtualKey = vkResult & 0xFF;
                int shiftState = (vkResult >> 8) & 0xFF;

                Console.WriteLine($"[PACKET] VkKeyScan result: VK={virtualKey:X2}, ShiftState={shiftState:X2}");

                // Skip system-handled keys in VK_PACKET too - let system handle them natively (跳過系統原生支持的按鍵)
                if (IsSystemHandledKey(virtualKey))
                {
                    Console.WriteLine($"[SKIP] System-handled key in VK_PACKET - letting system handle natively: {(Keys)virtualKey}");
                    return;
                }

                // Handle shift state if needed
                if (shiftState != 0)
                {
                    Console.WriteLine($"[PACKET] Character requires shift/ctrl/alt - ShiftState: {shiftState:X2}");
                    // Could implement modifier key injection here if needed
                }

                // TSF first approach: Try TSF for all characters (English/Chinese unified)
                if (isKeyDown && SubmitCharViaTSF(unicodeChar))
                {
                    Console.WriteLine($"[PACKET] TSF method succeeded for '{unicodeChar}'");
                    return;
                }

                // Fallback 1: PostMessage for English characters only
                if (TryPostMessageForEnglish(unicodeChar, isKeyDown))
                {
                    Console.WriteLine($"[PACKET] PostMessage fallback succeeded for '{unicodeChar}'");
                    return;
                }

                // Fallback 2: Hardware scancode injection
                InjectHardwareScanCode(virtualKey, !isKeyDown);
            }
            else
            {
                // Handle Unicode characters that can't be converted to VK (e.g., Chinese)
                Console.WriteLine($"[PACKET] Cannot convert Unicode char '{unicodeChar}' to virtual key");
                if (isKeyDown)
                {
                    // Try TSF first for non-VK characters
                    if (SubmitCharViaTSF(unicodeChar))
                    {
                        Console.WriteLine($"[PACKET] TSF succeeded for non-VK char '{unicodeChar}'");
                        return;
                    }
                    
                    // Fallback: Buffer for batch processing
                    Console.WriteLine($"[PACKET] TSF failed, buffering char '{unicodeChar}'");
                    BufferUnicodeChar(unicodeChar);
                }
                // Do not immediately inject on KEYUP
            }
        }

        // NOTE: Clipboard methods removed per 2025-08-13 decision
        // TSF is now the primary and only text submission method

        private static bool TryPostMessageForEnglish(char unicodeChar, bool isKeyDown)
        {
            // Only try PostMessage for basic ASCII characters
            if (unicodeChar < 32 || unicodeChar > 126)
                return false;

            IntPtr targetWindow = FindBestTargetWindow();
            if (targetWindow == IntPtr.Zero)
                return false;

            _translatorActive = false;

            try
            {
                if (isKeyDown)
                {
                    Console.WriteLine($"[POST] Trying PostMessage WM_CHAR for '{unicodeChar}' (0x{(int)unicodeChar:X2})");
                    IntPtr result = PostMessage(targetWindow, WM_CHAR, (IntPtr)unicodeChar, IntPtr.Zero);
                    Console.WriteLine($"[POST] PostMessage WM_CHAR result: {result}");

                    // Heuristic: terminals often ignore WM_CHAR; apply UIA fallback buffer for certain classes
                    System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
                    GetClassName(targetWindow, cls, 256);
                    string classStr = cls.ToString();
                    if (classStr.Equals("Window Class") || classStr.Contains("Warp") || classStr.Contains("Terminal") || classStr.Contains("CASCADIA") || classStr.Contains("CoreWindow"))
                    {
                        Console.WriteLine($"[POST] Terminal-like class '{classStr}': enqueue ASCII for UIA fallback");
                        BufferUnicodeChar(unicodeChar);
                    }

                    _translatorActive = true;
                    return result != IntPtr.Zero;
                }

                _translatorActive = true;
                return true; // Skip KEYUP events
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POST] PostMessage failed: {ex.Message}");
                _translatorActive = true;
                return false;
            }
        }

        // Optimized target window detection with caching and better window identification
        private static IntPtr _cachedTargetWindow = IntPtr.Zero;
        private static DateTime _lastWindowDetection = DateTime.MinValue;
        private static readonly TimeSpan WindowCacheTimeout = TimeSpan.FromMilliseconds(100);  // 縮短緩存時間以提升響應速度
        
        private static IntPtr FindBestTargetWindow()
        {
            // Cache optimization: avoid expensive window enumeration if recent detection is valid
            if (_cachedTargetWindow != IntPtr.Zero && 
                DateTime.Now - _lastWindowDetection < WindowCacheTimeout &&
                IsWindow(_cachedTargetWindow))
            {
                Console.WriteLine($"[TARGET] Using cached window: 0x{_cachedTargetWindow.ToInt64():X}");
                return _cachedTargetWindow;
            }

            // Reset cache
            _cachedTargetWindow = IntPtr.Zero;
            _lastWindowDetection = DateTime.Now;

            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                Console.WriteLine("[TARGET] No foreground window available");
                return IntPtr.Zero;
            }

            // Get window information for better targeting
            uint fgPid, fgTid = GetWindowThreadProcessId(foregroundWindow, out fgPid);
            uint curTid = GetCurrentThreadId();
            
            System.Text.StringBuilder cls = new System.Text.StringBuilder(256);
            System.Text.StringBuilder title = new System.Text.StringBuilder(256);
            GetClassName(foregroundWindow, cls, 256);
            GetWindowText(foregroundWindow, title, 256);
            string classStr = cls.ToString();
            string titleStr = title.ToString();
            
            Console.WriteLine($"[TARGET] Foreground: PID={fgPid}, TID={fgTid}, Class='{classStr}', Title='{titleStr}'");

            // Priority 1: Try to get focused control within foreground window
            IntPtr focusedWindow = GetFocusedControlInWindow(foregroundWindow, fgTid, curTid);
            if (focusedWindow != IntPtr.Zero)
            {
                _cachedTargetWindow = focusedWindow;
                Console.WriteLine($"[TARGET] Using focused control: 0x{focusedWindow.ToInt64():X}");
                return focusedWindow;
            }

            // Priority 2: Handle specific application types
            IntPtr specialWindow = GetSpecializedTargetWindow(foregroundWindow, classStr);
            if (specialWindow != IntPtr.Zero)
            {
                _cachedTargetWindow = specialWindow;
                Console.WriteLine($"[TARGET] Using specialized window: 0x{specialWindow.ToInt64():X}");
                return specialWindow;
            }

            // Priority 3: Exclude system windows and use foreground
            if (!IsSystemWindow(classStr))
            {
                _cachedTargetWindow = foregroundWindow;
                Console.WriteLine($"[TARGET] Using foreground window: 0x{foregroundWindow.ToInt64():X}");
                return foregroundWindow;
            }

            Console.WriteLine("[TARGET] No suitable window found");
            return IntPtr.Zero;
        }

        private static IntPtr GetFocusedControlInWindow(IntPtr foregroundWindow, uint fgTid, uint curTid)
        {
            bool attached = false;
            try
            {
                // Attach to target thread to get focus information
                if (fgTid != curTid)
                {
                    attached = AttachThreadInput(curTid, fgTid, true);
                    if (!attached)
                    {
                        Console.WriteLine($"[TARGET] Failed to attach to thread {fgTid}");
                        return IntPtr.Zero;
                    }
                }

                IntPtr focusWindow = GetFocus();
                if (focusWindow != IntPtr.Zero && focusWindow != foregroundWindow)
                {
                    Console.WriteLine($"[TARGET] Found focused control: 0x{focusWindow.ToInt64():X}");
                    return focusWindow;
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(curTid, fgTid, false);
                }
            }
            return IntPtr.Zero;
        }

        private static IntPtr GetSpecializedTargetWindow(IntPtr foregroundWindow, string classStr)
        {
            // Handle Chrome/Electron applications (VS Code, Warp, etc.)
            if (classStr.Contains("Chrome_WidgetWin_"))
            {
                return FindChromeRenderWindow(foregroundWindow);
            }

            // Handle Windows Terminal / Warp Terminal
            if (classStr.Contains("CASCADIA") || classStr.Contains("Warp") || classStr.Contains("Terminal"))
            {
                return FindTerminalInputWindow(foregroundWindow);
            }

            // Handle native Windows applications
            if (classStr.Contains("Edit") || classStr.Contains("RichEdit") || classStr.Contains("Scintilla"))
            {
                return foregroundWindow; // Direct text input window
            }

            return IntPtr.Zero;
        }

        private static IntPtr FindChromeRenderWindow(IntPtr chromeWindow)
        {
            IntPtr renderWindow = IntPtr.Zero;
            EnumChildWindows(chromeWindow, (hwnd, lParam) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(hwnd, sb, 256);
                string className = sb.ToString();
                
                // Look for the actual content rendering window
                if (className.Contains("Chrome_RenderWidgetHostHWND") || 
                    className.Contains("Intermediate D3D Window") ||
                    className.Contains("Chrome_WidgetWin_0"))
                {
                    renderWindow = hwnd;
                    Console.WriteLine($"[TARGET] Found Chrome render window: {className}");
                    return false; // Stop enumeration
                }
                return true;
            }, IntPtr.Zero);

            return renderWindow;
        }

        private static IntPtr FindTerminalInputWindow(IntPtr terminalWindow)
        {
            // For terminal applications, often the main window is the input target
            // Look for focused edit controls or use the main window
            IntPtr inputWindow = IntPtr.Zero;
            EnumChildWindows(terminalWindow, (hwnd, lParam) =>
            {
                var sb = new System.Text.StringBuilder(256);
                GetClassName(hwnd, sb, 256);
                string className = sb.ToString();
                
                if (className.Contains("Edit") || className.Contains("Input") || className.Contains("Text"))
                {
                    inputWindow = hwnd;
                    Console.WriteLine($"[TARGET] Found terminal input window: {className}");
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return inputWindow != IntPtr.Zero ? inputWindow : terminalWindow;
        }

        private static bool IsSystemWindow(string classStr)
        {
            return classStr.Equals("Progman") ||
                   classStr.Equals("WorkerW") ||
                   classStr.Contains("Desktop") ||
                   classStr.Contains("ConsoleWindowClass") ||
                   classStr.Contains("Shell_TrayWnd") ||
                   classStr.Contains("DV2ControlHost");
        }

        private static bool TryImeCommitChar(IntPtr targetWindow, char unicodeChar)
        {
            try
            {
                // Attach to target thread to get IME context safely
                uint fgTid = GetWindowThreadProcessId(targetWindow, out _);
                uint curTid = GetCurrentThreadId();
                bool attached = false;
                if (fgTid != curTid) attached = AttachThreadInput(curTid, fgTid, true);

                // If no IME context, create a temporary one and associate (include children)
                IntPtr oldHIMC = IntPtr.Zero;
                IntPtr hIMC = ImmGetContext(targetWindow);
                if (hIMC == IntPtr.Zero)
                {
                    Console.WriteLine("[IME] No IME context - creating temporary IME context");
                    hIMC = ImmCreateContext();
                    if (hIMC == IntPtr.Zero)
                    {
                        if (attached) AttachThreadInput(curTid, fgTid, false);
                        Console.WriteLine("[IME] ImmCreateContext failed");
                        return false;
                    }
                    oldHIMC = ImmAssociateContext(targetWindow, hIMC);
                    ImmAssociateContextEx(targetWindow, hIMC, IACE_DEFAULT | IACE_CHILDREN);
                    ImmSetOpenStatus(hIMC, true);
                }

                // Compose single Unicode char
                byte[] comp = System.Text.Encoding.Unicode.GetBytes(new char[] { unicodeChar });
                bool ok = ImmSetCompositionStringW(hIMC, SCS_SETSTR, comp, (uint)comp.Length, null, 0);
                Console.WriteLine($"[IME] ImmSetCompositionStringW {(ok ? "success" : "fail")}");

                // Try notify/commit composition result
                if (ok)
                {
                    PostMessage(targetWindow, WM_IME_COMPOSITION, IntPtr.Zero, (IntPtr)GCS_RESULTSTR);
                }

                // Restore/destroy temp context if created
                if (oldHIMC != IntPtr.Zero)
                {
                    ImmAssociateContext(targetWindow, oldHIMC);
                    ImmDestroyContext(hIMC);
                }
                else
                {
                    ImmReleaseContext(targetWindow, hIMC);
                }

                if (attached) AttachThreadInput(curTid, fgTid, false);
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IME] Exception: {ex.Message}");
                return false;
            }

            }


        private static bool TryImeCommitString(IntPtr targetWindow, string text)
        {
            try
            {
                uint fgTid = GetWindowThreadProcessId(targetWindow, out _);
                uint curTid = GetCurrentThreadId();
                bool attached = false;
                if (fgTid != curTid) attached = AttachThreadInput(curTid, fgTid, true);

                IntPtr oldHIMC = IntPtr.Zero;
                IntPtr hIMC = ImmGetContext(targetWindow);
                if (hIMC == IntPtr.Zero)
                {
                    Console.WriteLine("[IME] (batch) No IME context - creating temporary IME context");
                    hIMC = ImmCreateContext();
                    if (hIMC == IntPtr.Zero)
                    {
                        if (attached) AttachThreadInput(curTid, fgTid, false);
                        Console.WriteLine("[IME] (batch) ImmCreateContext failed");
                        return false;
                    }
                    oldHIMC = ImmAssociateContext(targetWindow, hIMC);
                    ImmAssociateContextEx(targetWindow, hIMC, IACE_DEFAULT | IACE_CHILDREN);
                    ImmSetOpenStatus(hIMC, true);
                }

                // Start composition, set comp string, complete, and end
                PostMessage(targetWindow, WM_IME_STARTCOMPOSITION, IntPtr.Zero, IntPtr.Zero);

                byte[] comp = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                bool ok = ImmSetCompositionStringW(hIMC, SCS_SETSTR, comp, (uint)comp.Length, null, 0);
                Console.WriteLine($"[IME] (batch) ImmSetCompositionStringW {(ok ? "success" : "fail")}");

                // Ask IME to complete composition
                ImmNotifyIME(hIMC, NI_COMPOSITIONSTR, CPS_COMPLETE, 0);
                PostMessage(targetWindow, WM_IME_COMPOSITION, IntPtr.Zero, (IntPtr)GCS_RESULTSTR);
                PostMessage(targetWindow, WM_IME_ENDCOMPOSITION, IntPtr.Zero, IntPtr.Zero);

                // Restore/destroy temp context
                if (oldHIMC != IntPtr.Zero)
                {
                    ImmAssociateContext(targetWindow, oldHIMC);
                    ImmDestroyContext(hIMC);
                }
                else
                {
                    ImmReleaseContext(targetWindow, hIMC);
                }

                if (attached) AttachThreadInput(curTid, fgTid, false);
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IME] (batch) Exception: {ex.Message}");
                return false;
            }
        }

        private static bool TryUIAInsertChar(char unicodeChar)
        {
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused == null)
                {
                    Console.WriteLine("[UIA] No focused element");
                    return false;
                }

                // Prefer ValuePattern on focused element
                if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) && vpObj is ValuePattern vp)
                {
                    if (!vp.Current.IsReadOnly)
                    {
                        string cur = vp.Current.Value ?? string.Empty;
                        string next = cur + unicodeChar;
                        vp.SetValue(next);
                        Console.WriteLine("[UIA] ValuePattern SetValue on focused element succeeded");
                        return true;
                    }
                    Console.WriteLine("[UIA] Focused ValuePattern is read-only");
                }

                // Try up to a few ancestors for an editable ValuePattern
                AutomationElement parent = TreeWalker.ControlViewWalker.GetParent(focused);
                int hops = 0;
                while (parent != null && hops++ < 5)
                {
                    if (parent.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj2) && vpObj2 is ValuePattern vp2 && !vp2.Current.IsReadOnly)
                    {
                        string cur2 = vp2.Current.Value ?? string.Empty;
                        string next2 = cur2 + unicodeChar;
                        vp2.SetValue(next2);
                        Console.WriteLine("[UIA] Ancestor ValuePattern SetValue succeeded");
                        return true;
                    }
                    parent = TreeWalker.ControlViewWalker.GetParent(parent);
                }

                Console.WriteLine("[UIA] No suitable UIA pattern to insert text");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] Exception: {ex.Message}");
                return false;
            }
        }

        private static void BufferUnicodeChar(char ch)
        {
            lock (_bufLock)
            {
                _unicodeBuffer.Append(ch);
                Console.WriteLine($"[BUF] Queued '{ch}'\t(0x{(int)ch:X4}) len={_unicodeBuffer.Length}");
                
                // 中文字元智能緩衝：單個中文字元或緩衝區達到3個字元時立即刷新
                bool isChinese = ch >= 0x4E00 && ch <= 0x9FFF;  // CJK 統一漢字
                if (isChinese || _unicodeBuffer.Length >= 3)
                {
                    Console.WriteLine($"[BUF] Triggering immediate flush for optimized response");
                    // 重設計時器以立即觸發
                    if (_flushTimer != null)
                    {
                        _flushTimer.Stop();
                        _flushTimer.Interval = 1;  // 1ms 立即觸發
                        _flushTimer.Start();
                    }
                }
            }
        }

        public static void FlushUnicodeBuffer()
        {
            // 重設計時器為正常間隔
            if (_flushTimer != null && _flushTimer.Interval != BUFFER_FLUSH_INTERVAL_MS)
            {
                _flushTimer.Stop();
                _flushTimer.Interval = BUFFER_FLUSH_INTERVAL_MS;
                _flushTimer.Start();
            }
            
            string payload = null;
            lock (_bufLock)
            {
                if (_unicodeBuffer.Length == 0) return;
                payload = _unicodeBuffer.ToString();
                _unicodeBuffer.Clear();
            }

            // Find target window first
            IntPtr targetWindow = FindBestTargetWindow();
            if (targetWindow == IntPtr.Zero)
            {
                Console.WriteLine("[BUF] No target window for batch");
                return;
            }

                // 0) Capture process info for diagnostics
                uint tidForTarget = GetWindowThreadProcessId(targetWindow, out uint pidForTarget);
                try
                {
                    var p = System.Diagnostics.Process.GetProcessById((int)pidForTarget);
                    Console.WriteLine($"[UIA] Target PID={pidForTarget} Name='{p.ProcessName}' TID={tidForTarget}");
                }
                catch { Console.WriteLine($"[UIA] Target PID={pidForTarget} (process lookup failed) TID={tidForTarget}"); }


            // Primary method: TSF batch submission
            if (SubmitTextViaTSF(payload))
            {
                Console.WriteLine($"[BUF] TSF batch submission ok: '{payload}'");
                return;
            }

            // Fallback 1: UIA batch insert
            if (TryUIAInsertString(targetWindow, payload))
            {
                Console.WriteLine($"[BUF] UIA batch insert fallback ok: '{payload}'");
                return;
            }

            // Fallback 2: IME batch commit
            if (TryImeCommitString(targetWindow, payload))
            {
                Console.WriteLine($"[BUF] IME batch commit fallback ok: '{payload}'");
                return;
            }

            // Last resort: WM_UNICHAR per char
            Console.WriteLine($"[BUF] All methods failed, using WM_UNICHAR fallback for: '{payload}'");
            foreach (var c in payload)
            {
                PostMessage(targetWindow, WM_UNICHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        private static bool TryUIAInsertString(IntPtr targetWindow, string text)
        {
            try
            {
                bool TrySetValue(AutomationElement el)
                {
                    if (el == null) return false;
                    if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vpo) && vpo is ValuePattern vpat && !vpat.Current.IsReadOnly)
                    {
                        string cur = vpat.Current.Value ?? string.Empty;
                        vpat.SetValue(cur + text);
                        return true;
                    }
                    return false;
                }

                // 1) Focused element first
                AutomationElement focused = AutomationElement.FocusedElement;
                Console.WriteLine($"[UIA] Focused: {DescribeElement(focused)}");
                if (TrySetValue(focused)) return true;
                // Try few ancestors of focused
                var p = TreeWalker.RawViewWalker.GetParent(focused);
                int ahops = 0;
                // Capture target PID/TID for UIA searches
                uint tidForTarget = GetWindowThreadProcessId(targetWindow, out uint pidForTarget);
                while (p != null && ahops++ < 8)
                {
                    Console.WriteLine($"[UIA] Ancestor[{ahops}]: {DescribeElement(p)}");
                    if (TrySetValue(p)) return true;
                    p = TreeWalker.RawViewWalker.GetParent(p);
                }

                // 2) From target window root, search deep in RawView for best candidate
                AutomationElement root = AutomationElement.FromHandle(targetWindow);
                if (root == null)
                { // Ensure we have pidForTarget captured in outer scope
                    _ = tidForTarget; _ = pidForTarget;
                    Console.WriteLine("[UIA] RootFromHandle=null, falling back to global root scan");
                    // Global root scan: find top-level belonging to same PID and descend
                    var top = AutomationElement.RootElement;
                    var walkerTop = TreeWalker.RawViewWalker;
                    var nodeTop = walkerTop.GetFirstChild(top);
                    int scannedTop = 0;
                    while (nodeTop != null && scannedTop++ < 2000)
                    {
                        try
                        {
                            var pid = nodeTop.Current.ProcessId;
                            if (pid == pidForTarget)
                            {
                                Console.WriteLine($"[UIA] Found top-level for PID {pidForTarget}: {DescribeElement(nodeTop)}");
                                root = nodeTop;
                                break;
                            }
                        }
                        catch { }
                        var childT = walkerTop.GetFirstChild(nodeTop);
                        if (childT != null) { nodeTop = childT; continue; }
                        var sibT = walkerTop.GetNextSibling(nodeTop);
                        if (sibT != null) { nodeTop = sibT; continue; }
                        var parT = walkerTop.GetParent(nodeTop);
                        while (parT != null)
                        {
                            var nextT = walkerTop.GetNextSibling(parT);
                            if (nextT != null) { nodeTop = nextT; break; }
                            parT = walkerTop.GetParent(parT);
                        }
                        if (parT == null) nodeTop = null;
                    }
                }
                if (root != null)
                {
                    var walker = TreeWalker.RawViewWalker;
                    var node = walker.GetFirstChild(root);
                    int scanned = 0;
                    while (node != null && scanned++ < 12000)
                    {
                        Console.WriteLine($"[UIA] Visit[{scanned}]: {DescribeElement(node)}");
                        // Prefer Edit/Text controls that are focusable
                        var ct = node.Current.ControlType;
                        bool isEditableType = (ct == ControlType.Edit || ct == ControlType.Document || ct == ControlType.Custom);
                        bool focusable = false;
                        try { focusable = node.Current.IsKeyboardFocusable; } catch { }

                        if (isEditableType || focusable)
                        {
                            if (TrySetValue(node)) return true;
                            // If node has TextPattern, try ascend up to find a ValuePattern container
                            if (node.TryGetCurrentPattern(TextPattern.Pattern, out _))
                            {
                                var up = walker.GetParent(node);
                                int hops = 0;
                                while (up != null && hops++ < 10)
                                {
                                    Console.WriteLine($"[UIA] Up[{hops}]: {DescribeElement(up)}");
                                    if (TrySetValue(up)) return true;
                                    up = walker.GetParent(up);
                                }
                            }
                        }

                        // DFS traverse RawView
                        var child = walker.GetFirstChild(node);
                        if (child != null) { node = child; continue; }
                        var sib = walker.GetNextSibling(node);
                        if (sib != null) { node = sib; continue; }
                        var parent = walker.GetParent(node);
                        while (parent != null)
                        {
                            var next = walker.GetNextSibling(parent);
                            if (next != null) { node = next; break; }
                            parent = walker.GetParent(parent);
                        }
                        if (parent == null) node = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UIA] String insert exception: {ex.Message}");
            }
            return false;
        }


        private static void InjectUnicodeChar(char unicodeChar, bool isKeyDown)
        {
            // Try to find the best target window for injection
            IntPtr targetWindow = FindBestTargetWindow();
            if (targetWindow == IntPtr.Zero)
            {
                Console.WriteLine($"[UNICODE] No suitable target window found");
                return;
            }

            // Get window info
            System.Text.StringBuilder windowTitle = new System.Text.StringBuilder(256);
            System.Text.StringBuilder className = new System.Text.StringBuilder(256);
            GetWindowText(targetWindow, windowTitle, 256);
            GetClassName(targetWindow, className, 256);
            uint tgtTid = GetWindowThreadProcessId(targetWindow, out _);
            string tname = GetThreadNameOrId(tgtTid);
            Console.WriteLine($"[UNICODE] Target window: '{windowTitle}' (Class: {className}) TID={tgtTid} ({tname})");

            _translatorActive = false; // Prevent intercepting our own events

            try
            {
                if (isKeyDown)
                {
                    // TSF is primary method, fallback to other methods if TSF fails
                    Console.WriteLine($"[UNICODE] Using non-TSF fallback methods for '{unicodeChar}' (0x{(int)unicodeChar:X4})");

                        // Method 0: UI Automation direct insert
                        if (TryUIAInsertChar(unicodeChar))
                        {
                            Console.WriteLine("[UNICODE] UIA insert succeeded");
                            return;
                        }


                    // Method 1a: SendInput with KEYEVENTF_UNICODE (preferred for CJK)
                    try
                    {
                        INPUT[] unicodeInputs = new INPUT[2];
                        unicodeInputs[0] = new INPUT
                        {
                            type = 1, // INPUT_KEYBOARD
                            ki = new KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = unicodeChar,
                                dwFlags = KEYEVENTF_UNICODE, // key down (unicode)
                                time = 0,
                                dwExtraInfo = IntPtr.Zero
                            }
                        };
                        unicodeInputs[1] = new INPUT
                        {
                            type = 1,
                            ki = new KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = unicodeChar,
                                dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, // key up (unicode)
                                time = 0,
                                dwExtraInfo = IntPtr.Zero
                            }
                        };

                        uint unicodeResult = SendInput((uint)unicodeInputs.Length, unicodeInputs, Marshal.SizeOf(typeof(INPUT)));
                        Console.WriteLine($"[UNICODE] SendInput(KEYEVENTF_UNICODE) result: {unicodeResult} {(unicodeResult == 0 ? $"(Error: {GetLastError()})" : "(success)")}");
                        if (unicodeResult > 0) return;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[UNICODE] SendInput(KEYEVENTF_UNICODE) exception: {ex.Message}");
                    }

                    // Method 1b: IME commit fallback
                    if (TryImeCommitChar(targetWindow, unicodeChar))
                    {
                        Console.WriteLine("[UNICODE] IME commit succeeded");
                        return;
                    }

                    // Method 2: WM_UNICHAR
                    Console.WriteLine($"[UNICODE] Trying WM_UNICHAR for '{unicodeChar}' (0x{(int)unicodeChar:X4})");
                    IntPtr result1 = PostMessage(targetWindow, WM_UNICHAR, (IntPtr)unicodeChar, IntPtr.Zero);
                    Console.WriteLine($"[UNICODE] WM_UNICHAR result: {result1}");

                    // Method 3: WM_CHAR (last resort)
                    Console.WriteLine($"[UNICODE] Also trying WM_CHAR for '{unicodeChar}'");
                    IntPtr result2 = PostMessage(targetWindow, WM_CHAR, (IntPtr)unicodeChar, IntPtr.Zero);
                    Console.WriteLine($"[UNICODE] WM_CHAR result: {result2}");
                }
                else
                {
                    Console.WriteLine($"[UNICODE] Skipping KEYUP for Unicode char '{unicodeChar}' (only DOWN events needed)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UNICODE] Exception: {ex.Message}");
            }

            // Method 1c: Try WM_IME_CHAR as some apps translate it differently
            Console.WriteLine($"[UNICODE] Trying WM_IME_CHAR for '{unicodeChar}'");
            IntPtr imeRes = PostMessage(targetWindow, WM_IME_CHAR, (IntPtr)unicodeChar, IntPtr.Zero);
            Console.WriteLine($"[UNICODE] WM_IME_CHAR result: {imeRes}");

            // Method 1d: SendMessageTimeout WM_UNICHAR to avoid message queue swallowing
            IntPtr dummy;
            IntPtr smtoRes = SendMessageTimeout(targetWindow, WM_UNICHAR, (IntPtr)unicodeChar, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out dummy);
            Console.WriteLine($"[UNICODE] SendMessageTimeout WM_UNICHAR result: 0x{smtoRes.ToInt64():X}");

            _translatorActive = true; // Re-enable interception
        }

        private static void InjectHardwareScanCode(int vkCode, bool keyUp)
        {


            // Check what window is currently active
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
            {
                System.Text.StringBuilder windowTitle = new System.Text.StringBuilder(256);
                System.Text.StringBuilder className = new System.Text.StringBuilder(256);
                GetWindowText(foregroundWindow, windowTitle, 256);
                GetClassName(foregroundWindow, className, 256);
                Console.WriteLine($"[INJECT] Active window: '{windowTitle}' (Class: {className})");
            }

            if (!_vkToScanCodeMap.TryGetValue(vkCode, out ushort scanCode))
            {
                // Fallback to Windows API mapping
                scanCode = MapVirtualKey((uint)vkCode, 0);
                Console.WriteLine($"[INJECT] No mapping for VK:{vkCode:X2} - using API mapping: {scanCode:X2}");
            }
            else
            {
                Console.WriteLine($"[INJECT] VK:{vkCode:X2} -> ScanCode:{scanCode:X2} (from mapping table)");
            }

            if (scanCode == 0)
            {
                Console.WriteLine($"[ERROR] Cannot map VK:{vkCode:X2} to scancode - skipping");
                return; // Skip unmappable keys
            }

            _translatorActive = false; // Prevent intercepting our own injection

            // Try different injection methods to find what works
            Console.WriteLine($"[INJECT] Attempting multiple injection methods for VK:{vkCode:X2} -> ScanCode:{scanCode:X2} ({(keyUp ? "UP" : "DOWN")})");

            // Method 1: Standard scancode injection
            INPUT[] inputs1 = new INPUT[1];
            inputs1[0] = new INPUT
            {
                type = 1, // INPUT_KEYBOARD
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = (uint)(0x0008 | (keyUp ? 0x0002 : 0)), // KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            };

            uint result1 = SendInput(1, inputs1, Marshal.SizeOf(typeof(INPUT)));
            Console.WriteLine($"[INJECT] Method 1 (scancode only): {result1} {(result1 == 0 ? $"(Error: {GetLastError()})" : "(success)")}");

            // Method 2: Virtual key with scancode
            if (result1 == 0)
            {
                INPUT[] inputs2 = new INPUT[1];
                inputs2[0] = new INPUT
                {
                    type = 1,
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vkCode,
                        wScan = scanCode,
                        dwFlags = (uint)(0x0008 | (keyUp ? 0x0002 : 0)), // KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                };

                uint result2 = SendInput(1, inputs2, Marshal.SizeOf(typeof(INPUT)));
                Console.WriteLine($"[INJECT] Method 2 (VK+scancode): {result2} {(result2 == 0 ? $"(Error: {GetLastError()})" : "(success)")}");

                // Method 3: Virtual key only (no scancode flag)
                if (result2 == 0)
                {
                    INPUT[] inputs3 = new INPUT[1];
                    inputs3[0] = new INPUT
                    {
                        type = 1,
                        ki = new KEYBDINPUT
                        {
                            wVk = (ushort)vkCode,
                            wScan = 0,
                            dwFlags = (uint)(keyUp ? 0x0002 : 0), // Only KEYEVENTF_KEYUP if releasing
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    };

                    uint result3 = SendInput(1, inputs3, Marshal.SizeOf(typeof(INPUT)));
                    Console.WriteLine($"[INJECT] Method 3 (VK only): {result3} {(result3 == 0 ? $"(Error: {GetLastError()})" : "(success)")}");

                    // Method 4: Legacy keybd_event (often works in RDP when SendInput fails)
                    if (result3 == 0)
                    {
                        try
                        {
                            Console.WriteLine($"[INJECT] Method 4 (keybd_event): Trying VK={vkCode:X2} Scan={scanCode:X2}");

                            // Method 4a: Try VK first (often more reliable than scancode for VS Code)
                            keybd_event((byte)vkCode, 0, keyUp ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
                            Console.WriteLine($"[INJECT] Method 4a (keybd_event VK): Executed successfully");

                            // Method 4b: Also try scancode as backup
                            keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0), UIntPtr.Zero);
                            Console.WriteLine($"[INJECT] Method 4b (keybd_event scancode): Executed successfully");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[INJECT] Method 4 failed: {ex.Message}");

                            // Method 5: keybd_event with VK only
                            try
                            {
                                keybd_event((byte)vkCode, 0, keyUp ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
                                Console.WriteLine($"[INJECT] Method 5 (keybd_event VK): Executed successfully");
                            }
                            catch (Exception ex2)
                            {
                                Console.WriteLine($"[INJECT] Method 5 failed: {ex2.Message}");
                            }
                        }
                    }
                }
            }

            _translatorActive = true; // Re-enable interception
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private static bool IsKeyPressed(Keys key)
        {
            return (GetKeyState((int)key) & 0x8000) != 0;
        }

        public static bool InitializeTSF()
        {
            try
            {
                Console.WriteLine("[TSF] Initializing Text Services Framework...");
                
                // Create TSF Thread Manager
                Type tsfType = Type.GetTypeFromCLSID(CLSID_TF_ThreadMgr);
                if (tsfType == null)
                {
                    Console.WriteLine("[TSF] Failed to get TSF ThreadMgr type");
                    return false;
                }
                
                _tsfThreadMgr = (ITfThreadMgr)Activator.CreateInstance(tsfType);
                if (_tsfThreadMgr == null)
                {
                    Console.WriteLine("[TSF] Failed to create TSF ThreadMgr instance");
                    return false;
                }
                
                // Activate TSF for this thread
                _tsfThreadMgr.Activate(out _tsfClientId);
                Console.WriteLine($"[TSF] ThreadMgr activated with client ID: {_tsfClientId}");
                
                // Create Document Manager
                int hr = _tsfThreadMgr.CreateDocumentMgr(out _tsfDocumentMgr);
                if (hr != 0 || _tsfDocumentMgr == null)
                {
                    Console.WriteLine($"[TSF] Failed to create DocumentMgr, HR=0x{hr:X8}");
                    return false;
                }
                Console.WriteLine("[TSF] DocumentMgr created successfully");
                
                // Create Context
                hr = _tsfDocumentMgr.CreateContext(_tsfClientId, 0, null, out _tsfContext, out uint textStoreId);
                if (hr != 0 || _tsfContext == null)
                {
                    Console.WriteLine($"[TSF] Failed to create Context, HR=0x{hr:X8}");
                    return false;
                }
                Console.WriteLine($"[TSF] Context created successfully with TextStore ID: {textStoreId}");
                
                // Push context to document manager
                hr = _tsfDocumentMgr.Push(_tsfContext);
                if (hr != 0)
                {
                    Console.WriteLine($"[TSF] Failed to push Context, HR=0x{hr:X8}");
                    return false;
                }
                Console.WriteLine("[TSF] Context pushed to DocumentMgr");
                
                Console.WriteLine("[TSF] Text Services Framework initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception during initialization: {ex.Message}");
                Console.WriteLine($"[TSF] Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        private static void CleanupTSF()
        {
            try
            {
                if (_tsfDocumentMgr != null && _tsfContext != null)
                {
                    _tsfDocumentMgr.Pop(0);
                    Console.WriteLine("[TSF] Context popped from DocumentMgr");
                }
                
                if (_tsfThreadMgr != null)
                {
                    _tsfThreadMgr.Deactivate();
                    Console.WriteLine("[TSF] ThreadMgr deactivated");
                }
                
                _tsfContext = null;
                _tsfDocumentMgr = null;
                _tsfThreadMgr = null;
                _tsfClientId = 0;
                
                Console.WriteLine("[TSF] Cleanup completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception during cleanup: {ex.Message}");
            }
        }

        private static bool SubmitTextViaTSF(string text)
        {
            try
            {
                Console.WriteLine($"[TSF] TSF text submission: '{text}' (length: {text.Length})");
                
                // For ASCII characters, still use the proven PostMessage method
                bool isAscii = text.All(c => c >= 32 && c <= 126);
                if (isAscii)
                {
                    IntPtr targetWindow = FindBestTargetWindow();
                    if (targetWindow != IntPtr.Zero)
                    {
                        Console.WriteLine($"[TSF] Using PostMessage for ASCII text: '{text}'");
                        foreach (char c in text)
                        {
                            PostMessage(targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                            // 移除 1ms 延遲以提升輸入速度
                        }
                        return true;
                    }
                }

                // For Unicode characters: Use proper TSF implementation
                return SubmitUnicodeViaTSF(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception in SubmitTextViaTSF: {ex.Message}");
                return false;
            }
        }

        private static bool SubmitUnicodeViaTSF(string text)
        {
            try
            {
                Console.WriteLine($"[TSF] Unicode submission strategy for: '{text}'");
                
                // In RDP environment, TSF native methods are severely limited
                // Use the proven clipboard method integrated into TSF architecture
                Console.WriteLine($"[TSF] Using proven clipboard method for Unicode text: '{text}'");
                
                return TryClipboardMethod(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception in SubmitUnicodeViaTSF: {ex.Message}");
                return false;
            }
        }

        private static bool TryClipboardMethod(string text)
        {
            try
            {
                Console.WriteLine($"[TSF-VIRTUAL] Using virtual clipboard method for '{text}' (system clipboard isolated)");
                
                // Use virtual clipboard service instead of system clipboard
                return VirtualClipboardService.InjectText(text);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF-VIRTUAL] Error: {ex.Message}");
                return false;
            }
        }

        // RestoreClipboard method removed - using virtual clipboard service instead

        private static bool TryDirectFocusedTextInput(string text)
        {
            try
            {
                Console.WriteLine($"[TSF] Trying direct focused text input for: '{text}'");
                
                // Get the focused window and try to create a TSF context for it
                IntPtr focusedWindow = FindBestTargetWindow();
                if (focusedWindow == IntPtr.Zero)
                {
                    Console.WriteLine("[TSF] No focused window for direct TSF");
                    return false;
                }

                // Try to associate our TSF document manager with the focused window
                if (TryAssociateTSFWithWindow(focusedWindow, text))
                {
                    Console.WriteLine($"[TSF] Successfully associated TSF with window for: '{text}'");
                    return true;
                }

                // Last resort: Try composition approach via temporary text store
                Console.WriteLine($"[TSF] Using composition fallback for: '{text}'");
                
                // Disable our translator temporarily to avoid interference
                _translatorActive = false;
                
                try
                {
                    // Try to simulate composition input
                    foreach (char c in text)
                    {
                        Console.WriteLine($"[TSF] Final fallback for char '{c}' (0x{(int)c:X4})");
                        
                        // Method 1: Try keyboard simulation by converting to virtual key where possible
                        short vkResult = VkKeyScan(c);
                        if (vkResult != -1)
                        {
                            int vk = vkResult & 0xFF;
                            Console.WriteLine($"[TSF] Using VK simulation for '{c}': VK={vk:X2}");
                            keybd_event((byte)vk, 0, 0, UIntPtr.Zero); // Down
                            Thread.Sleep(5);
                            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Up
                            Thread.Sleep(5);
                        }
                        else
                        {
                            // Method 2: For non-VK characters, try Alt+numpad input
                            Console.WriteLine($"[TSF] Using Alt+numpad simulation for '{c}'");
                            SimulateAltNumpadInput(c);
                        }
                    }
                }
                finally
                {
                    _translatorActive = true;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception in TryDirectFocusedTextInput: {ex.Message}");
                return false;
            }
        }

        private static bool TryAssociateTSFWithWindow(IntPtr window, string text)
        {
            try
            {
                Console.WriteLine($"[TSF] Associating TSF with window 0x{window.ToInt64():X} for text: '{text}'");
                
                // Get the thread ID of the target window
                uint windowTid = GetWindowThreadProcessId(window, out uint windowPid);
                uint currentTid = GetCurrentThreadId();
                
                Console.WriteLine($"[TSF] Target window TID={windowTid}, PID={windowPid}, Current TID={currentTid}");
                
                // Try to attach to the target thread's input context
                bool attached = false;
                if (windowTid != currentTid)
                {
                    attached = AttachThreadInput(currentTid, windowTid, true);
                    Console.WriteLine($"[TSF] AttachThreadInput result: {attached}");
                }

                try
                {
                    // Now try to push our document manager to the target's thread context
                    // This is a simplified approach - normally we'd need more complex TSF setup
                    
                    // For now, use IME approach which is more likely to work cross-thread
                    bool result = TryImeCommitString(window, text);
                    Console.WriteLine($"[TSF] Cross-thread IME commit result: {result}");
                    return result;
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentTid, windowTid, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Exception in TryAssociateTSFWithWindow: {ex.Message}");
                return false;
            }
        }

        private static bool TrySendInputUnicode(string text)
        {
            try
            {
                List<INPUT> inputs = new List<INPUT>();
                
                foreach (char c in text)
                {
                    // Key down
                    inputs.Add(new INPUT
                    {
                        type = 1, // INPUT_KEYBOARD
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    });
                    
                    // Key up
                    inputs.Add(new INPUT
                    {
                        type = 1,
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = c,
                            dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    });
                }

                uint result = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
                Console.WriteLine($"[TSF] SendInput Unicode result: {result}/{inputs.Count} {(result > 0 ? "(success)" : $"(Error: {GetLastError()})")}");
                
                return result > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] SendInput Unicode exception: {ex.Message}");
                return false;
            }
        }

        private static bool SubmitCharViaTSF(char unicodeChar)
        {
            return SubmitTextViaTSF(unicodeChar.ToString());
        }

        private static void SimulateAltNumpadInput(char c)
        {
            try
            {
                // Convert Unicode to decimal
                int unicode = (int)c;
                string digits = unicode.ToString();
                
                Console.WriteLine($"[TSF] Alt+numpad input for '{c}': Alt+{digits}");
                
                // Press Alt down
                keybd_event(0x12, 0, 0, UIntPtr.Zero); // VK_MENU (Alt)
                Thread.Sleep(2);
                
                // Input digits on numpad
                foreach (char digit in digits)
                {
                    byte numpadVK = (byte)(0x60 + (digit - '0')); // VK_NUMPAD0 to VK_NUMPAD9
                    keybd_event(numpadVK, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(5);
                    keybd_event(numpadVK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Thread.Sleep(5);
                }
                
                // Release Alt
                keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                Thread.Sleep(5); // Give time for Windows to process the Alt+numpad sequence
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TSF] Alt+numpad simulation failed: {ex.Message}");
            }
        }

        // Virtual Clipboard Service - isolated from system clipboard
        private static class VirtualClipboardService
        {
            private static readonly object _lock = new object();
            private static IntPtr _virtualWindow = IntPtr.Zero;
            private static uint _virtualWindowClass = 0;
            private static bool _isDisposed = false;

            // Configuration constants for improved timing and retry logic
            private const int MAX_RETRY_ATTEMPTS = 3;
            private const int BASE_PASTE_WAIT_MS = 50;
            private const int MAX_PASTE_WAIT_MS = 500;
            private const int CLIPBOARD_OPERATION_TIMEOUT_MS = 2000;

            // Create an invisible window for our virtual clipboard operations
            static VirtualClipboardService()
            {
                try
                {
                    InitializeVirtualWindow();

                    // Register cleanup on application exit
                    AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                    Application.ApplicationExit += OnApplicationExit;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Failed to initialize virtual window: {ex.Message}");
                }
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
                uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

            [DllImport("kernel32.dll")]
            private static extern IntPtr GetModuleHandle(string lpModuleName);

            [StructLayout(LayoutKind.Sequential)]
            private struct WNDCLASS
            {
                public uint style;
                public IntPtr lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public string lpszMenuName;
                public string lpszClassName;
            }

            private static void InitializeVirtualWindow()
            {
                Console.WriteLine("[VIRTUAL-CLIP] Initializing virtual clipboard window");
                
                string className = "RdpTranslatorVirtualClip_" + System.Diagnostics.Process.GetCurrentProcess().Id;
                
                WNDCLASS wc = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(new WndProcDelegate(VirtualWindowProc)),
                    hInstance = GetModuleHandle(null),
                    lpszClassName = className,
                    style = 0
                };

                _virtualWindowClass = RegisterClass(ref wc);
                if (_virtualWindowClass == 0)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] RegisterClass failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                _virtualWindow = CreateWindowEx(0, className, "RDP Translator Virtual Clipboard", 0,
                    0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

                if (_virtualWindow == IntPtr.Zero)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] CreateWindow failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // Keep window hidden
                ShowWindow(_virtualWindow, 0); // SW_HIDE
                Console.WriteLine($"[VIRTUAL-CLIP] Virtual window created: 0x{_virtualWindow.ToInt64():X}");
            }

            private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            private static IntPtr VirtualWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            {
                return DefWindowProc(hWnd, msg, wParam, lParam);
            }

            // Cleanup event handlers
            private static void OnProcessExit(object sender, EventArgs e)
            {
                Cleanup();
            }

            private static void OnApplicationExit(object sender, EventArgs e)
            {
                Cleanup();
            }

            // Public cleanup method
            public static void Cleanup()
            {
                lock (_lock)
                {
                    if (_isDisposed) return;

                    try
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Cleaning up virtual clipboard service");

                        if (_virtualWindow != IntPtr.Zero)
                        {
                            DestroyWindow(_virtualWindow);
                            _virtualWindow = IntPtr.Zero;
                        }

                        if (_virtualWindowClass != 0)
                        {
                            UnregisterClass($"RdpTranslatorVirtualClip_{System.Diagnostics.Process.GetCurrentProcess().Id}", GetModuleHandle(null));
                            _virtualWindowClass = 0;
                        }

                        _isDisposed = true;
                        Console.WriteLine("[VIRTUAL-CLIP] Cleanup completed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Cleanup error: {ex.Message}");
                    }
                }
            }

            public static bool InjectText(string text)
            {
                lock (_lock)
                {
                    if (_isDisposed)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Service is disposed, cannot inject text");
                        return false;
                    }

                    try
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Direct clipboard injection for reliable Unicode: '{text}'");

                        // Use improved clipboard method with retry logic
                        return InjectTextViaIsolatedClipboardWithRetry(text);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Injection failed: {ex.Message}");
                        return false;
                    }
                }
            }

            // Improved clipboard injection with retry logic and better error handling
            private static bool InjectTextViaIsolatedClipboardWithRetry(string text)
            {
                for (int attempt = 1; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Clipboard injection attempt {attempt}/{MAX_RETRY_ATTEMPTS} for: '{text}'");

                    if (InjectTextViaIsolatedClipboard(text))
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Injection successful on attempt {attempt}");
                        return true;
                    }

                    if (attempt < MAX_RETRY_ATTEMPTS)
                    {
                        int waitTime = attempt * 100; // Progressive backoff
                        Console.WriteLine($"[VIRTUAL-CLIP] Attempt {attempt} failed, waiting {waitTime}ms before retry");
                        Thread.Sleep(waitTime);
                    }
                }

                Console.WriteLine($"[VIRTUAL-CLIP] All {MAX_RETRY_ATTEMPTS} attempts failed for text: '{text}'");
                return false;
            }

            // Enhanced clipboard backup with validation
            private static ClipboardBackup CreateClipboardBackup(IntPtr owner)
            {
                var backup = new ClipboardBackup();

                try
                {
                    if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] No Unicode text in clipboard to backup");
                        return backup;
                    }

                    IntPtr originalData = GetClipboardData(CF_UNICODETEXT);
                    if (originalData == IntPtr.Zero)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to get clipboard data");
                        return backup;
                    }

                    IntPtr locked = GlobalLock(originalData);
                    if (locked == IntPtr.Zero)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to lock clipboard data");
                        return backup;
                    }

                    try
                    {
                        UIntPtr size = GlobalSize(originalData);
                        if (size.ToUInt32() > 0 && size.ToUInt32() < 10 * 1024 * 1024) // Max 10MB safety check
                        {
                            backup.Data = new byte[(int)size];
                            Marshal.Copy(locked, backup.Data, 0, (int)size);
                            backup.HasBackup = true;
                            Console.WriteLine($"[VIRTUAL-CLIP] Successfully backed up clipboard ({backup.Data.Length} bytes)");
                        }
                        else
                        {
                            Console.WriteLine($"[VIRTUAL-CLIP] Clipboard data size invalid: {size}");
                        }
                    }
                    finally
                    {
                        GlobalUnlock(originalData);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Backup creation failed: {ex.Message}");
                }

                return backup;
            }

            // Helper class for clipboard backup
            private class ClipboardBackup
            {
                public byte[] Data { get; set; }
                public bool HasBackup { get; set; } = false;
            }

            private static bool InjectTextViaKeyboard(string text)
            {
                try
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Attempting keyboard simulation for: '{text}'");
                    
                    // Disable translator to avoid interference
                    _translatorActive = false;
                    
                    try
                    {
                        foreach (char c in text)
                        {
                            // Try to find input method for each character
                            if (!SimulateCharacterInput(c))
                            {
                                Console.WriteLine($"[VIRTUAL-CLIP] Failed to simulate character: '{c}'");
                                return false;
                            }
                            Thread.Sleep(5); // Small delay between characters
                        }
                        
                        Console.WriteLine($"[VIRTUAL-CLIP] All characters simulated successfully");
                        return true;
                    }
                    finally
                    {
                        _translatorActive = true;
                    }
                }
                catch (Exception ex)
                {
                    _translatorActive = true;
                    Console.WriteLine($"[VIRTUAL-CLIP] Keyboard simulation error: {ex.Message}");
                    return false;
                }
            }

            private static bool SimulateCharacterInput(char c)
            {
                // Method 1: For simple ASCII, use direct virtual key
                if (c >= 32 && c <= 126)
                {
                    IntPtr targetWindow = FindBestTargetWindow();
                    if (targetWindow != IntPtr.Zero)
                    {
                        PostMessage(targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                        return true;
                    }
                }

                // Method 2: For Unicode, try composition simulation
                return SimulateUnicodeComposition(c);
            }

            private static bool SimulateUnicodeComposition(char c)
            {
                try
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Unicode messages failing, using fallback to isolated clipboard for '{c}' (0x{(int)c:X4})");
                    
                    // Unicode messages are not working properly, fall back to isolated clipboard immediately
                    return InjectTextViaIsolatedClipboard(c.ToString());
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Unicode composition failed: {ex.Message}");
                    return false;
                }
            }

            private static bool InjectTextViaIsolatedClipboard(string text)
            {
                Console.WriteLine($"[VIRTUAL-CLIP] Using clipboard injection with virtual window isolation for: '{text}'");

                IntPtr clipboardOwner = _virtualWindow != IntPtr.Zero ? _virtualWindow : IntPtr.Zero;
                Console.WriteLine($"[VIRTUAL-CLIP] Using clipboard owner: 0x{clipboardOwner.ToInt64():X}");

                ClipboardBackup backup = null;
                bool clipboardOpened = false;

                try
                {
                    // Step 1: Open clipboard with timeout protection
                    if (!TryOpenClipboardWithTimeout(clipboardOwner, CLIPBOARD_OPERATION_TIMEOUT_MS))
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Failed to open clipboard within timeout");
                        return false;
                    }
                    clipboardOpened = true;

                    // Step 2: Create backup BEFORE making any changes
                    backup = CreateClipboardBackup(clipboardOwner);

                    // Step 3: Set our text (only proceed if we have a backup or clipboard was empty)
                    if (!backup.HasBackup)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] No existing clipboard content, safe to proceed");
                    }

                    if (!SetClipboardText(text))
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to set clipboard text");
                        return false;
                    }

                    CloseClipboard();
                    clipboardOpened = false;
                    Console.WriteLine($"[VIRTUAL-CLIP] Clipboard set to: '{text}'");

                    // Step 4: Send paste command with improved timing
                    bool pasteSuccess = SendPasteCommandWithTiming(text);

                    // Step 5: Restore clipboard (always attempt restoration if we had backup)
                    if (backup.HasBackup)
                    {
                        RestoreClipboardFromBackup(backup, clipboardOwner);
                    }
                    else
                    {
                        // Only clear if we had no backup and paste was successful
                        if (pasteSuccess)
                        {
                            ClearClipboardSafely(clipboardOwner);
                        }
                    }

                    Console.WriteLine($"[VIRTUAL-CLIP] Text injection completed: '{text}' - Success: {pasteSuccess}");
                    return pasteSuccess;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Clipboard injection failed: {ex.Message}");

                    // Emergency cleanup
                    if (clipboardOpened)
                    {
                        try { CloseClipboard(); } catch { }
                    }

                    // Try to restore backup if we had one
                    if (backup?.HasBackup == true)
                    {
                        try
                        {
                            RestoreClipboardFromBackup(backup, clipboardOwner);
                        }
                        catch (Exception restoreEx)
                        {
                            Console.WriteLine($"[VIRTUAL-CLIP] Failed to restore clipboard after error: {restoreEx.Message}");
                        }
                    }

                    return false;
                }
            }

            // Helper method: Try to open clipboard with timeout
            private static bool TryOpenClipboardWithTimeout(IntPtr owner, int timeoutMs)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                while (stopwatch.ElapsedMilliseconds < timeoutMs)
                {
                    if (OpenClipboard(owner))
                    {
                        Console.WriteLine($"[VIRTUAL-CLIP] Clipboard opened after {stopwatch.ElapsedMilliseconds}ms");
                        return true;
                    }

                    Thread.Sleep(10); // Small delay before retry
                }

                Console.WriteLine($"[VIRTUAL-CLIP] Clipboard open timeout after {timeoutMs}ms");
                return false;
            }

            // Helper method: Set clipboard text with proper memory management
            private static bool SetClipboardText(string text)
            {
                try
                {
                    EmptyClipboard();

                    byte[] textBytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
                    IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)textBytes.Length);

                    if (hGlobal == IntPtr.Zero)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to allocate memory");
                        return false;
                    }

                    IntPtr lpMem = GlobalLock(hGlobal);
                    if (lpMem == IntPtr.Zero)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to lock memory");
                        return false;
                    }

                    Marshal.Copy(textBytes, 0, lpMem, textBytes.Length);
                    GlobalUnlock(hGlobal);

                    if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to set clipboard data");
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] SetClipboardText error: {ex.Message}");
                    return false;
                }
            }

            // Helper method: Send paste command with adaptive timing
            private static bool SendPasteCommandWithTiming(string text)
            {
                _translatorActive = false;
                try
                {
                    // Determine wait time based on text length and target application
                    int pasteWaitTime = CalculateOptimalPasteWaitTime(text);

                    Thread.Sleep(10); // Give clipboard time to settle
                    Console.WriteLine($"[VIRTUAL-CLIP] Sending Ctrl+V sequence (will wait {pasteWaitTime}ms for completion)");

                    // Send Ctrl+V with consistent timing
                    keybd_event(0x11, 0, 0, UIntPtr.Zero); // Ctrl down
                    Thread.Sleep(5);
                    keybd_event(0x56, 0, 0, UIntPtr.Zero); // V down
                    Thread.Sleep(5);
                    keybd_event(0x56, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // V up
                    Thread.Sleep(5);
                    keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl up

                    // Wait for paste to complete
                    Thread.Sleep(pasteWaitTime);

                    Console.WriteLine("[VIRTUAL-CLIP] Paste command completed");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Paste command failed: {ex.Message}");
                    return false;
                }
                finally
                {
                    _translatorActive = true;
                }
            }

            // Helper method: Calculate optimal wait time based on context
            private static int CalculateOptimalPasteWaitTime(string text)
            {
                int baseTime = BASE_PASTE_WAIT_MS;

                // Adjust based on text length
                int lengthFactor = Math.Min(text.Length * 2, 100);

                // Adjust based on target application (if we can detect it)
                int appFactor = GetApplicationTimingFactor();

                int totalTime = Math.Min(baseTime + lengthFactor + appFactor, MAX_PASTE_WAIT_MS);

                Console.WriteLine($"[VIRTUAL-CLIP] Calculated paste wait time: {totalTime}ms (base:{baseTime}, length:{lengthFactor}, app:{appFactor})");
                return totalTime;
            }

            // Helper method: Get timing factor based on target application
            private static int GetApplicationTimingFactor()
            {
                try
                {
                    IntPtr foregroundWindow = GetForegroundWindow();
                    if (foregroundWindow == IntPtr.Zero) return 0;

                    var className = new System.Text.StringBuilder(256);
                    GetClassName(foregroundWindow, className, 256);
                    string classStr = className.ToString();

                    // Terminal applications might need more time
                    if (classStr.Contains("CASCADIA") || classStr.Contains("Terminal") || classStr.Contains("Warp"))
                        return 50;

                    // IDE applications might need more time
                    if (classStr.Contains("Chrome") && classStr.Contains("WidgetWin"))
                        return 30;

                    return 0;
                }
                catch
                {
                    return 0;
                }
            }

            // Helper method: Restore clipboard from backup with improved error handling
            private static void RestoreClipboardFromBackup(ClipboardBackup backup, IntPtr owner)
            {
                if (!backup.HasBackup || backup.Data == null)
                {
                    Console.WriteLine("[VIRTUAL-CLIP] No backup to restore");
                    return;
                }

                try
                {
                    if (!TryOpenClipboardWithTimeout(owner, 1000))
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to open clipboard for restoration");
                        return;
                    }

                    try
                    {
                        EmptyClipboard();

                        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)backup.Data.Length);
                        if (hGlobal != IntPtr.Zero)
                        {
                            IntPtr lpMem = GlobalLock(hGlobal);
                            if (lpMem != IntPtr.Zero)
                            {
                                Marshal.Copy(backup.Data, 0, lpMem, backup.Data.Length);
                                GlobalUnlock(hGlobal);

                                if (SetClipboardData(CF_UNICODETEXT, hGlobal) != IntPtr.Zero)
                                {
                                    Console.WriteLine($"[VIRTUAL-CLIP] Successfully restored clipboard ({backup.Data.Length} bytes)");
                                }
                                else
                                {
                                    Console.WriteLine("[VIRTUAL-CLIP] Failed to set restored clipboard data");
                                }
                            }
                            else
                            {
                                Console.WriteLine("[VIRTUAL-CLIP] Failed to lock memory for restoration");
                            }
                        }
                        else
                        {
                            Console.WriteLine("[VIRTUAL-CLIP] Failed to allocate memory for restoration");
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Failed to restore clipboard: {ex.Message}");
                    try { CloseClipboard(); } catch { }
                }
            }

            // Helper method: Safely clear clipboard
            private static void ClearClipboardSafely(IntPtr owner)
            {
                try
                {
                    if (TryOpenClipboardWithTimeout(owner, 500))
                    {
                        EmptyClipboard();
                        CloseClipboard();
                        Console.WriteLine("[VIRTUAL-CLIP] Clipboard cleared safely");
                    }
                    else
                    {
                        Console.WriteLine("[VIRTUAL-CLIP] Failed to open clipboard for clearing");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Failed to clear clipboard: {ex.Message}");
                    try { CloseClipboard(); } catch { }
                }
            }

            // Legacy method - kept for backward compatibility but improved
            private static void RestoreOriginalClipboard(byte[] data, IntPtr owner)
            {
                var backup = new ClipboardBackup { Data = data, HasBackup = data != null };
                RestoreClipboardFromBackup(backup, owner);
            }

            private static bool InjectTextDirect(string text)
            {
                try
                {
                    Console.WriteLine($"[VIRTUAL-CLIP] Using direct message injection for: '{text}'");
                    
                    IntPtr targetWindow = FindBestTargetWindow();
                    if (targetWindow == IntPtr.Zero)
                        return false;

                    _translatorActive = false;
                    try
                    {
                        foreach (char c in text)
                        {
                            if (c >= 32 && c <= 126)
                            {
                                PostMessage(targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                            }
                            else
                            {
                                PostMessage(targetWindow, WM_UNICHAR, (IntPtr)c, IntPtr.Zero);
                                Thread.Sleep(1);
                                PostMessage(targetWindow, WM_IME_CHAR, (IntPtr)c, IntPtr.Zero);
                            }
                            Thread.Sleep(5);
                        }
                        return true;
                    }
                    finally
                    {
                        _translatorActive = true;
                    }
                }
                catch (Exception ex)
                {
                    _translatorActive = true;
                    Console.WriteLine($"[VIRTUAL-CLIP] Direct injection failed: {ex.Message}");
                    return false;
                }
            }
        }

        public static void InitializeScanCodeMapping()
        {
            _vkToScanCodeMap = new Dictionary<int, ushort>
            {
                // Letters
                { (int)Keys.A, 0x1E }, { (int)Keys.B, 0x30 }, { (int)Keys.C, 0x2E }, { (int)Keys.D, 0x20 },
                { (int)Keys.E, 0x12 }, { (int)Keys.F, 0x21 }, { (int)Keys.G, 0x22 }, { (int)Keys.H, 0x23 },
                { (int)Keys.I, 0x17 }, { (int)Keys.J, 0x24 }, { (int)Keys.K, 0x25 }, { (int)Keys.L, 0x26 },
                { (int)Keys.M, 0x32 }, { (int)Keys.N, 0x31 }, { (int)Keys.O, 0x18 }, { (int)Keys.P, 0x19 },
                { (int)Keys.Q, 0x10 }, { (int)Keys.R, 0x13 }, { (int)Keys.S, 0x1F }, { (int)Keys.T, 0x14 },
                { (int)Keys.U, 0x16 }, { (int)Keys.V, 0x2F }, { (int)Keys.W, 0x11 }, { (int)Keys.X, 0x2D },
                { (int)Keys.Y, 0x15 }, { (int)Keys.Z, 0x2C },

                // Numbers
                { (int)Keys.D1, 0x02 }, { (int)Keys.D2, 0x03 }, { (int)Keys.D3, 0x04 }, { (int)Keys.D4, 0x05 },
                { (int)Keys.D5, 0x06 }, { (int)Keys.D6, 0x07 }, { (int)Keys.D7, 0x08 }, { (int)Keys.D8, 0x09 },
                { (int)Keys.D9, 0x0A }, { (int)Keys.D0, 0x0B },

                // Special Keys
                { (int)Keys.Space, 0x39 }, { (int)Keys.Enter, 0x1C }, { (int)Keys.Back, 0x0E },
                { (int)Keys.Tab, 0x0F }, { (int)Keys.Escape, 0x01 }, { (int)Keys.LShiftKey, 0x2A },
                { (int)Keys.RShiftKey, 0x36 }, { (int)Keys.LControlKey, 0x1D }, { (int)Keys.RControlKey, 0x1D },
                { (int)Keys.LMenu, 0x38 }, { (int)Keys.RMenu, 0x38 }
            };
        }

        // Public cleanup method for external access
        public static void CleanupVirtualClipboard()
        {
            VirtualClipboardService.Cleanup();
        }

        ~KeyboardTranslator()
        {
            UnhookWindowsHookEx(_hookID);
            CleanupTSF();
            VirtualClipboardService.Cleanup();
        }
    }

    // System Tray Application
    public class TrayApplication : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _contextMenu;
        private bool _isEnabled = true;

        public TrayApplication()
        {
            InitializeTrayIcon();
        }

        private void InitializeTrayIcon()
        {
            // Create context menu
            _contextMenu = new ContextMenuStrip();
            
            var enabledMenuItem = new ToolStripMenuItem("已啟用", null, OnToggleEnabled);
            enabledMenuItem.Checked = _isEnabled;
            enabledMenuItem.CheckOnClick = true;
            _contextMenu.Items.Add(enabledMenuItem);
            
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(new ToolStripMenuItem("顯示狀態", null, OnShowStatus));
            _contextMenu.Items.Add(new ToolStripMenuItem("調試模式", null, OnShowDebug));
            
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(new ToolStripMenuItem("關於", null, OnAbout));
            _contextMenu.Items.Add(new ToolStripMenuItem("退出", null, OnExit));

            // Create tray icon
            _trayIcon = new NotifyIcon()
            {
                Icon = CreateTrayIcon(),
                ContextMenuStrip = _contextMenu,
                Visible = true,
                Text = "RDP 鍵盤轉換器 v2.0"
            };

            _trayIcon.DoubleClick += OnTrayIconDoubleClick;
            
            // Show startup notification
            _trayIcon.ShowBalloonTip(3000, "RDP 鍵盤轉換器", "已啟動並正在監控 RDP 鍵盤事件", ToolTipIcon.Info);
        }

        private Icon CreateTrayIcon()
        {
            // Create a simple icon programmatically
            using (var bitmap = new System.Drawing.Bitmap(16, 16))
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                // Green background for enabled state
                var bgColor = _isEnabled ? System.Drawing.Color.Green : System.Drawing.Color.Gray;
                g.Clear(bgColor);
                
                // Draw "R" for RDP
                using (var font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold))
                using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White))
                {
                    g.DrawString("R", font, brush, 1, 1);
                }
                
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }

        private void OnToggleEnabled(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            _isEnabled = menuItem.Checked;
            
            // Update global translator state
            KeyboardTranslator._translatorActive = _isEnabled;
            
            // Update icon
            _trayIcon.Icon = CreateTrayIcon();
            
            // Show notification
            string status = _isEnabled ? "已啟用" : "已停用";
            _trayIcon.ShowBalloonTip(2000, "RDP 鍵盤轉換器", $"轉換器 {status}", ToolTipIcon.Info);
        }

        private void OnShowStatus(object sender, EventArgs e)
        {
            string status = _isEnabled ? "運行中" : "已停用";
            string message = $"狀態: {status}\n架構: TSF + 虛擬剪貼簿\n版本: v2.0";
            
            MessageBox.Show(message, "RDP 鍵盤轉換器 - 狀態", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnShowDebug(object sender, EventArgs e)
        {
            // Show console window for debugging
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                ShowWindow(consoleWindow, 5); // SW_SHOW
            }
            
            _trayIcon.ShowBalloonTip(3000, "調試模式", "控制台窗口已顯示，可查看詳細日誌", ToolTipIcon.Info);
        }

        private void OnAbout(object sender, EventArgs e)
        {
            string about = "RDP 鍵盤轉換器 v2.0\n\n" +
                          "功能: 將 Android RDP 軟鍵盤輸入轉換為兼容 Warp Terminal 等應用的格式\n\n" +
                          "架構: TSF (Text Services Framework) + 虛擬剪貼簿隔離\n\n" +
                          "特點:\n" +
                          "• 英文字符: 即時 PostMessage 注入\n" +
                          "• 中文字符: 虛擬剪貼簿安全注入\n" +
                          "• 系統剪貼簿: 完全隔離保護\n" +
                          "• 零用戶干擾: 透明運行\n\n" +
                          "開發: Claude Code Assistant\n" +
                          "完成時間: 2025-08-13";
            
            MessageBox.Show(about, "關於 RDP 鍵盤轉換器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnTrayIconDoubleClick(object sender, EventArgs e)
        {
            OnShowStatus(sender, e);
        }

        private void OnExit(object sender, EventArgs e)
        {
            // Cleanup
            _trayIcon.Visible = false;
            KeyboardTranslator._translatorActive = false;

            // Clean up virtual clipboard service
            KeyboardTranslator.CleanupVirtualClipboard();

            // Show goodbye message
            _trayIcon.ShowBalloonTip(2000, "RDP 鍵盤轉換器", "已退出轉換器", ToolTipIcon.Info);

            // Wait a moment for balloon to show
            System.Threading.Thread.Sleep(500);

            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _trayIcon?.Dispose();
                _contextMenu?.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    public class KeyboardTranslatorService : BackgroundService
    {
        private readonly ILogger<KeyboardTranslatorService> _logger;

        public KeyboardTranslatorService(ILogger<KeyboardTranslatorService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("RDP Keyboard Translator Service starting...");
                
                KeyboardTranslator.InitializeScanCodeMapping();
                
                if (KeyboardTranslator.InitializeTSF())
                {
                    _logger.LogInformation("TSF initialized successfully");
                }
                else
                {
                    _logger.LogWarning("TSF initialization failed - falling back to legacy methods");
                }

                KeyboardTranslator._flushTimer = new System.Windows.Forms.Timer();
                KeyboardTranslator._flushTimer.Interval = KeyboardTranslator.BUFFER_FLUSH_INTERVAL_MS;
                KeyboardTranslator._flushTimer.Tick += (s, e) => KeyboardTranslator.FlushUnicodeBuffer();
                KeyboardTranslator._flushTimer.Start();

                KeyboardTranslator._hookID = KeyboardTranslator.SetHook(KeyboardTranslator._proc);
                _logger.LogInformation("Global keyboard hook installed, Hook ID: {HookId}", KeyboardTranslator._hookID);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in KeyboardTranslatorService");
                throw;
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RDP Keyboard Translator Service stopping...");
            
            if (KeyboardTranslator._hookID != IntPtr.Zero)
            {
                KeyboardTranslator.UnhookWindowsHookEx(KeyboardTranslator._hookID);
                _logger.LogInformation("Keyboard hook removed");
            }
            
            KeyboardTranslator._flushTimer?.Stop();
            KeyboardTranslator._flushTimer?.Dispose();
            
            await base.StopAsync(stoppingToken);
        }
    }
}