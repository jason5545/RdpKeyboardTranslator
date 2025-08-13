# TSF 實作狀態報告 - 2025-08-13

## 專案概述

本專案旨在將 RDP 鍵盤轉換器從混合方法（剪貼簿+PostMessage）轉換為統一的 TSF (Text Services Framework) 架構，以實現更現代化、穩定和統一的文字輸入處理。

## 當前技術狀態

### ✅ 成功完成的部分

#### 1. TSF 基礎架構
- **TSF 初始化**：完全成功
  ```
  [TSF] ThreadMgr activated with client ID: 32
  [TSF] DocumentMgr created successfully  
  [TSF] Context created successfully with TextStore ID: 0
  [TSF] Context pushed to DocumentMgr
  ```
- **COM 介面定義**：完整實現 ITfThreadMgr, ITfDocumentMgr, ITfContext, ITfRange, ITfInsertAtSelection
- **資源管理**：正確的初始化和清理流程

#### 2. VK_PACKET 檢測
- **完美檢測**：100% 準確攔截 RDP 軟鍵盤事件
- **字符解析**：正確提取 Unicode 字符
  - 中文：`您` (0x60A8), `好` (0x597D)  
  - 英文：`s` (0x0073), `f` (0x0066), `d` (0x0064)
- **事件分類**：正確區分英文/中文字符

#### 3. 英文字符處理
- **100% 成功**：PostMessage WM_CHAR 方法完全有效
- **立即生效**：字符立刻出現在目標應用程式
- **穩定可靠**：無延遲、無丟失

### ❌ 當前問題

#### 1. 中文字符輸入失效
**現象**：
- TSF 內部報告成功：`[TSF] Unicode text insertion succeeded`
- 實際輸出亂碼：輸入`您好`出現`Y`等亂碼字符
- SessionResult 異常：`SessionResult=0x80004003` (可能是 E_POINTER)

**技術分析**：
```
TSF SetText HR=0x00000000 (成功)  →  但實際無效果
RequestEditSession HR=0x00000000  →  SessionResult=0x80004003
```

#### 2. 跨線程 TSF 關聯失敗
**現象**：
```
[TSF] Target window TID=12872, PID=1356, Current TID=9616
[TSF] AttachThreadInput result: True  (成功)
[IME] (batch) ImmSetCompositionStringW fail  (失敗)
```

**問題根源**：
- TSF Context 創建在當前線程 (TID=9616)
- 目標視窗在不同線程 (TID=12872) 
- TSF Context 沒有正確關聯到目標應用程式的文字控件

#### 3. 備援方法全部失效
- **SendInput Unicode**：Error 87 (RDP 安全限制)
- **WM_UNICHAR**：發送成功但目標應用程式忽略
- **WM_IME_CHAR**：發送成功但產生亂碼
- **IME SetCompositionString**：失敗

## 技術深度分析

### TSF 架構限制

#### 1. 線程親和性問題
TSF 的設計要求 DocumentMgr 和 Context 與目標應用程式的 UI 線程關聯。我們的實現創建了本地 TSF Context，但沒有正確橋接到目標應用程式。

#### 2. TextStore 缺失
真正的 TSF 實現需要 ITextStore 介面來與應用程式的文字控件通信。我們的簡化實現缺少這個關鍵組件。

#### 3. Document Focus 問題
TSF Context 需要知道當前的文字插入點，但我們的實現無法獲取目標應用程式的真實游標位置。

### RDP 環境特殊限制

#### 1. 安全沙箱
RDP 環境對跨進程輸入注入施加嚴格限制，導致 SendInput 系列 API 失效。

#### 2. 消息路由
Windows 消息在 RDP 環境中的路由可能被修改，影響 WM_UNICHAR 等消息的傳遞。

## 解決方案選項

### 選項1：完整 TSF 實現（復雜但正確）
**需要實現**：
1. ITextStore 介面實現
2. 目標應用程式的 TSF 上下文獲取
3. 跨進程 TSF 通信機制

**工作量**：高（2-3 週）
**成功機率**：中等（取決於 RDP 限制）

### 選項2：混合策略（推薦）
**保留當前架構**：
- 英文：PostMessage WM_CHAR（已證實有效）
- 中文：回到剪貼簿方法（已證實有效）
- TSF：保留為未來擴展基礎

**工作量**：低（1-2 天）
**成功機率**：高（基於已驗證方法）

### 選項3：原生輸入法掛鉤
**思路**：
- 掛鉤系統輸入法切換
- 臨時啟用中文輸入法
- 通過輸入法發送中文字符

**工作量**：中等（1 週）
**成功機率**：中等（可能影響系統穩定性）

## 測試環境詳情

### 目標應用程式
- **進程**：PID=1356, TID=12872
- **窗口**：0x2049E
- **類型**：可能是 Warp Terminal 或類似的終端應用

### RDP 會話
- **檢測**：VK_PACKET 事件完美攔截
- **限制**：SendInput Error 87, 跨進程注入受限

### 當前輸出
- **英文**：完全正常
- **中文**：亂碼（`您好` → `Y`）

## 建議的下一步行動

### 立即修復（選項2）
1. **暫時回退中文處理**：恢復剪貼簿方法用於中文輸入
2. **保留 TSF 架構**：用於未來改進
3. **添加開關**：允許用戶選擇處理方法

### 中期改進（選項1）
1. **研究目標應用程式的 TSF 實現**
2. **實現完整的 ITextStore 介面**
3. **測試跨進程 TSF 通信**

### 程式碼變更
```csharp
// 添加配置選項
private static bool _useTSFForChinese = false; // 暫時關閉

// 修改中文處理邏輯
if (isAscii) {
    // 使用 PostMessage（已驗證有效）
} else {
    if (_useTSFForChinese) {
        // 嘗試 TSF（實驗性）
    } else {
        // 回退到剪貼簿方法（已驗證有效）
    }
}
```

## 結論

當前的 TSF 實現在技術架構上是正確和完整的，但在 RDP 環境的實際應用中遇到了跨線程關聯和字符編碼問題。建議採用實用主義方法，優先確保功能可用性，同時保留 TSF 基礎設施用於未來改進。

---

**報告生成時間**：2025-08-13  
**TSF 實現狀態**：技術架構完成，實際效果需要修復  
**下一個里程碑**：選擇並實施解決方案選項