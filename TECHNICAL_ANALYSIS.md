# RDP Keyboard Translator - Technical Analysis & Debug Guide

## Project Overview

This project solves the compatibility issue between Android RDP soft keyboards and high-performance applications like Warp Terminal that only listen to low-level hardware scancode events for performance optimization.

## Problem Statement

### Core Issue
- **Warp Terminal**: Only listens to low-level hardware scancodes for performance reasons
- **Android RDP Soft Keyboard**: Sends high-level software events (VK_PACKET) to Windows
- **Result**: Warp Terminal cannot detect RDP soft keyboard input

### Affected Applications
- Warp Terminal (primary target)
- VS Code Claude Code extension (confirmed similar behavior)
- Any application using low-level keyboard hooks for performance

## Technical Architecture

### Solution Overview
```
Android RDP Soft Keyboard → Windows VK_PACKET Events → [Translator] → Hardware Scancodes → Warp Terminal
```

### Core Components

1. **Global Keyboard Hook** (`SetWindowsHookEx` with `WH_KEYBOARD_LL`)
2. **RDP Event Detection** (VK_PACKET identification)
3. **Unicode Character Extraction** (from VK_PACKET scanCode field)
4. **Virtual Key Conversion** (`VkKeyScan` API)
5. **Hardware Scancode Injection** (Multiple methods: `SendInput` + `keybd_event`)

## Current Implementation Status

### ✅ Working Components
- Global keyboard hook installation
- VK_PACKET event detection and interception
- Unicode character extraction from VK_PACKET.scanCode field
- Virtual key code conversion using VkKeyScan()
- Scancode mapping table (A-Z, 0-9, common keys)
- Multiple injection methods (5 different approaches)
- Event filtering to prevent infinite loops
- Comprehensive debug logging

### ❌ Current Issues
- **SendInput API consistently fails with Error 87 (ERROR_INVALID_PARAMETER)**
- **keybd_event also appears to execute but keys not reaching Warp Terminal**
- **Only Enter key from physical keyboard successfully triggers in applications**

## Debug Output Analysis

### Successful VK_PACKET Detection
```
[HOOK] WM_KEYDOWN - VK:E7 (Packet) - ScanCode:73 - Flags:00
[RDP] Detected VK_PACKET - RDP software keyboard event
[PACKET] Unicode character: 's' (0x0073) - DOWN
[PACKET] VkKeyScan result: VK=53, ShiftState=00
[INJECT] VK:53 -> ScanCode:1F (from mapping table)
```

### Injection Failure Pattern
```
[INJECT] Method 1 (scancode only): 0 (Error: 87)
[INJECT] Method 2 (VK+scancode): 0 (Error: 87)
[INJECT] Method 3 (VK only): 0 (Error: 87)
[INJECT] Method 4 (keybd_event scancode): Executed successfully
[INJECT] Method 5 (keybd_event VK): Executed successfully
```

## Technical Deep Dive

### VK_PACKET Structure Analysis
```c
// VK_PACKET event structure
KBDLLHOOKSTRUCT {
    vkCode: 0xE7 (VK_PACKET)
    scanCode: [Unicode character value]  // e.g., 0x0073 for 's'
    flags: 0x00 (DOWN) or 0x80 (UP)
    time: [timestamp]
    dwExtraInfo: [usually 0]
}
```

### Character Conversion Process
```c#
char unicodeChar = (char)hookStruct.scanCode;  // Extract from scanCode field
short vkResult = VkKeyScan(unicodeChar);       // Convert to VK + shift state
int virtualKey = vkResult & 0xFF;              // Extract VK code
int shiftState = (vkResult >> 8) & 0xFF;       // Extract modifier state
ushort scanCode = MapVirtualKey((uint)virtualKey, 0); // Convert to hardware scancode
```

### Injection Methods Attempted

#### Method 1: Pure Scancode Injection
```c#
INPUT input = {
    type: INPUT_KEYBOARD,
    ki: {
        wVk: 0,
        wScan: scanCode,
        dwFlags: KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0),
        time: 0,
        dwExtraInfo: 0
    }
};
SendInput(1, &input, sizeof(INPUT));
```

#### Method 2: VK + Scancode Injection
```c#
INPUT input = {
    type: INPUT_KEYBOARD,
    ki: {
        wVk: virtualKey,
        wScan: scanCode,
        dwFlags: KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0),
        time: 0,
        dwExtraInfo: 0
    }
};
```

#### Method 3: Pure Virtual Key Injection
```c#
INPUT input = {
    type: INPUT_KEYBOARD,
    ki: {
        wVk: virtualKey,
        wScan: 0,
        dwFlags: keyUp ? KEYEVENTF_KEYUP : 0,
        time: 0,
        dwExtraInfo: 0
    }
};
```

#### Method 4: Legacy keybd_event with Scancode
```c#
keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0), 0);
```

#### Method 5: Legacy keybd_event with VK
```c#
keybd_event((byte)vkCode, 0, keyUp ? KEYEVENTF_KEYUP : 0, 0);
```

## Error Analysis

### Error 87: ERROR_INVALID_PARAMETER
**Common causes in RDP environments:**
1. **Session isolation** - RDP sessions have restricted input injection capabilities
2. **UIPI (User Interface Privilege Isolation)** - Integrity level mismatches
3. **Desktop session differences** - Console vs RDP desktop contexts
4. **Parameter validation** - Windows validates INPUT structure more strictly in RDP

### Potential Root Causes

#### 1. RDP Session Restrictions
RDP imposes security restrictions on input injection to prevent malicious activity:
- `SendInput` may be blocked or filtered
- Different desktop contexts (Console Desktop vs RDP Desktop)
- Session isolation preventing cross-session input injection

#### 2. Integrity Level Issues
- Application running at different integrity level than target
- UIPI preventing low-integrity process from injecting into higher integrity
- Need to match or exceed target application's integrity level

#### 3. Desktop Context Mismatch
- Hook installed in RDP desktop context
- Target application in different desktop session
- Input injection across desktop boundaries restricted

#### 4. Timing and Focus Issues
- Injection happening too fast after detection
- Target window not properly focused
- Race conditions between hook and injection

## Debugging Strategies

### Current Debug Information
- Active window title and class name
- Complete VK_PACKET parsing details
- All injection method results with error codes
- Character conversion pipeline tracing

### Recommended Additional Debug Steps

#### 1. Session Context Analysis
```c#
// Add these APIs for session debugging
[DllImport("kernel32.dll")]
static extern uint GetCurrentProcessId();

[DllImport("kernel32.dll")]
static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

[DllImport("user32.dll")]
static extern IntPtr GetThreadDesktop(uint dwThreadId);

[DllImport("kernel32.dll")]
static extern uint GetCurrentThreadId();
```

#### 2. Target Process Analysis
```c#
// Check target process details
[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

// Compare session IDs, desktop contexts, integrity levels
```

#### 3. Alternative Injection Methods
```c#
// Try PostMessage/SendMessage approach
[DllImport("user32.dll")]
static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

const uint WM_KEYDOWN = 0x0100;
const uint WM_KEYUP = 0x0101;
const uint WM_CHAR = 0x0102;
```

## Known Working Scenarios

### ✅ Physical Keyboard Enter Key
- Physical Enter key successfully triggers in VS Code Claude Code extension
- Suggests the target applications CAN receive low-level events
- Indicates our hook is not the issue, but injection method is

### ✅ VK_PACKET Detection
- Perfect detection and parsing of RDP soft keyboard events
- Unicode character extraction working correctly
- Virtual key conversion successful

## Next Steps for Resolution

### Priority 1: Session Context Investigation
1. Compare process sessions (RDP vs Console)
2. Check desktop handles and contexts
3. Verify integrity levels match

### Priority 2: Alternative Injection Approaches
1. **PostMessage/SendMessage** - Send WM_KEYDOWN/WM_KEYUP directly
2. **SetFocus + keybd_event** - Ensure proper focus before injection
3. **Raw Input injection** - Use lower-level input methods
4. **Thread context switching** - Inject from target thread context

### Priority 3: Target Application Analysis
1. Test with multiple applications to isolate Warp-specific issues
2. Check if Warp Terminal has specific input filtering
3. Test different scancode values and timings

## File Structure

### Core Implementation
- `RdpKeyboardTranslator.cs` - Main application logic
- `RdpKeyboardTranslator.csproj` - .NET project configuration
- `app.manifest` - Administrator privilege configuration

### Build & Execution Scripts  
- `build.bat` - Build the application
- `run_admin.bat` - Run with admin privileges
- `run_debug.bat` - Run with console output visible

### Documentation
- `README.md` - User documentation
- `TESTING_GUIDE.md` - Comprehensive testing instructions
- `TECHNICAL_ANALYSIS.md` - This technical deep dive

## Environment Requirements

### Development
- .NET 6.0 SDK or later
- Windows 10/11
- Administrator privileges (required for global hooks)

### Runtime
- Windows RDP environment
- Android RDP client (Microsoft Remote Desktop)
- Target applications (Warp Terminal, VS Code with Claude extension)

## Collaboration Notes

### For Other Assistants/Developers
1. **Focus on the injection failure** - Detection and parsing work perfectly
2. **RDP security restrictions** are the primary blocker
3. **Error 87 pattern** suggests parameter validation issues
4. **Physical Enter key success** proves target apps can receive low-level input
5. **Alternative injection methods** needed - SendInput blocked in RDP context

### Key Files to Examine
- Lines 250-370 in `RdpKeyboardTranslator.cs` (injection logic)
- VK_PACKET handling in `HandleVkPacket()` method
- All five injection methods in `InjectHardwareScanCode()`

### Test Environment Setup
- Run `run_debug.bat` to see complete debug output
- Use Android RDP to connect to Windows machine
- Open Warp Terminal and try typing with soft keyboard
- Monitor console output for injection results

## Contact Points for Further Debug

If you're continuing this debug effort, focus on:

1. **Session/Desktop context mismatches** in RDP environment
2. **Alternative messaging approaches** (PostMessage/SendMessage)
3. **Integrity level and privilege escalation** issues
4. **Timing and focus management** during injection

The foundation is solid - we just need to find the right injection method that works within RDP security constraints.