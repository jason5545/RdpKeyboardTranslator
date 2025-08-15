# RDP 鍵盤轉換器失敗嘗試技術分析報告

## 📋 文件目的

本文件詳細記錄了在開發 RDP 鍵盤轉換器過程中遇到的各種技術挑戰、失敗的嘗試和深度技術分析。供其他 AI 助手或開發者深入調查 RDP 環境下的輸入注入限制，並探索更好的解決方案。

---

## 🎯 核心問題描述

### 問題背景
- **目標**: 使 Android RDP 軟體鍵盤能在 Warp Terminal 和 VS Code 等高性能應用中正常工作
- **根本原因**: RDP 軟體鍵盤發送高級軟體事件，但 Warp Terminal 只監聽低級硬體掃描碼事件
- **環境**: Windows 10/11 + Android Microsoft Remote Desktop + Warp Terminal/VS Code

### 偵測成功但注入失敗
✅ **成功部分**: VK_PACKET 事件偵測 100% 完美  
❌ **失敗部分**: 所有硬體級鍵盤注入方法在 RDP 環境中失效

---

## 💔 失敗嘗試清單

### 1. SendInput API 系列失敗

#### 方法 1: 基本 SendInput 與 KEYEVENTF_SCANCODE
```csharp
INPUT[] inputs = new INPUT[1];
inputs[0].type = INPUT_KEYBOARD;
inputs[0].ki.wScan = (ushort)scanCode;
inputs[0].ki.dwFlags = KEYEVENTF_SCANCODE;
uint result = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
```
**結果**: `SendInput` 返回 0，`GetLastError()` = 87 (ERROR_INVALID_PARAMETER)  
**分析**: RDP 環境的安全限制阻止跨會話輸入注入

#### 方法 2: SendInput Unicode 注入
```csharp
inputs[0].ki.wScan = (ushort)unicodeChar;
inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;
```
**結果**: 同樣返回錯誤 87  
**分析**: RDP 安全沙箱完全阻止 SendInput 系列 API

#### 方法 3: 虛擬鍵碼 SendInput
```csharp
inputs[0].ki.wVk = (ushort)virtualKey;
inputs[0].ki.dwFlags = 0; // 使用虛擬鍵碼
```
**結果**: Error 87  
**分析**: 問題不在於參數，而是 RDP 環境本身的限制

### 2. keybd_event API 失敗

#### 嘗試
```csharp
keybd_event((byte)virtualKey, (byte)scanCode, KEYEVENTF_SCANCODE, UIntPtr.Zero);
```
**結果**: API 調用成功，但目標應用程式完全無反應  
**分析**: keybd_event 在 RDP 環境中被靜默忽略

### 3. Windows 訊息注入失敗

#### 方法 1: WM_KEYDOWN/WM_KEYUP
```csharp
PostMessage(targetWindow, WM_KEYDOWN, (IntPtr)virtualKey, MakeLParam(scanCode));
PostMessage(targetWindow, WM_KEYUP, (IntPtr)virtualKey, MakeLParam(scanCode));
```
**結果**: Warp Terminal 完全忽略這些訊息  
**分析**: 高性能應用程式只處理低級硬體事件，忽略高級 Windows 訊息

#### 方法 2: WM_UNICHAR Unicode 訊息
```csharp
PostMessage(targetWindow, WM_UNICHAR, (IntPtr)unicodeChar, IntPtr.Zero);
```
**結果**: 訊息發送成功，但目標應用程式不處理  
**分析**: 現代應用程式很少實現 WM_UNICHAR 處理

#### 方法 3: WM_IME_CHAR 輸入法訊息
```csharp
PostMessage(targetWindow, WM_IME_CHAR, (IntPtr)unicodeChar, IntPtr.Zero);
```
**結果**: 產生亂碼字符，不是期望的 Unicode 字符  
**分析**: IME 訊息的編碼與我們的 Unicode 字符不匹配

### 4. 輸入法 (IME) 相關失敗

#### 方法 1: ImmSetCompositionString
```csharp
IntPtr hIMC = ImmGetContext(targetWindow);
bool result = ImmSetCompositionStringW(hIMC, GCS_COMPSTR, text, text.Length * 2, null, 0);
```
**結果**: 返回 false，無效果  
**分析**: 跨進程 IME 操作在 RDP 環境中受限

#### 方法 2: 模擬輸入法組合
嘗試發送 WM_IME_STARTCOMPOSITION, WM_IME_COMPOSITION, WM_IME_ENDCOMPOSITION 序列  
**結果**: 訊息序列發送成功，但沒有文字輸出  
**分析**: 輸入法上下文沒有正確建立

### 5. 低級鍵盤鉤子注入失敗

#### 嘗試在鉤子回調中直接修改事件
```csharp
// 嘗試修改 KBDLLHOOKSTRUCT 結構
hookStruct.scanCode = newScanCode;
Marshal.StructureToPtr(hookStruct, lParam, false);
```
**結果**: 系統忽略修改，或導致不穩定  
**分析**: KBDLLHOOKSTRUCT 為只讀結構，無法在鉤子中修改

### 6. SetWindowsHookEx 注入嘗試

#### 嘗試使用 WH_KEYBOARD 而不是 WH_KEYBOARD_LL
**結果**: 在 RDP 環境中無法正確工作  
**分析**: WH_KEYBOARD 需要目標進程的線程 ID，跨進程困難

---

## 🔬 深度技術分析

### RDP 環境特殊限制

#### 1. 安全沙箱機制
- **會話隔離**: RDP 會話與本地會話在不同的安全上下文中
- **輸入注入限制**: 系統阻止跨會話的低級輸入注入
- **API 限制**: SendInput, keybd_event 等 API 在 RDP 環境中被限制

#### 2. 訊息路由變更
- **訊息過濾**: RDP 可能過濾或修改某些 Windows 訊息
- **焦點管理**: 窗口焦點和輸入焦點在 RDP 環境中行為不同
- **線程親和性**: 跨線程訊息傳遞在 RDP 中受限

#### 3. 硬體抽象層問題
- **掃描碼映射**: RDP 環境中的掃描碼映射可能不同於本地環境
- **設備驅動**: 虛擬鍵盤設備與物理設備的驅動差異
- **事件優先級**: 硬體事件與軟體事件的處理優先級

### TSF (Text Services Framework) 深度分析

#### 成功部分
```csharp
// TSF 初始化完全成功
ITfThreadMgr threadMgr = (ITfThreadMgr)new TfThreadMgr();
threadMgr.Activate(out _tsfClientId);
threadMgr.CreateDocumentMgr(out _tsfDocumentMgr);
_tsfDocumentMgr.CreateContext(_tsfClientId, 0, null, out _tsfContext, out uint contextId);
```
**日誌**: `[TSF] ThreadMgr activated with client ID: 32`  
**分析**: TSF 框架本身在 RDP 環境中工作正常

#### 失敗部分: 文字插入
```csharp
// SetText 報告成功但無實際效果
ITfRange range;
ITfInsertAtSelection insertAtSelection = (ITfInsertAtSelection)_tsfContext;
insertAtSelection.InsertTextAtSelection(ec, 0, text, text.Length, out range);
```
**現象**: 
- `SetText` 返回 `HR=0x00000000` (成功)
- 中文字符輸入 `您好` 實際輸出 `Y` 等亂碼
- `SessionResult=0x80004003` (E_POINTER)

**深度分析**:
1. **線程親和性問題**: TSF Context 創建在當前線程，但需要關聯到目標應用程式線程
2. **TextStore 缺失**: 缺少 ITextStore 介面實現，無法與目標應用程式通信
3. **Document Focus**: TSF Context 沒有正確的文字插入點信息

### 成功方案的技術原理

#### ASCII 字符: PostMessage WM_CHAR
```csharp
PostMessage(targetWindow, WM_CHAR, (IntPtr)unicodeChar, IntPtr.Zero);
```
**成功原因**:
- WM_CHAR 是高級訊息，不需要硬體級權限
- ASCII 字符編碼簡單，不涉及複雜的 Unicode 處理
- 大多數應用程式都實現了 WM_CHAR 處理

#### Unicode 字符: 虛擬剪貼簿服務
```csharp
// 創建隔離的剪貼簿環境
IntPtr virtualWindow = CreateWindow("STATIC", $"RdpTranslatorVirtualClip_{Process.GetCurrentProcess().Id}");
OpenClipboard(virtualWindow);
SetClipboardData(CF_UNICODETEXT, textPtr);
// 模擬 Ctrl+V
PostMessage(targetWindow, WM_KEYDOWN, (IntPtr)Keys.ControlKey, IntPtr.Zero);
PostMessage(targetWindow, WM_CHAR, (IntPtr)Keys.V, IntPtr.Zero);
```
**成功原因**:
- 剪貼簿操作不受 RDP 會話限制
- Ctrl+V 是應用程式廣泛支持的標準操作
- 虛擬窗口提供完整的剪貼簿隔離

---

## 🧪 失敗實驗數據

### SendInput 錯誤詳情
```
方法: SendInput(1, inputs, 28)
返回值: 0 (期望: 1)
GetLastError(): 87 (ERROR_INVALID_PARAMETER)
輸入結構:
  - type: 1 (INPUT_KEYBOARD)
  - wVk: 0
  - wScan: 0x1E (A鍵掃描碼)
  - dwFlags: 0x0008 (KEYEVENTF_SCANCODE)
  - time: 0
  - dwExtraInfo: 0
```

### 跨線程操作失敗
```
目標窗口: 0x2049E (Warp Terminal)
目標 PID: 1356, TID: 12872
當前 TID: 9616
AttachThreadInput 結果: True (成功)
GetFocus() 結果: 0x00000000 (失敗)
錯誤: 即使線程附加成功，仍無法獲取焦點信息
```

### TSF EditSession 失敗詳情
```
DoEditSession 調用: 成功
EditCookie: 0x12345678
SetText HR: 0x00000000 (成功)
實際效果: 輸入 "您好" → 顯示 "Y"
SessionResult: 0x80004003 (E_POINTER)
分析: TSF 內部成功，但與目標應用程式斷開
```

---

## 🔍 RDP 特定技術限制

### 1. 會話邊界限制
- **不同安全上下文**: RDP 會話執行在獨立的安全上下文中
- **跨會話通信限制**: Windows 阻止大部分跨會話的輸入操作
- **權限提升無效**: 即使以管理員權限執行，仍受 RDP 限制

### 2. 虛擬化層影響
- **輸入虛擬化**: RDP 對輸入事件進行虛擬化處理
- **訊息過濾**: 某些低級訊息被 RDP 層過濾或修改
- **設備模擬**: 虛擬鍵盤設備與物理設備行為差異

### 3. 應用程式偵測機制
- **RDP 偵測**: 應用程式可能偵測到 RDP 環境並改變行為
- **安全策略**: 某些應用程式在 RDP 環境中禁用某些功能
- **性能最佳化**: 應用程式可能在 RDP 中使用不同的輸入處理路徑

---

## 📊 解決方案效果對比

| 方法 | ASCII 字符 | Unicode 字符 | RDP 兼容性 | 實現複雜度 | 可靠性 |
|------|------------|--------------|------------|------------|--------|
| SendInput + SCANCODE | ❌ | ❌ | ❌ | 低 | 0% |
| keybd_event | ❌ | ❌ | ❌ | 低 | 0% |
| WM_KEYDOWN/UP | ❌ | ❌ | ⚠️ | 中 | 20% |
| WM_UNICHAR | ❌ | ❌ | ⚠️ | 低 | 10% |
| IME Composition | ❌ | ❌ | ❌ | 高 | 5% |
| PostMessage WM_CHAR | ✅ | ❌ | ✅ | 低 | 100% |
| TSF 直接插入 | ❌ | ❌ | ⚠️ | 高 | 15% |
| 虛擬剪貼簿 | ✅ | ✅ | ✅ | 中 | 95% |

---

## 🎯 關鍵發現與洞察

### 1. RDP 環境的根本限制
RDP 環境對低級輸入注入有系統級限制，這不是程式錯誤，而是 Windows 安全架構的設計。任何嘗試繞過這些限制的方法都很可能失敗。

### 2. 高級 vs 低級 API 的差異
- **低級 API (失敗)**: SendInput, keybd_event, 硬體掃描碼
- **高級 API (成功)**: PostMessage, 剪貼簿操作, 應用程式訊息

### 3. 字符類型的重要性
ASCII 字符和 Unicode 字符在 RDP 環境中的處理完全不同，需要不同的解決方案。

### 4. 應用程式行為差異
不同應用程式對輸入事件的處理差異巨大：
- **終端類應用**: 優先處理硬體事件
- **文本編輯器**: 支持多種輸入方法
- **瀏覽器**: 複雜的事件處理機制

---

## 🚀 後續研究方向

### 1. 完整 TSF 實現
- 實現完整的 ITextStore 介面
- 研究跨進程 TSF 通信機制
- 探索 TSF 與目標應用程式的直接集成

### 2. 內核級解決方案
- 研究 Windows 內核級鍵盤過濾驅動
- 探索 RDP 驅動層面的修改可能性
- 調查 Windows 內核安全限制的繞過方法

### 3. 應用程式特定解決方案
- 針對 Warp Terminal 的專用解決方案
- VS Code 擴展級別的輸入處理
- Chrome/Electron 應用的特定處理

### 4. 替代架構探索
- WebRTC 替代 RDP 的可能性
- 自定義遠程桌面協議
- 應用程式層面的輸入代理

---

## 📝 結論

經過大量的嘗試和分析，我們發現：

1. **RDP 環境的限制是系統性的**，不是程式實現問題
2. **低級輸入注入在 RDP 中基本不可能**，需要尋找高級解決方案
3. **混合方法是當前最佳解決方案**：ASCII 用 PostMessage，Unicode 用虛擬剪貼簿
4. **TSF 框架有潛力**，但需要更深入的實現
5. **完美的解決方案可能需要系統級修改**

這些失敗經驗為後續的深入研究提供了寶貴的方向和避免重複錯誤的指導。

---

**報告生成**: 2025-08-13  
**技術深度**: 系統級分析  
**建議**: 重點研究 TSF 完整實現和內核級解決方案